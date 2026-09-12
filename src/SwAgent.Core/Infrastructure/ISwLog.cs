using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SwAgent.Core.Infrastructure
{
    /// <summary>
    /// Diagnostics sink. Deliberately small, and deliberately redacting.
    ///
    /// The user's API key must appear in no log, no crash dump and no outbound
    /// request other than to api.anthropic.com, and that is enforced by a test
    /// rather than by inspection. Everything written through this interface is
    /// scrubbed on the way out, so a careless log statement elsewhere in the
    /// codebase cannot leak a key.
    /// </summary>
    public interface ISwLog
    {
        void Debug(string message);
        void Info(string message);
        void Error(string message);
    }

    /// <summary>Redaction shared by every log implementation.</summary>
    public static class LogRedaction
    {
        // Anthropic keys look like sk-ant-... . Match generously: it is better
        // to redact something harmless than to print half a key.
        private static readonly Regex ApiKeyPattern = new Regex(
            @"sk-ant-[A-Za-z0-9\-_]{8,}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Replace anything that looks like a key with a safe marker.</summary>
        public static string Scrub(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            return ApiKeyPattern.Replace(message, "sk-ant-[REDACTED]");
        }
    }

    /// <summary>Discards everything. The default, so logging is never required.</summary>
    public sealed class NullSwLog : ISwLog
    {
        public static readonly NullSwLog Instance = new NullSwLog();
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Error(string message) { }
    }

    /// <summary>
    /// Appends to a rolling file under the user's local app data. Never throws:
    /// a logging failure must not be able to fail an operation.
    /// </summary>
    public sealed class FileSwLog : ISwLog
    {
        private readonly string _path;
        private readonly object _gate = new object();
        private readonly bool _verbose;

        public FileSwLog(string path, bool verbose = false)
        {
            _path = path;
            _verbose = verbose;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
            }
            catch
            {
                // If we cannot create the directory we simply do not log.
            }
        }

        /// <summary>The default location: %LOCALAPPDATA%\SwAgent\logs\swagent.log</summary>
        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SwAgent", "logs", "swagent.log");

        public void Debug(string message) { if (_verbose) Write("DBG", message); }
        public void Info(string message) => Write("INF", message);
        public void Error(string message) => Write("ERR", message);

        private void Write(string level, string message)
        {
            try
            {
                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [").Append(level).Append("] ")
                    .Append(LogRedaction.Scrub(message))
                    .AppendLine()
                    .ToString();

                lock (_gate)
                {
                    File.AppendAllText(_path, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Swallow. A log write must never be able to fail a CAD operation.
            }
        }
    }
}
