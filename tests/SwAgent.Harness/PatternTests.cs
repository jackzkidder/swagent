using System;
using System.Linq;
using System.Text.Json;
using SwAgent.Core.Inspection;
using SwAgent.Core.Session;
using SwAgent.Core.Tools;
using SwAgent.Core.Tools.Builtin;

namespace SwAgent.Harness
{
    /// <summary>
    /// Patterns, checked for geometry AND for history.
    ///
    /// The geometry half is ordinary arithmetic. The history half is the reason
    /// patterns are worth building at all: four holes as one pattern is a
    /// single spacing dimension somebody can change later, and four separate
    /// cuts are four edits. Both look identical in a screenshot, so the feature
    /// tree is the only thing that can tell them apart.
    /// </summary>
    public static class PatternTests
    {
        private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;
        private static JsonElement NoArgs => Args("{}");

        public static void LinearPatternRepeatsAHole(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();

            run.Step("plate 100 x 60 x 10 with one hole 30 mm off centre");
            Expect(run, registry, session, "sw_new_part", NoArgs);
            Expect(run, registry, session, "sw_sketch_open", Args(@"{""plane"":""front""}"));
            Expect(run, registry, session, "sw_sketch_rect", Args(@"{""width_mm"":100,""height_mm"":60}"));
            var plateSketch = Expect(run, registry, session, "sw_sketch_close", NoArgs);
            string plateName = ExtractQuoted(plateSketch.Text) ?? "Sketch1";
            Expect(run, registry, session, "sw_extrude",
                Args($@"{{""sketch_name"":""{plateName}"",""depth_mm"":10}}"));

            var plate = PartMeasure.OfActivePart(session);
            run.AssertClose(plate.VolumeCm3, 60.0, 0.02, "plate is 60 cm3");

            Expect(run, registry, session, "sw_sketch_open", Args(@"{""plane"":""front""}"));
            Expect(run, registry, session, "sw_sketch_circle",
                Args(@"{""center_x_mm"":-30,""center_y_mm"":0,""radius_mm"":5}"));
            var holeSketch = Expect(run, registry, session, "sw_sketch_close", NoArgs);
            string holeName = ExtractQuoted(holeSketch.Text) ?? "Sketch2";

            var cut = Expect(run, registry, session, "sw_cut",
                Args($@"{{""sketch_name"":""{holeName}"",""end_condition"":""through_all_both""}}"));

            var oneHole = PartMeasure.OfActivePart(session);
            run.Note($"one hole: {oneHole.Describe()}");

            // pi * 5^2 * 10 = 785.398 mm3 = 0.7854 cm3
            const double holeVolumeCm3 = 0.785398;
            run.AssertClose(oneHole.VolumeCm3, 60.0 - holeVolumeCm3, 0.01, "one hole removed 0.785 cm3");

            // Work out which axis the 100 mm span runs along - that is the one
            // the holes should march down.
            var box = oneHole.BoundingBoxMm;
            string axis = LongestAxis(box);
            run.Note($"the 100 mm span runs along {axis}");

            run.Step("find the cut's feature name");
            var tree = Expect(run, registry, session, "sw_feature_tree", NoArgs);
            string cutFeature = FindFeatureNamed(tree.Text, "Cut-Extrude");
            run.Note($"patterning '{cutFeature}'");
            run.Assert(cutFeature != null, "the cut feature was found in the tree");
            if (cutFeature == null) return;

            run.Step("pattern it 4 times at 20 mm");
            var pattern = registry.Execute("sw_linear_pattern", Args(JsonSerializer.Serialize(new
            {
                feature_name = cutFeature,
                direction = axis,
                count = 4,
                spacing_mm = 20.0,
            })), session);

            run.Note(FirstLine(pattern.Text));

            double expected = 60.0 - 4 * holeVolumeCm3;

            // Retry on VOLUME, not on success - which is the whole lesson here.
            //
            // Patterning the wrong way marches the instances off the end of the
            // plate. SOLIDWORKS creates the feature anyway, rebuilds CLEAN,
            // reports no error of any kind, and leaves 1.5 holes instead of 4.
            // Every signal except the measurement says it worked.
            bool wrongWay = !pattern.Ok
                || Math.Abs(PartMeasure.OfActivePart(session).VolumeCm3 - expected) > 0.5;

            if (wrongWay)
            {
                run.Note("volume is wrong for four holes - instances went off the end; undoing");
                registry.Execute("sw_undo", Args(@"{""steps"":1}"), session);

                pattern = registry.Execute("sw_linear_pattern", Args(JsonSerializer.Serialize(new
                {
                    feature_name = cutFeature,
                    direction = Flip(axis),
                    count = 4,
                    spacing_mm = 20.0,
                })), session);
                run.Note(FirstLine(pattern.Text));
            }

            run.Assert(pattern.Ok, "the pattern was created");
            if (!pattern.Ok) return;

            run.Step("the result states the per-instance effect, so a short pattern is noticeable");
            run.Assert(pattern.Text.Contains("cm3 each"),
                "the tool reports how much each added instance changed the volume");

            run.Step("four holes' worth of material is gone");
            var patterned = PartMeasure.OfActivePart(session);
            run.Note($"patterned: {patterned.Describe()}");
            run.AssertClose(patterned.VolumeCm3, 60.0 - 4 * holeVolumeCm3, 0.01,
                "volume is the plate minus exactly four holes");

            run.Step("the outside is untouched");
            var dims = patterned.SortedDimsMm;
            run.AssertClose(dims[0], 100.0, 0.01, "still 100 mm");
            run.AssertClose(dims[1], 60.0, 0.01, "still 60 mm");

            run.Step("and the history records a pattern, not four cuts");
            // This is the half a screenshot cannot check.
            var finalTree = Expect(run, registry, session, "sw_feature_tree", NoArgs);
            run.Note(FirstLine(finalTree.Text));

            int cutCount = CountOccurrences(finalTree.Text, "Cut-Extrude");
            run.Assert(cutCount == 1, $"there is still exactly ONE cut feature (found {cutCount})");
            run.Assert(finalTree.Text.IndexOf("Pattern", StringComparison.OrdinalIgnoreCase) >= 0,
                "the tree contains a pattern feature");
        }

        /// <summary>An unknown feature name must fail usefully, naming what does exist.</summary>
        public static void UnknownFeatureIsReportedUsefully(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();

            Expect(run, registry, session, "sw_new_part", NoArgs);
            Expect(run, registry, session, "sw_sketch_open", Args(@"{""plane"":""front""}"));
            Expect(run, registry, session, "sw_sketch_rect", Args(@"{""width_mm"":50,""height_mm"":50}"));
            var s = Expect(run, registry, session, "sw_sketch_close", NoArgs);
            Expect(run, registry, session, "sw_extrude",
                Args($@"{{""sketch_name"":""{ExtractQuoted(s.Text) ?? "Sketch1"}"",""depth_mm"":10}}"));

            run.Step("pattern a feature that does not exist");
            var bogus = registry.Execute("sw_linear_pattern", Args(@"{
                ""feature_name"":""Cut-Extrude99"",""direction"":""+x"",""count"":3,""spacing_mm"":10}"), session);

            run.Assert(!bogus.Ok, "an unknown feature name fails");
            run.Assert(bogus.Text.Contains("Features present:"),
                "the error lists the features that DO exist, so the agent can correct itself");
            run.Note($"  {Truncate(bogus.Text, 170)}");

            run.Step("a count of 1 is rejected as not being a pattern");
            var single = registry.Execute("sw_linear_pattern", Args(@"{
                ""feature_name"":""Boss-Extrude1"",""direction"":""+x"",""count"":1,""spacing_mm"":10}"), session);
            run.Assert(!single.Ok, "count of 1 is refused by the schema bounds");
        }

        // --- helpers ---------------------------------------------------

        private static string LongestAxis(double[] box)
        {
            if (box == null) return "+x";
            double dx = box[3] - box[0], dy = box[4] - box[1], dz = box[5] - box[2];
            if (dx >= dy && dx >= dz) return "+x";
            return dy >= dz ? "+y" : "+z";
        }

        private static string Flip(string axis) => axis.StartsWith("+") ? "-" + axis.Substring(1) : "+" + axis.Substring(1);

        private static string FindFeatureNamed(string treeText, string prefix)
        {
            foreach (string line in (treeText ?? string.Empty).Split('\n'))
            {
                int i = line.IndexOf(prefix, StringComparison.Ordinal);
                if (i < 0) continue;

                string rest = line.Substring(i);
                int end = rest.IndexOf(' ');
                return end < 0 ? rest.Trim() : rest.Substring(0, end).Trim();
            }
            return null;
        }

        private static int CountOccurrences(string text, string needle)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return text.Split(new[] { needle }, StringSplitOptions.None).Length - 1;
        }

        private static ToolResult Expect(TestRun run, ToolRegistry registry, SwSession session,
                                         string tool, JsonElement args)
        {
            var result = registry.Execute(tool, args, session);
            if (!result.Ok) run.Fail($"{tool} failed: {Truncate(result.Text, 160)}");
            return result;
        }

        private static string ExtractQuoted(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int a = text.IndexOf('\'');
            if (a < 0) return null;
            int b = text.IndexOf('\'', a + 1);
            return b < 0 ? null : text.Substring(a + 1, b - a - 1);
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int i = s.IndexOf('\n');
            return Truncate(i < 0 ? s : s.Substring(0, i), 150);
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
