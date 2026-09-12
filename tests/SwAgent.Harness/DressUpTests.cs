using System;
using System.Text.Json;
using SwAgent.Core.Inspection;
using SwAgent.Core.Session;
using SwAgent.Core.Tools;
using SwAgent.Core.Tools.Builtin;

namespace SwAgent.Harness
{
    /// <summary>
    /// Shell, fillet and chamfer, checked against hand calculation.
    ///
    /// Shell is the one that matters most: it is the difference between a
    /// housing with a wall thickness you specified and a block with a pocket
    /// whose walls are whatever the sketch placement happened to leave.
    /// </summary>
    public static class DressUpTests
    {
        private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;
        private static JsonElement NoArgs => Args("{}");

        public static void ShellLeavesUniformWalls(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();

            run.Step("build a 100 mm cube");
            Expect(run, registry, session, "sw_new_part", NoArgs);
            Expect(run, registry, session, "sw_sketch_open", Args(@"{""plane"":""front""}"));
            Expect(run, registry, session, "sw_sketch_rect", Args(@"{""width_mm"":100,""height_mm"":100}"));
            var closed = Expect(run, registry, session, "sw_sketch_close", NoArgs);
            string sketch = ExtractQuoted(closed.Text) ?? "Sketch1";
            Expect(run, registry, session, "sw_extrude",
                Args($@"{{""sketch_name"":""{sketch}"",""depth_mm"":100}}"));

            var cube = PartMeasure.OfActivePart(session);
            run.AssertClose(cube.VolumeCm3, 1000.0, 0.02, "cube is 1000 cm3");

            var box = cube.BoundingBoxMm;
            if (box == null) { run.Fail("no bounding box"); return; }

            double midX = (box[0] + box[3]) / 2.0;
            double midY = (box[1] + box[4]) / 2.0;
            double aboveTop = box[5] + 20;

            run.Step("shell to 5 mm, open at the top");
            var shell = registry.Execute("sw_shell", Args(JsonSerializer.Serialize(new
            {
                thickness_mm = 5.0,
                open_a_face = true,
                from_x_mm = midX,
                from_y_mm = midY,
                from_z_mm = aboveTop,
                direction = "-z",
            })), session);

            run.Note(FirstLine(shell.Text));
            run.Assert(shell.Ok, "shell succeeded");
            if (!shell.Ok) { run.Note(Truncate(shell.Text, 200)); return; }

            // Hand calculation, which is the whole point of asserting here:
            //   outer            100 x 100 x 100 = 1,000,000 mm3
            //   cavity (top open) 90 x  90 x  95 =   769,500 mm3
            //   remaining                        =   230,500 mm3 = 230.5 cm3
            var shelled = PartMeasure.OfActivePart(session);
            run.Note($"shelled: {shelled.Describe()}");
            run.AssertClose(shelled.VolumeCm3, 230.5, 0.02,
                "volume matches a 5 mm shell open on one face (230.5 cm3)");

            run.Step("the outside is unchanged");
            var dims = shelled.SortedDimsMm;
            run.AssertClose(dims[0], 100.0, 0.01, "still 100 mm across");
            run.AssertClose(dims[2], 100.0, 0.01, "still 100 mm deep");

            run.Step("an absurd wall thickness is refused, not modelled");
            // On a FRESH cube: 60 mm of wall needs 120 mm of material to fit
            // between two opposing walls, so there is nothing to hollow.
            Expect(run, registry, session, "sw_new_part", NoArgs);
            Expect(run, registry, session, "sw_sketch_open", Args(@"{""plane"":""front""}"));
            Expect(run, registry, session, "sw_sketch_rect", Args(@"{""width_mm"":100,""height_mm"":100}"));
            var fresh = Expect(run, registry, session, "sw_sketch_close", NoArgs);
            string freshSketch = ExtractQuoted(fresh.Text) ?? "Sketch1";
            Expect(run, registry, session, "sw_extrude",
                Args($@"{{""sketch_name"":""{freshSketch}"",""depth_mm"":100}}"));

            var beforeThick = PartMeasure.OfActivePart(session);
            var tooThick = registry.Execute("sw_shell", Args(@"{""thickness_mm"":60}"), session);
            var afterThick = PartMeasure.OfActivePart(session);

            run.Note($"  volume {beforeThick.VolumeCm3:0.###} -> {afterThick.VolumeCm3:0.###} cm3");
            run.Note($"  bbox now {afterThick.SortedDimsMm[0]:0.#} x {afterThick.SortedDimsMm[1]:0.#} x {afterThick.SortedDimsMm[2]:0.#} mm");
            run.Note($"  result: {Truncate(FirstLine(tooThick.Text), 120)}");

            // Either SOLIDWORKS refuses it, or it produces something - and if it
            // produces something, the part must genuinely have been hollowed.
            // What is NOT acceptable is reporting success over an unchanged part.
            bool changed = afterThick.VolumeCm3 < beforeThick.VolumeCm3 - 1e-6;
            run.Assert(!tooThick.Ok || changed,
                "a shell either fails, or actually removes material - never 'succeeds' unchanged");
        }

        public static void FilletAndChamferBreakEdges(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();

            run.Step("build a 60 x 40 x 20 block");
            Expect(run, registry, session, "sw_new_part", NoArgs);
            Expect(run, registry, session, "sw_sketch_open", Args(@"{""plane"":""front""}"));
            Expect(run, registry, session, "sw_sketch_rect", Args(@"{""width_mm"":60,""height_mm"":40}"));
            var closed = Expect(run, registry, session, "sw_sketch_close", NoArgs);
            string sketch = ExtractQuoted(closed.Text) ?? "Sketch1";
            Expect(run, registry, session, "sw_extrude",
                Args($@"{{""sketch_name"":""{sketch}"",""depth_mm"":20}}"));

            var before = PartMeasure.OfActivePart(session);
            run.AssertClose(before.VolumeCm3, 48.0, 0.02, "block is 48 cm3");

            run.Step("fillet every edge at 3 mm");
            var fillet = registry.Execute("sw_fillet", Args(@"{""radius_mm"":3,""edges"":""all""}"), session);
            run.Note(FirstLine(fillet.Text));
            run.Assert(fillet.Ok, "fillet succeeded");

            if (fillet.Ok)
            {
                run.Assert(fillet.Text.Contains("edge(s)"), "it reports how many edges it touched");

                var after = PartMeasure.OfActivePart(session);
                run.Note($"after fillet: {after.Describe()}");

                // Rounding edges can only remove material, never add it, and it
                // must remove some or the feature did nothing.
                run.Assert(after.VolumeCm3 < before.VolumeCm3,
                    $"rounding removed material ({before.VolumeCm3:0.##} -> {after.VolumeCm3:0.##} cm3)");
                run.Assert(after.VolumeCm3 > before.VolumeCm3 * 0.9,
                    "it removed a plausible amount, not most of the part");
            }

            run.Step("a radius that cannot fit is refused");
            var huge = registry.Execute("sw_fillet", Args(@"{""radius_mm"":40,""edges"":""all""}"), session);
            run.Assert(!huge.Ok, "a 40 mm radius on a 20 mm thick block is refused");
            run.Note($"  {Truncate(FirstLine(huge.Text), 140)}");

            run.Step("chamfer on a fresh block");
            Expect(run, registry, session, "sw_new_part", NoArgs);
            Expect(run, registry, session, "sw_sketch_open", Args(@"{""plane"":""front""}"));
            Expect(run, registry, session, "sw_sketch_rect", Args(@"{""width_mm"":60,""height_mm"":40}"));
            var c2 = Expect(run, registry, session, "sw_sketch_close", NoArgs);
            string s2 = ExtractQuoted(c2.Text) ?? "Sketch1";
            Expect(run, registry, session, "sw_extrude", Args($@"{{""sketch_name"":""{s2}"",""depth_mm"":20}}"));

            var beforeChamfer = PartMeasure.OfActivePart(session);
            var chamfer = registry.Execute("sw_chamfer", Args(@"{""distance_mm"":2,""edges"":""all""}"), session);
            run.Note(FirstLine(chamfer.Text));
            run.Assert(chamfer.Ok, "chamfer succeeded");

            if (chamfer.Ok)
            {
                var afterChamfer = PartMeasure.OfActivePart(session);
                run.Assert(afterChamfer.VolumeCm3 < beforeChamfer.VolumeCm3,
                    $"chamfering removed material ({beforeChamfer.VolumeCm3:0.##} -> {afterChamfer.VolumeCm3:0.##} cm3)");
            }
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
