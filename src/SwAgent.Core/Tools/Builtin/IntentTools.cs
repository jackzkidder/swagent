using System;
using System.Collections.Generic;
using SwAgent.Core.Intent;
using SwAgent.Core.Session;

namespace SwAgent.Core.Tools.Builtin
{
    /// <summary>
    /// Commit to what the part will be, before building it.
    ///
    /// This is a prediction, not a description. Its value comes entirely from
    /// being made first and being hard to satisfy accidentally.
    /// </summary>
    public sealed class DeclareIntentTool : SwTool
    {
        public override string Name => "sw_declare_intent";
        public override string Description =>
            "Commit to what you are about to build, BEFORE creating any features. State the overall " +
            "size and the volume you expect the finished part to have, worked out from the dimensions " +
            "you intend to use. The part is checked against this at the end, so it is how you and the " +
            "user find out whether what got built is what was meant. It cannot be changed afterwards.";

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Text("summary",
                "One line describing the part, e.g. 'house body with four window openings'."),

            ToolParameter.Number("envelope_long_mm",
                "Longest overall dimension of the finished part, mm.", 0.1, Limits.MaxMm),
            ToolParameter.Number("envelope_mid_mm",
                "Middle overall dimension, mm.", 0.1, Limits.MaxMm),
            ToolParameter.Number("envelope_short_mm",
                "Shortest overall dimension, mm.", 0.1, Limits.MaxMm),

            ToolParameter.Number("volume_min_cm3",
                "Lower bound of the expected finished volume, cm3. Calculate this from the geometry " +
                "you intend to build - do not guess a wide range.", 0.001, 1e7),
            ToolParameter.Number("volume_max_cm3",
                "Upper bound, cm3. Must be within +/-15% of your estimate; a looser band is rejected.",
                0.001, 1e7),

            ToolParameter.Integer("min_features",
                "Fewest features the finished part should have. 0 to skip this check.",
                0, 500, required: false, defaultValue: 0),
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            // Require a part so the contract is bound to something real.
            session.RequirePart();

            var intent = new DesignIntent
            {
                Summary = args.GetString("summary"),
                EnvelopeLongMm = args.GetDouble("envelope_long_mm"),
                EnvelopeMidMm = args.GetDouble("envelope_mid_mm"),
                EnvelopeShortMm = args.GetDouble("envelope_short_mm"),
                VolumeMinCm3 = args.GetDouble("volume_min_cm3"),
                VolumeMaxCm3 = args.GetDouble("volume_max_cm3"),
                MinFeatures = args.GetInt("min_features"),
            };

            session.Intent.Declare(intent);

            return ToolResult.Success(
                "Intent recorded: " + intent.Describe() + ". " +
                "Build to this. Call sw_check_intent when the part is finished - you cannot report " +
                "the job done until the part has been checked against what you just committed to.");
        }
    }

    /// <summary>Measure the part and hold it against the declared intent.</summary>
    public sealed class CheckIntentTool : SwTool
    {
        public override string Name => "sw_check_intent";
        public override string Description =>
            "Compare the finished part against the intent declared before building. Call this before " +
            "telling the user the work is done. If it reports a mismatch, fix the part - do not explain " +
            "the mismatch away.";

        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            var check = session.Intent.Check(session);

            // A mismatch is a real failure and is reported as one, so the agent
            // treats it the way it treats a refused feature rather than as a
            // remark it can acknowledge and move past.
            return check.Passed
                ? ToolResult.Success(check.Describe())
                : ToolResult.Failure(check.Describe(), "intent_mismatch");
        }
    }
}
