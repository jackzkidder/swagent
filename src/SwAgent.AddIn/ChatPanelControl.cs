using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SwAgent.Core.Infrastructure;

namespace SwAgent.AddIn
{
    /// <summary>
    /// The task pane panel: a WebView2 browser hosting the chat UI, with a
    /// plain-text fallback for when the browser cannot start.
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
        private readonly WebView2 _webView;
        private readonly Label _fallback;
        private bool _initialisationStarted;

        public ChatPanelControl(ISwLog log)
        {
            _log = log ?? NullSwLog.Instance;

            Dock = DockStyle.Fill;
            BackColor = Color.White;

            _fallback = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(16),
                Text = "Starting SwAgent…"
            };

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Visible = false
            };

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

                _webView.NavigateToString(PlaceholderHtml);
                _webView.Visible = true;
                _fallback.Visible = false;

                _log.Info("WebView2 panel ready.");
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

        /// <summary>
        /// Placeholder until the agent loop is wired up. Deliberately states
        /// what does not work yet rather than presenting a chat box that
        /// silently does nothing.
        /// </summary>
        private const string PlaceholderHtml = @"<!doctype html>
<html><head><meta charset='utf-8'>
<style>
  :root { color-scheme: light dark; }
  body { font: 13px/1.5 'Segoe UI', system-ui, sans-serif; margin: 0; padding: 20px;
         background: #fff; color: #1a1a1a; }
  @media (prefers-color-scheme: dark) { body { background: #1f1f1f; color: #e8e8e8; } }
  h1 { font-size: 15px; margin: 0 0 4px; }
  .sub { opacity: .65; margin-bottom: 18px; }
  ul { padding-left: 18px; margin: 0 0 18px; }
  li { margin-bottom: 5px; }
  .note { border-left: 3px solid #888; padding: 8px 12px; opacity: .8; }
</style></head>
<body>
  <h1>SwAgent</h1>
  <div class='sub'>Connected to SOLIDWORKS.</div>
  <p>The COM layer is live and verified. Not wired up yet:</p>
  <ul>
    <li>API key setup</li>
    <li>The chat box and the agent loop</li>
    <li>Drawings, exports and properties</li>
  </ul>
  <div class='note'>Nothing leaves this machine. When the agent is connected,
  your API key will be stored encrypted locally and used only to talk to
  api.anthropic.com.</div>
</body></html>";

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _webView?.Dispose(); }
                catch (Exception ex) { _log.Debug($"Disposing WebView2: {ex.Message}"); }
            }

            base.Dispose(disposing);
        }
    }
}
