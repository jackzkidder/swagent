using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Session;

namespace SwAgent.Core.Modeling
{
    /// <summary>
    /// Patterns and mirrors.
    ///
    /// These matter more for the feature history than for the geometry. Four
    /// mounting holes cut as four separate features look identical to four
    /// holes in a pattern - until somebody opens the part and needs to change
    /// the spacing, at which point one is an edit and the other is four edits
    /// and a mistake. We are shipping a parametric model that a person will
    /// modify, so the history is part of the deliverable.
    /// </summary>
    public static class PatternOps
    {
        /// <summary>
        /// SOLIDWORKS distinguishes the things in a selection by "mark", not by
        /// order. A pattern needs the feature being patterned on mark 4 and the
        /// direction reference on mark 1; get the marks wrong and the call
        /// fails with no indication of which selection it disliked.
        /// </summary>
        private const int MarkDirection1 = 1;
        private const int MarkDirection2 = 2;
        private const int MarkFeature = 4;

        /// <summary>
        /// Find a straight edge running along the requested axis, to use as a
        /// pattern direction.
        ///
        /// A linear pattern needs a direction reference and the agent thinks in
        /// axes, so we bridge the two by looking for an edge that happens to
        /// point the right way. On a prismatic part there is essentially always
        /// one.
        /// </summary>
        public static IEdge FindAxisAlignedEdge(SwSession session, RayDirection direction)
        {
            var doc = session.RequirePart();
            var part = (IPartDoc)doc;

            var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            if (bodies == null || bodies.Length == 0)
                throw new InvalidOperationException("The part has no solid body.");

            var (dx, dy, dz) = AxisVector(direction);
            IEdge best = null;
            double bestLength = 0;

            foreach (object bodyObj in bodies)
            {
                var body = bodyObj as IBody2;
                var edges = body?.GetEdges() as object[];
                if (edges == null) continue;

                foreach (object edgeObj in edges)
                {
                    var edge = edgeObj as IEdge;
                    if (edge == null) continue;

                    var curve = edge.GetCurve() as ICurve;
                    if (curve == null || !curve.IsLine()) continue;

                    // LineParams is point (3) then direction (3).
                    var line = curve.LineParams as double[];
                    if (line == null || line.Length < 6) continue;

                    double ex = line[3], ey = line[4], ez = line[5];
                    double len = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                    if (len < 1e-9) continue;

                    ex /= len; ey /= len; ez /= len;

                    // Parallel to the axis, either way round.
                    double dot = Math.Abs(ex * dx + ey * dy + ez * dz);
                    if (dot < 0.999) continue;

                    double edgeLength = EdgeLength(edge);
                    if (edgeLength > bestLength)
                    {
                        bestLength = edgeLength;
                        best = edge;
                    }
                }
            }

            if (best == null)
            {
                throw new InvalidOperationException(
                    $"No straight edge runs along {Describe(direction)}, so there is nothing to use as a " +
                    "pattern direction. This part may not be prismatic enough to pattern along an axis.");
            }

            return best;
        }

        private static double EdgeLength(IEdge edge)
        {
            try
            {
                var start = (edge.GetStartVertex() as IVertex)?.GetPoint() as double[];
                var end = (edge.GetEndVertex() as IVertex)?.GetPoint() as double[];
                if (start == null || end == null) return 0;

                double dx = end[0] - start[0], dy = end[1] - start[1], dz = end[2] - start[2];
                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Pattern a named feature along an axis.
        ///
        /// FeatureLinearPattern4 takes 20 positional arguments; wrapped once,
        /// here, like every other long signature in this codebase.
        /// </summary>
        public static IFeature LinearPattern(
            SwSession session, string featureName, RayDirection direction,
            int count, double spacingMm, out double volumeChangeCm3)
        {
            if (count < 2)
                throw new ArgumentException($"A pattern of {count} is not a pattern. Use at least 2.");

            var doc = session.RequirePart();
            double spacing = Units.LengthToApi(spacingMm, "spacing_mm");

            var feature = FindFeature(doc, featureName);
            var edge = FindAxisAlignedEdge(session, direction);

            session.ClearSelection();

            var selection = (ISelectionMgr)doc.SelectionManager;

            // Direction reference on mark 1.
            var directionData = selection.CreateSelectData();
            directionData.Mark = MarkDirection1;
            if (!((IEntity)edge).Select4(true, directionData))
                throw new InvalidOperationException("Could not select an edge as the pattern direction.");

            // The feature being patterned on mark 4.
            if (!feature.Select2(true, MarkFeature))
                throw new InvalidOperationException($"Could not select '{featureName}' to pattern.");

            // Measure first. A pattern whose instances fall outside the
            // material produces FEWER copies than asked for and still rebuilds
            // clean - SOLIDWORKS raises nothing at all. Volume is the only
            // signal that the pattern did what was asked.
            double before = SafeVolumeCm3(session);

            var fm = (IFeatureManager)doc.FeatureManager;

            var pattern = fm.FeatureLinearPattern4(
                /*  0 Num1            */ count,
                /*  1 Spacing1 (m)    */ spacing,
                /*  2 Num2            */ 1,
                /*  3 Spacing2        */ 0,
                /*  4 FlipDir1        */ IsNegative(direction),
                /*  5 FlipDir2        */ false,
                /*  6 DName1          */ "",
                /*  7 DName2          */ "",
                /*  8 GeometryPattern */ false,
                /*  9 VaryInstance    */ false,
                /* 10 HasOffset1      */ false,
                /* 11 HasOffset2      */ false,
                /* 12 CtrlByNum1      */ true,
                /* 13 CtrlByNum2      */ true,
                /* 14 FromCentroid1   */ false,
                /* 15 FromCentroid2   */ false,
                /* 16 RevOffset1      */ false,
                /* 17 RevOffset2      */ false,
                /* 18 Offset1         */ 0,
                /* 19 Offset2         */ 0);

            if (pattern == null)
            {
                throw new InvalidOperationException(
                    $"SOLIDWORKS refused the pattern of '{featureName}'. Instances that fall outside the " +
                    "material are the usual cause - check the spacing against the size of the part.");
            }

            volumeChangeCm3 = Math.Abs(before - SafeVolumeCm3(session));
            return pattern;
        }

        /// <summary>Volume in cm3, or zero if it cannot be measured.</summary>
        private static double SafeVolumeCm3(SwSession session)
        {
            try { return Inspection.PartMeasure.OfActivePart(session).VolumeCm3; }
            catch { return 0; }
        }

        /// <summary>Mirror a named feature about a standard plane.</summary>
        public static IFeature Mirror(SwSession session, string featureName, StandardPlane plane)
        {
            var doc = session.RequirePart();

            var feature = FindFeature(doc, featureName);

            session.ClearSelection();

            // The mirror plane goes on mark 2, the feature on mark 1 - not the
            // same marks a pattern uses, which is exactly the sort of detail
            // that makes these calls fail silently.
            string planeName = PlaneSelector.Select(session, plane);

            var selection = (ISelectionMgr)doc.SelectionManager;
            selection.SetSelectedObjectMark(1, 2, (int)swSelectionMarkAction_e.swSelectionMarkSet);

            if (!feature.Select2(true, 1))
                throw new InvalidOperationException($"Could not select '{featureName}' to mirror.");

            var fm = (IFeatureManager)doc.FeatureManager;

            var mirrored = fm.InsertMirrorFeature2(
                BMirrorBody: false,
                BGeometryPattern: false,
                BMerge: true,
                BKnit: false,
                ScopeOptions: (int)swFeatureScope_e.swFeatureScope_AllBodies);

            if (mirrored == null)
            {
                throw new InvalidOperationException(
                    $"SOLIDWORKS refused to mirror '{featureName}' about the {plane} plane ('{planeName}'). " +
                    "The mirrored copy may fall outside the material.");
            }

            return mirrored;
        }

        /// <summary>Find a feature by name, with a message that lists what does exist.</summary>
        private static IFeature FindFeature(IModelDoc2 doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A feature name is required.");

            var names = new List<string>();
            var feature = (IFeature)doc.FirstFeature();

            while (feature != null)
            {
                string featureName = null;
                try { featureName = feature.Name; } catch { }

                if (string.Equals(featureName, name, StringComparison.OrdinalIgnoreCase))
                    return feature;

                if (!string.IsNullOrEmpty(featureName)) names.Add(featureName);
                feature = (IFeature)feature.GetNextFeature();
            }

            throw new InvalidOperationException(
                $"There is no feature called '{name}'. Check sw_feature_tree for the exact name. " +
                $"Features present: {string.Join(", ", names)}");
        }

        private static (double, double, double) AxisVector(RayDirection d)
        {
            switch (d)
            {
                case RayDirection.PlusX: case RayDirection.MinusX: return (1, 0, 0);
                case RayDirection.PlusY: case RayDirection.MinusY: return (0, 1, 0);
                default: return (0, 0, 1);
            }
        }

        private static bool IsNegative(RayDirection d)
            => d == RayDirection.MinusX || d == RayDirection.MinusY || d == RayDirection.MinusZ;

        private static string Describe(RayDirection d)
        {
            switch (d)
            {
                case RayDirection.PlusX: case RayDirection.MinusX: return "the X axis";
                case RayDirection.PlusY: case RayDirection.MinusY: return "the Y axis";
                default: return "the Z axis";
            }
        }
    }
}
