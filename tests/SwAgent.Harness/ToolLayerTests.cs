using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SolidWorks.Interop.sldworks;
using SwAgent.Core.Session;
using SwAgent.Core.Tools;
using SwAgent.Core.Tools.Builtin;

namespace SwAgent.Harness
{
    /// <summary>
    /// Exercises the tool layer over the exact path the agent will use: a tool
    /// name plus a JSON argument object, in; a ToolResult, out.
    ///
    /// Testing the underlying ops directly would miss the half that actually
    /// fails in production - argument parsing, validation, selection hygiene
    /// and the exception boundary.
    /// </summary>
    public static class ToolLayerTests
    {
        private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;
        private static JsonElement NoArgs => Args("{}");

        /// <summary>The registry must describe itself in valid, complete JSON.</summary>
        public static void RegistryEmitsValidSchema(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();

            run.Step("registry is populated");
            run.Assert(registry.Count >= 29, $"at least 29 tools registered (got {registry.Count})");

            run.Step("schema is valid JSON");
            string json = registry.ToJsonDefinitions();
            JsonDocument doc = null;
            try
            {
                doc = JsonDocument.Parse(json);
                run.Assert(true, "tool definitions parse as JSON");
            }
            catch (Exception ex)
            {
                run.Fail($"tool definitions are not valid JSON: {ex.Message}");
                return;
            }

            run.Step("every tool declares name, description and schema");
            bool allWellFormed = true;
            int count = 0;

            foreach (var tool in doc.RootElement.EnumerateArray())
            {
                count++;
                bool ok = tool.TryGetProperty("name", out var name)
                          && name.ValueKind == JsonValueKind.String
                          && !string.IsNullOrWhiteSpace(name.GetString())
                          && tool.TryGetProperty("description", out var desc)
                          && !string.IsNullOrWhiteSpace(desc.GetString())
                          && tool.TryGetProperty("input_schema", out var schema)
                          && schema.TryGetProperty("type", out _)
                          && schema.TryGetProperty("properties", out _);

                if (!ok)
                {
                    run.Fail($"tool definition is incomplete: {tool}");
                    allWellFormed = false;
                }
            }

            run.Assert(allWellFormed, "all tool definitions are well formed");
            run.Assert(count == registry.Count, $"schema lists every registered tool ({count})");
            run.Note($"tools: {string.Join(", ", registry.Tools.Select(t => t.Name).OrderBy(n => n))}");

            run.Step("no tool offers code execution");
            // The promise is that every capability is a named tool with typed
            // parameters. If this ever fails, the security story is gone.
            //
            // Matched on EXACT names, not substrings. The substring version of
            // this check flagged sw_shell - a CAD feature that hollows a solid
            // - as a shell-command tool. Keyword matching against a domain
            // vocabulary produces false positives, and a security check that
            // cries wolf gets switched off.
            var forbiddenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "sw_eval", "sw_exec", "sw_execute", "sw_run", "sw_run_vba", "sw_vba",
                "sw_macro", "sw_run_macro", "sw_script", "sw_run_script",
                "sw_shell_command", "sw_command", "sw_cmd", "sw_powershell", "sw_process",
            };

            var offenders = registry.Tools
                .Where(t => forbiddenNames.Contains(t.Name))
                .Select(t => t.Name)
                .ToList();

            run.Assert(offenders.Count == 0,
                offenders.Count == 0 ? "no code-execution tool is registered" : $"found: {string.Join(", ", offenders)}");

            run.Step("no tool takes a free-form code or path-glob payload");
            // The stronger property, and the one that actually matters: every
            // parameter is typed and bounded. A tool could be called anything
            // and still be dangerous if it accepted a script as a string.
            var suspicious = registry.Tools
                .SelectMany(t => t.Parameters.Select(p => new { Tool = t.Name, Param = p }))
                .Where(x => x.Param.Type == ToolParamType.String
                            && LooksLikeCodePayload(x.Param.Name))
                .Select(x => $"{x.Tool}.{x.Param.Name}")
                .ToList();

            if (suspicious.Count > 0)
                foreach (var x in suspicious) run.Note($"  {x}");

            run.Assert(suspicious.Count == 0,
                suspicious.Count == 0
                    ? "no tool accepts code, a script or a command as a parameter"
                    : $"found {suspicious.Count} suspicious parameter(s)");
        }

        /// <summary>
        /// Build the reference plate entirely through tool calls, and check the
        /// result against the same hand calculation as the direct-op test.
        /// </summary>
        public static void BuildsPlateThroughTools(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();

            run.Step("sw_new_part");
            Expect(run, registry, session, "sw_new_part", NoArgs);

            run.Step("sw_sketch_open on the front plane");
            Expect(run, registry, session, "sw_sketch_open", Args(@"{""plane"":""front""}"));

            run.Step("sw_sketch_rect 60 x 40");
            Expect(run, registry, session, "sw_sketch_rect",
                Args(@"{""center_x_mm"":0,""center_y_mm"":0,""width_mm"":60,""height_mm"":40}"));

            run.Step("sw_sketch_close");
            var closed = Expect(run, registry, session, "sw_sketch_close", NoArgs);

            // The tool tells the agent the sketch name; parse it back the same
            // way the agent would have to.
            string sketchName = ExtractQuoted(closed.Text) ?? "Sketch1";
            run.Note($"sketch name parsed from result: '{sketchName}'");

            run.Step("sw_extrude 10 mm blind");
            var extruded = Expect(run, registry, session, "sw_extrude",
                Args($@"{{""sketch_name"":""{sketchName}"",""end_condition"":""blind"",""depth_mm"":10}}"));

            run.Note(FirstLines(extruded.Text, 4));

            run.Step("the result carries the verification round-trip");
            run.Assert(extruded.Text.Contains("Rebuild clean"), "result reports rebuild state");
            run.Assert(extruded.Text.Contains("24") && extruded.Text.Contains("cm3"),
                "result reports volume (expected 24 cm3)");
            run.Assert(extruded.Text.Contains("Feature tree"), "result reports the feature tree");
            run.Assert(extruded.Images.Count == 1, "result carries exactly one screenshot");

            if (extruded.Images.Count == 1)
            {
                var png = extruded.Images[0].PngBytes;
                run.Assert(png.Length > 1000, $"screenshot is a real image ({png.Length:N0} bytes)");
                run.Assert(IsPng(png), "screenshot is a valid PNG");
                run.Note($"screenshot: {png.Length:N0} bytes, {extruded.Images[0].MediaType}");
            }

            run.Step("sw_mass_properties agrees with hand calculation");
            var mass = Expect(run, registry, session, "sw_mass_properties", NoArgs);
            run.Assert(mass.Text.Contains("24"), "mass properties report 24 g / 24 cm3");
            run.Note(FirstLines(mass.Text, 2));
        }

        /// <summary>Undo must actually remove the feature, and say so.</summary>
        public static void UndoRemovesTheFeature(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();

            run.Step("build a plate");
            Expect(run, registry, session, "sw_new_part", NoArgs);
            Expect(run, registry, session, "sw_sketch_open", Args(@"{""plane"":""front""}"));
            Expect(run, registry, session, "sw_sketch_rect", Args(@"{""width_mm"":60,""height_mm"":40}"));
            var closed = Expect(run, registry, session, "sw_sketch_close", NoArgs);
            string sketchName = ExtractQuoted(closed.Text) ?? "Sketch1";
            Expect(run, registry, session, "sw_extrude",
                Args($@"{{""sketch_name"":""{sketchName}"",""depth_mm"":10}}"));

            run.Step("feature tree contains the extrude");
            var before = registry.Execute("sw_feature_tree", NoArgs, session);
            run.Assert(before.Text.Contains("Boss-Extrude"), "extrude is in the tree before undo");

            run.Step("sw_undo");
            var undone = Expect(run, registry, session, "sw_undo", Args(@"{""steps"":1}"));
            run.Note(FirstLines(undone.Text, 1));

            run.Step("feature tree no longer contains the extrude");
            var after = registry.Execute("sw_feature_tree", NoArgs, session);
            run.Assert(!after.Text.Contains("Boss-Extrude"),
                "extrude is gone from the tree after undo");
        }

        /// <summary>
        /// Malformed arguments must fail cleanly with a message the model can
        /// act on, and must never crash SOLIDWORKS.
        /// </summary>
        public static void RejectsMalformedArguments(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();
            Expect(run, registry, session, "sw_new_part", NoArgs);

            run.Step("unknown tool name");
            var unknown = registry.Execute("sw_make_it_nice", NoArgs, session);
            run.Assert(!unknown.Ok, "unknown tool fails rather than throwing");
            run.Assert(unknown.ErrorKind == "unknown_tool", "classified as unknown_tool");
            run.Note($"  {Truncate(unknown.Text, 110)}");

            run.Step("missing required argument");
            var missing = registry.Execute("sw_sketch_rect", Args(@"{""width_mm"":60}"), session);
            run.Assert(!missing.Ok, "missing height_mm is rejected");
            run.Note($"  {missing.Text}");

            run.Step("wrong type");
            var wrongType = registry.Execute("sw_sketch_rect",
                Args(@"{""width_mm"":""wide"",""height_mm"":40}"), session);
            run.Assert(!wrongType.Ok, "non-numeric width is rejected");
            run.Note($"  {wrongType.Text}");

            run.Step("out of declared range");
            var outOfRange = registry.Execute("sw_sketch_rect",
                Args(@"{""width_mm"":60000,""height_mm"":40}"), session);
            run.Assert(!outOfRange.Ok, "60,000 mm width is rejected by the schema bounds");
            run.Note($"  {outOfRange.Text}");

            run.Step("bad enum value");
            var badEnum = registry.Execute("sw_sketch_open", Args(@"{""plane"":""sideways""}"), session);
            run.Assert(!badEnum.Ok, "unknown plane is rejected");
            run.Note($"  {badEnum.Text}");

            run.Step("null and empty argument objects");
            var noArgs = registry.Execute("sw_extrude", NoArgs, session);
            run.Assert(!noArgs.Ok, "extrude with no arguments is rejected");

            run.Step("sketch entity with no sketch open");
            var noSketch = registry.Execute("sw_sketch_rect",
                Args(@"{""width_mm"":60,""height_mm"":40}"), session);
            run.Assert(!noSketch.Ok, "drawing with no open sketch is rejected");
            run.Note($"  {Truncate(noSketch.Text, 110)}");

            run.Step("SOLIDWORKS survived all of it");
            run.Assert(session.App.RevisionNumber() != null, "session still alive");
        }

        // -----------------------------------------------------------------

        /// <summary>Parameter names that would indicate an execution escape hatch.</summary>
        private static bool LooksLikeCodePayload(string parameterName)
        {
            string[] names = { "code", "script", "macro", "vba", "command", "cmd", "expression", "eval" };
            string lower = (parameterName ?? string.Empty).ToLowerInvariant();
            return names.Any(n => lower == n || lower.EndsWith("_" + n) || lower.StartsWith(n + "_"));
        }

        private static ToolResult Expect(TestRun run, ToolRegistry registry, SwSession session,
                                         string tool, JsonElement args)
        {
            var result = registry.Execute(tool, args, session);
            if (!result.Ok)
                run.Fail($"{tool} failed: {result.Text}");
            return result;
        }

        /// <summary>Pull the first single-quoted token out of a result string.</summary>
        private static string ExtractQuoted(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int a = text.IndexOf('\'');
            if (a < 0) return null;
            int b = text.IndexOf('\'', a + 1);
            if (b < 0) return null;
            return text.Substring(a + 1, b - a - 1);
        }

        private static bool IsPng(byte[] bytes)
        {
            return bytes.Length > 8
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
        }

        private static string FirstLines(string text, int n)
        {
            var lines = (text ?? string.Empty).Split('\n').Take(n).Select(l => l.TrimEnd());
            return string.Join(" | ", lines);
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
