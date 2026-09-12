using System;
using SolidWorks.Interop.sldworks;
using SwAgent.Core.Inspection;
using SwAgent.Core.Modeling;
using SwAgent.Core.Session;

namespace SwAgent.Harness
{
    /// <summary>
    /// The regression suite of reference parts.
    ///
    /// Every part here asserts mass and bounding box against a hand
    /// calculation, within 1%. This is the unit-error test: a part built in
    /// metres instead of millimetres comes out 1000x too large while every API
    /// call returns success, so an assertion on the resulting number is the only
    /// thing that catches it.
    ///
    /// Every new modelling tool adds a reference part here and must leave all
    /// the existing ones passing.
    /// </summary>
    public static class ReferencePartTests
    {
        /// <summary>
        /// A 60 x 40 x 10 mm plate.
        ///
        /// Hand calculation:
        ///   volume  = 60 * 40 * 10 = 24,000 mm3 = 24 cm3
        ///   density = 1000 kg/m3 (SOLIDWORKS default when no material is set)
        ///   mass    = 24 cm3 * 1 g/cm3 = 24 g
        ///
        /// This is deliberately the "is this 24 cm3 or 24,000 cm3" case from the
        /// spec. Build it in metres by mistake and the volume comes back as
        /// 2.4e13 cm3, which this test fails on loudly.
        /// </summary>
        public static void Plate60x40x10(TestRun run, SwSession session)
        {
            run.Step("new part from default template");
            NewPart(session);

            run.Step("open sketch on Front plane");
            string plane = SketchOps.OpenOnPlane(session, StandardPlane.Front);
            run.Note($"sketch opened on '{plane}'");

            run.Step("centred rectangle 60 x 40 mm");
            SketchOps.CenteredRectangle(session, 0, 0, 60, 40);

            run.Step("close sketch");
            string sketchName = SketchOps.Close(session);
            run.Note($"sketch closed as '{sketchName}'");

            run.Step("select sketch and extrude 10 mm");
            var doc = session.RequirePart();
            session.ClearSelection();
            if (!doc.Extension.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null, 0))
                throw new Exception($"Could not reselect sketch '{sketchName}' for the extrude.");

            var feature = ExtrudeOp.Boss(session, new ExtrudeOptions
            {
                DepthMm = 10.0,
                End = EndCondition.Blind,
                Merge = true
            });
            run.Note($"extrude created: '{feature.Name}'");

            run.Step("force rebuild");
            var rebuild = session.Rebuild();
            run.Note(rebuild.Describe());
            run.Assert(rebuild.IsClean, "rebuild is clean");

            run.Step("measure against hand calculation");
            var m = PartMeasure.OfActivePart(session);
            run.Note(m.Describe());

            var dims = m.SortedDimsMm;
            run.AssertClose(dims[0], 60.0, 0.01, "largest bbox dimension == 60 mm");
            run.AssertClose(dims[1], 40.0, 0.01, "middle bbox dimension == 40 mm");
            run.AssertClose(dims[2], 10.0, 0.01, "smallest bbox dimension == 10 mm");
            run.AssertClose(m.VolumeCm3, 24.0, 0.01, "volume == 24 cm3");
            run.AssertClose(m.MassGrams, 24.0, 0.01, "mass == 24 g");
        }

        /// <summary>
        /// The same plate with a 10 mm through hole in the middle.
        ///
        /// Hand calculation:
        ///   plate volume = 24,000 mm3
        ///   hole volume  = pi * 5^2 * 10 = 785.398 mm3
        ///   net volume   = 23,214.602 mm3 = 23.2146 cm3
        ///   mass         = 23.2146 g
        ///
        /// This one exercises the cut path and, because the hole volume is only
        /// 3% of the plate, it fails if the cut silently did nothing.
        /// </summary>
        public static void PlateWithThroughHole(TestRun run, SwSession session)
        {
            run.Step("new part from default template");
            NewPart(session);

            run.Step("plate 60 x 40 x 10 mm");
            SketchOps.OpenOnPlane(session, StandardPlane.Front);
            SketchOps.CenteredRectangle(session, 0, 0, 60, 40);
            string sketchName = SketchOps.Close(session);

            var doc = session.RequirePart();
            session.ClearSelection();
            doc.Extension.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null, 0);
            ExtrudeOp.Boss(session, new ExtrudeOptions { DepthMm = 10.0, End = EndCondition.Blind });

            run.Step("sketch a 10 mm circle on the Front plane");
            SketchOps.OpenOnPlane(session, StandardPlane.Front);
            SketchOps.Circle(session, 0, 0, 5.0);
            string holeSketch = SketchOps.Close(session);

            run.Step("cut through all");
            session.ClearSelection();
            if (!doc.Extension.SelectByID2(holeSketch, "SKETCH", 0, 0, 0, false, 0, null, 0))
                throw new Exception($"Could not reselect sketch '{holeSketch}' for the cut.");

            // ThroughAllBoth, not ThroughAll: the circle is sketched on the same
            // plane the plate was extruded from, so a one-directional through
            // cut points away from the material and removes nothing.
            var cut = ExtrudeOp.Cut(session, new ExtrudeOptions { End = EndCondition.ThroughAllBoth });
            run.Note($"cut created: '{cut.Name}'");

            run.Step("force rebuild");
            var rebuild = session.Rebuild();
            run.Note(rebuild.Describe());
            run.Assert(rebuild.IsClean, "rebuild is clean");

            run.Step("measure against hand calculation");
            var m = PartMeasure.OfActivePart(session);
            run.Note(m.Describe());

            const double expectedVolumeCm3 = 23.2146018366;

            var dims = m.SortedDimsMm;
            run.AssertClose(dims[0], 60.0, 0.01, "largest bbox dimension == 60 mm");
            run.AssertClose(dims[1], 40.0, 0.01, "middle bbox dimension == 40 mm");
            run.AssertClose(dims[2], 10.0, 0.01, "smallest bbox dimension == 10 mm");
            run.AssertClose(m.VolumeCm3, expectedVolumeCm3, 0.01, "volume == 23.215 cm3 (plate minus hole)");
            run.AssertClose(m.MassGrams, expectedVolumeCm3, 0.01, "mass == 23.215 g");
        }

        /// <summary>
        /// Deliberately malformed arguments must fail cleanly, not crash and not
        /// model something absurd. This is the boundary test that protects
        /// against a 1000x unit error reaching geometry.
        /// </summary>
        public static void RejectsAbsurdArguments(TestRun run, SwSession session)
        {
            run.Step("new part from default template");
            NewPart(session);

            run.Step("a 60 metre plate (i.e. mm/m confusion) must be rejected");
            SketchOps.OpenOnPlane(session, StandardPlane.Front);
            run.AssertThrows(
                () => SketchOps.CenteredRectangle(session, 0, 0, 60000, 40000),
                "rectangle of 60,000 mm is rejected at the boundary");

            run.Step("a zero-width rectangle must be rejected");
            run.AssertThrows(
                () => SketchOps.CenteredRectangle(session, 0, 0, 0, 40),
                "zero-width rectangle is rejected");

            run.Step("NaN must be rejected");
            run.AssertThrows(
                () => SketchOps.Circle(session, 0, 0, double.NaN),
                "NaN radius is rejected");

            SketchOps.Close(session);

            run.Step("an absurd extrude depth must be rejected");
            run.AssertThrows(
                () => ExtrudeOp.Boss(session, new ExtrudeOptions { DepthMm = 999999 }),
                "999,999 mm extrude depth is rejected");

            run.Note("SOLIDWORKS still responding after malformed input");
            run.Assert(session.App.RevisionNumber() != null, "session still alive");
        }

        private static void NewPart(SwSession session)
        {
            string template = session.App.GetUserPreferenceStringValue(
                (int)SolidWorks.Interop.swconst.swUserPreferenceStringValue_e.swDefaultTemplatePart);

            if (string.IsNullOrWhiteSpace(template))
                throw new Exception("No default part template is configured in this SOLIDWORKS installation.");

            var doc = (IModelDoc2)session.App.NewDocument(template, 0, 0, 0);
            if (doc == null)
                throw new Exception($"SOLIDWORKS refused to create a part from template '{template}'.");
        }
    }
}
