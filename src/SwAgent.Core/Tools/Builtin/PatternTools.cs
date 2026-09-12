using System;
using System.Collections.Generic;
using SwAgent.Core.Modeling;
using SwAgent.Core.Session;

namespace SwAgent.Core.Tools.Builtin
{
    /// <summary>Repeat a feature along an axis.</summary>
    public sealed class LinearPatternTool : SwTool
    {
        public override string Name => "sw_linear_pattern";
        public override string Description =>
            "Repeat an existing feature in a straight line. Use this rather than creating the same " +
            "cut several times: four mounting holes as one pattern is a single dimension somebody can " +
            "later change, whereas four separate cuts are four edits and a mistake waiting to happen.";

        public override bool MutatesModel => true;

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Text("feature_name",
                "Exact name of the feature to repeat, as shown by sw_feature_tree, e.g. 'Cut-Extrude1'."),
            ToolParameter.Choice("direction", "Axis to repeat along.",
                new[] { "+x", "-x", "+y", "-y", "+z", "-z" }),
            ToolParameter.Integer("count",
                "Total number of instances, INCLUDING the original. Four holes means 4.", 2, 200),
            ToolParameter.Number("spacing_mm", "Centre-to-centre spacing, mm.", 0.1, Limits.MaxMm),
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            string featureName = args.GetString("feature_name");
            var direction = FaceSelector.ParseDirection(args.GetString("direction"));
            int count = args.GetInt("count");
            double spacing = args.GetDouble("spacing_mm");

            var pattern = PatternOps.LinearPattern(
                session, featureName, direction, count, spacing, out double changed);

            int added = count - 1;
            double perInstance = added > 0 ? changed / added : 0;

            // CHECK THIS NUMBER. A pattern whose instances fall off the edge of
            // the part creates fewer copies than asked for, rebuilds clean, and
            // reports no error of any kind. The per-instance figure against
            // what the original feature did is the only way to notice.
            return Observation.AfterChange(
                session,
                $"Pattern '{pattern.Name}': {count} instances of '{featureName}' at {spacing} mm spacing " +
                $"along {args.GetString("direction")}. The {added} added instance(s) changed the volume by " +
                $"{changed:0.###} cm3, i.e. {perInstance:0.###} cm3 each. Compare that against what " +
                $"'{featureName}' itself changed: if it is smaller, some instances fell outside the " +
                "material and you have fewer copies than you asked for. Undo and pattern the other way, " +
                "or reduce the spacing.");
        }
    }

    /// <summary>Mirror a feature about a standard plane.</summary>
    public sealed class MirrorTool : SwTool
    {
        public override string Name => "sw_mirror";
        public override string Description =>
            "Mirror an existing feature about one of the standard planes. Use it for symmetric parts " +
            "so the symmetry is recorded in the model rather than being a coincidence of two " +
            "separately placed features.";

        public override bool MutatesModel => true;

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Text("feature_name",
                "Exact name of the feature to mirror, as shown by sw_feature_tree."),
            ToolParameter.Choice("plane", "Plane to mirror about.",
                new[] { "front", "top", "right" }),
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            string featureName = args.GetString("feature_name");
            var plane = SketchOpenTool.ParsePlane(args.GetString("plane"));

            var mirrored = PatternOps.Mirror(session, featureName, plane);

            return Observation.AfterChange(
                session, $"Mirror '{mirrored.Name}': '{featureName}' mirrored about the {plane} plane.");
        }
    }
}
