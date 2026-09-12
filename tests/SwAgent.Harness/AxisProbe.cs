using System;
using System.Globalization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Modeling;
using SwAgent.Core.Session;

namespace SwAgent.Harness
{
    /// <summary>
    /// Diagnostic: establish empirically how a sketch plane maps onto the
    /// part's global axes, and what order GetPartBox returns.
    ///
    /// This exists because assuming the mapping is how you ship a bracket that
    /// is correct in volume and wrong in orientation - which the mass assertion
    /// alone will not catch, since a rotated box has identical mass.
    /// </summary>
    internal static class AxisProbe
    {
        public static void Run(SwSession session)
        {
            Console.WriteLine();
            Console.WriteLine("AXIS PROBE: 60 (sketch X) x 40 (sketch Y) rectangle, extruded 10mm");
            Console.WriteLine(new string('-', 68));

            foreach (StandardPlane plane in new[] { StandardPlane.Front, StandardPlane.Top, StandardPlane.Right })
            {
                try
                {
                    Probe(session, plane);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {plane,-6} FAILED: {ex.Message}");
                }
                finally
                {
                    CloseQuietly(session);
                }
            }

            Console.WriteLine();
            Console.WriteLine("Also listing reference planes in tree order:");
            try
            {
                NewPart(session);
                var doc = session.RequirePart();
                var names = PlaneSelector.ListReferencePlanes(doc);
                for (int i = 0; i < names.Count; i++)
                    Console.WriteLine($"  [{i}] {names[i]}");
            }
            finally
            {
                CloseQuietly(session);
            }
        }

        private static void Probe(SwSession session, StandardPlane plane)
        {
            NewPart(session);

            string planeName = SketchOps.OpenOnPlane(session, plane);

            // Read the plane's orientation straight from the API, before any
            // geometry exists. This is the independent check: it tells us where
            // the plane actually is, without relying on how a bounding box is
            // ordered.
            DescribeSketchFrame(session, plane, planeName);

            SketchOps.CenteredRectangle(session, 0, 0, 60, 40);
            string sketch = SketchOps.Close(session);

            var doc = session.RequirePart();
            session.ClearSelection();
            doc.Extension.SelectByID2(sketch, "SKETCH", 0, 0, 0, false, 0, null, 0);
            ExtrudeOp.Boss(session, new ExtrudeOptions { DepthMm = 10.0, End = EndCondition.Blind });
            session.Rebuild();

            var part = (IPartDoc)doc;
            var box = part.GetPartBox(true) as double[];

            var c = CultureInfo.InvariantCulture;
            if (box == null || box.Length < 6)
            {
                Console.WriteLine($"  {plane,-6} ({planeName}): GetPartBox returned nothing usable");
                return;
            }

            double dx = (box[3] - box[0]) * 1000.0;
            double dy = (box[4] - box[1]) * 1000.0;
            double dz = (box[5] - box[2]) * 1000.0;

            Console.WriteLine(
                $"  {plane,-6} ({planeName,-12}) raw=[{box[0].ToString("0.####", c)}, {box[1].ToString("0.####", c)}, " +
                $"{box[2].ToString("0.####", c)}, {box[3].ToString("0.####", c)}, {box[4].ToString("0.####", c)}, " +
                $"{box[5].ToString("0.####", c)}]  ->  dX={dx.ToString("0.##", c)} dY={dy.ToString("0.##", c)} dZ={dz.ToString("0.##", c)}");

            CompareBoxes(session);
        }

        /// <summary>
        /// Print the sketch's frame in model space: where sketch X, sketch Y and
        /// the extrude normal actually point.
        ///
        /// SOLIDWORKS MathTransform.ArrayData is 16 doubles: [0..8] the rotation
        /// matrix, [9..11] translation, [12] scale, the rest unused. For an
        /// axis-aligned plane the rotation is a signed permutation matrix, which
        /// reads directly.
        /// </summary>
        private static void DescribeSketchFrame(SwSession session, StandardPlane plane, string planeName)
        {
            try
            {
                var doc = session.RequirePart();
                var sm = (ISketchManager)doc.SketchManager;
                var sketch = sm.ActiveSketch;
                if (sketch == null) { Console.WriteLine($"  {plane,-6} no active sketch to inspect"); return; }

                var xform = sketch.ModelToSketchTransform;
                var data = xform?.ArrayData as double[];
                if (data == null || data.Length < 12)
                {
                    Console.WriteLine($"  {plane,-6} ModelToSketchTransform unavailable");
                    return;
                }

                var c = CultureInfo.InvariantCulture;
                Console.WriteLine($"  {plane,-6} ({planeName}) model->sketch rotation:");
                Console.WriteLine($"           [{data[0].ToString("0.##", c),5} {data[1].ToString("0.##", c),5} {data[2].ToString("0.##", c),5}]");
                Console.WriteLine($"           [{data[3].ToString("0.##", c),5} {data[4].ToString("0.##", c),5} {data[5].ToString("0.##", c),5}]");
                Console.WriteLine($"           [{data[6].ToString("0.##", c),5} {data[7].ToString("0.##", c),5} {data[8].ToString("0.##", c),5}]");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {plane,-6} could not read sketch frame: {ex.Message}");
            }
        }

        /// <summary>
        /// Cross-check GetPartBox against GetBodyBox. If the two disagree in
        /// ordering, one of them is not what its documentation implies.
        /// </summary>
        private static void CompareBoxes(SwSession session)
        {
            try
            {
                var doc = session.RequirePart();
                var part = (IPartDoc)doc;

                var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                if (bodies == null || bodies.Length == 0) return;

                var body = (IBody2)bodies[0];
                var bodyBox = body.GetBodyBox() as double[];
                if (bodyBox == null || bodyBox.Length < 6) return;

                var c = CultureInfo.InvariantCulture;
                double dx = (bodyBox[3] - bodyBox[0]) * 1000.0;
                double dy = (bodyBox[4] - bodyBox[1]) * 1000.0;
                double dz = (bodyBox[5] - bodyBox[2]) * 1000.0;
                Console.WriteLine(
                    $"           GetBodyBox -> dX={dx.ToString("0.##", c)} dY={dy.ToString("0.##", c)} dZ={dz.ToString("0.##", c)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"           GetBodyBox failed: {ex.Message}");
            }
        }

        private static void NewPart(SwSession session)
        {
            string template = session.App.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            var doc = (IModelDoc2)session.App.NewDocument(template, 0, 0, 0);
            if (doc == null) throw new Exception("Could not create a part.");
        }

        private static void CloseQuietly(SwSession session)
        {
            try
            {
                var doc = session.ActiveDoc;
                if (doc == null) return;
                if (SketchOps.IsSketchActuallyOpen(doc))
                    ((ISketchManager)doc.SketchManager).InsertSketch(true);
                session.App.CloseDoc(doc.GetTitle());
            }
            catch { }
        }
    }
}
