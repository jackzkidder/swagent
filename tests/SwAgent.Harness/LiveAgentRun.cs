using System;
using System.Threading;
using System.Threading.Tasks;
using SwAgent.Agent;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Session;
using SwAgent.Core.Tools.Builtin;

namespace SwAgent.Harness
{
    /// <summary>
    /// The end-to-end run: one sentence in, a real part out.
    ///
    /// This spends the user's own API credit, so it is opt-in, it prints what
    /// it cost, and it never runs as part of the ordinary suite.
    /// </summary>
    internal static class LiveAgentRun
    {
        public static int Run(SwSession session, string prompt, string modelOverride)
        {
            Console.WriteLine();
            Console.WriteLine("LIVE AGENT RUN");
            Console.WriteLine(new string('-', 68));
            Console.WriteLine("This calls the Anthropic API and spends real credit from your own key.");
            Console.WriteLine();

            string apiKey = ResolveApiKey(session.Log);
            if (apiKey == null) return 2;

            var registry = BuiltinTools.CreateRegistry();

            using (var dispatcher = new PumpDispatcher())
            {
                var agent = new CadAgent(apiKey, dispatcher, session, registry, session.Log);
                if (!string.IsNullOrWhiteSpace(modelOverride)) agent.Model = modelOverride;

                Console.WriteLine($"Model:  {agent.Model}");
                Console.WriteLine($"Tools:  {registry.Count}");
                Console.WriteLine($"Prompt: {prompt}");
                Console.WriteLine(new string('-', 68));

                var progress = new Progress<AgentEvent>(Report);

                // Start the loop, then pump this thread so tool calls have
                // somewhere to land. This is the same split the add-in uses:
                // the network wait happens off this thread, the COM work on it.
                Task<AgentRunResult> task = agent.RunAsync(prompt, progress, CancellationToken.None);
                dispatcher.PumpUntil(task);

                AgentRunResult result;
                try
                {
                    result = task.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\nThe run threw: {ex.Message}");
                    return 1;
                }

                Console.WriteLine();
                Console.WriteLine(new string('-', 68));
                Console.WriteLine($"Completed:   {result.Completed}");
                Console.WriteLine($"Turns:       {result.Turns}");
                Console.WriteLine($"Tool calls:  {result.ToolCalls}");
                Console.WriteLine($"Marshalled:  {dispatcher.MarshalledCalls} call(s) moved onto the CAD thread");
                Console.WriteLine($"Cost:        {agent.Cost.Describe()}");

                if (!string.IsNullOrWhiteSpace(result.StoppedBecause))
                    Console.WriteLine($"Stopped:     {result.StoppedBecause}");

                if (!string.IsNullOrWhiteSpace(result.FinalText))
                {
                    Console.WriteLine();
                    Console.WriteLine("The agent says:");
                    Console.WriteLine(result.FinalText);
                }

                // The part is left open on purpose so it can be inspected.
                Console.WriteLine();
                Console.WriteLine("The part has been left open in SOLIDWORKS for inspection. Nothing was saved.");

                return result.Completed ? 0 : 1;
            }
        }

        private static void Report(AgentEvent e)
        {
            switch (e.Type)
            {
                case AgentEvent.Kind.ToolCall:
                    Console.WriteLine($"  -> {e.ToolName}");
                    break;

                case AgentEvent.Kind.ToolResult:
                    string firstLine = (e.Message ?? string.Empty).Split('\n')[0];
                    Console.WriteLine($"     {(e.Ok ? "ok  " : "FAIL")} {Truncate(firstLine, 100)}");
                    break;

                case AgentEvent.Kind.Text:
                    Console.WriteLine($"  \"{Truncate(e.Message, 140)}\"");
                    break;

                case AgentEvent.Kind.Retry:
                    Console.WriteLine($"  ... {e.Message}");
                    break;

                case AgentEvent.Kind.Failed:
                    Console.WriteLine($"  !! {e.Message}");
                    break;
            }
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";

        /// <summary>
        /// Find a key: the environment first, so a test run never has to touch
        /// the user's saved key, then the encrypted store.
        /// </summary>
        private static string ResolveApiKey(ISwLog log)
        {
            string fromEnv = Environment.GetEnvironmentVariable("SWAGENT_API_KEY");
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                Console.WriteLine($"Using the key from SWAGENT_API_KEY ({ApiKeyStore.Mask(fromEnv.Trim())}).");
                return fromEnv.Trim();
            }

            var store = new ApiKeyStore(log: log);
            if (store.TryLoad(out string stored))
            {
                Console.WriteLine($"Using the stored key ({ApiKeyStore.Mask(stored)}).");
                return stored;
            }

            Console.WriteLine("No API key found.");
            Console.WriteLine();
            Console.WriteLine("Set one for this run only:");
            Console.WriteLine("    $env:SWAGENT_API_KEY = \"sk-ant-...\"");
            Console.WriteLine();
            Console.WriteLine("or save it encrypted for reuse:");
            Console.WriteLine("    SwAgent.Harness.exe --save-key sk-ant-...");
            return null;
        }
    }
}
