using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SwAgent.Core.Infrastructure;

namespace SwAgent.Agent
{
    /// <summary>
    /// Stores the user's Anthropic API key encrypted on their own machine.
    ///
    /// The promise to the user is specific and must stay true: the key is
    /// encrypted with their Windows account, never leaves the machine except to
    /// api.anthropic.com, and cannot be read back out of the UI once saved.
    ///
    /// DPAPI with CurrentUser scope means the ciphertext is bound to the
    /// Windows account. Another user on the same machine cannot decrypt it, and
    /// copying the file to another machine yields nothing. That is the right
    /// bar for a credential that bills against the user's own card.
    ///
    /// This type is deliberately write-only from the UI's point of view:
    /// <see cref="TryLoad"/> exists for the transport, and
    /// <see cref="GetMaskedKey"/> is what any display path gets.
    /// </summary>
    public sealed class ApiKeyStore
    {
        /// <summary>
        /// Extra entropy mixed into the DPAPI blob. Not a secret - it scopes
        /// the ciphertext to this application, so another program running as
        /// the same user cannot decrypt our blob simply by pointing DPAPI at
        /// the file.
        /// </summary>
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("SwAgent.ApiKey.v1");

        private readonly string _path;
        private readonly ISwLog _log;

        public ApiKeyStore(string path = null, ISwLog log = null)
        {
            _path = path ?? DefaultPath;
            _log = log ?? NullSwLog.Instance;
        }

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SwAgent", "apikey.dat");

        /// <summary>True when a key has been saved.</summary>
        public bool HasKey => File.Exists(_path);

        /// <summary>
        /// Encrypt and save. Overwrites any existing key.
        /// </summary>
        public void Save(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("The API key is empty.", nameof(apiKey));

            apiKey = apiKey.Trim();

            byte[] plaintext = Encoding.UTF8.GetBytes(apiKey);
            try
            {
                byte[] ciphertext = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllBytes(_path, ciphertext);

                // Never log the key itself. The log scrubs sk-ant- patterns on
                // the way out, but the correct habit is not to hand it one.
                _log.Info($"API key saved ({Mask(apiKey)}).");
            }
            finally
            {
                // Do not leave the plaintext sitting in a heap buffer any
                // longer than necessary.
                Array.Clear(plaintext, 0, plaintext.Length);
            }
        }

        /// <summary>
        /// Decrypt and return the key, or null if none is stored or it cannot
        /// be decrypted.
        ///
        /// Failure here is expected and benign: the key was saved under a
        /// different Windows account, or the file was copied from elsewhere.
        /// The caller should route the user back to the setup flow.
        /// </summary>
        public bool TryLoad(out string apiKey)
        {
            apiKey = null;

            if (!File.Exists(_path)) return false;

            try
            {
                byte[] ciphertext = File.ReadAllBytes(_path);
                byte[] plaintext = ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
                apiKey = Encoding.UTF8.GetString(plaintext);
                Array.Clear(plaintext, 0, plaintext.Length);
                return !string.IsNullOrWhiteSpace(apiKey);
            }
            catch (CryptographicException ex)
            {
                _log.Error("The stored API key could not be decrypted. It was most likely saved " +
                           $"under a different Windows account. ({ex.GetType().Name})");
                return false;
            }
            catch (Exception ex)
            {
                _log.Error($"Could not read the stored API key: {ex.GetType().Name}.");
                return false;
            }
        }

        /// <summary>Delete the stored key.</summary>
        public void Clear()
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
                _log.Info("API key removed.");
            }
            catch (Exception ex)
            {
                _log.Error($"Could not remove the stored API key: {ex.Message}");
            }
        }

        /// <summary>
        /// What the UI is allowed to show: the prefix and last four characters.
        /// Returns null when no key is stored.
        /// </summary>
        public string GetMaskedKey()
        {
            return TryLoad(out string key) ? Mask(key) : null;
        }

        /// <summary>
        /// Mask a key for display. Shows enough for the user to recognise which
        /// key it is, and not enough to use it.
        /// </summary>
        public static string Mask(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey)) return null;

            const string prefix = "sk-ant-";
            string tail = apiKey.Length >= 4 ? apiKey.Substring(apiKey.Length - 4) : "????";

            return apiKey.StartsWith(prefix, StringComparison.Ordinal)
                ? prefix + "..." + tail
                : "..." + tail;
        }

        /// <summary>
        /// A cheap shape check before spending a network round trip. Deliberately
        /// loose: key formats change, and rejecting a valid key is worse than
        /// letting the API reject an invalid one.
        /// </summary>
        public static bool LooksWellFormed(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return false;
            apiKey = apiKey.Trim();
            return apiKey.StartsWith("sk-ant-", StringComparison.Ordinal) && apiKey.Length >= 20;
        }
    }
}
