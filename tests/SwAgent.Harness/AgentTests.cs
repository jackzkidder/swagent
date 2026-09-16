using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Anthropic.Models.Messages;
using SwAgent.Agent;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Tools.Builtin;

namespace SwAgent.Harness
{
    /// <summary>
    /// Tests for the agent layer that need no SOLIDWORKS and no API key.
    ///
    /// The key-handling tests here are the ones that matter most: the promise
    /// that the user's API key never appears in a log and never travels
    /// anywhere but api.anthropic.com has to be enforced by a test, because
    /// enforcing it by inspection means it stays true only until someone adds a
    /// debug line.
    /// </summary>
    public static class AgentTests
    {
        /// <summary>A realistic-looking key that is not a real one.</summary>
        private const string FakeKey = "sk-ant-api03-NOTAREALKEY-000111222333444555666777888999-AbCdEf";

        public static void ApiKeyIsStoredEncrypted(TestRun run)
        {
            string dir = Path.Combine(Path.GetTempPath(), "swagent_keytest_" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "apikey.dat");

            try
            {
                var store = new ApiKeyStore(path);

                run.Step("no key initially");
                run.Assert(!store.HasKey, "store reports no key before anything is saved");
                run.Assert(store.GetMaskedKey() == null, "masked key is null when nothing is stored");

                run.Step("save and reload");
                store.Save(FakeKey);
                run.Assert(store.HasKey, "store reports a key after saving");
                run.Assert(store.TryLoad(out string loaded), "key loads back");
                run.Assert(loaded == FakeKey, "round-trips exactly");

                run.Step("the file on disk does not contain the key");
                // This is the actual security property, not the API surface.
                byte[] raw = File.ReadAllBytes(path);
                string asAscii = Encoding.ASCII.GetString(raw);
                string asUtf16 = Encoding.Unicode.GetString(raw);

                run.Assert(!asAscii.Contains(FakeKey), "ciphertext does not contain the key as ASCII");
                run.Assert(!asUtf16.Contains(FakeKey), "ciphertext does not contain the key as UTF-16");
                run.Assert(!asAscii.Contains("sk-ant-api03-NOTAREALKEY"),
                    "ciphertext does not contain even the key's distinctive prefix");
                run.Note($"ciphertext is {raw.Length} bytes for a {FakeKey.Length}-character key");

                run.Step("masking shows enough to recognise, not enough to use");
                string masked = store.GetMaskedKey();
                run.Note($"masked: {masked}");
                run.Assert(masked != null && masked.StartsWith("sk-ant-"), "masked form keeps the prefix");
                run.Assert(masked.EndsWith("AbCdEf".Substring(2)), "masked form keeps the last four characters");
                run.Assert(!masked.Contains("NOTAREALKEY"), "masked form hides the body of the key");
                run.Assert(masked.Length < 20, $"masked form is short ({masked.Length} chars)");

                run.Step("clear removes it");
                store.Clear();
                run.Assert(!store.HasKey, "key is gone after Clear");
                run.Assert(!File.Exists(path), "file is deleted");

                run.Step("shape check rejects obvious rubbish");
                run.Assert(ApiKeyStore.LooksWellFormed(FakeKey), "a well-formed key passes");
                run.Assert(!ApiKeyStore.LooksWellFormed(""), "empty is rejected");
                run.Assert(!ApiKeyStore.LooksWellFormed("hunter2"), "a non-key is rejected");
                run.Assert(!ApiKeyStore.LooksWellFormed("sk-ant-x"), "a too-short key is rejected");
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>The key must not survive a trip through the log.</summary>
        public static void LogNeverContainsTheKey(TestRun run)
        {
            string dir = Path.Combine(Path.GetTempPath(), "swagent_logtest_" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "test.log");

            try
            {
                var log = new FileSwLog(path, verbose: true);

                run.Step("write the key through every log level, as carelessly as possible");
                log.Info($"Connecting with key {FakeKey}");
                log.Debug($"Authorization: Bearer {FakeKey}");
                log.Error($"Request failed for {FakeKey} against api.anthropic.com");
                log.Info("{\"x-api-key\":\"" + FakeKey + "\"}");

                run.Step("nothing readable survives");
                string contents = File.ReadAllText(path);
                run.Assert(!contents.Contains(FakeKey), "the full key is not in the log");
                run.Assert(!contents.Contains("NOTAREALKEY"), "no distinctive fragment of the key is in the log");
                run.Assert(contents.Contains("sk-ant-[REDACTED]"), "the key was replaced by a redaction marker");
                run.Assert(contents.Contains("api.anthropic.com"),
                    "surrounding text is preserved, so the log is still useful");

                int redactions = contents.Split(new[] { "sk-ant-[REDACTED]" }, StringSplitOptions.None).Length - 1;
                run.Assert(redactions == 4, $"all four occurrences were redacted (found {redactions})");

                run.Step("the scrubber does not mangle ordinary text");
                string clean = LogRedaction.Scrub("Extrude created. Bbox 60x40x10mm. Mass 142.3 g.");
                run.Assert(clean == "Extrude created. Bbox 60x40x10mm. Mass 142.3 g.",
                    "text with no key is passed through unchanged");
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// The key may travel to exactly one host. This asserts the client's
        /// configured endpoint rather than trusting that nobody changed it.
        /// </summary>
        public static void TrafficGoesOnlyToAnthropic(TestRun run)
        {
            run.Step("the SDK's default endpoint is api.anthropic.com");
            var client = new Anthropic.AnthropicClient { ApiKey = FakeKey };

            string baseUrl = client.BaseUrl?.ToString() ?? "(null)";
            run.Note($"BaseUrl = {baseUrl}");

            run.Assert(baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
                "the endpoint is HTTPS");
            run.Assert(baseUrl.IndexOf("api.anthropic.com", StringComparison.OrdinalIgnoreCase) >= 0,
                "the endpoint is api.anthropic.com");

            run.Step("the repository contains no other outbound host");
            // Catches a base URL being overridden, or a telemetry endpoint
            // being added, anywhere in the agent or core source.
            var offenders = ScanSourceForOutboundHosts();
            if (offenders.Count > 0)
                foreach (var o in offenders) run.Note($"  {o}");

            run.Assert(offenders.Count == 0,
                offenders.Count == 0
                    ? "no unexpected outbound hosts in source"
                    : $"found {offenders.Count} unexpected outbound host reference(s)");
        }

        /// <summary>
        /// Find real outbound URLs in our own source: a scheme, a host, and a
        /// host nobody authorised.
        ///
        /// This used to flag any non-comment line containing the letters
        /// "http" - which is how HttpClient, HttpClientHandler, and the words
        /// "HTTPS traffic" inside a user-facing error message became ten
        /// security findings, none of them a host.
        ///
        /// The repo learned this once already, in the code-execution check:
        /// keyword matching against a domain vocabulary produces false
        /// positives, and a security check that cries wolf gets switched off.
        /// So this matches URLs and compares HOSTS, exactly. It is stricter
        /// than what it replaces, not looser: a telemetry endpoint is still
        /// caught, and plain http to a non-namespace host is now caught too.
        ///
        /// Comments are scanned as well. A URL in a comment is not traffic, but
        /// it is how a "temporary" endpoint gets parked before someone uses it,
        /// and the allowlist makes the honest ones cheap to declare.
        /// </summary>
        private static List<string> ScanSourceForOutboundHosts()
        {
            var offenders = new List<string>();

            string repoRoot = FindRepoRoot();
            if (repoRoot == null) return offenders;

            var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "api.anthropic.com",       // the only host we ever send anything to
                "console.anthropic.com",   // user-facing text, and the panel's open-page allowlist
                "polyformproject.org",     // the licence
                "github.com",              // repository links in user-facing text
                "schemas.microsoft.com",   // XML namespaces
                "wixtoolset.org",          // installer schema namespace
                "www.w3.org",              // SVG and XML namespaces
            };

            // Hosts that legitimately appear as plain http, because a namespace
            // URI is an identifier and is never fetched.
            var namespaceHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "schemas.microsoft.com", "wixtoolset.org", "www.w3.org",
            };

            var url = new System.Text.RegularExpressions.Regex(
                @"(?<scheme>https?)://(?<host>[A-Za-z0-9._\-]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            string src = Path.Combine(repoRoot, "src");
            var files = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(src, "*.html", SearchOption.AllDirectories));

            foreach (string file in files)
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (System.Text.RegularExpressions.Match m in url.Matches(lines[i]))
                    {
                        string host = m.Groups["host"].Value;
                        string scheme = m.Groups["scheme"].Value;

                        if (!allowedHosts.Contains(host))
                        {
                            offenders.Add($"{Path.GetFileName(file)}:{i + 1}: unexpected host {host}");
                        }
                        else if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                                 && !namespaceHosts.Contains(host))
                        {
                            offenders.Add($"{Path.GetFileName(file)}:{i + 1}: plain http to {host}");
                        }
                    }
                }
            }

            return offenders;
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src"))
                    && File.Exists(Path.Combine(dir.FullName, "SwAgent.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>
        /// Old screenshots must fall out of history while their text stays, or
        /// a twenty-feature part cannot finish.
        /// </summary>
        public static void OldScreenshotsArePruned(TestRun run)
        {
            var conversation = new Conversation { MaxImagesRetained = 2 };
            byte[] fakePng = new byte[4096];

            run.Step("simulate a twenty-feature part");
            conversation.AddUserText("Make a bracket.");

            for (int i = 1; i <= 20; i++)
            {
                conversation.AddAssistant(new List<ContentBlockParam>
                {
                    new ToolUseBlockParam { ID = $"toolu_{i}", Name = "sw_extrude", Input = new Dictionary<string, System.Text.Json.JsonElement>() },
                });

                conversation.AddToolResults(new[]
                {
                    new ToolResultEntry($"toolu_{i}", $"Feature {i} created. Bbox 60x40x{i}mm.", fakePng, false),
                });
            }

            var messages = conversation.BuildMessages();

            run.Step("count images that survived");
            int images = CountImages(messages);
            run.Note($"{messages.Count} messages, {images} image(s) retained, {conversation.ImagesPruned} pruned");

            run.Assert(images == 2, $"exactly 2 images retained (got {images})");
            run.Assert(conversation.ImagesPruned == 18, $"18 images pruned (got {conversation.ImagesPruned})");

            run.Step("every tool_use still has a matching tool_result");
            // The API rejects the request otherwise, so this is not cosmetic.
            int toolUses = CountBlocks(messages, Role.Assistant, isToolUse: true);
            int toolResults = CountBlocks(messages, Role.User, isToolUse: false);
            run.Assert(toolUses == 20, $"20 tool_use blocks present (got {toolUses})");
            run.Assert(toolResults == 20, $"20 tool_result blocks present (got {toolResults})");

            run.Step("pruned results keep their measurements");
            string all = Flatten(messages);
            run.Assert(all.Contains("Feature 1 created"), "text from the oldest feature survives");
            run.Assert(all.Contains("Bbox 60x40x1mm"), "its measurements survive");
            run.Assert(all.Contains("omitted from history"),
                "the model is told a screenshot existed rather than left to infer it");

            run.Step("the most recent feature keeps its image");
            run.Assert(all.Contains("Feature 20 created"), "the newest result is present");
        }

        // --- helpers ---------------------------------------------------

        private static int CountImages(List<MessageParam> messages)
        {
            int count = 0;
            foreach (var m in messages)
            {
                if (!m.Content.TryPickContentBlockParams(out var blocks)) continue;
                foreach (var b in blocks)
                {
                    if (!b.TryPickToolResult(out ToolResultBlockParam tr)) continue;
                    if (!tr.Content.TryPickBlocks(out var inner)) continue;
                    count += inner.Count(x => x.TryPickImageBlockParam(out _));
                }
            }
            return count;
        }

        private static int CountBlocks(List<MessageParam> messages, Role role, bool isToolUse)
        {
            int count = 0;
            foreach (var m in messages)
            {
                if (m.Role != role) continue;
                if (!m.Content.TryPickContentBlockParams(out var blocks)) continue;
                foreach (var b in blocks)
                {
                    if (isToolUse && b.TryPickToolUse(out _)) count++;
                    if (!isToolUse && b.TryPickToolResult(out _)) count++;
                }
            }
            return count;
        }

        private static string Flatten(List<MessageParam> messages)
        {
            var sb = new StringBuilder();
            foreach (var m in messages)
            {
                if (m.Content.TryPickString(out string s)) { sb.AppendLine(s); continue; }
                if (!m.Content.TryPickContentBlockParams(out var blocks)) continue;

                foreach (var b in blocks)
                {
                    if (b.TryPickToolResult(out ToolResultBlockParam tr))
                    {
                        if (tr.Content.TryPickString(out string trs)) sb.AppendLine(trs);
                        else if (tr.Content.TryPickBlocks(out var inner))
                            foreach (var x in inner)
                                if (x.TryPickTextBlockParam(out TextBlockParam t)) sb.AppendLine(t.Text);
                    }
                }
            }
            return sb.ToString();
        }

        /// <summary>The tool schema the model receives must be complete and well formed.</summary>
        public static void ToolSchemaReachesTheApiIntact(TestRun run)
        {
            var registry = BuiltinTools.CreateRegistry();

            run.Step("every registered tool converts to an SDK tool");
            var sdkTools = registry.Tools.Select(AnthropicToolAdapter.ToSdkTool).ToList();
            run.Assert(sdkTools.Count == registry.Count,
                $"all {registry.Count} tools converted (got {sdkTools.Count})");

            run.Step("bounds survive the conversion");
            // The declared range is what stops a 60,000 mm rectangle before it
            // costs a round trip, so it must actually reach the model.
            bool foundBounds = sdkTools.Any(t =>
                t.Name == "sw_sketch_rect"
                && t.InputSchema.Properties.ContainsKey("width_mm")
                && t.InputSchema.Properties["width_mm"].ToString().Contains("maximum"));

            run.Assert(foundBounds, "sw_sketch_rect publishes a maximum for width_mm");

            run.Step("required parameters survive");
            var rect = sdkTools.First(t => t.Name == "sw_sketch_rect");
            run.Assert(rect.InputSchema.Required != null && rect.InputSchema.Required.Contains("width_mm"),
                "width_mm is marked required");
            run.Assert(!rect.InputSchema.Required.Contains("center_x_mm"),
                "center_x_mm is optional, as declared");
        }
    }
}
