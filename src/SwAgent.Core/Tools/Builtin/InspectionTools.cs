using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SwAgent.Core.Inspection;
using SwAgent.Core.Session;

namespace SwAgent.Core.Tools.Builtin
{
    /// <summary>Read the feature tree.</summary>
    public sealed class FeatureTreeTool : SwTool
    {
        public override string Name => "sw_feature_tree";
        public override string Description =>
            "List the features in the active document, with their types, suppression state and any errors.";

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Flag("include_reference_geometry",
                "Include reference planes and template folders. Off by default, as they are the same in every part.")
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            bool includeAll = args.GetBool("include_reference_geometry");
            var nodes = FeatureTree.Read(session, includeAll);
            return ToolResult.Success(FeatureTree.Render(nodes));
        }
    }

    /// <summary>Mass, volume, bounding box, centre of mass.</summary>
    public sealed class MassPropertiesTool : SwTool
    {
        public override string Name => "sw_mass_properties";
        public override string Description =>
            "Measure the active part: mass, volume, surface area, bounding box and centre of mass. " +
            "These numbers are ground truth - check them against what was asked for. Set the material " +
            "first if the mass matters, since mass depends on density.";

        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            var m = PartMeasure.OfActivePart(session);
            var c = CultureInfo.InvariantCulture;

            if (!m.HasBody)
            {
                return ToolResult.Success(
                    "The part has no solid body yet: volume is zero. Create a feature first.");
            }

            var sb = new StringBuilder();
            sb.AppendLine(m.Describe());

            if (m.BoundingBoxMm != null)
            {
                sb.AppendLine(
                    $"Bounding box (mm): X {m.BoundingBoxMm[0].ToString("0.###", c)} to {m.BoundingBoxMm[3].ToString("0.###", c)}, " +
                    $"Y {m.BoundingBoxMm[1].ToString("0.###", c)} to {m.BoundingBoxMm[4].ToString("0.###", c)}, " +
                    $"Z {m.BoundingBoxMm[2].ToString("0.###", c)} to {m.BoundingBoxMm[5].ToString("0.###", c)}");
            }

            sb.AppendLine($"Surface area: {m.SurfaceAreaCm2.ToString("0.###", c)} cm2.");
            sb.AppendLine(
                $"Centre of mass (mm): ({m.CentreOfMassMm[0].ToString("0.###", c)}, " +
                $"{m.CentreOfMassMm[1].ToString("0.###", c)}, {m.CentreOfMassMm[2].ToString("0.###", c)})");

            string material = PartMeasure.ActiveMaterial(session);
            sb.Append(material == null
                ? $"No material assigned; density defaults to {m.DensityKgPerM3.ToString("0.#", c)} kg/m3."
                : $"Material: {material} ({m.DensityKgPerM3.ToString("0.#", c)} kg/m3).");

            return ToolResult.Success(sb.ToString());
        }
    }

    /// <summary>Capture the viewport.</summary>
    public sealed class ScreenshotTool : SwTool
    {
        public override string Name => "sw_screenshot";
        public override string Description =>
            "Capture the 3D viewport from a named direction. Use this to sanity-check a feature's " +
            "direction or position. A single viewpoint has blind spots, so ask for front, top or right " +
            "when checking a specific dimension.";

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Choice("view", "Camera direction.",
                new[] { "isometric", "front", "back", "left", "right", "top", "bottom", "trimetric" },
                required: false, defaultValue: "isometric")
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            var view = ParseView(args.GetString("view"));
            byte[] png = Viewport.Capture(session, view, Viewport.DefaultScratchDirectory);

            // Send the numbers alongside the picture, always. "Looks about
            // right" cannot distinguish 24 cm3 from 24,000 cm3; a bounding box
            // can.
            string summary;
            try
            {
                summary = PartMeasure.OfActivePart(session).Describe();
            }
            catch
            {
                summary = "(measurements unavailable)";
            }

            return ToolResult.Success(
                $"{view} view of the active document. {summary}",
                new ToolImage(png, $"{view} view"));
        }

        private static NamedView ParseView(string value)
        {
            switch ((value ?? "isometric").ToLowerInvariant())
            {
                case "isometric": return NamedView.Isometric;
                case "front": return NamedView.Front;
                case "back": return NamedView.Back;
                case "left": return NamedView.Left;
                case "right": return NamedView.Right;
                case "top": return NamedView.Top;
                case "bottom": return NamedView.Bottom;
                case "trimetric": return NamedView.Trimetric;
                default: throw new ArgumentException($"Unknown view '{value}'.");
            }
        }
    }

    /// <summary>The tools that exist today, in one place.</summary>
    public static class BuiltinTools
    {
        /// <summary>
        /// Build a registry of every tool currently implemented.
        ///
        /// Keep this list honest. A tool registered here is a promise to the
        /// model that the capability works; registering a stub means the agent
        /// will plan around something that does not exist and fail late.
        /// </summary>
        public static ToolRegistry CreateRegistry()
        {
            var registry = new ToolRegistry();

            registry.RegisterAll(new SwTool[]
            {
                // Intent - declared first, checked last
                new DeclareIntentTool(),
                new CheckIntentTool(),

                // Modelling
                new NewPartTool(),
                new SketchOpenTool(),
                new SketchOnFaceTool(),
                new SketchCloseTool(),
                new SketchRectTool(),
                new SketchCircleTool(),
                new SketchLineTool(),
                new ExtrudeTool(),
                new CutTool(),
                new ShellTool(),
                new FilletTool(),
                new ChamferTool(),
                new RebuildTool(),
                new UndoTool(),

                // Inspection
                new FeatureTreeTool(),
                new MassPropertiesTool(),
                new ScreenshotTool(),

                // Properties and materials
                new PropertyReadTool(),
                new PropertyWriteTool(),
                new MaterialSetTool(),

                // Files
                new SaveAsTool(),
                new ExportTool(),

                // Drawings
                new DrawingCreateTool(),
                new DrawingDimensionsTool(),
                new TitleBlockTool()
            });

            return registry;
        }
    }
}
