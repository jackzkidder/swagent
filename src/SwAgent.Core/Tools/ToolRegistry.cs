using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Session;

namespace SwAgent.Core.Tools
{
    /// <summary>
    /// The set of capabilities the agent has, and the only route to them.
    ///
    /// The registry is the security boundary as much as it is a lookup table:
    /// if a capability is not registered here it does not exist, and there is no
    /// generic escape hatch that could add one at runtime.
    /// </summary>
    public sealed class ToolRegistry
    {
        private readonly Dictionary<string, SwTool> _tools = new Dictionary<string, SwTool>(StringComparer.Ordinal);

        public void Register(SwTool tool)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));

            if (_tools.ContainsKey(tool.Name))
                throw new InvalidOperationException($"A tool named '{tool.Name}' is already registered.");

            ValidateToolName(tool.Name);
            _tools[tool.Name] = tool;
        }

        public void RegisterAll(IEnumerable<SwTool> tools)
        {
            foreach (var t in tools) Register(t);
        }

        public int Count => _tools.Count;

        public IReadOnlyCollection<SwTool> Tools => _tools.Values.ToList();

        public bool TryGet(string name, out SwTool tool) => _tools.TryGetValue(name, out tool);

        /// <summary>
        /// Run a tool by name. An unknown name is a normal failure, not an
        /// exception: models occasionally invent tools, and the right response
        /// is to tell it which ones exist.
        /// </summary>
        public ToolResult Execute(string name, JsonElement arguments, SwSession session, ISwLog log = null)
        {
            if (!_tools.TryGetValue(name, out var tool))
            {
                return ToolResult.Failure(
                    $"There is no tool called '{name}'. Available tools: {string.Join(", ", _tools.Keys.OrderBy(k => k))}.",
                    "unknown_tool");
            }

            return tool.Execute(arguments, session, log);
        }

        /// <summary>
        /// The JSON array of tool definitions for the Messages API request.
        /// </summary>
        public string ToJsonDefinitions()
        {
            var buffer = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
            {
                w.WriteStartArray();
                foreach (var tool in _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
                    tool.WriteDefinition(w);
                w.WriteEndArray();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        /// <summary>
        /// Tool names go into an API contract and into logs, so keep them to a
        /// predictable shape rather than discovering a rejected request later.
        /// </summary>
        private static void ValidateToolName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A tool must have a name.");

            foreach (char c in name)
            {
                if (!char.IsLetterOrDigit(c) && c != '_')
                    throw new ArgumentException(
                        $"Tool name '{name}' may contain only letters, digits and underscores.");
            }
        }
    }
}
