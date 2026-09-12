using System;
using System.Globalization;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Session;

namespace SwAgent.Core.Modeling
{
    /// <summary>A direction to fire a picking ray along.</summary>
    public enum RayDirection
    {
        PlusX, MinusX, PlusY, MinusY, PlusZ, MinusZ
    }

    /// <summary>What was found, described well enough to verify it was the right face.</summary>
    public sealed class FaceHit
    {
        public bool Found { get; set; }
        public double AreaMm2 { get; set; }
        public double[] NormalXyz { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Picks a face by firing a ray at the model.
    ///
    /// Without this, sketches can only be opened on the three origin planes,
    /// and every feature therefore starts from the middle of the part and runs
    /// outward. That is why a window cut into a house went through both walls:
    /// there was no way to say "this wall, from its outer surface".
    ///
    /// A ray is the right primitive because it is how the model already thinks
    /// about the part. It knows the bounding box from the mass properties, so
    /// "fire from outside the left edge, travelling right" is something it can
    /// work out without seeing the geometry.
    /// </summary>
    public static class FaceSelector
    {
        /// <summary>
        /// Select the first face hit by a ray from the given point.
        /// Leaves the face selected, ready for a sketch or a feature.
        /// </summary>
        public static FaceHit SelectByRay(
            SwSession session, double xMm, double yMm, double zMm, RayDirection direction, double toleranceMm = 1.0)
        {
            var doc = session.RequirePart();
            session.ClearSelection();

            double x = Units.CoordToApi(xMm, "x_mm");
            double y = Units.CoordToApi(yMm, "y_mm");
            double z = Units.CoordToApi(zMm, "z_mm");
            double radius = Units.LengthToApi(Math.Max(0.01, toleranceMm), "tolerance_mm");

            var (dx, dy, dz) = ToVector(direction);

            bool hit = doc.Extension.SelectByRay(
                x, y, z,
                dx, dy, dz,
                radius,
                (int)swSelectType_e.swSelFACES,
                false,   // do not append to the selection
                0,       // mark
                (int)swSelectOption_e.swSelectOptionDefault);

            if (!hit)
            {
                return new FaceHit
                {
                    Found = false,
                    Description =
                        $"No face was hit by a ray from ({xMm}, {yMm}, {zMm}) mm travelling {Describe(direction)}. " +
                        "Check the bounding box with sw_mass_properties and start the ray outside the part, " +
                        "pointing at it.",
                };
            }

            return DescribeSelectedFace(session, doc);
        }

        /// <summary>
        /// Read back what actually got selected.
        ///
        /// This is the verification round-trip applied to selection. A ray that
        /// hits the wrong face fails silently and produces a feature in the
        /// wrong place, so the area and the outward normal go back to the agent
        /// to check against what it intended.
        /// </summary>
        private static FaceHit DescribeSelectedFace(SwSession session, IModelDoc2 doc)
        {
            var result = new FaceHit { Found = true };

            try
            {
                var selection = (ISelectionMgr)doc.SelectionManager;
                if (selection.GetSelectedObjectCount2(-1) == 0)
                {
                    result.Found = false;
                    result.Description = "The ray reported a hit but nothing ended up selected.";
                    return result;
                }

                var face = selection.GetSelectedObject6(1, -1) as IFace2;
                if (face == null)
                {
                    result.Found = false;
                    result.Description = "The ray selected something that is not a face.";
                    return result;
                }

                result.AreaMm2 = face.GetArea() * 1e6;   // m2 -> mm2

                var normal = face.Normal as double[];
                if (normal != null && normal.Length >= 3)
                    result.NormalXyz = new[] { normal[0], normal[1], normal[2] };

                var c = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.Append($"Face selected, area {result.AreaMm2.ToString("0.#", c)} mm2");

                if (result.NormalXyz != null)
                {
                    sb.Append($", outward normal ({result.NormalXyz[0].ToString("0.##", c)}, " +
                              $"{result.NormalXyz[1].ToString("0.##", c)}, {result.NormalXyz[2].ToString("0.##", c)})");
                    sb.Append($" - it faces {DescribeNormal(result.NormalXyz)}");
                }

                sb.Append('.');
                result.Description = sb.ToString();
            }
            catch (Exception ex)
            {
                session.Log.Debug($"Could not describe the selected face: {ex.Message}");
                result.Description = "Face selected, but its properties could not be read.";
            }

            return result;
        }

        /// <summary>Turn a normal vector into words, for the agent's benefit.</summary>
        private static string DescribeNormal(double[] n)
        {
            double ax = Math.Abs(n[0]), ay = Math.Abs(n[1]), az = Math.Abs(n[2]);

            if (ax > ay && ax > az) return n[0] > 0 ? "+X" : "-X";
            if (ay > ax && ay > az) return n[1] > 0 ? "+Y" : "-Y";
            if (az > ax && az > ay) return n[2] > 0 ? "+Z" : "-Z";
            return "a direction that is not axis-aligned";
        }

        private static (double, double, double) ToVector(RayDirection d)
        {
            switch (d)
            {
                case RayDirection.PlusX: return (1, 0, 0);
                case RayDirection.MinusX: return (-1, 0, 0);
                case RayDirection.PlusY: return (0, 1, 0);
                case RayDirection.MinusY: return (0, -1, 0);
                case RayDirection.PlusZ: return (0, 0, 1);
                case RayDirection.MinusZ: return (0, 0, -1);
                default: throw new ArgumentOutOfRangeException(nameof(d));
            }
        }

        private static string Describe(RayDirection d)
        {
            switch (d)
            {
                case RayDirection.PlusX: return "+X";
                case RayDirection.MinusX: return "-X";
                case RayDirection.PlusY: return "+Y";
                case RayDirection.MinusY: return "-Y";
                case RayDirection.PlusZ: return "+Z";
                case RayDirection.MinusZ: return "-Z";
                default: return d.ToString();
            }
        }

        public static RayDirection ParseDirection(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "+x": case "x": return RayDirection.PlusX;
                case "-x": return RayDirection.MinusX;
                case "+y": case "y": return RayDirection.PlusY;
                case "-y": return RayDirection.MinusY;
                case "+z": case "z": return RayDirection.PlusZ;
                case "-z": return RayDirection.MinusZ;
                default:
                    throw new ArgumentException(
                        $"Unknown ray direction '{value}'. Use +x, -x, +y, -y, +z or -z.");
            }
        }

        /// <summary>
        /// Open a sketch on the currently selected face, and report where the
        /// sketch's own coordinate system landed in model space.
        ///
        /// That last part is not a nicety. A sketch on a face has its own
        /// origin and axes, chosen by SOLIDWORKS, and they are NOT the model
        /// origin. Without being told where sketch (0,0) is and which way
        /// sketch X and Y point, the agent is placing geometry blind and a
        /// window ends up in the wrong corner of the wall.
        /// </summary>
        public static string OpenSketchOnSelectedFace(SwSession session)
        {
            var doc = session.RequirePart();

            if (SketchOps.IsSketchActuallyOpen(doc))
                throw new InvalidOperationException(
                    "A sketch is already open. Close it (sw_sketch_close) before opening another.");

            var sm = (ISketchManager)doc.SketchManager;
            sm.InsertSketch(true);

            if (!SketchOps.IsSketchActuallyOpen(doc))
            {
                session.MarkSketchClosed();
                throw new InvalidOperationException(
                    "SOLIDWORKS did not open a sketch on that face. The selection may have been lost.");
            }

            session.MarkSketchOpened();
            return DescribeSketchFrame(session, doc);
        }

        /// <summary>Where sketch (0,0) sits in model space, and which way its axes point.</summary>
        private static string DescribeSketchFrame(SwSession session, IModelDoc2 doc)
        {
            try
            {
                var sm = (ISketchManager)doc.SketchManager;
                var sketch = sm.ActiveSketch;
                var data = sketch?.ModelToSketchTransform?.ArrayData as double[];

                if (data == null || data.Length < 12)
                    return "Sketch opened on the selected face. Its origin could not be determined - " +
                           "place geometry relative to features you can measure.";

                var c = CultureInfo.InvariantCulture;

                // Rows of the rotation are the images of the model basis
                // vectors - established empirically, see docs/findings.md.
                string sketchXInModel = AxisName(data[0], data[3], data[6]);
                string sketchYInModel = AxisName(data[1], data[4], data[7]);

                double ox = Units.LengthFromApi(data[9]);
                double oy = Units.LengthFromApi(data[10]);
                double oz = Units.LengthFromApi(data[11]);

                return
                    "Sketch opened on the selected face. Sketch (0,0) is at model " +
                    $"({ox.ToString("0.##", c)}, {oy.ToString("0.##", c)}, {oz.ToString("0.##", c)}) mm. " +
                    $"Sketch +X runs along model {sketchXInModel}, sketch +Y along model {sketchYInModel}. " +
                    "Place geometry in those sketch coordinates, not model coordinates.";
            }
            catch (Exception ex)
            {
                session.Log.Debug($"Could not describe the sketch frame: {ex.Message}");
                return "Sketch opened on the selected face.";
            }
        }

        private static string AxisName(double x, double y, double z)
        {
            double ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);
            if (ax > ay && ax > az) return x > 0 ? "+X" : "-X";
            if (ay > ax && ay > az) return y > 0 ? "+Y" : "-Y";
            if (az > ax && az > ay) return z > 0 ? "+Z" : "-Z";
            return "an off-axis direction";
        }
    }
}
