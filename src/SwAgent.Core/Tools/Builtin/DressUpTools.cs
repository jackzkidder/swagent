using System;
using System.Collections.Generic;
using SwAgent.Core.Modeling;
using SwAgent.Core.Session;

namespace SwAgent.Core.Tools.Builtin
{
    /// <summary>Hollow a solid out, leaving uniform walls.</summary>
    public sealed class ShellTool : SwTool
    {
        public override string Name => "sw_shell";
        public override string Description =>
            "Hollow out the part, leaving walls of a uniform thickness. This is how you make an " +
            "enclosure, a housing or a box - not by cutting a pocket, which gives walls whose " +
            "thickness depends on where the sketch happened to be. Optionally remove one face to " +
            "leave the part open there, by firing a ray at it the same way sw_sketch_open_on_face does.";

        public override bool MutatesModel => true;

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Number("thickness_mm", "Wall thickness to leave, mm.", 0.1, 1000),

            ToolParameter.Flag("open_a_face",
                "Remove one face so the part is open there, e.g. the top of a box."),

            ToolParameter.Number("from_x_mm", "Ray start X, mm. Only used when open_a_face is true.",
                Limits.MinMm, Limits.MaxMm, required: false, defaultValue: 0),
            ToolParameter.Number("from_y_mm", "Ray start Y, mm.", Limits.MinMm, Limits.MaxMm,
                required: false, defaultValue: 0),
            ToolParameter.Number("from_z_mm", "Ray start Z, mm.", Limits.MinMm, Limits.MaxMm,
                required: false, defaultValue: 0),
            ToolParameter.Choice("direction", "Ray direction, for picking the face to remove.",
                new[] { "+x", "-x", "+y", "-y", "+z", "-z" }, required: false, defaultValue: "-z"),
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            double thickness = args.GetDouble("thickness_mm");
            bool openFace = args.GetBool("open_a_face");

            string faceNote = "sealed on all sides";

            if (openFace)
            {
                var hit = FaceSelector.SelectByRay(
                    session,
                    args.GetDouble("from_x_mm"),
                    args.GetDouble("from_y_mm"),
                    args.GetDouble("from_z_mm"),
                    FaceSelector.ParseDirection(args.GetString("direction")));

                if (!hit.Found)
                    return ToolResult.Failure(hit.Description, "face_not_found");

                faceNote = "open where the selected face was removed";
            }

            double removed = DressUpOps.Shell(session, thickness);

            // Report how much came out, not just that it worked.
            //
            // A shell thickness too large for the part does NOT fail: SOLIDWORKS
            // produces a nearly-solid body instead. A 60 mm shell on a 100 mm
            // cube removes 8 cm3 of 1000 and reports success. Only the agent
            // knows roughly how hollow the part was supposed to be, so it needs
            // the number to judge - the tool cannot.
            return Observation.AfterChange(
                session,
                $"Shelled to a {thickness} mm wall thickness, {faceNote}. " +
                $"Removed {removed:0.##} cm3. If that is far less than you expected, the wall is too " +
                "thick for this part - undo and use a thinner one.");
        }
    }

    /// <summary>Shared behaviour for fillet and chamfer, which differ only in the feature.</summary>
    public abstract class EdgeDressUpTool : SwTool
    {
        public override bool MutatesModel => true;

        protected abstract string SizeParameterName { get; }
        protected abstract string SizeDescription { get; }

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Number(SizeParameterName, SizeDescription, 0.05, 1000),

            ToolParameter.Choice("edges",
                "Which edges to apply it to. 'all' breaks every edge on the part. 'face' applies it " +
                "only to the edges around one face, picked with the ray arguments below.",
                new[] { "all", "face" }, required: false, defaultValue: "all"),

            ToolParameter.Number("from_x_mm", "Ray start X, mm. Only used when edges is 'face'.",
                Limits.MinMm, Limits.MaxMm, required: false, defaultValue: 0),
            ToolParameter.Number("from_y_mm", "Ray start Y, mm.", Limits.MinMm, Limits.MaxMm,
                required: false, defaultValue: 0),
            ToolParameter.Number("from_z_mm", "Ray start Z, mm.", Limits.MinMm, Limits.MaxMm,
                required: false, defaultValue: 0),
            ToolParameter.Choice("direction", "Ray direction.",
                new[] { "+x", "-x", "+y", "-y", "+z", "-z" }, required: false, defaultValue: "-z"),
        };

        protected abstract Modeling.DressUpOps.EdgeScope NoScope { get; }

        protected int SelectTargetEdges(ToolArgs args, SwSession session, out string scopeNote)
        {
            string scope = args.GetString("edges") ?? "all";

            if (string.Equals(scope, "face", StringComparison.OrdinalIgnoreCase))
            {
                var hit = FaceSelector.SelectByRay(
                    session,
                    args.GetDouble("from_x_mm"),
                    args.GetDouble("from_y_mm"),
                    args.GetDouble("from_z_mm"),
                    FaceSelector.ParseDirection(args.GetString("direction")));

                if (!hit.Found)
                    throw new InvalidOperationException(hit.Description);

                int n = DressUpOps.SelectEdges(session, DressUpOps.EdgeScope.FaceEdges);
                scopeNote = $"{n} edge(s) around the selected face";
                return n;
            }

            int all = DressUpOps.SelectEdges(session, DressUpOps.EdgeScope.AllEdges);
            scopeNote = $"all {all} edge(s) of the part";
            return all;
        }
    }

    /// <summary>Round edges.</summary>
    public sealed class FilletTool : EdgeDressUpTool
    {
        public override string Name => "sw_fillet";
        public override string Description =>
            "Round off edges. A radius larger than the surrounding material will be refused - a 5 mm " +
            "fillet does not fit on a 4 mm wall.";

        protected override string SizeParameterName => "radius_mm";
        protected override string SizeDescription => "Fillet radius, mm.";
        protected override DressUpOps.EdgeScope NoScope => DressUpOps.EdgeScope.AllEdges;

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            double radius = args.GetDouble("radius_mm");
            SelectTargetEdges(args, session, out string scopeNote);

            var feature = DressUpOps.Fillet(session, radius);

            return Observation.AfterChange(
                session, $"Fillet '{feature.Name}' of {radius} mm applied to {scopeNote}.");
        }
    }

    /// <summary>Break edges with a flat chamfer.</summary>
    public sealed class ChamferTool : EdgeDressUpTool
    {
        public override string Name => "sw_chamfer";
        public override string Description =>
            "Break edges with a flat chamfer. Use this rather than a fillet where a part needs an " +
            "edge break for handling or assembly.";

        protected override string SizeParameterName => "distance_mm";
        protected override string SizeDescription => "Chamfer distance, mm.";
        protected override DressUpOps.EdgeScope NoScope => DressUpOps.EdgeScope.AllEdges;

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            double distance = args.GetDouble("distance_mm");
            SelectTargetEdges(args, session, out string scopeNote);

            var feature = DressUpOps.Chamfer(session, distance);

            return Observation.AfterChange(
                session, $"Chamfer '{feature.Name}' of {distance} mm applied to {scopeNote}.");
        }
    }
}
