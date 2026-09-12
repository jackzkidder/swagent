using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Session;

namespace SwAgent.Core.Modeling
{
    /// <summary>
    /// Shell, fillet and chamfer - the features that turn a block into a part.
    ///
    /// Their absence is why a house had to be faked as a block with a pocket
    /// cut in it. A pocket gives you walls whose thickness depends on where you
    /// happened to put the sketch; a shell gives you a uniform wall thickness
    /// you asked for and can verify.
    /// </summary>
    public static class DressUpOps
    {
        /// <summary>
        /// Hollow the part out, leaving walls of the given thickness.
        ///
        /// Any faces selected beforehand are removed, leaving the part open
        /// there. Select none and you get a sealed hollow body.
        ///
        /// InsertFeatureShell lives on IModelDoc2, not IFeatureManager, which
        /// is worth knowing because it is the only feature in this file that
        /// does.
        /// </summary>
        /// <returns>How much material the shell removed, in cm3.</returns>
        public static double Shell(SwSession session, double thicknessMm, bool outward = false)
        {
            var doc = session.RequirePart();

            // Metres, like every other length crossing into the API.
            double thickness = Units.LengthToApi(thicknessMm, "thickness_mm");

            // InsertFeatureShell returns VOID. No feature object, no success
            // flag, no error - exactly like EditUndo2. So a shell that was
            // geometrically impossible (a 60 mm wall on a 100 mm cube) is
            // indistinguishable from one that worked, and gets reported as a
            // success while the part is unchanged.
            //
            // The only way to know is to measure. A shell must remove material;
            // if the volume did not drop, nothing happened.
            double before = SafeVolumeCm3(session);

            doc.InsertFeatureShell(thickness, outward);

            double after = SafeVolumeCm3(session);

            if (before > 0 && after >= before - 1e-6)
            {
                throw new InvalidOperationException(
                    $"The {thicknessMm} mm shell did not remove any material - the part is unchanged at " +
                    $"{after:0.##} cm3. The wall is almost certainly too thick for the part: hollowing " +
                    $"a body needs the walls from both sides to fit inside it, so a {thicknessMm} mm " +
                    $"wall needs at least {thicknessMm * 2:0.#} mm of material across every direction. " +
                    "Use a thinner wall.");
            }

            return Math.Max(0, before - after);
        }

        /// <summary>Volume in cm3, or zero if it cannot be measured.</summary>
        private static double SafeVolumeCm3(SwSession session)
        {
            try
            {
                return Inspection.PartMeasure.OfActivePart(session).VolumeCm3;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>How to choose which edges get filleted or chamfered.</summary>
        public enum EdgeScope
        {
            /// <summary>Every edge of the solid body. "Break all the edges."</summary>
            AllEdges,

            /// <summary>Only the edges bounding one face, picked by ray.</summary>
            FaceEdges
        }

        /// <summary>
        /// Select edges for a dress-up feature and report how many were found.
        ///
        /// The count matters. A fillet applied to zero edges succeeds and does
        /// nothing; a fillet applied to 200 edges when you meant 4 quietly
        /// rounds the whole part. Either way the feature "worked", so the
        /// number has to go back to the agent.
        /// </summary>
        public static int SelectEdges(SwSession session, EdgeScope scope)
        {
            var doc = session.RequirePart();
            session.ClearSelection();

            var edges = new List<object>();

            if (scope == EdgeScope.AllEdges)
            {
                var part = (IPartDoc)doc;
                var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];

                if (bodies == null || bodies.Length == 0)
                    throw new InvalidOperationException("The part has no solid body to work on.");

                foreach (object bodyObj in bodies)
                {
                    var body = bodyObj as IBody2;
                    var bodyEdges = body?.GetEdges() as object[];
                    if (bodyEdges != null) edges.AddRange(bodyEdges);
                }
            }
            else
            {
                // The caller has already selected a face by ray.
                var selection = (ISelectionMgr)doc.SelectionManager;
                var face = selection.GetSelectedObject6(1, -1) as IFace2;

                if (face == null)
                    throw new InvalidOperationException(
                        "No face is selected, so there are no face edges to work on.");

                var faceEdges = face.GetEdges() as object[];
                if (faceEdges != null) edges.AddRange(faceEdges);

                session.ClearSelection();
            }

            int selected = 0;
            foreach (object edgeObj in edges)
            {
                var entity = edgeObj as IEntity;
                if (entity == null) continue;

                // Append, so each edge adds to the set rather than replacing it.
                if (entity.Select4(true, null)) selected++;
            }

            if (selected == 0)
                throw new InvalidOperationException("No edges could be selected.");

            return selected;
        }

        /// <summary>
        /// Round the currently selected edges.
        ///
        /// FeatureFillet3 takes 14 positional arguments. Like FeatureExtrusion3,
        /// it is wrapped exactly once - see docs/api-signatures.md for the order
        /// verified against this interop.
        /// </summary>
        public static IFeature Fillet(SwSession session, double radiusMm)
        {
            var doc = session.RequirePart();
            double radius = Units.LengthToApi(radiusMm, "radius_mm");

            var fm = (IFeatureManager)doc.FeatureManager;

            var feature = fm.FeatureFillet3(
                /*  0 Options        */ (int)(swFeatureFilletOptions_e.swFeatureFilletPropagate
                                            | swFeatureFilletOptions_e.swFeatureFilletUniformRadius),
                /*  1 R1 (metres)    */ radius,
                /*  2 R2             */ 0,
                /*  3 Rho            */ 0,
                /*  4 Ftyp           */ (int)swFeatureFilletType_e.swFeatureFilletType_Simple,
                /*  5 OverflowType   */ (int)swFilletOverFlowType_e.swFilletOverFlowType_Default,
                /*  6 ConicRhoType   */ 0,
                /*  7 Radii          */ null,
                /*  8 Dist2Arr       */ null,
                /*  9 RhoArr         */ null,
                /* 10 SetBackDist    */ null,
                /* 11 PointRadiusArr */ null,
                /* 12 PointDist2Arr  */ null,
                /* 13 PointRhoArr    */ null) as IFeature;

            if (feature == null)
            {
                throw new InvalidOperationException(
                    $"SOLIDWORKS refused the {radiusMm} mm fillet. The usual cause is a radius larger " +
                    "than the material allows - a 5 mm fillet cannot fit on a 4 mm wall. Try a smaller " +
                    "radius, or fewer edges.");
            }

            return feature;
        }

        /// <summary>Chamfer the currently selected edges, at 45 degrees by default.</summary>
        public static IFeature Chamfer(SwSession session, double distanceMm, double angleDeg = 45.0)
        {
            var doc = session.RequirePart();

            double distance = Units.LengthToApi(distanceMm, "distance_mm");
            double angle = Units.AngleToApi(angleDeg, "angle_deg", 89.0);

            var fm = (IFeatureManager)doc.FeatureManager;

            var feature = fm.InsertFeatureChamfer(
                /* 0 Options          */ (int)swFeatureFilletOptions_e.swFeatureFilletPropagate,
                /* 1 ChamferType      */ (int)swChamferType_e.swChamferAngleDistance,
                /* 2 Width (metres)   */ distance,
                /* 3 Angle (radians)  */ angle,
                /* 4 OtherDist        */ 0,
                /* 5 VertexChamDist1  */ 0,
                /* 6 VertexChamDist2  */ 0,
                /* 7 VertexChamDist3  */ 0);

            if (feature == null)
            {
                throw new InvalidOperationException(
                    $"SOLIDWORKS refused the {distanceMm} mm chamfer. It may be larger than the " +
                    "material allows on one of the selected edges.");
            }

            return feature;
        }
    }
}
