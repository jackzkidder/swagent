using System;
using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;
using SwAgent.Core.Session;

namespace SwAgent.Core.Inspection
{
    /// <summary>
    /// Reads the feature tree as text.
    ///
    /// This is the part of the round-trip that catches a wrong *history* which
    /// happens to look right. Two parts can have identical mass, identical
    /// bounding box and an identical screenshot while one of them is built from
    /// a sensible sequence of features and the other is a mess that will fall
    /// apart the moment someone edits a dimension. We are shipping a parametric
    /// model that a person will open and change, so the history is part of the
    /// deliverable, not an implementation detail.
    /// </summary>
    public static class FeatureTree
    {
        /// <summary>
        /// Features that exist in every part template and carry no information
        /// about what was built. Hiding them keeps the listing short enough to
        /// send after every operation.
        /// </summary>
        private static readonly HashSet<string> BoilerplateTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "OriginProfileFeature",  // the origin
            "CoordSys",
            "HistoryFolder",
            "SensorFolder",
            "DetailCabinet",
            "MaterialFolder",
            "SolidBodyFolder",
            "SurfaceBodyFolder",
            "EnvFolder",
            "FavoriteFolder",
            "DocsFolder",
            "CommentsFolder",
            "SelectionSetFolder"
        };

        /// <summary>Walk the tree of the active document.</summary>
        public static IReadOnlyList<FeatureNode> Read(SwSession session, bool includeBoilerplate = false)
        {
            var doc = session.RequireModel();
            var nodes = new List<FeatureNode>();

            var feature = (IFeature)doc.FirstFeature();
            while (feature != null)
            {
                string typeName = SafeTypeName(feature);

                bool isPlane = string.Equals(typeName, "RefPlane", StringComparison.Ordinal);
                bool isBoilerplate = BoilerplateTypes.Contains(typeName) || isPlane;

                if (includeBoilerplate || !isBoilerplate)
                    nodes.Add(Describe(feature, typeName));

                feature = (IFeature)feature.GetNextFeature();
            }

            return nodes;
        }

        private static FeatureNode Describe(IFeature feature, string typeName)
        {
            string name = "(unnamed)";
            bool suppressed = false;
            int errorCode = 0;
            bool isWarning = false;

            try { name = feature.Name; } catch { }

            try
            {
                // IsSuppressed2 wants a configuration option; 1 means the
                // currently active configuration, which is what the user sees.
                var flags = feature.IsSuppressed2(1, null) as bool[];
                suppressed = flags != null && flags.Length > 0 && flags[0];
            }
            catch { }

            try { errorCode = feature.GetErrorCode2(out isWarning); } catch { }

            return new FeatureNode(name, typeName, suppressed, errorCode, isWarning);
        }

        private static string SafeTypeName(IFeature feature)
        {
            try { return feature.GetTypeName2() ?? "(unknown)"; }
            catch { return "(unknown)"; }
        }

        /// <summary>
        /// Render the tree for a tool result. Terse by design: this goes back to
        /// the model after every feature-creating operation, and a verbose
        /// format multiplied by twenty features is a context window.
        /// </summary>
        public static string Render(IReadOnlyList<FeatureNode> nodes)
        {
            if (nodes == null || nodes.Count == 0)
                return "Feature tree is empty (no features yet).";

            var sb = new StringBuilder();
            sb.Append("Feature tree (").Append(nodes.Count).AppendLine(" features):");

            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                sb.Append("  ").Append(i + 1).Append(". ").Append(n.Name);
                sb.Append(" [").Append(n.TypeName).Append(']');

                if (n.Suppressed) sb.Append(" SUPPRESSED");
                if (n.HasProblem) sb.Append(n.IsWarning ? " WARNING" : " ERROR").Append('(').Append(n.ErrorCode).Append(')');

                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>One entry in the feature tree.</summary>
    public sealed class FeatureNode
    {
        public FeatureNode(string name, string typeName, bool suppressed, int errorCode, bool isWarning)
        {
            Name = name;
            TypeName = typeName;
            Suppressed = suppressed;
            ErrorCode = errorCode;
            IsWarning = isWarning;
        }

        public string Name { get; }
        public string TypeName { get; }
        public bool Suppressed { get; }
        public int ErrorCode { get; }
        public bool IsWarning { get; }

        /// <summary>
        /// SOLIDWORKS uses 0 for "no problem". Anything else is a rebuild error
        /// or a warning on this feature.
        /// </summary>
        public bool HasProblem => ErrorCode != 0;

        public override string ToString() => $"{Name} [{TypeName}]";
    }
}
