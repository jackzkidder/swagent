using System;
using System.Text.Json;
using SwAgent.Core.Session;
using SwAgent.Core.Tools;
using SwAgent.Core.Tools.Builtin;

namespace SwAgent.Harness
{
    /// <summary>
    /// The design-intent contract, and specifically whether it can be cheated.
    ///
    /// A contract the agent can satisfy by predicting loosely is worse than no
    /// contract, because it produces the appearance of verification. So most
    /// of these tests are attempts to game it.
    /// </summary>
    public static class IntentTests
    {
        private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;
        private static JsonElement NoArgs => Args("{}");

        public static void ContractCannotBeGamed(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();

            run.Step("a part to attach a contract to");
            Expect(run, registry, session, "sw_new_part", NoArgs);

            run.Step("a uselessly wide volume band is rejected");
            // The whole mechanism dies here if this passes: "somewhere between
            // 100 and 900" is satisfied by almost anything.
            var loose = registry.Execute("sw_declare_intent", Args(@"{
                ""summary"":""a box"",
                ""envelope_long_mm"":100,""envelope_mid_mm"":100,""envelope_short_mm"":100,
                ""volume_min_cm3"":100,""volume_max_cm3"":900}"), session);
            run.Assert(!loose.Ok, "a +/-80% band is refused");
            run.Note($"  {Truncate(loose.Text, 150)}");

            run.Step("a volume larger than the envelope that contains it is rejected");
            var impossible = registry.Execute("sw_declare_intent", Args(@"{
                ""summary"":""a box"",
                ""envelope_long_mm"":10,""envelope_mid_mm"":10,""envelope_short_mm"":10,
                ""volume_min_cm3"":500,""volume_max_cm3"":520}"), session);
            run.Assert(!impossible.Ok, "1 cm3 of envelope cannot hold 500 cm3 of material");
            run.Note($"  {Truncate(impossible.Text, 130)}");

            run.Step("envelope given out of order is rejected");
            var misordered = registry.Execute("sw_declare_intent", Args(@"{
                ""summary"":""a box"",
                ""envelope_long_mm"":10,""envelope_mid_mm"":100,""envelope_short_mm"":50,
                ""volume_min_cm3"":45,""volume_max_cm3"":50}"), session);
            run.Assert(!misordered.Ok, "largest-to-smallest ordering is enforced");

            run.Step("a tight, honest contract is accepted");
            var good = registry.Execute("sw_declare_intent", Args(@"{
                ""summary"":""100mm cube"",
                ""envelope_long_mm"":100,""envelope_mid_mm"":100,""envelope_short_mm"":100,
                ""volume_min_cm3"":950,""volume_max_cm3"":1050,
                ""min_features"":1}"), session);
            run.Assert(good.Ok, "a +/-5% band on a real prediction is accepted");
            run.Note($"  {Truncate(good.Text, 130)}");

            run.Step("it cannot be revised once building has started");
            // Without this, the prediction quietly becomes a description of
            // whatever was built, which is the exact failure being prevented.
            var revised = registry.Execute("sw_declare_intent", Args(@"{
                ""summary"":""actually a smaller cube"",
                ""envelope_long_mm"":50,""envelope_mid_mm"":50,""envelope_short_mm"":50,
                ""volume_min_cm3"":120,""volume_max_cm3"":130}"), session);
            run.Assert(!revised.Ok, "a second declaration is refused");
            run.Note($"  {Truncate(revised.Text, 150)}");
        }

        /// <summary>The contract must actually catch a part that misses it.</summary>
        public static void CheckCatchesAWrongPart(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();

            run.Step("declare a 100mm cube, then build one");
            Expect(run, registry, session, "sw_new_part", NoArgs);
            Expect(run, registry, session, "sw_declare_intent", Args(@"{
                ""summary"":""100mm cube"",
                ""envelope_long_mm"":100,""envelope_mid_mm"":100,""envelope_short_mm"":100,
                ""volume_min_cm3"":950,""volume_max_cm3"":1050}"));

            Expect(run, registry, session, "sw_sketch_open", Args(@"{""plane"":""front""}"));
            Expect(run, registry, session, "sw_sketch_rect", Args(@"{""width_mm"":100,""height_mm"":100}"));
            var closed = Expect(run, registry, session, "sw_sketch_close", NoArgs);
            string sketch = ExtractQuoted(closed.Text) ?? "Sketch1";
            Expect(run, registry, session, "sw_extrude",
                Args($@"{{""sketch_name"":""{sketch}"",""depth_mm"":100}}"));

            run.Step("the check passes on the right part");
            var pass = registry.Execute("sw_check_intent", NoArgs, session);
            run.Note(Truncate(pass.Text, 200));
            run.Assert(pass.Ok, "a part built to its contract passes");

            run.Step("now cut far too much out of it");
            // Stands in for the house: a cut that removed more than intended.
            Expect(run, registry, session, "sw_sketch_open", Args(@"{""plane"":""front""}"));
            Expect(run, registry, session, "sw_sketch_rect", Args(@"{""width_mm"":90,""height_mm"":90}"));
            var holeSketch = Expect(run, registry, session, "sw_sketch_close", NoArgs);
            string holeName = ExtractQuoted(holeSketch.Text) ?? "Sketch2";
            registry.Execute("sw_cut",
                Args($@"{{""sketch_name"":""{holeName}"",""end_condition"":""through_all_both""}}"), session);

            run.Step("the check now fails, and says which way it is wrong");
            var fail = registry.Execute("sw_check_intent", NoArgs, session);
            run.Note(Truncate(fail.Text, 260));

            run.Assert(!fail.Ok, "a part that no longer matches its contract fails the check");
            run.Assert(fail.ErrorKind == "intent_mismatch", "classified as intent_mismatch");
            run.Assert(fail.Text.Contains("Too little material"),
                "it says the cut removed too much, not merely that something is wrong");
            run.Assert(fail.Text.IndexOf("through more walls", StringComparison.OrdinalIgnoreCase) >= 0,
                "it points at the likely cause");
        }

        /// <summary>A new part is a new commitment.</summary>
        public static void NewPartClearsTheContract(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();

            Expect(run, registry, session, "sw_new_part", NoArgs);
            Expect(run, registry, session, "sw_declare_intent", Args(@"{
                ""summary"":""first part"",
                ""envelope_long_mm"":100,""envelope_mid_mm"":100,""envelope_short_mm"":100,
                ""volume_min_cm3"":950,""volume_max_cm3"":1050}"));

            run.Step("a new part drops the previous contract");
            Expect(run, registry, session, "sw_new_part", NoArgs);

            var second = registry.Execute("sw_declare_intent", Args(@"{
                ""summary"":""second part"",
                ""envelope_long_mm"":50,""envelope_mid_mm"":50,""envelope_short_mm"":50,
                ""volume_min_cm3"":120,""volume_max_cm3"":130}"), session);

            run.Assert(second.Ok, "a fresh part accepts a fresh contract");
            run.Note($"  {Truncate(second.Text, 130)}");

            run.Step("checking with no contract explains itself");
            Expect(run, registry, session, "sw_new_part", NoArgs);
            var orphan = registry.Execute("sw_check_intent", NoArgs, session);
            run.Assert(!orphan.Ok, "checking without a declared intent fails");
            run.Note($"  {Truncate(orphan.Text, 140)}");
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

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s.Replace("\n", " ") : s.Substring(0, max).Replace("\n", " ") + "...";
    }
}
