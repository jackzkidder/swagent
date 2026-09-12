using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using SwAgent.Core.Infrastructure;

namespace SwAgent.AddIn
{
    /// <summary>
    /// Resolves our dependencies from our own directory, by simple name.
    ///
    /// This exists because a COM add-in has no application configuration file.
    /// We are a class library loaded into SLDWORKS.exe, so the CLR reads
    /// SLDWORKS.exe.config for binding redirects - a file inside Dassault's
    /// install directory that we must not touch and cannot rely on.
    ///
    /// That matters because the Anthropic SDK brings a dozen BCL support
    /// assemblies with it (System.Text.Json, System.Memory,
    /// System.Runtime.CompilerServices.Unsafe and friends). On .NET Framework
    /// those normally need binding redirects to unify versions, and without
    /// them the failure is a FileLoadException reading "the located assembly's
    /// manifest definition does not match the assembly reference" - thrown the
    /// first time the panel tries to talk to the API, long after load, with
    /// nothing in the message to suggest it is our fault.
    ///
    /// Resolving by simple name from our own folder sidesteps the whole
    /// problem: whatever version we shipped is the version we load. It also
    /// covers the case where SOLIDWORKS or another add-in has already loaded a
    /// different version of the same assembly, by preferring what is already in
    /// the process over loading a second copy.
    /// </summary>
    internal static class AssemblyRedirector
    {
        private static readonly object Gate = new object();
        private static bool _installed;
        private static string _baseDirectory;
        private static ISwLog _log;

        /// <summary>
        /// Install the resolver. Must be called before anything touches a type
        /// from a dependent assembly, which in practice means first thing in
        /// ConnectToSW. Safe to call more than once.
        /// </summary>
        public static void Install(ISwLog log)
        {
            lock (Gate)
            {
                if (_installed) return;

                _log = log ?? NullSwLog.Instance;

                try
                {
                    _baseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                }
                catch
                {
                    _baseDirectory = null;
                }

                if (string.IsNullOrEmpty(_baseDirectory))
                {
                    _log.Error("Could not determine the add-in directory; dependency resolution may fail.");
                    return;
                }

                AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                _installed = true;

                _log.Info($"Assembly resolver installed for {_baseDirectory}");
            }
        }

        /// <summary>Remove the resolver on unload, so we leave nothing behind in the host.</summary>
        public static void Uninstall()
        {
            lock (Gate)
            {
                if (!_installed) return;

                try { AppDomain.CurrentDomain.AssemblyResolve -= Resolve; }
                catch { }

                _installed = false;
            }
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string simpleName = new AssemblyName(args.Name).Name;

                // Never interfere with SOLIDWORKS' own assemblies. If the host
                // cannot find one of those, that is the host's business and
                // substituting ours would be worse than the failure.
                if (simpleName.StartsWith("SolidWorks.", StringComparison.OrdinalIgnoreCase))
                    return null;

                // Prefer an assembly already in the process. Loading a second
                // copy of the same simple name gives two incompatible type
                // identities, which fails later and much more confusingly.
                foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                        return loaded;
                }

                string candidate = Path.Combine(_baseDirectory, simpleName + ".dll");
                if (!File.Exists(candidate)) return null;

                Assembly resolved = Assembly.LoadFrom(candidate);
                _log?.Debug($"Resolved {simpleName} from {candidate}");
                return resolved;
            }
            catch (Exception ex)
            {
                // A resolver that throws takes down whatever triggered the
                // load, which could be anything in the host.
                _log?.Error($"Assembly resolution failed for '{args?.Name}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Force-load every dependency we ship, and report any that fail.
        ///
        /// Called once at connect so a missing or unloadable dependency shows
        /// up in the log immediately, with a name, instead of surfacing as an
        /// inexplicable failure the first time the user sends a message.
        /// </summary>
        public static IReadOnlyList<string> VerifyDependencies(params string[] simpleNames)
        {
            var failures = new List<string>();

            foreach (string name in simpleNames)
            {
                try
                {
                    Assembly.Load(name);
                }
                catch (Exception ex)
                {
                    failures.Add($"{name}: {ex.GetType().Name}");
                    _log?.Error($"Dependency '{name}' could not be loaded: {ex.Message}");
                }
            }

            return failures;
        }
    }
}
