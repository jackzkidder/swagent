using System;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Session;

namespace SwAgent.Core.Modeling
{
    /// <summary>How an extrude or cut terminates.</summary>
    public enum EndCondition
    {
        /// <summary>Stop at a given depth.</summary>
        Blind = 0,

        /// <summary>
        /// Pass through the entire body, in ONE direction only.
        ///
        /// This is a trap for cuts. A hole sketched on the plane the material
        /// was extruded from goes through all in the default direction, which
        /// points away from the material, and SOLIDWORKS then refuses the
        /// feature because the cut removes nothing. Prefer
        /// <see cref="ThroughAllBoth"/> for a through hole unless the direction
        /// genuinely matters.
        /// </summary>
        ThroughAll = 1,

        /// <summary>
        /// Pass through the entire body in BOTH directions.
        ///
        /// Direction-proof, which makes it tempting as a default - but it
        /// removes material through the WHOLE model. A window sketched on the
        /// front of a house cuts the back wall out too. Use it only when the
        /// feature genuinely must pass through everything in its path.
        /// </summary>
        ThroughAllBoth = 9,

        /// <summary>
        /// Pass through the NEXT solid encountered, then stop.
        ///
        /// This is what an opening in a single wall needs, and its absence is
        /// why a house came out with its windows cut clean through both sides.
        /// </summary>
        ThroughNext = 2,

        /// <summary>Stop at the next face, without passing through it.</summary>
        UpToNext = 11,

        /// <summary>Extrude symmetrically about the sketch plane, total depth as given.</summary>
        MidPlane = 6
    }

    /// <summary>Options for a boss extrude or a cut extrude.</summary>
    public sealed class ExtrudeOptions
    {
        /// <summary>Depth in MILLIMETRES. Ignored when <see cref="End"/> is ThroughAll.</summary>
        public double DepthMm { get; set; } = 10.0;

        public EndCondition End { get; set; } = EndCondition.Blind;

        /// <summary>Extrude in the reverse direction from the sketch plane normal.</summary>
        public bool Reverse { get; set; }

        /// <summary>Merge with existing solid bodies. Almost always true for a boss.</summary>
        public bool Merge { get; set; } = true;

        /// <summary>Draft angle in DEGREES. Zero for no draft.</summary>
        public double DraftAngleDeg { get; set; }

        /// <summary>Draft outward rather than inward.</summary>
        public bool DraftOutward { get; set; }
    }

    /// <summary>
    /// The one and only place FeatureExtrusion3 and FeatureCut4 are called.
    ///
    /// FeatureExtrusion3 takes 23 positional arguments and FeatureCut4 takes 27.
    /// Both are trivially easy to get subtly wrong, and a wrong positional
    /// boolean produces a feature that builds successfully and is not what was
    /// asked for. So the signature appears exactly once, here, and every caller
    /// goes through the typed options above.
    ///
    /// Argument order verified by reflection against interop 33.5.0.53 - see
    /// docs/api-signatures.md. Regenerate that file before changing the
    /// supported interop version.
    /// </summary>
    public static class ExtrudeOp
    {
        /// <summary>
        /// Create a boss extrude from the currently open or selected sketch.
        /// The caller is responsible for having selected the sketch.
        /// </summary>
        public static IFeature Boss(SwSession session, ExtrudeOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var doc = session.RequirePart();

            // Metres and radians, converted at exactly one chokepoint.
            double depth = UsesDepth(options.End)
                ? Units.LengthToApi(options.DepthMm, "depth_mm")
                : 0.0;
            double draft = Units.AngleToApi(options.DraftAngleDeg, "draft_angle_deg", 89.0);

            var fm = (IFeatureManager)doc.FeatureManager;

            IFeature feature = fm.FeatureExtrusion3(
                /*  0 Sd                */ IsSingleEnded(options.End),
                /*  1 Flip              */ false,             // flip side to cut (n/a for boss)
                /*  2 Dir               */ options.Reverse,   // reverse direction
                /*  3 T1                */ (int)EndConditionDir1(options.End),
                /*  4 T2                */ (int)EndConditionDir2(options.End),
                /*  5 D1  (metres)      */ depth,
                /*  6 D2  (metres)      */ 0.0,
                /*  7 Dchk1             */ false,             // draft while extruding, dir 1
                /*  8 Dchk2             */ false,
                /*  9 Ddir1             */ options.DraftOutward,
                /* 10 Ddir2             */ false,
                /* 11 Dang1 (radians)   */ draft,
                /* 12 Dang2 (radians)   */ 0.0,
                /* 13 OffsetReverse1    */ false,
                /* 14 OffsetReverse2    */ false,
                /* 15 TranslateSurface1 */ false,
                /* 16 TranslateSurface2 */ false,
                /* 17 Merge             */ options.Merge,
                /* 18 UseFeatScope      */ true,
                /* 19 UseAutoSelect     */ true,
                /* 20 T0 (start cond)   */ (int)swStartConditions_e.swStartSketchPlane,
                /* 21 StartOffset       */ 0.0,
                /* 22 FlipStartOffset   */ false);

            if (feature == null)
            {
                throw new InvalidOperationException(
                    "SOLIDWORKS refused the extrude. The usual cause is that the sketch is not " +
                    "a closed profile, or no sketch was selected.");
            }

            return feature;
        }

        /// <summary>
        /// Create a cut extrude from the currently open or selected sketch.
        /// </summary>
        public static IFeature Cut(SwSession session, ExtrudeOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var doc = session.RequirePart();

            double depth = UsesDepth(options.End)
                ? Units.LengthToApi(options.DepthMm, "depth_mm")
                : 0.0;
            double draft = Units.AngleToApi(options.DraftAngleDeg, "draft_angle_deg", 89.0);

            var fm = (IFeatureManager)doc.FeatureManager;

            IFeature feature = fm.FeatureCut4(
                /*  0 Sd                      */ IsSingleEnded(options.End),
                /*  1 Flip                    */ false,
                /*  2 Dir                     */ options.Reverse,
                /*  3 T1                      */ (int)EndConditionDir1(options.End),
                /*  4 T2                      */ (int)EndConditionDir2(options.End),
                /*  5 D1  (metres)            */ depth,
                /*  6 D2  (metres)            */ 0.0,
                /*  7 Dchk1                   */ false,
                /*  8 Dchk2                   */ false,
                /*  9 Ddir1                   */ options.DraftOutward,
                /* 10 Ddir2                   */ false,
                /* 11 Dang1 (radians)         */ draft,
                /* 12 Dang2 (radians)         */ 0.0,
                /* 13 OffsetReverse1          */ false,
                /* 14 OffsetReverse2          */ false,
                /* 15 TranslateSurface1       */ false,
                /* 16 TranslateSurface2       */ false,
                /* 17 NormalCut               */ false,
                /* 18 UseFeatScope            */ true,
                /* 19 UseAutoSelect           */ true,
                /* 20 AssemblyFeatureScope    */ false,
                /* 21 AutoSelectComponents    */ false,
                /* 22 PropagateFeatureToParts */ false,
                /* 23 T0 (start cond)         */ (int)swStartConditions_e.swStartSketchPlane,
                /* 24 StartOffset             */ 0.0,
                /* 25 FlipStartOffset         */ false,
                /* 26 OptimizeGeometry        */ true);

            if (feature == null)
            {
                throw new InvalidOperationException(
                    "SOLIDWORKS refused the cut. Common causes: the sketch profile does not " +
                    "intersect the body, or the cut would consume the entire body.");
            }

            return feature;
        }

        private static swEndConditions_e ToSwEndCondition(EndCondition end)
        {
            switch (end)
            {
                case EndCondition.Blind: return swEndConditions_e.swEndCondBlind;
                case EndCondition.ThroughAll: return swEndConditions_e.swEndCondThroughAll;
                case EndCondition.ThroughAllBoth: return swEndConditions_e.swEndCondThroughAllBoth;
                case EndCondition.MidPlane: return swEndConditions_e.swEndCondMidPlane;
                case EndCondition.ThroughNext: return swEndConditions_e.swEndCondThroughNext;
                case EndCondition.UpToNext: return swEndConditions_e.swEndCondUpToNext;
                default: throw new ArgumentOutOfRangeException(nameof(end));
            }
        }

        /// <summary>Depth is only meaningful for the end conditions that consume it.</summary>
        private static bool UsesDepth(EndCondition end)
            => end == EndCondition.Blind || end == EndCondition.MidPlane;

        /// <summary>
        /// The Sd argument means "single ended". ThroughAllBoth is by definition
        /// two-ended, so passing single-ended alongside it is a contradiction
        /// and SOLIDWORKS refuses the feature - returning null, with no
        /// indication of which of the 23 arguments it objected to.
        /// </summary>
        private static bool IsSingleEnded(EndCondition end)
            => end != EndCondition.ThroughAllBoth;

        /// <summary>
        /// End condition for direction 1. "Through all both" is expressed as a
        /// two-ended feature with through-all on each side, rather than via the
        /// swEndCondThroughAllBoth constant, which this interop refuses in
        /// combination with FeatureCut4 - verified by CutProbe.
        /// </summary>
        private static swEndConditions_e EndConditionDir1(EndCondition end)
            => end == EndCondition.ThroughAllBoth
                ? swEndConditions_e.swEndCondThroughAll
                : ToSwEndCondition(end);

        /// <summary>End condition for direction 2. Only meaningful when two-ended.</summary>
        private static swEndConditions_e EndConditionDir2(EndCondition end)
            => end == EndCondition.ThroughAllBoth
                ? swEndConditions_e.swEndCondThroughAll
                : swEndConditions_e.swEndCondBlind;
    }
}
