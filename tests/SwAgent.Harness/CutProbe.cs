using System;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Modeling;
using SwAgent.Core.Session;

namespace SwAgent.Harness
{
    /// <summary>
    /// Diagnostic: find the combination of end condition and direction that
    /// actually cuts a through hole in a plate.
    ///
    /// FeatureCut4 returns null on refusal without saying why, so the only way
    /// to learn the rule is to try the combinations against real geometry.
    /// </summary>
    internal static class CutProbe
    {
        public static void Run(SwSession session)
        {
            Console.WriteLine();
            Console.WriteLine("CUT PROBE: 10mm hole through a 60x40x10 plate");
            Console.WriteLine(new string('-', 68));

            Try(session, "ThroughAll, forward", new ExtrudeOptions { End = EndCondition.ThroughAll });
            Try(session, "ThroughAll, reverse", new ExtrudeOptions { End = EndCondition.ThroughAll, Reverse = true });
            Try(session, "ThroughAllBoth", new ExtrudeOptions { End = EndCondition.ThroughAllBoth });
            Try(session, "Blind 10mm", new ExtrudeOptions { End = EndCondition.Blind, DepthMm = 10.0 });
            Try(session, "Blind 10mm, reverse", new ExtrudeOptions { End = EndCondition.Blind, DepthMm = 10.0, Reverse = true });
        }

        private static void Try(SwSession session, string label, ExtrudeOptions cutOptions)
        {
            try
            {
                BuildPlate(session, out IModelDoc2 doc);

                // Sketch the hole circle on the same plane the plate came from.
                SketchOps.OpenOnPlane(session, StandardPlane.Front);
                SketchOps.Circle(session, 0, 0, 5.0);
                string holeSketch = SketchOps.Close(session);

                session.ClearSelection();
                bool selected = doc.Extension.SelectByID2(holeSketch, "SKETCH", 0, 0, 0, false, 0, null, 0);

                IFeature cut = null;
                string error = null;
                try
                {
                    cut = ExtrudeOp.Cut(session, cutOptions);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                var rebuild = session.Rebuild();
                var m = SwAgent.Core.Inspection.PartMeasure.OfActivePart(session);

                Console.WriteLine(
                    $"  {label,-24} sketch='{holeSketch}' selected={selected} " +
                    $"cut={(cut == null ? "NULL" : cut.Name)} vol={m.VolumeCm3:0.####} cm3  {rebuild.Describe()}");

                if (error != null)
                    Console.WriteLine($"        error: {error}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {label,-24} SETUP FAILED: {ex.Message}");
            }
            finally
            {
                CloseQuietly(session);
            }
        }

        private static void BuildPlate(SwSession session, out IModelDoc2 doc)
        {
            string template = session.App.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            doc = (IModelDoc2)session.App.NewDocument(template, 0, 0, 0);
            if (doc == null) throw new Exception("Could not create a part.");

            SketchOps.OpenOnPlane(session, StandardPlane.Front);
            SketchOps.CenteredRectangle(session, 0, 0, 60, 40);
            string sketch = SketchOps.Close(session);

            session.ClearSelection();
            if (!doc.Extension.SelectByID2(sketch, "SKETCH", 0, 0, 0, false, 0, null, 0))
                throw new Exception($"could not select '{sketch}'");

            ExtrudeOp.Boss(session, new ExtrudeOptions { DepthMm = 10.0, End = EndCondition.Blind });
            session.Rebuild();
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
