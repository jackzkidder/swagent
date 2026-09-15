using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Session;

namespace SwAgent.Core.Inspection
{
    /// <summary>
    /// What the user had selected when they pressed Send.
    ///
    /// This has to be captured BEFORE the agent runs, and that is not a detail.
    /// Every tool begins with ClearSelection, because selection is global
    /// mutable state and a stale one is why the fillet landed on the wrong edge
    /// (rule 4). The first tool call of a run therefore destroys the very thing
    /// "fillet this edge" refers to. So the panel takes this snapshot at the
    /// start of a request and the agent reads the snapshot, never the live
    /// selection.
    ///
    /// Why bother: picking geometry is the failure the model is worst at. It
    /// aims a ray and hopes. A person clicking the face they mean is exact, free
    /// and instant - and it removes a whole class of wrong-face mistakes.
    /// </summary>
    public static class SelectionReader
    {
        /// <summary>How many entities to describe. Beyond this the list stops being readable.</summary>
        private const int MaxEntities = 12;

        public static SelectionSnapshot Capture(SwSession session)
        {
            try
            {
                var doc = session.ActiveDoc;
                if (doc == null) return SelectionSnapshot.Empty;

                var mgr = doc.SelectionManager as ISelectionMgr;
                if (mgr == null) return SelectionSnapshot.Empty;

                int count = mgr.GetSelectedObjectCount2(-1);
                if (count <= 0) return SelectionSnapshot.Empty;

                var items = new List<SelectedEntity>();
                for (int i = 1; i <= count && items.Count < MaxEntities; i++)
                {
                    SelectedEntity item = Read(mgr, i, session.Log);
                    if (item != null) items.Add(item);
                }

                return new SelectionSnapshot(count, items);
            }
            catch (Exception ex)
            {
                session.Log.Debug($"Could not read the user's selection: {ex.Message}");
                return SelectionSnapshot.Empty;
            }
        }

        private static SelectedEntity Read(ISelectionMgr mgr, int index, ISwLog log)
        {
            try
            {
                var type = (swSelectType_e)mgr.GetSelectedObjectType3(index, -1);
                object selected = mgr.GetSelectedObject6(index, -1);
                if (selected == null) return null;

                string description;
                switch (type)
                {
                    case swSelectType_e.swSelFACES: description = DescribeFace(selected as IFace2); break;
                    case swSelectType_e.swSelEDGES: description = DescribeEdge(selected as IEdge); break;
                    case swSelectType_e.swSelVERTICES: description = DescribeVertex(selected as IVertex); break;
                    case swSelectType_e.swSelDATUMPLANES: description = "a reference plane" + FeatureName(selected); break;
                    case swSelectType_e.swSelSKETCHES: description = "a sketch" + FeatureName(selected); break;
                    case swSelectType_e.swSelBODYFEATURES: description = "a feature" + FeatureName(selected); break;
                    default: description = type.ToString().Replace("swSel", "").ToLowerInvariant(); break;
                }

                // The COM object is kept, not just the words. Re-selecting the
                // exact face the user clicked is the whole point; a description
                // would only let the agent guess at it again.
                return new SelectedEntity(type, selected, description);
            }
            catch (Exception ex)
            {
                log.Debug($"Selection entry {index} could not be read: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Put a captured entity back in SOLIDWORKS' selection, so a feature can
        /// consume it. False if the pointer is stale - which happens as soon as
        /// the model changes under it, and must be reported rather than ignored.
        /// </summary>
        public static bool Reselect(SelectedEntity item)
        {
            if (item?.ComObject == null) return false;

            try
            {
                if (item.ComObject is IEntity entity) return entity.Select2(false, 0);
                if (item.ComObject is IFeature feature) return feature.Select2(false, 0);
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static string DescribeFace(IFace2 face)
        {
            if (face == null) return "a face";

            var sb = new StringBuilder("a face");

            try
            {
                // GetArea returns square metres; the tool boundary speaks mm.
                double areaMm2 = face.GetArea() * 1e6;
                sb.Append(", area ").Append(Fmt(areaMm2)).Append(" mm2");
            }
            catch { }

            try
            {
                var normal = face.Normal as double[];
                string direction = AxisWords(normal);
                if (direction != null) sb.Append(", facing ").Append(direction);
            }
            catch { }

            try
            {
                var box = face.GetBox() as double[];
                if (box != null && box.Length >= 6)
                {
                    sb.Append(", centred near (")
                      .Append(Fmt(Units.LengthFromApi((box[0] + box[3]) / 2))).Append(", ")
                      .Append(Fmt(Units.LengthFromApi((box[1] + box[4]) / 2))).Append(", ")
                      .Append(Fmt(Units.LengthFromApi((box[2] + box[5]) / 2))).Append(") mm");
                }
            }
            catch { }

            return sb.ToString();
        }

        private static string DescribeEdge(IEdge edge)
        {
            if (edge == null) return "an edge";

            try
            {
                var start = (edge.GetStartVertex() as IVertex)?.GetPoint() as double[];
                var end = (edge.GetEndVertex() as IVertex)?.GetPoint() as double[];

                // A circular edge has no start or end vertex, which is itself
                // the most useful thing to say about it.
                if (start == null || end == null) return "a closed (circular) edge";

                double length = Math.Sqrt(
                    Math.Pow(start[0] - end[0], 2) + Math.Pow(start[1] - end[1], 2) + Math.Pow(start[2] - end[2], 2));

                return $"an edge from ({Point(start)}) to ({Point(end)}) mm, {Fmt(Units.LengthFromApi(length))} mm long";
            }
            catch
            {
                return "an edge";
            }
        }

        private static string DescribeVertex(IVertex vertex)
        {
            var point = vertex?.GetPoint() as double[];
            return point == null ? "a vertex" : $"a vertex at ({Point(point)}) mm";
        }

        private static string FeatureName(object selected)
        {
            try
            {
                string name = (selected as IFeature)?.Name;
                return string.IsNullOrWhiteSpace(name) ? string.Empty : $" '{name}'";
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// A normal as an axis name when it is within a degree or so of one, and
        /// as raw components otherwise. "Facing +Z" is something the model can
        /// aim a feature at; three decimals are not.
        /// </summary>
        private static string AxisWords(double[] n)
        {
            if (n == null || n.Length < 3) return null;

            string[] axes = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(Math.Abs(n[i]) - 1.0) < 0.02)
                    return (n[i] > 0 ? "+" : "-") + axes[i];
            }

            return $"({Fmt(n[0])}, {Fmt(n[1])}, {Fmt(n[2])})";
        }

        private static string Point(double[] metres)
            => string.Join(", ", metres.Take(3).Select(v => Fmt(Units.LengthFromApi(v))));

        private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>One thing the user had selected, kept as a live COM pointer.</summary>
    public sealed class SelectedEntity
    {
        public SelectedEntity(swSelectType_e type, object comObject, string description)
        {
            Type = type;
            ComObject = comObject;
            Description = description;
        }

        public swSelectType_e Type { get; }
        public object ComObject { get; }
        public string Description { get; }

        public bool IsFace => Type == swSelectType_e.swSelFACES;
        public bool IsEdge => Type == swSelectType_e.swSelEDGES;
    }

    /// <summary>What was selected in SOLIDWORKS when the user sent their message.</summary>
    public sealed class SelectionSnapshot
    {
        public static readonly SelectionSnapshot Empty = new SelectionSnapshot(0, new List<SelectedEntity>());

        public SelectionSnapshot(int count, IReadOnlyList<SelectedEntity> items)
        {
            Count = count;
            Items = items ?? new List<SelectedEntity>();
            CapturedAtUtc = DateTime.UtcNow;
        }

        public int Count { get; }
        public IReadOnlyList<SelectedEntity> Items { get; }
        public DateTime CapturedAtUtc { get; }

        public bool IsEmpty => Count <= 0 || Items.Count == 0;

        public string Describe()
        {
            if (IsEmpty)
                return "Nothing was selected in SOLIDWORKS when the user sent this message.";

            var sb = new StringBuilder();
            sb.Append("The user had ").Append(Count).Append(Count == 1 ? " thing" : " things")
              .AppendLine(" selected when they sent this message:");

            for (int i = 0; i < Items.Count; i++)
                sb.Append("  ").Append(i + 1).Append(". ").AppendLine(Items[i].Description);

            if (Count > Items.Count)
                sb.Append("  ...and ").Append(Count - Items.Count).AppendLine(" more, not listed.");

            return sb.ToString().TrimEnd();
        }
    }
}
