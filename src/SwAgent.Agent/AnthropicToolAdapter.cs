using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Anthropic.Models.Messages;
using SwAgent.Core.Tools;

namespace SwAgent.Agent
{
    /// <summary>
    /// Translates our tool definitions into the SDK's.
    ///
    /// This adapter is why SwAgent.Core has no dependency on the Anthropic SDK:
    /// the COM layer describes its capabilities in its own terms, and only this
    /// project knows they will be sent to a model. That keeps the headless
    /// harness able to drive every tool without a transport present.
    /// </summary>
    public static class AnthropicToolAdapter
    {
        public static List<ToolUnion> ToSdkTools(ToolRegistry registry)
        {
            return registry.Tools
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .Select(ToSdkTool)
                .Select(t => (ToolUnion)t)
                .ToList();
        }

        /// <summary>Convert one tool. Public so the harness can assert on the result.</summary>
        public static Tool ToSdkTool(SwTool tool)
        {
            var properties = new Dictionary<string, JsonElement>();

            foreach (var p in tool.Parameters)
                properties[p.Name] = JsonSerializer.SerializeToElement(BuildPropertySchema(p));

            return new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = new()
                {
                    Properties = properties,
                    Required = tool.Parameters.Where(p => p.Required).Select(p => p.Name).ToList(),
                },
            };
        }

        /// <summary>
        /// Build the JSON Schema fragment for one parameter.
        ///
        /// Bounds are published as well as enforced. Telling the model the
        /// range up front means it avoids the error instead of discovering it,
        /// which is one fewer wasted round trip per mistake.
        /// </summary>
        private static object BuildPropertySchema(ToolParameter p)
        {
            string jsonType;
            switch (p.Type)
            {
                case ToolParamType.Number: jsonType = "number"; break;
                case ToolParamType.Integer: jsonType = "integer"; break;
                case ToolParamType.Boolean: jsonType = "boolean"; break;
                default: jsonType = "string"; break;
            }

            if (p.Type == ToolParamType.Enum)
            {
                return new
                {
                    type = jsonType,
                    description = p.Description,
                    @enum = p.EnumValues ?? Array.Empty<string>(),
                };
            }

            if (p.Minimum.HasValue && p.Maximum.HasValue)
                return new { type = jsonType, description = p.Description, minimum = p.Minimum.Value, maximum = p.Maximum.Value };

            if (p.Minimum.HasValue)
                return new { type = jsonType, description = p.Description, minimum = p.Minimum.Value };

            if (p.Maximum.HasValue)
                return new { type = jsonType, description = p.Description, maximum = p.Maximum.Value };

            return new { type = jsonType, description = p.Description };
        }
    }
}
