using System;
using System.Runtime.InteropServices;
using System.Threading;
using SolidWorks.Interop.sldworks;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Session;

namespace SwAgent.Harness
{
    /// <summary>
    /// Headless test harness for the COM layer.
    ///
    /// The layering rule is that SwAgent.Core must be drivable with no UI
    /// present: if it is not, it cannot be tested, and an untestable COM layer
    /// is how unit errors reach customers. This process is the proof of that
    /// rule - it references Core and nothing else, and runs every reference
    /// part against a real SOLIDWORKS session.
    ///
    /// Unlike the add-in, this harness DOES acquire its own ISldWorks pointer.
    /// That is legitimate here and nowhere else: the add-in is handed a pointer
    /// in ConnectToSW and must use that one for its whole life.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool attachOnly = Array.IndexOf(args, "--attach-only") >= 0;

            // --save-key runs before anything touches SOLIDWORKS: storing a key
            // has nothing to do with CAD, and requiring a running session to do
            // it would be daft.
            int saveKeyAt = Array.IndexOf(args, "--save-key");
            if (saveKeyAt >= 0)
            {
                if (saveKeyAt + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Usage: SwAgent.Harness.exe --save-key sk-ant-...");
                    return 2;
                }

                return SaveKey(args[saveKeyAt + 1]);
            }

            Console.WriteLine("SwAgent headless harness");
            Console.WriteLine(new string('-', 68));

            ISldWorks sw;
            try
            {
                sw = Connect(attachOnly);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not connect to SOLIDWORKS: {ex.Message}");
                return 2;
            }

            try
            {
                Console.WriteLine($"Connected to SOLIDWORKS {sw.RevisionNumber()}");
                Console.WriteLine($"Visible: {sw.Visible}");

                var log = new FileSwLog(FileSwLog.DefaultPath, verbose: true);
                var session = new SwSession(sw, log);
                var run = new TestRun();

                // --agent "<prompt>" runs the real loop against the real API.
                int agentAt = Array.IndexOf(args, "--agent");
                if (agentAt >= 0)
                {
                    string prompt = agentAt + 1 < args.Length
                        ? args[agentAt + 1]
                        : "Make a 60 x 40 x 10 mm plate with a 10 mm hole in the middle.";

                    int modelAt = Array.IndexOf(args, "--model");
                    string model = modelAt >= 0 && modelAt + 1 < args.Length ? args[modelAt + 1] : null;

                    return LiveAgentRun.Run(session, prompt, model);
                }

                if (Array.IndexOf(args, "--cutprobe") >= 0)
                {
                    CutProbe.Run(session);
                    return 0;
                }

                if (Array.IndexOf(args, "--probe") >= 0)
                {
                    AxisProbe.Run(session);
                    return 0;
                }

                RunTest(run, session, "Reference part: 60x40x10 plate",
                    () => ReferencePartTests.Plate60x40x10(run, session));

                RunTest(run, session, "Reference part: plate with 10mm through hole",
                    () => ReferencePartTests.PlateWithThroughHole(run, session));

                RunTest(run, session, "Boundary: malformed arguments are rejected",
                    () => ReferencePartTests.RejectsAbsurdArguments(run, session));

                RunTest(run, session, "Tool layer: registry emits valid schema",
                    () => ToolLayerTests.RegistryEmitsValidSchema(run, session));

                RunTest(run, session, "Tool layer: builds the plate through tool calls",
                    () => ToolLayerTests.BuildsPlateThroughTools(run, session));

                RunTest(run, session, "Tool layer: undo removes the feature",
                    () => ToolLayerTests.UndoRemovesTheFeature(run, session));

                RunTest(run, session, "Tool layer: malformed arguments are rejected",
                    () => ToolLayerTests.RejectsMalformedArguments(run, session));

                RunTest(run, session, "Deliverable: material, properties, save, drawing, export",
                    () => DeliverableTests.ProducesTheWholeDeliverable(run, session));

                // These need neither SOLIDWORKS nor an API key.
                RunTest(run, session, "Agent: API key is stored encrypted",
                    () => AgentTests.ApiKeyIsStoredEncrypted(run));

                RunTest(run, session, "Agent: the key never reaches the log",
                    () => AgentTests.LogNeverContainsTheKey(run));

                RunTest(run, session, "Agent: traffic goes only to api.anthropic.com",
                    () => AgentTests.TrafficGoesOnlyToAnthropic(run));

                RunTest(run, session, "Agent: old screenshots are pruned from history",
                    () => AgentTests.OldScreenshotsArePruned(run));

                RunTest(run, session, "Agent: tool schema reaches the API intact",
                    () => AgentTests.ToolSchemaReachesTheApiIntact(run));

                return run.Summarize();
            }
            finally
            {
                // Never release into the user's session in a way that could
                // close their SOLIDWORKS. We attached; we simply let go.
                if (sw != null && Marshal.IsComObject(sw))
                    Marshal.ReleaseComObject(sw);
            }
        }

        /// <summary>
        /// Run one reference part, catching anything it throws so that a single
        /// broken test cannot abort the suite - and, more importantly, so a
        /// crash shows up as a failure rather than taking the process down.
        /// </summary>
        private static void RunTest(TestRun run, SwSession session, string name, Action body)
        {
            run.BeginTest(name);
            try
            {
                body();
            }
            catch (Exception ex)
            {
                run.Fail($"threw {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                CloseActiveDocQuietly(session);
            }
        }

        /// <summary>
        /// Discard the scratch document without saving. The harness never writes
        /// a file: it must not touch anything the user did not name.
        /// </summary>
        private static void CloseActiveDocQuietly(SwSession session)
        {
            try
            {
                var doc = session.ActiveDoc;
                if (doc == null) return;

                // Leave any sketch we might have left open, or the close is refused.
                if (Modeling.SketchOpsAccess.IsSketchOpen(doc))
                    ((ISketchManager)doc.SketchManager).InsertSketch(true);

                string title = doc.GetTitle();
                session.App.CloseDoc(title);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      (could not close scratch document: {ex.Message})");
            }
        }

        /// <summary>Encrypt and store an API key, then confirm without echoing it.</summary>
        private static int SaveKey(string key)
        {
            try
            {
                var store = new SwAgent.Agent.ApiKeyStore(log: new FileSwLog(FileSwLog.DefaultPath));

                if (!SwAgent.Agent.ApiKeyStore.LooksWellFormed(key))
                {
                    Console.Error.WriteLine(
                        "That does not look like an Anthropic API key (they start with 'sk-ant-'). Nothing was saved.");
                    return 2;
                }

                store.Save(key);

                // Confirm by reading back the masked form, never the key.
                Console.WriteLine($"Saved, encrypted for your Windows account: {store.GetMaskedKey()}");
                Console.WriteLine($"Location: {SwAgent.Agent.ApiKeyStore.DefaultPath}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not save the key: {ex.Message}");
                return 2;
            }
        }

        private static ISldWorks Connect(bool attachOnly)
        {
            // Prefer an already-running session: starting a second one is slow
            // and confusing on a developer machine.
            try
            {
                var running = Marshal.GetActiveObject("SldWorks.Application");
                Console.WriteLine("Attached to a running SOLIDWORKS session.");
                return (ISldWorks)running;
            }
            catch (COMException)
            {
                if (attachOnly)
                    throw new InvalidOperationException(
                        "No running SOLIDWORKS session found and --attach-only was specified.");
            }

            Console.WriteLine("No running session; starting SOLIDWORKS (this takes a while)...");

            var type = Type.GetTypeFromProgID("SldWorks.Application");
            if (type == null)
                throw new InvalidOperationException("SOLIDWORKS does not appear to be installed.");

            var app = (ISldWorks)Activator.CreateInstance(type);
            app.Visible = true;

            // Give it a moment to finish coming up before we drive it.
            Thread.Sleep(2000);
            return app;
        }
    }
}

namespace SwAgent.Harness.Modeling
{
    using SolidWorks.Interop.sldworks;

    /// <summary>Small shim so the harness can check sketch state during cleanup.</summary>
    internal static class SketchOpsAccess
    {
        public static bool IsSketchOpen(IModelDoc2 doc)
            => SwAgent.Core.Modeling.SketchOps.IsSketchActuallyOpen(doc);
    }
}
