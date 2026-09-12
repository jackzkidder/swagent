using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Session;

namespace SwAgent.Core.Tools
{
    /// <summary>The JSON types a tool parameter may take.</summary>
    public enum ToolParamType
    {
        Number,
        Integer,
        String,
        Boolean,
        Enum
    }

    /// <summary>
    /// One typed, validated tool parameter.
    ///
    /// Everything the agent can do arrives through one of these. There is no
    /// code-execution tool and there never will be: an agent that can run
    /// arbitrary code inside SOLIDWORKS can also reach the filesystem and the
    /// network, and we would not be able to tell a customer what it did.
    /// Bounded, named parameters are what make that promise checkable.
    /// </summary>
    public sealed class ToolParameter
    {
        public string Name { get; set; }
        public ToolParamType Type { get; set; }
        public string Description { get; set; }
        public bool Required { get; set; } = true;

        /// <summary>Inclusive bounds, for numeric parameters.</summary>
        public double? Minimum { get; set; }
        public double? Maximum { get; set; }

        /// <summary>Allowed values, for Enum parameters.</summary>
        public string[] EnumValues { get; set; }

        /// <summary>Used when the parameter is absent and not required.</summary>
        public object DefaultValue { get; set; }

        public static ToolParameter Number(string name, string description, double? min = null, double? max = null, bool required = true, double? defaultValue = null)
            => new ToolParameter
            {
                Name = name, Type = ToolParamType.Number, Description = description,
                Minimum = min, Maximum = max, Required = required,
                DefaultValue = defaultValue
            };

        public static ToolParameter Integer(string name, string description, double? min = null, double? max = null, bool required = true, int? defaultValue = null)
            => new ToolParameter
            {
                Name = name, Type = ToolParamType.Integer, Description = description,
                Minimum = min, Maximum = max, Required = required,
                DefaultValue = defaultValue
            };

        public static ToolParameter Text(string name, string description, bool required = true, string defaultValue = null)
            => new ToolParameter
            {
                Name = name, Type = ToolParamType.String, Description = description,
                Required = required, DefaultValue = defaultValue
            };

        public static ToolParameter Flag(string name, string description, bool required = false, bool defaultValue = false)
            => new ToolParameter
            {
                Name = name, Type = ToolParamType.Boolean, Description = description,
                Required = required, DefaultValue = defaultValue
            };

        public static ToolParameter Choice(string name, string description, string[] values, bool required = true, string defaultValue = null)
            => new ToolParameter
            {
                Name = name, Type = ToolParamType.Enum, Description = description,
                EnumValues = values, Required = required, DefaultValue = defaultValue
            };

        /// <summary>Emit this parameter as a JSON Schema property.</summary>
        internal void WriteSchema(Utf8JsonWriter w)
        {
            w.WriteStartObject(Name);

            switch (Type)
            {
                case ToolParamType.Number: w.WriteString("type", "number"); break;
                case ToolParamType.Integer: w.WriteString("type", "integer"); break;
                case ToolParamType.Boolean: w.WriteString("type", "boolean"); break;
                case ToolParamType.String: w.WriteString("type", "string"); break;
                case ToolParamType.Enum:
                    w.WriteString("type", "string");
                    w.WriteStartArray("enum");
                    foreach (var v in EnumValues ?? Array.Empty<string>()) w.WriteStringValue(v);
                    w.WriteEndArray();
                    break;
            }

            if (!string.IsNullOrEmpty(Description))
                w.WriteString("description", Description);

            // Publish the bounds in the schema as well as enforcing them. The
            // model can then avoid the error rather than discover it.
            if (Minimum.HasValue) w.WriteNumber("minimum", Minimum.Value);
            if (Maximum.HasValue) w.WriteNumber("maximum", Maximum.Value);

            w.WriteEndObject();
        }
    }

    /// <summary>
    /// Validated access to a tool call's arguments.
    ///
    /// Every accessor either returns a value of the right type and within
    /// range, or throws an ArgumentException whose message tells the model
    /// exactly what was wrong. A tool body never sees a malformed argument.
    /// </summary>
    public sealed class ToolArgs
    {
        private readonly JsonElement _root;
        private readonly IReadOnlyDictionary<string, ToolParameter> _schema;
        private readonly string _toolName;

        public ToolArgs(string toolName, JsonElement root, IReadOnlyList<ToolParameter> parameters)
        {
            _toolName = toolName;
            _root = root;

            var map = new Dictionary<string, ToolParameter>(StringComparer.Ordinal);
            foreach (var p in parameters ?? Array.Empty<ToolParameter>()) map[p.Name] = p;
            _schema = map;
        }

        /// <summary>
        /// Check every declared parameter before the tool body runs, so a
        /// missing required argument fails cleanly rather than halfway through
        /// a modelling operation with a sketch left open.
        /// </summary>
        public void ValidateAll()
        {
            foreach (var kv in _schema)
            {
                var p = kv.Value;
                bool present = TryGet(p.Name, out _);

                if (!present && p.Required)
                    throw new ArgumentException($"required argument '{p.Name}' is missing.");

                if (present)
                {
                    switch (p.Type)
                    {
                        case ToolParamType.Number:
                        case ToolParamType.Integer:
                            GetDouble(p.Name);
                            break;
                        case ToolParamType.Boolean:
                            GetBool(p.Name);
                            break;
                        case ToolParamType.Enum:
                            GetString(p.Name);
                            break;
                    }
                }
            }
        }

        private bool TryGet(string name, out JsonElement value)
        {
            value = default(JsonElement);
            if (_root.ValueKind != JsonValueKind.Object) return false;
            if (!_root.TryGetProperty(name, out value)) return false;
            return value.ValueKind != JsonValueKind.Null;
        }

        private ToolParameter Schema(string name)
        {
            if (_schema.TryGetValue(name, out var p)) return p;
            throw new InvalidOperationException(
                $"{_toolName} asked for undeclared argument '{name}'. This is a bug in the add-in.");
        }

        public double GetDouble(string name)
        {
            var p = Schema(name);

            if (!TryGet(name, out var el))
            {
                if (p.Required) throw new ArgumentException($"required argument '{name}' is missing.");
                return p.DefaultValue is double d ? d : Convert.ToDouble(p.DefaultValue ?? 0.0, CultureInfo.InvariantCulture);
            }

            double value;
            if (el.ValueKind == JsonValueKind.Number)
            {
                value = el.GetDouble();
            }
            else if (el.ValueKind == JsonValueKind.String
                     && double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                // Models sometimes quote numbers. Accepting that is kinder than
                // an error, as long as it still passes the range checks.
                value = parsed;
            }
            else
            {
                throw new ArgumentException(
                    $"'{name}' must be a number, got {DescribeKind(el)}.");
            }

            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException($"'{name}' must be a finite number.");

            if (p.Minimum.HasValue && value < p.Minimum.Value)
                throw new ArgumentException(
                    $"'{name}' is {Fmt(value)}, below the minimum of {Fmt(p.Minimum.Value)}.");

            if (p.Maximum.HasValue && value > p.Maximum.Value)
                throw new ArgumentException(
                    $"'{name}' is {Fmt(value)}, above the maximum of {Fmt(p.Maximum.Value)}.");

            return value;
        }

        public int GetInt(string name)
        {
            double d = GetDouble(name);
            if (Math.Abs(d - Math.Round(d)) > 1e-9)
                throw new ArgumentException($"'{name}' must be a whole number, got {Fmt(d)}.");
            return (int)Math.Round(d);
        }

        public bool GetBool(string name)
        {
            var p = Schema(name);

            if (!TryGet(name, out var el))
            {
                if (p.Required) throw new ArgumentException($"required argument '{name}' is missing.");
                return p.DefaultValue is bool b && b;
            }

            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;

            if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool parsed))
                return parsed;

            throw new ArgumentException($"'{name}' must be true or false, got {DescribeKind(el)}.");
        }

        public string GetString(string name)
        {
            var p = Schema(name);

            if (!TryGet(name, out var el))
            {
                if (p.Required) throw new ArgumentException($"required argument '{name}' is missing.");
                return p.DefaultValue as string;
            }

            if (el.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"'{name}' must be a string, got {DescribeKind(el)}.");

            string value = el.GetString();

            if (p.Type == ToolParamType.Enum && p.EnumValues != null)
            {
                foreach (var allowed in p.EnumValues)
                {
                    if (string.Equals(allowed, value, StringComparison.OrdinalIgnoreCase))
                        return allowed; // normalise to the declared casing
                }

                throw new ArgumentException(
                    $"'{name}' must be one of [{string.Join(", ", p.EnumValues)}], got '{value}'.");
            }

            return value;
        }

        private static string DescribeKind(JsonElement el) => el.ValueKind.ToString().ToLowerInvariant();
        private static string Fmt(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Base class for every capability the agent has.
    ///
    /// Execute is sealed and runs the body inside <see cref="SwGuard"/>, so a
    /// tool cannot skip the exception boundary by forgetting to write one.
    /// Argument validation happens before the body, for the same reason.
    /// </summary>
    public abstract class SwTool
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract IReadOnlyList<ToolParameter> Parameters { get; }

        /// <summary>
        /// True when this tool changes the model, and its result should
        /// therefore carry a fresh screenshot and measurements.
        /// </summary>
        public virtual bool MutatesModel => false;

        protected abstract ToolResult Run(ToolArgs args, SwSession session);

        /// <summary>
        /// Validate and run. Never throws: every failure becomes a ToolResult
        /// the agent can read and react to.
        /// </summary>
        public ToolResult Execute(JsonElement arguments, SwSession session, ISwLog log = null)
        {
            return SwGuard.Run(Name, () =>
            {
                var args = new ToolArgs(Name, arguments, Parameters);
                args.ValidateAll();

                // Selection is global mutable state, and a stale selection from
                // a previous call is why the fillet landed on the wrong edge.
                session.ClearSelection();

                return Run(args, session);
            }, log);
        }

        /// <summary>Emit this tool's definition for the Messages API.</summary>
        internal void WriteDefinition(Utf8JsonWriter w)
        {
            w.WriteStartObject();
            w.WriteString("name", Name);
            w.WriteString("description", Description);

            w.WriteStartObject("input_schema");
            w.WriteString("type", "object");

            w.WriteStartObject("properties");
            foreach (var p in Parameters) p.WriteSchema(w);
            w.WriteEndObject();

            w.WriteStartArray("required");
            foreach (var p in Parameters)
                if (p.Required) w.WriteStringValue(p.Name);
            w.WriteEndArray();

            w.WriteEndObject();
            w.WriteEndObject();
        }
    }
}
