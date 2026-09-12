using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Session;

namespace SwAgent.AddIn
{
    /// <summary>
    /// The COM add-in SOLIDWORKS loads.
    ///
    /// Everything here is written on the assumption that this code runs inside
    /// someone's live CAD session with hours of unsaved work in it. Connect and
    /// disconnect are both fully guarded: a failure during load leaves
    /// SOLIDWORKS running without the add-in, which is a bad day for us and an
    /// ordinary one for the user. That is the correct trade.
    /// </summary>
    [ComVisible(true)]
    [Guid(AddInGuid)]
    [ProgId("SwAgent.SwAddIn")]
    [ClassInterface(ClassInterfaceType.None)]
    public class SwAddIn : ISwAddin
    {
        /// <summary>
        /// The add-in's identity. This exact GUID must appear in three places
        /// and match: this attribute, HKLM\SOFTWARE\SolidWorks\Addins, and
        /// HKCU\Software\SolidWorks\AddInsStartup. If they disagree,
        /// SOLIDWORKS either cannot see the add-in or throws on every launch
        /// pointing at a registration that resolves to nothing.
        /// </summary>
        public const string AddInGuid = "7DADCD66-C0C5-4ABB-A17D-5FDCDA0860A2";

        private const string DisplayTitle = "SwAgent";
        private const string DisplayDescription =
            "Describe a part in plain English and get the deliverable: model, drawing, STEP and PDF.";

        private ISldWorks _sw;
        private int _cookie;
        private ISwLog _log;
        private SwSession _session;
        private UiThreadDispatcher _dispatcher;
        private ITaskpaneView _taskPaneView;
        private ChatPanelControl _panel;

        // ---------------------------------------------------------------
        // ISwAddin
        // ---------------------------------------------------------------

        /// <summary>
        /// Called by SOLIDWORKS on load, on the SOLIDWORKS UI thread.
        ///
        /// Returning false means "do not load me", which SOLIDWORKS handles
        /// gracefully. Throwing, by contrast, is an unhandled exception inside
        /// the host. So the entire body is guarded and every failure path
        /// returns false.
        /// </summary>
        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            try
            {
                _log = new FileSwLog(FileSwLog.DefaultPath);
                _log.Info("---- ConnectToSW ----");

                // Store the pointer we are given and use it for the add-in's
                // whole life. Never Dispatch a new one: that can start a second
                // SOLIDWORKS session behind the user's back.
                _sw = ThisSW as ISldWorks;
                if (_sw == null)
                {
                    _log.Error("ConnectToSW was called without a usable ISldWorks pointer.");
                    return false;
                }

                _cookie = Cookie;

                var version = SwVersionGate.Evaluate(SafeRevision(_sw));
                _log.Info($"Host: {version.Message}");

                if (!version.IsSupported)
                {
                    // Refuse in words, not in a COM error from deep inside a
                    // feature call three operations from now.
                    TellUser(version.Message);
                    _log.Error("Refusing to load: unsupported SOLIDWORKS version.");
                    return false;
                }

                // Constructed here on purpose: this is the SOLIDWORKS UI thread,
                // and the dispatcher captures whichever thread builds it.
                _dispatcher = new UiThreadDispatcher(_log);
                _session = new SwSession(_sw, _log);

                if (!CreateTaskPane())
                {
                    _log.Error("Could not create the task pane; unloading.");
                    Teardown();
                    return false;
                }

                _log.Info("Connected.");
                return true;
            }
            catch (Exception ex)
            {
                // Nothing may escape into SOLIDWORKS.
                try { _log?.Error($"ConnectToSW failed: {ex}"); } catch { }
                try { Teardown(); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Called by SOLIDWORKS on unload.
        ///
        /// Task panes and command groups that are not removed leak and can
        /// destabilise the host, and a half-torn-down add-in is a crash on the
        /// next launch. Teardown is therefore unconditional and individually
        /// guarded: one failing step must not skip the rest.
        /// </summary>
        public bool DisconnectFromSW()
        {
            try
            {
                _log?.Info("---- DisconnectFromSW ----");
                Teardown();
                _log?.Info("Disconnected cleanly.");
            }
            catch (Exception ex)
            {
                try { _log?.Error($"DisconnectFromSW: {ex}"); } catch { }
            }

            // Release our references and let the CLR hand the RCWs back before
            // SOLIDWORKS finishes shutting down.
            GC.Collect();
            GC.WaitForPendingFinalizers();

            return true;
        }

        // ---------------------------------------------------------------
        // Task pane
        // ---------------------------------------------------------------

        private bool CreateTaskPane()
        {
            try
            {
                string icon = ResolveIconPath();
                _taskPaneView = _sw.CreateTaskpaneView2(icon, DisplayTitle);

                if (_taskPaneView == null)
                {
                    _log.Error("CreateTaskpaneView2 returned null.");
                    return false;
                }

                _panel = new ChatPanelControl(_log);

                // DisplayWindowFromHandlex64, not DisplayWindowFromHandle: this
                // is an x64 build, and the 32-bit overload takes an Int32 that
                // would truncate the window handle.
                if (!_taskPaneView.DisplayWindowFromHandlex64(_panel.Handle.ToInt64()))
                {
                    _log.Error("DisplayWindowFromHandlex64 refused the panel handle.");
                    return false;
                }

                // Kick off the browser initialisation. It is asynchronous and
                // must not block the SOLIDWORKS thread we are currently on.
                _panel.BeginInitialize();

                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"CreateTaskPane failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// The task pane icon, if we shipped one. An empty string is acceptable
        /// to SOLIDWORKS and simply yields a default icon, which is far better
        /// than failing to load over a missing bitmap.
        /// </summary>
        private string ResolveIconPath()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string candidate = Path.Combine(dir ?? string.Empty, "Assets", "taskpane.bmp");
                return File.Exists(candidate) ? candidate : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void Teardown()
        {
            // Each step guarded separately: a failure in one must not prevent
            // the others, or we leak a task pane into the host.
            try
            {
                if (_taskPaneView != null)
                {
                    _taskPaneView.DeleteView();
                    Marshal.ReleaseComObject(_taskPaneView);
                }
            }
            catch (Exception ex) { _log?.Debug($"Teardown taskpane: {ex.Message}"); }
            finally { _taskPaneView = null; }

            try { _panel?.Dispose(); }
            catch (Exception ex) { _log?.Debug($"Teardown panel: {ex.Message}"); }
            finally { _panel = null; }

            try { _dispatcher?.Dispose(); }
            catch (Exception ex) { _log?.Debug($"Teardown dispatcher: {ex.Message}"); }
            finally { _dispatcher = null; }

            _session = null;

            // We do not release _sw: SOLIDWORKS gave us that pointer and owns
            // its lifetime. Releasing a pointer we did not create can take the
            // host down on shutdown.
            _sw = null;
        }

        private static string SafeRevision(ISldWorks sw)
        {
            try { return sw.RevisionNumber(); }
            catch { return null; }
        }

        private void TellUser(string message)
        {
            try
            {
                _sw?.SendMsgToUser2(
                    message,
                    (int)SolidWorks.Interop.swconst.swMessageBoxIcon_e.swMbWarning,
                    (int)SolidWorks.Interop.swconst.swMessageBoxBtn_e.swMbOk);
            }
            catch (Exception ex)
            {
                _log?.Debug($"Could not show a message to the user: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------
        // COM registration
        // ---------------------------------------------------------------
        //
        // regasm /codebase makes this assembly a COM server. That alone is not
        // enough: SOLIDWORKS only looks at add-ins listed under its own Addins
        // key, so registration is two separate things and both are required.
        //
        // These run as part of regasm, which the installer invokes elevated.
        // The user should never be asked to do this at a command prompt.

        private const string AddinsKeyPath = @"SOFTWARE\SolidWorks\Addins\{" + AddInGuid + "}";
        private const string StartupKeyPath = @"Software\SolidWorks\AddInsStartup\{" + AddInGuid + "}";

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            try
            {
                // Explicit 64-bit view: this is an x64-only add-in and must not
                // land in the WOW6432Node redirect, where SOLIDWORKS will not
                // look for it.
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = hklm.CreateSubKey(AddinsKeyPath))
                {
                    // The default value is a DWORD: 1 means "load this add-in
                    // by default for new users".
                    key.SetValue(null, 1, RegistryValueKind.DWord);
                    key.SetValue("Title", DisplayTitle, RegistryValueKind.String);
                    key.SetValue("Description", DisplayDescription, RegistryValueKind.String);
                }

                using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                using (var key = hkcu.CreateSubKey(StartupKeyPath))
                {
                    // Per-user: load at startup for this user.
                    key.SetValue(null, 1, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                // Surface it: a silent registration failure produces an add-in
                // that installs "successfully" and never appears.
                throw new InvalidOperationException(
                    "SwAgent COM registration failed. The installer must run elevated. " + ex.Message, ex);
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            // Uninstall must fully unregister. A stale add-in entry pointing at
            // a deleted DLL throws an error on every SOLIDWORKS launch, forever,
            // and generates support tickets from people who no longer use the
            // product. Each removal is guarded so one failure cannot orphan the
            // other key.
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    hklm.DeleteSubKeyTree(AddinsKeyPath, throwOnMissingSubKey: false);
            }
            catch { }

            try
            {
                using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                    hkcu.DeleteSubKeyTree(StartupKeyPath, throwOnMissingSubKey: false);
            }
            catch { }
        }
    }
}
