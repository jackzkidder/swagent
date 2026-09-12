using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Inspection;
using SwAgent.Core.Modeling;
using SwAgent.Core.Session;

namespace SwAgent.Core.Tools.Builtin
{
    /// <summary>Shared limits, so every tool rejects the same absurd values.</summary>
    internal static class Limits
    {
        public const double MaxMm = 10000.0;
        public const double MinMm = -10000.0;
        public const double MaxDepthMm = 10000.0;
    }

    /// <summary>Create a new part from the user's default template.</summary>
    public sealed class NewPartTool : SwTool
    {
        public override string Name => "sw_new_part";
        public override string Description =>
            "Create a new, empty part document from the user's default part template. " +
            "Do this before any modelling if no part is open.";
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
        public override bool MutatesModel => true;

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            // The user's template, never one of ours. Their template carries
            // their units, their custom properties and their drawing standards.
            string template = session.App.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);

            if (string.IsNullOrWhiteSpace(template))
            {
                return ToolResult.Failure(
                    "No default part template is configured in this SOLIDWORKS installation. " +
                    "The user needs to set one under Tools > Options > Default Templates.",
                    "no_template");
            }

            var doc = session.App.NewDocument(template, 0, 0, 0) as IModelDoc2;
            if (doc == null)
                return ToolResult.Failure($"SOLIDWORKS refused to create a part from '{template}'.");

            session.MarkSketchClosed();

            // A new part is a new commitment: the previous contract described a
            // different object and must not carry over.
            session.Intent.Clear();

            return ToolResult.Success(
                $"New part created from the default template. Title: {doc.GetTitle()}. " +
                "No features yet. Declare what you intend to build with sw_declare_intent before " +
                "adding features.");
        }
    }

    /// <summary>Open a sketch on one of the three standard planes.</summary>
    public sealed class SketchOpenTool : SwTool
    {
        public override string Name => "sw_sketch_open";
        public override string Description =>
            "Open a new sketch on a standard reference plane. Only one sketch may be open at a time; " +
            "close it with sw_sketch_close before extruding.";

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Choice("plane", "Which reference plane to sketch on.",
                new[] { "front", "top", "right" })
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            var plane = ParsePlane(args.GetString("plane"));
            string planeName = SketchOps.OpenOnPlane(session, plane);

            return ToolResult.Success(
                $"Sketch opened on the {plane} plane ('{planeName}'). " +
                "Add sketch entities, then call sw_sketch_close.");
        }

        internal static StandardPlane ParsePlane(string value)
        {
            switch ((value ?? string.Empty).ToLowerInvariant())
            {
                case "front": return StandardPlane.Front;
                case "top": return StandardPlane.Top;
                case "right": return StandardPlane.Right;
                default: throw new ArgumentException($"Unknown plane '{value}'. Use front, top or right.");
            }
        }
    }

    /// <summary>
    /// Open a sketch directly on a face of the existing solid.
    ///
    /// This is what makes anything beyond a single block possible. Sketching
    /// only on the three origin planes means every feature starts from the
    /// middle of the part and runs outward - which is how an opening in one
    /// wall of a house became an opening through both.
    /// </summary>
    public sealed class SketchOnFaceTool : SwTool
    {
        public override string Name => "sw_sketch_open_on_face";
        public override string Description =>
            "Open a sketch on a FACE of the existing solid, found by firing a ray at the model. " +
            "Use this to put a feature on a specific surface - a window in one wall, a hole on the top " +
            "face. Start the ray outside the part and aim it at the face you want: check the bounding box " +
            "with sw_mass_properties first. The result tells you where the sketch origin landed and which " +
            "way its axes run, which you need before placing anything.";

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Number("from_x_mm", "X of the ray's starting point, mm. Put it outside the part.",
                Limits.MinMm, Limits.MaxMm),
            ToolParameter.Number("from_y_mm", "Y of the ray's starting point, mm.", Limits.MinMm, Limits.MaxMm),
            ToolParameter.Number("from_z_mm", "Z of the ray's starting point, mm.", Limits.MinMm, Limits.MaxMm),
            ToolParameter.Choice("direction",
                "Which way the ray travels from that point. It selects the first face it hits.",
                new[] { "+x", "-x", "+y", "-y", "+z", "-z" }),
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            double x = args.GetDouble("from_x_mm");
            double y = args.GetDouble("from_y_mm");
            double z = args.GetDouble("from_z_mm");
            var direction = FaceSelector.ParseDirection(args.GetString("direction"));

            var hit = FaceSelector.SelectByRay(session, x, y, z, direction);
            if (!hit.Found)
                return ToolResult.Failure(hit.Description, "face_not_found");

            string frame = FaceSelector.OpenSketchOnSelectedFace(session);

            // Both halves matter: which face was hit, and where its sketch
            // coordinates are. Either alone leaves geometry placed by guesswork.
            return ToolResult.Success(hit.Description + " " + frame);
        }
    }

    /// <summary>Close the open sketch.</summary>
    public sealed class SketchCloseTool : SwTool
    {
        public override string Name => "sw_sketch_close";
        public override string Description =>
            "Close the currently open sketch. Returns the sketch's name, which you pass to sw_extrude or sw_cut.";
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            string name = SketchOps.Close(session);
            return ToolResult.Success(
                $"Sketch closed. Its name is '{name}' - pass that as sketch_name to sw_extrude or sw_cut.");
        }
    }

    /// <summary>Rectangle in the open sketch.</summary>
    public sealed class SketchRectTool : SwTool
    {
        public override string Name => "sw_sketch_rect";
        public override string Description =>
            "Draw a rectangle in the open sketch. All values are in MILLIMETRES, measured in the " +
            "sketch plane's own coordinates, with the origin at the plane's origin.";

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Number("center_x_mm", "X of the rectangle centre, mm.", Limits.MinMm, Limits.MaxMm, required: false, defaultValue: 0),
            ToolParameter.Number("center_y_mm", "Y of the rectangle centre, mm.", Limits.MinMm, Limits.MaxMm, required: false, defaultValue: 0),
            ToolParameter.Number("width_mm", "Width along the sketch X axis, mm.", 0.001, Limits.MaxMm),
            ToolParameter.Number("height_mm", "Height along the sketch Y axis, mm.", 0.001, Limits.MaxMm)
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            double cx = args.GetDouble("center_x_mm");
            double cy = args.GetDouble("center_y_mm");
            double w = args.GetDouble("width_mm");
            double h = args.GetDouble("height_mm");

            SketchOps.CenteredRectangle(session, cx, cy, w, h);

            return ToolResult.Success(
                $"Rectangle {w} x {h} mm drawn, centred at ({cx}, {cy}) mm in the sketch plane.");
        }
    }

    /// <summary>Circle in the open sketch.</summary>
    public sealed class SketchCircleTool : SwTool
    {
        public override string Name => "sw_sketch_circle";
        public override string Description =>
            "Draw a circle in the open sketch. All values in MILLIMETRES, in the sketch plane's coordinates.";

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Number("center_x_mm", "X of the circle centre, mm.", Limits.MinMm, Limits.MaxMm, required: false, defaultValue: 0),
            ToolParameter.Number("center_y_mm", "Y of the circle centre, mm.", Limits.MinMm, Limits.MaxMm, required: false, defaultValue: 0),
            ToolParameter.Number("radius_mm", "Radius, mm. For a hole, this is half the diameter.", 0.001, Limits.MaxMm)
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            double cx = args.GetDouble("center_x_mm");
            double cy = args.GetDouble("center_y_mm");
            double r = args.GetDouble("radius_mm");

            SketchOps.Circle(session, cx, cy, r);

            return ToolResult.Success(
                $"Circle of radius {r} mm (diameter {r * 2} mm) drawn, centred at ({cx}, {cy}) mm.");
        }
    }

    /// <summary>Line in the open sketch.</summary>
    public sealed class SketchLineTool : SwTool
    {
        public override string Name => "sw_sketch_line";
        public override string Description =>
            "Draw a straight line in the open sketch, in MILLIMETRES, in the sketch plane's coordinates.";

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Number("x1_mm", "Start X, mm.", Limits.MinMm, Limits.MaxMm),
            ToolParameter.Number("y1_mm", "Start Y, mm.", Limits.MinMm, Limits.MaxMm),
            ToolParameter.Number("x2_mm", "End X, mm.", Limits.MinMm, Limits.MaxMm),
            ToolParameter.Number("y2_mm", "End Y, mm.", Limits.MinMm, Limits.MaxMm)
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            double x1 = args.GetDouble("x1_mm"), y1 = args.GetDouble("y1_mm");
            double x2 = args.GetDouble("x2_mm"), y2 = args.GetDouble("y2_mm");

            SketchOps.Line(session, x1, y1, x2, y2);

            return ToolResult.Success($"Line drawn from ({x1}, {y1}) to ({x2}, {y2}) mm.");
        }
    }

    /// <summary>Shared base for the boss and cut tools, which differ only in which feature they make.</summary>
    public abstract class ExtrudeToolBase : SwTool
    {
        protected abstract bool IsCut { get; }
        public override bool MutatesModel => true;

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Text("sketch_name",
                "Name of the closed sketch to use, as returned by sw_sketch_close (e.g. 'Sketch1')."),
            ToolParameter.Choice("end_condition",
                IsCut
                    ? "How far to cut. 'through_next' cuts through the FIRST solid it meets and stops - use it " +
                      "for a window or opening in one wall. 'through_all_both' cuts through the ENTIRE model in " +
                      "both directions - only for a hole that genuinely must pass through everything. 'blind' " +
                      "uses depth_mm."
                    : "How far to extrude. 'blind' uses depth_mm.",
                new[] { "blind", "through_next", "through_all", "through_all_both", "mid_plane", "up_to_next" },
                required: false,
                defaultValue: IsCut ? "through_all_both" : "blind"),
            ToolParameter.Number("depth_mm",
                "Depth in MILLIMETRES. Used only for 'blind' and 'mid_plane'.",
                0.001, Limits.MaxDepthMm, required: false, defaultValue: 10.0),
            ToolParameter.Flag("reverse",
                "Reverse the direction. If a blind operation produces nothing, or produces material on " +
                "the wrong side, try this."),
            ToolParameter.Flag("merge",
                "Merge the result with existing solid bodies. Almost always true for a boss.",
                required: false, defaultValue: true)
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            string sketchName = args.GetString("sketch_name");
            var end = ParseEnd(args.GetString("end_condition"));

            var options = new ExtrudeOptions
            {
                End = end,
                DepthMm = args.GetDouble("depth_mm"),
                Reverse = args.GetBool("reverse"),
                Merge = args.GetBool("merge")
            };

            var doc = session.RequirePart();

            // Selection was already cleared by SwTool.Execute; select exactly
            // the sketch we were told to use and nothing else.
            if (!doc.Extension.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null, 0))
            {
                return ToolResult.Failure(
                    $"No sketch named '{sketchName}' could be selected. Check the name returned by " +
                    "sw_sketch_close, and make sure the sketch is closed.",
                    "sketch_not_found");
            }

            IFeature feature = IsCut
                ? ExtrudeOp.Cut(session, options)
                : ExtrudeOp.Boss(session, options);

            string what = IsCut ? "Cut" : "Extrude";
            string depthText = end == EndCondition.Blind || end == EndCondition.MidPlane
                ? $" {options.DepthMm} mm"
                : string.Empty;

            return Observation.AfterChange(
                session,
                $"{what} '{feature.Name}' created from '{sketchName}'{depthText} ({DescribeEnd(end)}).");
        }

        private static EndCondition ParseEnd(string value)
        {
            switch ((value ?? string.Empty).ToLowerInvariant())
            {
                case "blind": return EndCondition.Blind;
                case "through_all": return EndCondition.ThroughAll;
                case "through_all_both": return EndCondition.ThroughAllBoth;
                case "through_next": return EndCondition.ThroughNext;
                case "up_to_next": return EndCondition.UpToNext;
                case "mid_plane": return EndCondition.MidPlane;
                default: throw new ArgumentException($"Unknown end condition '{value}'.");
            }
        }

        private static string DescribeEnd(EndCondition end)
        {
            switch (end)
            {
                case EndCondition.Blind: return "blind";
                case EndCondition.ThroughAll: return "through all";
                case EndCondition.ThroughAllBoth: return "through all, both directions";
                case EndCondition.ThroughNext: return "through next solid only";
                case EndCondition.UpToNext: return "up to next face";
                case EndCondition.MidPlane: return "mid plane";
                default: return end.ToString();
            }
        }
    }

    /// <summary>Boss extrude.</summary>
    public sealed class ExtrudeTool : ExtrudeToolBase
    {
        protected override bool IsCut => false;
        public override string Name => "sw_extrude";
        public override string Description =>
            "Extrude a closed sketch profile into solid material. The sketch must be closed first.";
    }

    /// <summary>Cut extrude.</summary>
    public sealed class CutTool : ExtrudeToolBase
    {
        protected override bool IsCut => true;
        public override string Name => "sw_cut";
        public override string Description =>
            "Cut material away using a closed sketch profile. For a hole that passes right through, " +
            "use end_condition 'through_all_both'.";
    }

    /// <summary>Force a rebuild.</summary>
    public sealed class RebuildTool : SwTool
    {
        public override string Name => "sw_rebuild";
        public override string Description =>
            "Force a full rebuild and report any feature errors by name.";
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            var outcome = session.Rebuild();
            return outcome.IsClean
                ? ToolResult.Success(outcome.Describe())
                : ToolResult.Failure(outcome.Describe(), "rebuild_error");
        }
    }

    /// <summary>Undo. The recovery path, and not optional.</summary>
    public sealed class UndoTool : SwTool
    {
        public override string Name => "sw_undo";
        public override string Description =>
            "Undo the last operation. Use this as soon as you see that a feature is wrong - " +
            "undo and rebuild it correctly rather than building on top of a mistake.";
        public override bool MutatesModel => true;

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Integer("steps", "How many operations to undo.", 1, 20, required: false, defaultValue: 1)
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            int steps = args.GetInt("steps");
            var doc = session.RequireModel();

            // EditUndo2 returns void, so it cannot tell us whether anything
            // happened. Compare the tree before and after instead.
            int before = FeatureTree.Read(session).Count;

            doc.EditUndo2(steps);

            // Undo can leave a sketch open or closed; our bookkeeping is no
            // longer trustworthy, so re-derive it from SOLIDWORKS.
            session.MarkSketchClosed();

            int after = FeatureTree.Read(session).Count;
            int removed = before - after;

            string headline = removed > 0
                ? $"Undid {steps} step(s); {removed} feature(s) removed from the tree."
                : $"Undo of {steps} step(s) ran, but the feature count did not change ({after}). " +
                  "The last operation may not have been undoable.";

            return Observation.AfterChange(session, headline);
        }
    }
}
