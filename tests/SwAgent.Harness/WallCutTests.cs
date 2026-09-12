using System;
using System.Text.Json;
using SwAgent.Core.Inspection;
using SwAgent.Core.Session;
using SwAgent.Core.Tools;
using SwAgent.Core.Tools.Builtin;

namespace SwAgent.Harness
{
    /// <summary>
    /// The regression test for the house.
    ///
    /// Asked for a model house, the agent cut the windows with
    /// 'through_all_both' - because the system prompt told it to - and the
    /// openings went straight through the far wall as well. The volume was
    /// wrong, the rebuild was clean, and nothing caught it.
    ///
    /// This proves the two fixes: a sketch can be opened on a chosen face, and
    /// 'through_next' stops at the first solid it passes through.
    ///
    /// It asserts the RELATIONSHIP between the two end conditions rather than
    /// an absolute volume, so it stays valid regardless of exactly where on the
    /// wall the opening lands.
    /// </summary>
    public static class WallCutTests
    {
        private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;
        private static JsonElement NoArgs => Args("{}");

        public static void ThroughNextStopsAtTheFirstWall(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();

            // ---- a hollow box: two opposing walls with a gap between them ----
            run.Step("build a 100mm cube");
            Expect(run, registry, session, "sw_new_part", NoArgs);
            Expect(run, registry, session, "sw_sketch_open", Args(@"{""plane"":""front""}"));
            Expect(run, registry, session, "sw_sketch_rect", Args(@"{""width_mm"":100,""height_mm"":100}"));
            var closed = Expect(run, registry, session, "sw_sketch_close", NoArgs);
            string sketch = ExtractQuoted(closed.Text) ?? "Sketch1";
            Expect(run, registry, session, "sw_extrude",
                Args($@"{{""sketch_name"":""{sketch}"",""depth_mm"":100}}"));

            var solid = PartMeasure.OfActivePart(session);
            run.Note($"cube: {solid.Describe()}");
            run.AssertClose(solid.VolumeCm3, 1000.0, 0.02, "cube is 1000 cm3");

            // Work out where the box actually is, the same way the agent has to.
            var box = solid.BoundingBoxMm;
            if (box == null) { run.Fail("no bounding box - cannot aim a ray"); return; }

            double midX = (box[0] + box[3]) / 2.0;
            double midY = (box[1] + box[4]) / 2.0;
            double midZ = (box[2] + box[5]) / 2.0;

            // Hollow from the TOP, then cut the window in a SIDE wall.
            //
            // Doing both on the same face is a trap I fell into first time
            // round: the pocket leaves that face open, so the window has only
            // one wall in its path and both end conditions remove exactly the
            // same volume. The test passes nothing and proves nothing.
            double aboveTop = box[5] + 20;
            double outsideMinY = box[1] - 20;

            // ---- hollow it out from the top ----
            run.Step("open a sketch on the top (+Z) face by firing a ray down at it");
            var faceResult = registry.Execute("sw_sketch_open_on_face",
                Args(JsonSerializer.Serialize(new
                {
                    from_x_mm = midX,
                    from_y_mm = midY,
                    from_z_mm = aboveTop,
                    direction = "-z",
                })), session);

            run.Note(Truncate(faceResult.Text, 190));
            run.Assert(faceResult.Ok, "a face was found and a sketch opened on it");
            if (!faceResult.Ok) return;

            run.Assert(faceResult.Text.Contains("Sketch (0,0) is at model"),
                "the tool reports where the sketch origin landed");
            run.Assert(faceResult.Text.Contains("Sketch +X runs along model"),
                "the tool reports which way the sketch axes run");

            run.Step("pocket it out, leaving walls");
            Expect(run, registry, session, "sw_sketch_rect", Args(@"{""width_mm"":80,""height_mm"":80}"));
            var pocketSketch = Expect(run, registry, session, "sw_sketch_close", NoArgs);
            string pocketName = ExtractQuoted(pocketSketch.Text) ?? "Sketch2";

            var pocket = registry.Execute("sw_cut",
                Args($@"{{""sketch_name"":""{pocketName}"",""end_condition"":""blind"",""depth_mm"":80}}"), session);
            run.Note(Truncate(FirstLine(pocket.Text), 120));

            if (!pocket.Ok)
            {
                run.Note("pocket refused; retrying reversed");
                pocket = registry.Execute("sw_cut",
                    Args($@"{{""sketch_name"":""{pocketName}"",""end_condition"":""blind"",""depth_mm"":80,""reverse"":true}}"), session);
            }

            run.Assert(pocket.Ok, "the box is hollowed out");
            if (!pocket.Ok) return;

            var hollow = PartMeasure.OfActivePart(session);
            run.Note($"hollow box: {hollow.Describe()}");
            run.Assert(hollow.VolumeCm3 < solid.VolumeCm3, "hollowing removed material");

            double hollowVolume = hollow.VolumeCm3;

            // ---- the actual comparison ----
            run.Step("cut a 20mm window in a SIDE wall with through_next");
            double removedByNext = CutWindowAndMeasure(run, registry, session, "through_next", hollowVolume,
                midX, outsideMinY, midZ);

            run.Step("undo it");
            Expect(run, registry, session, "sw_undo", Args(@"{""steps"":1}"));
            var restored = PartMeasure.OfActivePart(session);
            run.AssertClose(restored.VolumeCm3, hollowVolume, 0.01, "undo restored the hollow box");

            run.Step("cut the same window with through_all_both");
            double removedByAll = CutWindowAndMeasure(run, registry, session, "through_all_both", hollowVolume,
                midX, outsideMinY, midZ);

            // ---- the point of the whole test ----
            run.Step("compare");
            run.Note($"through_next removed {removedByNext:0.##} cm3");
            run.Note($"through_all_both removed {removedByAll:0.##} cm3");

            run.Assert(removedByNext > 0, "through_next actually cut something");
            run.Assert(removedByAll > removedByNext,
                $"through_all_both removes MORE than through_next " +
                $"({removedByAll:0.##} vs {removedByNext:0.##} cm3) - it exits the far wall, which is the house bug");

            if (removedByNext > 0)
            {
                double ratio = removedByAll / removedByNext;
                run.Note($"ratio {ratio:0.##}x - roughly 2x means it went through both walls");
            }
        }

        /// <summary>Cut a window on a chosen face and report how much volume went.</summary>
        private static double CutWindowAndMeasure(
            TestRun run, ToolRegistry registry, SwSession session,
            string endCondition, double volumeBefore,
            double fromX, double fromY, double fromZ)
        {
            var opened = registry.Execute("sw_sketch_open_on_face",
                Args(JsonSerializer.Serialize(new
                {
                    from_x_mm = fromX,
                    from_y_mm = fromY,
                    from_z_mm = fromZ,
                    direction = "+y",
                })), session);

            if (!opened.Ok) { run.Fail($"could not open a sketch for the {endCondition} window: {opened.Text}"); return 0; }

            registry.Execute("sw_sketch_rect", Args(@"{""width_mm"":20,""height_mm"":20}"), session);
            var closed = registry.Execute("sw_sketch_close", NoArgs, session);
            string name = ExtractQuoted(closed.Text) ?? "Sketch";

            var cut = registry.Execute("sw_cut",
                Args($@"{{""sketch_name"":""{name}"",""end_condition"":""{endCondition}""}}"), session);

            run.Note($"  {endCondition}: {Truncate(FirstLine(cut.Text), 110)}");

            if (!cut.Ok) { run.Fail($"the {endCondition} cut failed: {Truncate(cut.Text, 140)}"); return 0; }

            var after = PartMeasure.OfActivePart(session);
            return volumeBefore - after.VolumeCm3;
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
            return i < 0 ? s : s.Substring(0, i);
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
