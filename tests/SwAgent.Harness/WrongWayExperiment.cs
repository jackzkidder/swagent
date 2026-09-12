using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SwAgent.Agent;
using SwAgent.Core.Inspection;
using SwAgent.Core.Session;
using SwAgent.Core.Tools;
using SwAgent.Core.Tools.Builtin;

namespace SwAgent.Harness
{
    /// <summary>
    /// The experiment that decides whether the viewport image earns its place.
    ///
    /// The definition of done asks for this: a deliberately wrong feature,
    /// caught by the model from the screenshot, undone, and rebuilt correctly.
    /// It has never been run.
    ///
    /// It also settles an argument. Every bug found so far was caught by a
    /// number, not a picture, which is an argument for dropping the image and
    /// saving the tokens - except that it is survivorship-biased. It counts
    /// only failures that survived long enough to be debugged, not ones the
    /// image quietly prevented.
    ///
    /// So this planting is VOLUME-NEUTRAL AND BOUNDING-BOX-NEUTRAL. The boss is
    /// on the wrong END of a symmetric plate: same mass, same volume, same
    /// overall size, different part. Every number the agent routinely sees is
    /// identical between right and wrong. If it catches this, it caught it from
    /// the picture, and the image has earned its cost.
    /// </summary>
    internal static class WrongWayExperiment
    {
        private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;
        private static JsonElement NoArgs => Args("{}");

        /// <summary>What the part is supposed to be. Given to the agent verbatim.</summary>
        private const string Specification =
            "a 100 x 60 x 10 mm plate with a 20 x 20 x 15 mm square boss standing proud of one face, " +
            "positioned 30 mm from the PLUS-Y end of the plate";

        public static int Run(SwSession session, string modelOverride)
        {
            Console.WriteLine();
            Console.WriteLine("WRONG-WAY EXPERIMENT");
            Console.WriteLine(new string('-', 68));
            Console.WriteLine("Planting a feature on the wrong side, then asking the agent to verify.");
            Console.WriteLine("Volume, mass and bounding box are IDENTICAL either way - only the");
            Console.WriteLine("viewport image distinguishes right from wrong.");
            Console.WriteLine();

            var registry = BuiltinTools.CreateRegistry();

            if (!PlantTheError(session, registry, out string setupNote))
            {
                Console.WriteLine("Could not set the experiment up: " + setupNote);
                return 2;
            }

            Console.WriteLine(setupNote);
            Console.WriteLine();

            string apiKey = Environment.GetEnvironmentVariable("SWAGENT_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                var store = new ApiKeyStore();
                if (!store.TryLoad(out apiKey))
                {
                    Console.WriteLine("No API key. Set SWAGENT_API_KEY or use --save-key.");
                    Console.WriteLine("The part has been left in SOLIDWORKS with the error planted,");
                    Console.WriteLine("so you can look at it yourself.");
                    return 2;
                }
            }

            var before = PartMeasure.OfActivePart(session);
            Console.WriteLine($"Before:  {before.Describe()}");
            Console.WriteLine();

            string prompt =
                "Someone else built the part that is currently open. It is supposed to be " +
                Specification + ". " +
                "Check whether it actually matches that, and if it does not, fix it. " +
                "Tell me plainly what you found.";

            using (var dispatcher = new PumpDispatcher())
            {
                var agent = new CadAgent(apiKey, dispatcher, session, registry, session.Log);
                if (!string.IsNullOrWhiteSpace(modelOverride)) agent.Model = modelOverride;

                Console.WriteLine($"Model: {agent.Model}");
                Console.WriteLine(new string('-', 68));

                var progress = new Progress<AgentEvent>(e =>
                {
                    switch (e.Type)
                    {
                        case AgentEvent.Kind.ToolCall:
                            Console.WriteLine($"  -> {e.ToolName}");
                            break;
                        case AgentEvent.Kind.ToolResult:
                            Console.WriteLine($"     {(e.Ok ? "ok  " : "FAIL")} {Truncate(FirstLine(e.Message), 90)}");
                            break;
                        case AgentEvent.Kind.Text:
                            Console.WriteLine($"  \"{Truncate(e.Message, 200)}\"");
                            break;
                        case AgentEvent.Kind.Failed:
                            Console.WriteLine($"  !! {e.Message}");
                            break;
                    }
                });

                Task<AgentRunResult> task = agent.RunAsync(prompt, progress, CancellationToken.None);
                dispatcher.PumpUntil(task);

                AgentRunResult result;
                try { result = task.GetAwaiter().GetResult(); }
                catch (Exception ex) { Console.WriteLine($"Run threw: {ex.Message}"); return 1; }

                var after = PartMeasure.OfActivePart(session);

                Console.WriteLine();
                Console.WriteLine(new string('-', 68));
                Console.WriteLine("VERDICT");
                Console.WriteLine($"  Before: {before.Describe()}");
                Console.WriteLine($"  After:  {after.Describe()}");
                Console.WriteLine($"  Tools:  {result.ToolCalls} in {result.Turns} turn(s)");
                Console.WriteLine($"  Cost:   {agent.Cost.Describe()}");
                Console.WriteLine();

                // Did it actually change anything, and did it say the right thing?
                string said = result.FinalText ?? string.Empty;
                bool claimsWrong =
                    said.IndexOf("wrong", StringComparison.OrdinalIgnoreCase) >= 0
                    || said.IndexOf("minus-y", StringComparison.OrdinalIgnoreCase) >= 0
                    || said.IndexOf("-y", StringComparison.OrdinalIgnoreCase) >= 0
                    || said.IndexOf("incorrect", StringComparison.OrdinalIgnoreCase) >= 0
                    || said.IndexOf("opposite", StringComparison.OrdinalIgnoreCase) >= 0
                    || said.IndexOf("moved", StringComparison.OrdinalIgnoreCase) >= 0
                    || said.IndexOf("fixed", StringComparison.OrdinalIgnoreCase) >= 0;

                Console.WriteLine(claimsWrong
                    ? "  The agent reported the part did NOT match the specification."
                    : "  The agent did NOT report a mismatch.");

                Console.WriteLine();
                Console.WriteLine("  Judge it yourself from the transcript above and the part on screen:");
                Console.WriteLine("   - did it notice the boss was on the wrong end?");
                Console.WriteLine("   - did it look at a screenshot before deciding?");
                Console.WriteLine("   - did it undo and rebuild, or patch over the top?");
                Console.WriteLine();
                Console.WriteLine("  If it caught this, it caught it from the IMAGE - every number was");
                Console.WriteLine("  identical either way. Keep the transcript; it is the definition-of-done");
                Console.WriteLine("  item that has never been evidenced.");

                return claimsWrong ? 0 : 1;
            }
        }

        /// <summary>
        /// Build the plate and put the boss on the MINUS-Y end, when the
        /// specification given to the agent says plus-Y.
        /// </summary>
        private static bool PlantTheError(SwSession session, ToolRegistry registry, out string note)
        {
            note = null;

            var newPart = registry.Execute("sw_new_part", NoArgs, session);
            if (!newPart.Ok) { note = newPart.Text; return false; }

            Run(registry, session, "sw_sketch_open", @"{""plane"":""front""}");
            Run(registry, session, "sw_sketch_rect", @"{""width_mm"":100,""height_mm"":60}");
            var closed = registry.Execute("sw_sketch_close", NoArgs, session);
            string plateSketch = ExtractQuoted(closed.Text) ?? "Sketch1";

            var extrude = registry.Execute("sw_extrude",
                Args($@"{{""sketch_name"":""{plateSketch}"",""depth_mm"":10}}"), session);
            if (!extrude.Ok) { note = "plate: " + extrude.Text; return false; }

            var plate = PartMeasure.OfActivePart(session);
            var box = plate.BoundingBoxMm;
            if (box == null) { note = "no bounding box after the plate"; return false; }

            // Put the boss on a face, 30 mm along - on the WRONG side.
            double faceX = box[3] + 20;
            double midY = (box[1] + box[4]) / 2.0;
            double midZ = (box[2] + box[5]) / 2.0;

            var onFace = registry.Execute("sw_sketch_open_on_face", Args(JsonSerializer.Serialize(new
            {
                from_x_mm = faceX,
                from_y_mm = midY,
                from_z_mm = midZ,
                direction = "-x",
            })), session);

            if (!onFace.Ok) { note = "boss face: " + onFace.Text; return false; }

            // -30 rather than +30: the planted error.
            Run(registry, session, "sw_sketch_rect",
                @"{""center_x_mm"":-30,""center_y_mm"":0,""width_mm"":20,""height_mm"":20}");

            var bossSketch = registry.Execute("sw_sketch_close", NoArgs, session);
            string bossName = ExtractQuoted(bossSketch.Text) ?? "Sketch2";

            var boss = registry.Execute("sw_extrude",
                Args($@"{{""sketch_name"":""{bossName}"",""depth_mm"":15}}"), session);

            if (!boss.Ok)
            {
                boss = registry.Execute("sw_extrude",
                    Args($@"{{""sketch_name"":""{bossName}"",""depth_mm"":15,""reverse"":true}}"), session);
            }

            if (!boss.Ok) { note = "boss: " + boss.Text; return false; }

            var final = PartMeasure.OfActivePart(session);
            note =
                "Planted: boss on the sketch-minus side, while the agent is told it should be at\n" +
                "plus-Y. The part measures " + final.Describe() + " - which is exactly what the\n" +
                "correct part would measure too.";

            return true;
        }

        private static void Run(ToolRegistry registry, SwSession session, string tool, string json)
            => registry.Execute(tool, Args(json), session);

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
