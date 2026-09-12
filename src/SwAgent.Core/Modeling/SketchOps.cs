using System;
using SolidWorks.Interop.sldworks;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Session;

namespace SwAgent.Core.Modeling
{
    /// <summary>
    /// Sketch mode, made non-modal from the caller's point of view.
    ///
    /// InsertSketch toggles: the same call opens a sketch and closes it. Calling
    /// it twice because you were not sure of the state is how you end up drawing
    /// into the wrong sketch, or closing one you meant to keep open.
    ///
    /// We track the state, and we also cross-check it against
    /// ISketchManager.ActiveSketch before every toggle, so our bookkeeping
    /// cannot drift out of step with reality when the user clicks something in
    /// SOLIDWORKS while the agent is working.
    /// </summary>
    public static class SketchOps
    {
        /// <summary>
        /// Open a new sketch on one of the standard reference planes.
        /// Returns the name of the plane actually used.
        /// </summary>
        public static string OpenOnPlane(SwSession session, StandardPlane plane)
        {
            var doc = session.RequirePart();

            if (IsSketchActuallyOpen(doc))
            {
                throw new InvalidOperationException(
                    "A sketch is already open. Close it (sw_sketch_close) before opening another.");
            }

            string planeName = PlaneSelector.Select(session, plane);

            var sm = (ISketchManager)doc.SketchManager;
            sm.InsertSketch(true);

            if (!IsSketchActuallyOpen(doc))
            {
                session.MarkSketchClosed();
                throw new InvalidOperationException(
                    $"Opened a sketch on '{planeName}' but SOLIDWORKS reports no active sketch. " +
                    "The plane selection may not have taken effect.");
            }

            session.MarkSketchOpened();
            return planeName;
        }

        /// <summary>
        /// Close the open sketch. Returns the sketch's feature name so the
        /// caller can select it later.
        /// </summary>
        public static string Close(SwSession session)
        {
            var doc = session.RequirePart();

            if (!IsSketchActuallyOpen(doc))
            {
                session.MarkSketchClosed();
                throw new InvalidOperationException("No sketch is open, so there is nothing to close.");
            }

            var sm = (ISketchManager)doc.SketchManager;

            // Grab the name before closing; afterwards ActiveSketch is null.
            string name = null;
            try
            {
                var sketch = sm.ActiveSketch;
                var feature = (IFeature)sketch;
                name = feature?.Name;
            }
            catch (Exception ex)
            {
                session.Log.Debug($"SketchOps.Close: could not read sketch name: {ex.Message}");
            }

            sm.InsertSketch(true);
            session.MarkSketchClosed();

            return name ?? "(unnamed sketch)";
        }

        /// <summary>
        /// Ask SOLIDWORKS directly whether a sketch is open, rather than
        /// trusting our own bookkeeping.
        /// </summary>
        public static bool IsSketchActuallyOpen(IModelDoc2 doc)
        {
            try
            {
                var sm = (ISketchManager)doc.SketchManager;
                return sm.ActiveSketch != null;
            }
            catch
            {
                return false;
            }
        }

        private static ISketchManager RequireOpenSketch(SwSession session)
        {
            var doc = session.RequirePart();

            if (!IsSketchActuallyOpen(doc))
            {
                session.MarkSketchClosed();
                throw new InvalidOperationException(
                    "No sketch is open. Open one with sw_sketch_open before adding sketch entities.");
            }

            session.MarkSketchOpened();
            return (ISketchManager)doc.SketchManager;
        }

        /// <summary>
        /// Corner-to-corner rectangle, in millimetres on the sketch plane.
        /// </summary>
        public static void Rectangle(SwSession session, double x1Mm, double y1Mm, double x2Mm, double y2Mm)
        {
            var sm = RequireOpenSketch(session);

            double x1 = Units.CoordToApi(x1Mm, "x1_mm");
            double y1 = Units.CoordToApi(y1Mm, "y1_mm");
            double x2 = Units.CoordToApi(x2Mm, "x2_mm");
            double y2 = Units.CoordToApi(y2Mm, "y2_mm");

            if (Math.Abs(x2Mm - x1Mm) < Units.MinLengthMm || Math.Abs(y2Mm - y1Mm) < Units.MinLengthMm)
            {
                throw new ArgumentException(
                    $"Rectangle is degenerate: corners ({x1Mm}, {y1Mm}) and ({x2Mm}, {y2Mm}) " +
                    "give it zero width or zero height.");
            }

            var result = sm.CreateCornerRectangle(x1, y1, 0.0, x2, y2, 0.0);
            if (result == null)
                throw new InvalidOperationException("SOLIDWORKS refused the rectangle.");
        }

        /// <summary>
        /// Rectangle centred on a point, given width and height in millimetres.
        /// The form most parts are actually described in.
        /// </summary>
        public static void CenteredRectangle(SwSession session, double cxMm, double cyMm, double widthMm, double heightMm)
        {
            if (widthMm <= 0 || heightMm <= 0)
                throw new ArgumentException($"Width and height must be positive (got {widthMm} x {heightMm} mm).");

            Rectangle(session,
                cxMm - widthMm / 2.0, cyMm - heightMm / 2.0,
                cxMm + widthMm / 2.0, cyMm + heightMm / 2.0);
        }

        /// <summary>Circle by centre and radius, in millimetres.</summary>
        public static void Circle(SwSession session, double cxMm, double cyMm, double radiusMm)
        {
            var sm = RequireOpenSketch(session);

            double cx = Units.CoordToApi(cxMm, "cx_mm");
            double cy = Units.CoordToApi(cyMm, "cy_mm");
            double r = Units.LengthToApi(radiusMm, "radius_mm");

            var seg = sm.CreateCircleByRadius(cx, cy, 0.0, r);
            if (seg == null)
                throw new InvalidOperationException("SOLIDWORKS refused the circle.");
        }

        /// <summary>Line between two points, in millimetres.</summary>
        public static void Line(SwSession session, double x1Mm, double y1Mm, double x2Mm, double y2Mm)
        {
            var sm = RequireOpenSketch(session);

            double x1 = Units.CoordToApi(x1Mm, "x1_mm");
            double y1 = Units.CoordToApi(y1Mm, "y1_mm");
            double x2 = Units.CoordToApi(x2Mm, "x2_mm");
            double y2 = Units.CoordToApi(y2Mm, "y2_mm");

            var seg = sm.CreateLine(x1, y1, 0.0, x2, y2, 0.0);
            if (seg == null)
                throw new InvalidOperationException("SOLIDWORKS refused the line.");
        }
    }
}
