using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SolidWorks.Interop.swconst;
using SwAgent.Agent;
using SwAgent.Core.Batch;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Inspection;
using SwAgent.Core.Session;
using SwAgent.Core.Tools;
using SwAgent.Core.Tools.Builtin;

namespace SwAgent.AddIn
{
    /// <summary>
    /// The task pane panel: a WebView2 chat UI bridged to the agent loop, with
    /// a plain-text fallback for when the browser cannot start.
    ///
    /// Two things here are load-bearing.
    ///
    /// First, the user data folder. WebView2 defaults it to the directory of
    /// the host executable, which here is SLDWORKS.exe in Program Files. That
    /// directory is not writable by a normal user, so the default fails and the
    /// task pane becomes a blank white rectangle with no error at all. We point
    /// it at LocalApplicationData explicitly.
    ///
    /// Second, initialisation is asynchronous and must stay that way. This
    /// control is created on the SOLIDWORKS UI thread, and blocking that thread
    /// waiting for a browser to start freezes the entire CAD application during
    /// add-in load.
    /// </summary>
    internal sealed class ChatPanelControl : UserControl
    {
        private readonly ISwLog _log;
        private readonly SwSession _session;
        private readonly ISwDispatcher _dispatcher;
        private readonly ApiKeyStore _keyStore;
        private readonly SettingsStore _settingsStore;
        private readonly ToolRegistry _registry;

        /// <summary>The user's saved choices. Applied when an agent is built.</summary>
        private SwAgentSettings _settings;

        private readonly WebView2 _webView;
        private readonly Label _fallback;

        private bool _initialisationStarted;
        private CadAgent _agent;
        private CancellationTokenSource _running;

        private volatile bool _batchRunning;
        private CancellationTokenSource _batchStop;
        private readonly ConcurrentQueue<string> _batchNotes = new ConcurrentQueue<string>();

        /// <summary>
        /// What conversations closed by New chat or a key change cost. The
        /// meter lives on the agent, which is discarded then, and the session
        /// total the user sees must not reset with it.
        /// </summary>
        private decimal _closedConversationsCost;

        /// <summary>
        /// The only pages the panel can open. The page names a destination and
        /// never supplies a URL, so nothing that reaches the page - model text
        /// included - can make it open an arbitrary address.
        /// </summary>
        private static readonly Dictionary<string, string> ExternalPages = new Dictionary<string, string>
        {
            ["keys"] = "https://console.anthropic.com/settings/keys",
            ["billing"] = "https://console.anthropic.com/settings/billing",
        };

        public ChatPanelControl(ISwLog log, SwSession session, ISwDispatcher dispatcher)
        {
            _log = log ?? NullSwLog.Instance;
            _session = session;
            _dispatcher = dispatcher;
            _keyStore = new ApiKeyStore(log: _log);
            _settingsStore = new SettingsStore(log: _log);
            _settings = _settingsStore.Load();
            _registry = BuiltinTools.CreateRegistry();

            if (_session != null)
                _session.Batches.PlanCreated += OnBatchPlanCreated;

            // Paint the page's own background before it loads. Otherwise a dark
            // SOLIDWORKS flashes a white pane every time it starts.
            bool dark = SolidWorksTheme() == "dark";
            // Must match --bg in Panel/panel.html for each theme.
            Color ground = dark ? Color.FromArgb(0x26, 0x26, 0x24) : Color.FromArgb(0xfa, 0xf9, 0xf5);

            Dock = DockStyle.Fill;
            BackColor = ground;

            _fallback = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(16),
                ForeColor = dark ? Color.FromArgb(0xf1, 0xef, 0xe8) : Color.FromArgb(0x1f, 0x1e, 0x1b),
                Text = "Starting SwAgent…",
            };

            _webView = new WebView2 { Dock = DockStyle.Fill, Visible = false, DefaultBackgroundColor = ground };

            Controls.Add(_webView);
            Controls.Add(_fallback);

            // Force the handle now: the task pane needs a real HWND to host,
            // and it must be created on this (the SOLIDWORKS UI) thread.
            var _ = Handle;
        }

        /// <summary>
        /// Begin browser initialisation without blocking the caller. Safe to
        /// call once; subsequent calls are ignored.
        /// </summary>
        public void BeginInitialize()
        {
            if (_initialisationStarted) return;
            _initialisationStarted = true;

            // Fire and forget on purpose: the SOLIDWORKS thread must not wait
            // for this. Failures land in ShowFallback, never in the host.
            var _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            try
            {
                if (!NativeDependencies.EnsureWebView2Loader(_log))
                {
                    ShowFallback(
                        "SwAgent could not find WebView2Loader.dll next to the add-in.\r\n\r\n" +
                        "This is an installation problem, not a SOLIDWORKS one. Reinstalling " +
                        "SwAgent should fix it.");
                    return;
                }

                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SwAgent", "WebView2");

                Directory.CreateDirectory(userDataFolder);

                var environment = await CoreWebView2Environment
                    .CreateAsync(browserExecutableFolder: null, userDataFolder: userDataFolder)
                    .ConfigureAwait(true);

                await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

                ConfigureBrowser();

                _webView.CoreWebView2.WebMessageReceived += OnWebMessage;
                _webView.NavigateToString(PanelHtml.Text);

                _webView.Visible = true;
                _fallback.Visible = false;

                _log.Info("Chat panel ready.");
            }
            catch (WebView2RuntimeNotFoundException ex)
            {
                _log.Error($"WebView2 runtime missing: {ex.Message}");
                ShowFallback(
                    "SwAgent needs the Microsoft Edge WebView2 Runtime, which is not installed " +
                    "on this machine.\r\n\r\nThe installer normally sets this up. You can install " +
                    "it from Microsoft's website, then restart SOLIDWORKS.");
            }
            catch (Exception ex)
            {
                _log.Error($"WebView2 initialisation failed: {ex.Message}");
                ShowFallback(
                    "SwAgent could not start its interface.\r\n\r\n" + ex.Message +
                    "\r\n\r\nSOLIDWORKS is unaffected. See the log at\r\n" + FileSwLog.DefaultPath);
            }
        }

        private void ConfigureBrowser()
        {
            var settings = _webView.CoreWebView2.Settings;

            // This is our own UI, not the web. Turn off everything that only
            // makes sense for browsing someone else's pages.
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;

            // Nothing in this panel should ever open a second window.
            _webView.CoreWebView2.NewWindowRequested += (s, e) => e.Handled = true;
        }

        // ---------------------------------------------------------------
        // Bridge
        // ---------------------------------------------------------------

        /// <summary>
        /// Messages from the page. Guarded completely: a malformed message must
        /// not be able to throw on the SOLIDWORKS UI thread.
        /// </summary>
        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(raw)) return;

                using (var doc = JsonDocument.Parse(raw))
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeProp)) return;

                    switch (typeProp.GetString())
                    {
                        case "ready":
                            SendInit();
                            break;

                        case "saveKey":
                            // Deliberately never logged, at any level.
                            if (root.TryGetProperty("key", out var keyProp))
                                RunDetached(ValidateAndSaveKeyAsync(keyProp.GetString()));
                            break;

                        case "send":
                            if (root.TryGetProperty("text", out var textProp))
                                RunDetached(RunAgentAsync(textProp.GetString()));
                            break;

                        case "cancel":
                            _running?.Cancel();
                            break;

                        case "retry":
                            RunDetached(RunAgentAsync(null));
                            break;

                        case "newChat":
                            StartNewChat();
                            break;

                        case "openUrl":
                            if (root.TryGetProperty("url", out var pageProp))
                                OpenExternalPage(pageProp.GetString());
                            break;

                        case "openLogs":
                            OpenLogFolder();
                            break;

                        case "saveSettings":
                            SaveSettings(root);
                            break;

                        case "batchApply":
                            if (root.TryGetProperty("id", out var applyId))
                                RunDetached(ApplyBatchAsync(applyId.GetString()));
                            break;

                        case "batchDiscard":
                            if (root.TryGetProperty("id", out var discardId))
                                RunDetached(DiscardBatchAsync(discardId.GetString()));
                            break;

                        case "batchStop":
                            _batchStop?.Cancel();
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Panel message failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Observe a fire-and-forget task so a failure inside it is logged
        /// rather than becoming an unobserved exception in the host process.
        /// </summary>
        private void RunDetached(Task task)
        {
            task.ContinueWith(t =>
            {
                if (t.Exception != null)
                    _log.Error($"Background panel work failed: {t.Exception.GetBaseException().Message}");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void SendInit()
        {
            string masked = _keyStore.GetMaskedKey();
            PostToPage(new
            {
                type = "init",
                hasKey = masked != null,
                maskedKey = masked,
                model = _settings.Model,
                maxTurns = _settings.MaxTurns,
                version = typeof(ChatPanelControl).Assembly.GetName().Version.ToString(3),
                theme = SolidWorksTheme(),
                sessionCostUsd = SessionCost(),
            });
        }

        /// <summary>
        /// "dark" or "light" to match the SOLIDWORKS interface, or null to let
        /// the page follow Windows. SOLIDWORKS has its own brightness setting,
        /// and a dark pane in a light CAD window looks broken rather than themed.
        ///
        /// Reads the FeatureManager background - the thing that sits right next
        /// to this pane - and judges it by luminance, rather than relying on
        /// the theme enum keeping its meaning across releases.
        /// </summary>
        private string SolidWorksTheme()
        {
            try
            {
                if (_session?.App == null) return null;

                _session.App.GetInterfaceBrightnessThemeColors(out object colors);

                int index = (int)swInterfaceBrightnessColor_e.swIBColor_FeatureMgrBkgnd;
                if (!(colors is Array values) || values.Length <= index) return null;

                // A COLORREF (0x00BBGGRR). The themes are greys, so channel order
                // barely matters, but read it properly anyway.
                int bgr = Convert.ToInt32(values.GetValue(index));
                int r = bgr & 0xFF, g = (bgr >> 8) & 0xFF, b = (bgr >> 16) & 0xFF;
                double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;

                return luminance < 0.5 ? "dark" : "light";
            }
            catch (Exception ex)
            {
                _log.Debug($"Could not read the SOLIDWORKS theme: {ex.Message}");
                return null;
            }
        }

        private async Task ValidateAndSaveKeyAsync(string key)
        {
            try
            {
                // Validate off the UI thread: this is a network call, and it
                // must not freeze SOLIDWORKS while it runs.
                var validation = await KeyValidator.ValidateAsync(key).ConfigureAwait(false);

                if (validation.IsValid)
                {
                    _keyStore.Save(key);
                    DropAgent(); // rebuilt with the new key on next use

                    PostToPage(new
                    {
                        type = "keyResult",
                        ok = true,
                        message = "Key accepted.",
                        maskedKey = _keyStore.GetMaskedKey(),
                    });
                }
                else
                {
                    PostToPage(new { type = "keyResult", ok = false, code = Describe(validation.Result), message = validation.Message });
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Key validation failed: {ex.Message}");
                PostToPage(new { type = "keyResult", ok = false, message = "Could not check the key: " + ex.Message });
            }
        }

        /// <summary>
        /// Run the agent on the user's message. With null, retry instead:
        /// continue the conversation as it stands after a failed request,
        /// without repeating the message.
        /// </summary>
        private async Task RunAgentAsync(string text)
        {
            if (text != null && string.IsNullOrWhiteSpace(text)) return;

            if (_running != null)
            {
                // The page never sends while a run is live. Two loops on one
                // conversation would interleave its history, so refuse quietly.
                _log.Error("A run was requested while another was in progress; ignored.");
                return;
            }

            if (_batchRunning)
            {
                PostToPage(new { type = "done", completed = false, code = "busy", stoppedBecause = "A batch is being applied. Send again when it has finished." });
                return;
            }

            var running = new CancellationTokenSource();

            try
            {
                if (!_keyStore.TryLoad(out string apiKey))
                {
                    PostToPage(new { type = "done", completed = false, code = "no_key", stoppedBecause = "No API key is set." });
                    return;
                }

                if (text == null && (_agent == null || !_agent.CanResume))
                {
                    PostToPage(new { type = "done", completed = false, code = "nothing_to_retry", stoppedBecause = "There is nothing to retry. Send your message again." });
                    return;
                }

                if (_agent == null)
                {
                    _agent = new CadAgent(apiKey, _dispatcher, _session, _registry, _log, _settings.Model)
                    {
                        MaxTurns = _settings.MaxTurns,
                    };
                }

                CadAgent agent = _agent;
                decimal costBefore = agent.Cost.EstimatedCostUsd;
                _running = running;

                // Both of these must happen BEFORE the loop starts. Every tool
                // begins by clearing the selection, so the first tool call would
                // destroy the thing "this face" refers to; and a checkpoint
                // taken later would not cover the whole request.
                await _dispatcher.InvokeAsync(() => PrepareSession()).ConfigureAwait(false);

                // Progress arrives on worker threads; PostToPage marshals back
                // onto the UI thread itself. Replies and failures go across
                // whole; a tool result is one line (see FirstLine).
                var progress = new Progress<AgentEvent>(ev => PostToPage(new
                {
                    type = "agent",
                    kind = Describe(ev.Type),
                    message = ev.Type == AgentEvent.Kind.ToolResult ? FirstLine(ev.Message) : ev.Message,
                    toolName = ev.ToolName,
                    ok = ev.Ok,
                    code = ev.Code,
                }));

                // The loop runs off the UI thread. Everything inside it that
                // touches COM goes back through the dispatcher, which is the
                // whole reason the panel hands one in.
                string message = text == null ? null : WithBatchNotes(text);
                AgentRunResult result = await agent.RunAsync(message, progress, running.Token)
                    .ConfigureAwait(false);

                PostToPage(new
                {
                    type = "done",
                    completed = result.Completed,
                    stopped = running.IsCancellationRequested,
                    stoppedBecause = result.StoppedBecause,
                    runCostUsd = agent.Cost.EstimatedCostUsd - costBefore,
                    sessionCostUsd = SessionCost(),
                    costDetail = agent.Cost.Describe(),
                });
            }
            catch (OperationCanceledException)
            {
                PostToPage(new { type = "done", completed = false, stopped = true, stoppedBecause = "Stopped.", sessionCostUsd = SessionCost() });
            }
            catch (Exception ex)
            {
                _log.Error($"Agent run failed: {ex.Message}");
                PostToPage(new { type = "done", completed = false, code = "unexpected", stoppedBecause = "The agent stopped: " + ex.Message, sessionCostUsd = SessionCost() });
            }
            finally
            {
                if (ReferenceEquals(_running, running)) _running = null;
                try { running.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Snapshot what the user selected, and mark the feature tree so this
        /// request's work can be removed as a unit. Runs on the SOLIDWORKS
        /// thread, and never throws: neither of these is worth failing a run.
        /// </summary>
        private void PrepareSession()
        {
            try
            {
                _session.UserSelection = SelectionReader.Capture(_session);
            }
            catch (Exception ex)
            {
                _log.Debug($"Could not capture the selection: {ex.Message}");
                _session.UserSelection = null;
            }

            try
            {
                _session.Checkpoints.Mark(FeatureTree.Read(_session).Select(n => n.Name));
            }
            catch (Exception ex)
            {
                // No document open yet, most likely. A mark that does not
                // describe the tree is worse than none.
                _log.Debug($"Could not mark a checkpoint: {ex.Message}");
                _session.Checkpoints.Clear();
            }
        }

        /// <summary>
        /// Save the user's settings and apply them.
        ///
        /// A model change ends the conversation: the cost meter is built from
        /// the model, the prompt cache is keyed on the exact request prefix,
        /// and carrying a half-finished part across a model swap would report
        /// costs from one model against work done by another. The page says so
        /// before the user presses Save.
        /// </summary>
        private void SaveSettings(JsonElement root)
        {
            if (_running != null || _batchRunning)
            {
                PostToPage(new { type = "settingsResult", ok = false, message = "Wait for SwAgent to finish first." });
                return;
            }

            var wanted = new SwAgentSettings
            {
                Model = root.TryGetProperty("model", out var model) ? model.GetString() : _settings.Model,
                MaxTurns = root.TryGetProperty("maxTurns", out var turns) && turns.TryGetInt32(out int t)
                    ? t
                    : _settings.MaxTurns,
            };

            bool modelChanged = !string.Equals(wanted.Model, _settings.Model, StringComparison.OrdinalIgnoreCase);

            _settings = _settingsStore.Save(wanted);
            if (modelChanged) DropAgent();

            PostToPage(new
            {
                type = "settingsResult",
                ok = true,
                model = _settings.Model,
                maxTurns = _settings.MaxTurns,
                chatReset = modelChanged,
                sessionCostUsd = SessionCost(),
                message = modelChanged
                    ? $"Saved. {DescribeModel(_settings.Model)} will be used, starting a new chat."
                    : "Saved.",
            });
        }

        private static string DescribeModel(string modelId)
        {
            if (string.Equals(modelId, ModelIds.Sonnet5, StringComparison.OrdinalIgnoreCase)) return "Sonnet 5";
            if (string.Equals(modelId, ModelIds.Opus5, StringComparison.OrdinalIgnoreCase)) return "Opus 5";
            return modelId;
        }

        private void StartNewChat()
        {
            if (_running != null || _batchRunning) return;

            DropAgent();
            PostToPage(new { type = "chatReset", sessionCostUsd = SessionCost() });
        }

        /// <summary>Forget the conversation, keeping what it cost in the session total.</summary>
        private void DropAgent()
        {
            CadAgent agent = _agent;
            _agent = null;
            if (agent != null) _closedConversationsCost += agent.Cost.EstimatedCostUsd;
        }

        private decimal SessionCost() => _closedConversationsCost + (_agent?.Cost.EstimatedCostUsd ?? 0m);

        private void OpenExternalPage(string page)
        {
            if (page == null || !ExternalPages.TryGetValue(page, out string url))
            {
                _log.Debug($"Panel asked to open an unknown page: {page}");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _log.Error($"Could not open {url}: {ex.Message}");
            }
        }

        private void OpenLogFolder()
        {
            try
            {
                string folder = Path.GetDirectoryName(FileSwLog.DefaultPath);
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _log.Error($"Could not open the log folder: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------
        // Batches
        // ---------------------------------------------------------------

        /// <summary>
        /// A preview produced a plan: put it in front of the user, file by file.
        /// Raised on the SOLIDWORKS thread in the middle of a tool call.
        ///
        /// This card is the only place file names are shown. They go to this
        /// page, which is local, and never into the conversation.
        /// </summary>
        private void OnBatchPlanCreated(BatchPlan plan)
        {
            PostToPage(new
            {
                type = "batchPlan",
                id = plan.Id,
                title = plan.Title,
                folder = plan.Request.Folder,
                writesFiles = plan.WritesSourceFiles,
                ready = plan.CountOf(BatchRowStatus.Ready),
                unchanged = plan.CountOf(BatchRowStatus.Unchanged),
                skipped = plan.CountOf(BatchRowStatus.Skipped),
                rows = plan.Rows.Select((r, i) => new
                {
                    n = i + 1,
                    file = r.File.RelativePath,
                    status = Describe(r.Status),
                    current = r.Current,
                    proposed = r.Proposed,
                    note = r.Reason ?? r.Warning,
                    warn = r.Warning != null,
                }).ToArray(),
            });
        }

        /// <summary>
        /// The user pressed Apply. This, and not any tool, is what applies a batch.
        /// </summary>
        private async Task ApplyBatchAsync(string id)
        {
            if (_running != null)
            {
                PostToPage(new { type = "batchDone", id, ok = false, message = "Wait for SwAgent to finish before applying a batch." });
                return;
            }

            if (_batchRunning)
            {
                PostToPage(new { type = "batchDone", id, ok = false, message = "Another batch is being applied." });
                return;
            }

            _batchRunning = true;
            _batchStop = new CancellationTokenSource();

            try
            {
                BatchPlan plan;
                try
                {
                    plan = await _dispatcher.InvokeAsync(() => _session.Batches.BeginApply(id)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PostToPage(new { type = "batchDone", id, ok = false, message = ex.Message });
                    return;
                }

                PostToPage(new { type = "batchStarted", id });
                bool stopped = false;

                for (int i = 0; i < plan.Rows.Count; i++)
                {
                    var row = plan.Rows[i];
                    if (row.Status != BatchRowStatus.Ready) continue;

                    if (_batchStop.IsCancellationRequested)
                    {
                        stopped = true;
                        break;
                    }

                    int n = i + 1;
                    PostToPage(new { type = "batchRow", id, n, status = "working", note = (string)null });

                    try
                    {
                        // One file per dispatch: SOLIDWORKS gets its thread back
                        // between files, so it stays responsive and Stop works.
                        await _dispatcher.InvokeAsync(() => BatchRunner.ApplyRow(_session, plan, row)).ConfigureAwait(false);
                    }
                    catch (SwSessionLostException ex)
                    {
                        row.Status = BatchRowStatus.Failed;
                        row.Result = ex.Message;
                        stopped = true;
                    }
                    catch (Exception ex)
                    {
                        row.Status = BatchRowStatus.Failed;
                        row.Result = ex.Message;
                    }

                    PostToPage(RowUpdate(id, n, row));
                    if (stopped) break;
                }

                if (stopped)
                {
                    var untouched = plan.Rows
                        .Select((r, i) => (Row: r, N: i + 1))
                        .Where(x => x.Row.Status == BatchRowStatus.Ready)
                        .ToList();

                    BatchRunner.MarkRemainingStopped(plan);
                    foreach (var x in untouched) PostToPage(RowUpdate(id, x.N, x.Row));
                }

                await _dispatcher.InvokeAsync(() => _session.Batches.Finish(plan)).ConfigureAwait(false);

                string outcome = plan.DescribeOutcome() + (stopped ? " It was stopped part way." : "");
                _batchNotes.Enqueue(outcome);
                _log.Info(outcome);

                PostToPage(new { type = "batchDone", id, ok = true, message = outcome });
            }
            catch (Exception ex)
            {
                _log.Error($"Batch apply failed: {ex.Message}");
                PostToPage(new { type = "batchDone", id, ok = false, message = "The batch stopped: " + ex.Message });
            }
            finally
            {
                _batchRunning = false;
                try { _batchStop?.Dispose(); } catch { }
                _batchStop = null;
            }
        }

        private async Task DiscardBatchAsync(string id)
        {
            try
            {
                bool discarded = await _dispatcher.InvokeAsync(() => _session.Batches.Discard(id)).ConfigureAwait(false);
                if (discarded) _batchNotes.Enqueue($"The user discarded batch plan {id}; nothing was changed.");

                PostToPage(new
                {
                    type = "batchDone",
                    id,
                    ok = discarded,
                    discarded = true,
                    message = discarded ? "Discarded. Nothing was changed." : "This plan can no longer be discarded.",
                });
            }
            catch (Exception ex)
            {
                _log.Error($"Batch discard failed: {ex.Message}");
            }
        }

        private static object RowUpdate(string id, int n, BatchRow row) => new
        {
            type = "batchRow",
            id,
            n,
            status = Describe(row.Status),
            note = row.Result ?? row.Reason ?? row.Warning,
        };

        /// <summary>
        /// A batch is applied outside the conversation, so the model would not
        /// otherwise know it happened. The outcome rides along with the user's
        /// next message - counts only, no file names.
        /// </summary>
        private string WithBatchNotes(string text)
        {
            var notes = new List<string>();
            while (_batchNotes.TryDequeue(out string note)) notes.Add(note);

            return notes.Count == 0
                ? text
                : "[SwAgent note: " + string.Join(" ", notes) + "]\n\n" + text;
        }

        private static string Describe(BatchRowStatus status) => status.ToString().ToLowerInvariant();

        private static string Describe(KeyValidationResult result)
        {
            switch (result)
            {
                case KeyValidationResult.InvalidKey: return "bad_key";
                case KeyValidationResult.NoCredit: return "no_credit";
                case KeyValidationResult.NoNetwork: return "network";
                default: return "unknown";
            }
        }

        private static string Describe(AgentEvent.Kind kind)
        {
            switch (kind)
            {
                case AgentEvent.Kind.ToolCall: return "toolCall";
                case AgentEvent.Kind.ToolResult: return "toolResult";
                case AgentEvent.Kind.Retry: return "retry";
                case AgentEvent.Kind.Text: return "text";
                case AgentEvent.Kind.Failed: return "failed";
                default: return "other";
            }
        }

        /// <summary>
        /// Only the first line of a tool result belongs in the activity list.
        /// The full text - tree, measurements - is what the model reads, and
        /// putting all of it on screen would bury the conversation.
        /// </summary>
        private static string FirstLine(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            int nl = message.IndexOf('\n');
            string line = nl >= 0 ? message.Substring(0, nl) : message;
            return line.Length > 160 ? line.Substring(0, 160) + "..." : line.TrimEnd();
        }

        /// <summary>
        /// Send a message to the page, marshalling onto the UI thread first.
        /// Never throws: a quiet panel is better than a dead host.
        /// </summary>
        private void PostToPage(object payload)
        {
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke((Action)(() => PostToPage(payload)));
                    return;
                }

                if (_webView?.CoreWebView2 == null) return;

                _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
            }
            catch (Exception ex)
            {
                _log.Debug($"Could not post to the panel: {ex.Message}");
            }
        }

        private void ShowFallback(string message)
        {
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke((Action)(() => ShowFallback(message)));
                    return;
                }

                _fallback.Text = message;
                _fallback.Visible = true;
                _webView.Visible = false;
            }
            catch (Exception ex)
            {
                _log.Debug($"Could not show fallback message: {ex.Message}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _running?.Cancel(); } catch { }
                try { _batchStop?.Cancel(); } catch { }
                if (_session != null) _session.Batches.PlanCreated -= OnBatchPlanCreated;
                try { _webView?.Dispose(); }
                catch (Exception ex) { _log.Debug($"Disposing WebView2: {ex.Message}"); }
            }

            base.Dispose(disposing);
        }
    }
}
