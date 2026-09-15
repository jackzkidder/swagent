using System;
using System.IO;
using System.Text;
using System.Text.Json;
using SwAgent.Core.Infrastructure;

namespace SwAgent.Agent
{
    /// <summary>
    /// The handful of choices a user is allowed to make, saved as plain JSON
    /// beside the encrypted key.
    ///
    /// Plain, deliberately: a model name and a turn limit are not secrets, and
    /// encrypting them would only make the file impossible to inspect or fix by
    /// hand. The key is the one thing that gets DPAPI - see ApiKeyStore.
    ///
    /// Every value is validated on the way in and on the way out. A settings
    /// file edited by hand, copied from another machine, or left over from an
    /// older version must never be able to put the agent in a state the UI
    /// cannot show or the API will reject.
    /// </summary>
    public sealed class SwAgentSettings
    {
        /// <summary>Anthropic model id. Defaults to Opus 5.</summary>
        public string Model { get; set; } = ModelIds.Opus5;

        /// <summary>
        /// Ceiling on tool calls per message. This is a spend control: a model
        /// that has misunderstood can otherwise loop against the user's own
        /// credit, which is the anxiety BYOK creates.
        /// </summary>
        public int MaxTurns { get; set; } = 40;

        public const int MinTurns = 5;
        public const int MaxAllowedTurns = 80;

        /// <summary>
        /// Return a copy with every value forced into range. Unknown model ids
        /// are kept rather than rewritten - the user may be pointing at a model
        /// newer than this build - but anything non-finite or absurd is not.
        /// </summary>
        public SwAgentSettings Sanitised()
        {
            string model = string.IsNullOrWhiteSpace(Model) ? ModelIds.Opus5 : Model.Trim();

            int turns = MaxTurns;
            if (turns < MinTurns) turns = MinTurns;
            if (turns > MaxAllowedTurns) turns = MaxAllowedTurns;

            return new SwAgentSettings { Model = model, MaxTurns = turns };
        }
    }

    /// <summary>Loads and saves <see cref="SwAgentSettings"/>. Never throws.</summary>
    public sealed class SettingsStore
    {
        private readonly string _path;
        private readonly ISwLog _log;

        public SettingsStore(string path = null, ISwLog log = null)
        {
            _path = path ?? DefaultPath;
            _log = log ?? NullSwLog.Instance;
        }

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SwAgent", "settings.json");

        /// <summary>
        /// Read the saved settings, or the defaults. A corrupt or unreadable
        /// file is not an error the user should have to deal with mid-session:
        /// it is logged and replaced by defaults, and the next save fixes it.
        /// </summary>
        public SwAgentSettings Load()
        {
            try
            {
                if (!File.Exists(_path)) return new SwAgentSettings();

                string json = File.ReadAllText(_path, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<SwAgentSettings>(json);

                return (loaded ?? new SwAgentSettings()).Sanitised();
            }
            catch (Exception ex)
            {
                _log.Debug($"Settings could not be read ({ex.Message}); using defaults.");
                return new SwAgentSettings();
            }
        }

        /// <summary>Save, and return what was actually written after sanitising.</summary>
        public SwAgentSettings Save(SwAgentSettings settings)
        {
            var clean = (settings ?? new SwAgentSettings()).Sanitised();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));

                string json = JsonSerializer.Serialize(clean, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json, new UTF8Encoding(false));

                _log.Info($"Settings saved: model {clean.Model}, max {clean.MaxTurns} tool calls per message.");
            }
            catch (Exception ex)
            {
                // A settings file we cannot write is not worth failing a run
                // over; the choice simply does not survive a restart.
                _log.Error($"Settings could not be saved: {ex.Message}");
            }

            return clean;
        }
    }
}
