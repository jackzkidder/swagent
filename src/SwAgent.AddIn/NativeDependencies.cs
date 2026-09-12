using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using SwAgent.Core.Infrastructure;

namespace SwAgent.AddIn
{
    /// <summary>
    /// Loads the native WebView2 loader from our own directory before anything
    /// touches WebView2.
    ///
    /// This is not optional, and the failure it prevents is invisible.
    ///
    /// WebView2's managed assemblies P/Invoke into WebView2Loader.dll. Windows
    /// resolves a P/Invoke by searching, among other places, the directory of
    /// the running EXECUTABLE - which here is SLDWORKS.exe, in the SOLIDWORKS
    /// install folder. It does not search the directory of the DLL that made
    /// the call, which is where our copy actually lives. So the P/Invoke fails,
    /// WebView2 fails to initialise, and the task pane renders as a blank white
    /// rectangle with no error anywhere.
    ///
    /// Pre-loading the library from an absolute path puts it in the process's
    /// module table under the name the P/Invoke asks for, so the later
    /// resolution finds it already loaded and never searches at all.
    /// </summary>
    internal static class NativeDependencies
    {
        private const string LoaderName = "WebView2Loader.dll";

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        private static bool _attempted;
        private static bool _loaded;

        /// <summary>
        /// Ensure WebView2Loader.dll is loaded. Returns false if it could not
        /// be found, so the caller can show a real message instead of a blank
        /// panel. Safe to call more than once.
        /// </summary>
        public static bool EnsureWebView2Loader(ISwLog log)
        {
            if (_attempted) return _loaded;
            _attempted = true;

            try
            {
                string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(baseDir))
                {
                    log?.Error("Could not determine the add-in directory; WebView2 will likely fail.");
                    return false;
                }

                foreach (string candidate in CandidatePaths(baseDir))
                {
                    if (!File.Exists(candidate)) continue;

                    IntPtr handle = LoadLibraryW(candidate);
                    if (handle != IntPtr.Zero)
                    {
                        log?.Info($"Loaded {LoaderName} from {candidate}");
                        _loaded = true;
                        return true;
                    }

                    int err = Marshal.GetLastWin32Error();
                    log?.Error($"LoadLibrary failed for {candidate} (Win32 error {err}).");
                }

                log?.Error($"{LoaderName} was not found next to the add-in. " +
                           "The build or installer did not deploy it.");
                return false;
            }
            catch (Exception ex)
            {
                log?.Error($"Loading {LoaderName} failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Where the loader might be. The build copies it beside the assembly;
        /// the NuGet layout leaves it under runtimes\win-x64\native, which we
        /// also check so a developer running straight from a build output that
        /// predates the copy target still works.
        /// </summary>
        private static string[] CandidatePaths(string baseDir)
        {
            return new[]
            {
                Path.Combine(baseDir, LoaderName),
                Path.Combine(baseDir, "runtimes", "win-x64", "native", LoaderName)
            };
        }
    }
}
