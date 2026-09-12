using System;
using System.Globalization;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Session;

namespace SwAgent.Core.Inspection
{
    /// <summary>
    /// Mass, volume and bounding box - the numbers that go back to the model
    /// alongside every screenshot.
    ///
    /// These are the ground truth in the verification round-trip. The model does
    /// not see 3D; it sees a 2D projection and some scalars. "Looks about right"
    /// cannot distinguish 24 cm3 from 24,000 cm3, and a bounding box can, which
    /// is what catches a metres-for-millimetres error on the very first feature
    /// instead of twenty features later.
    /// </summary>
    public static class PartMeasure
    {
        /// <summary>Read mass properties and the bounding box of the active part.</summary>
        public static PartMeasurements OfActivePart(SwSession session)
        {
            var doc = session.RequirePart();
            var part = (IPartDoc)doc;

            // --- Mass properties -------------------------------------------------
            var massProp = (IMassProperty)doc.Extension.CreateMassProperty();
            if (massProp == null)
                throw new InvalidOperationException("Could not create a mass property evaluator for this part.");

            // System units means kilograms, cubic metres and metres, whatever
            // the document's display units happen to be set to. Without this,
            // the numbers we send the model depend on the user's template.
            massProp.UseSystemUnits = true;

            double massKg = massProp.Mass;
            double volumeM3 = massProp.Volume;
            double surfaceAreaM2 = massProp.SurfaceArea;
            double densityKgM3 = massProp.Density;

            double[] com = { 0, 0, 0 };
            try
            {
                var comRaw = massProp.CenterOfMass as double[];
                if (comRaw != null && comRaw.Length >= 3) com = comRaw;
            }
            catch (Exception ex)
            {
                session.Log.Debug($"Measure: centre of mass unavailable: {ex.Message}");
            }

            // --- Bounding box ----------------------------------------------------
            // GetPartBox(true) means "no conversion": values come back in system
            // units (metres) as xmin, ymin, zmin, xmax, ymax, zmax.
            double[] box = null;
            try
            {
                box = part.GetPartBox(true) as double[];
            }
            catch (Exception ex)
            {
                session.Log.Debug($"Measure: bounding box unavailable: {ex.Message}");
            }

            bool hasBody = volumeM3 > 0;

            return new PartMeasurements(
                hasBody: hasBody,
                massGrams: Units.MassFromApiGrams(massKg),
                volumeCm3: Units.VolumeFromApiCm3(volumeM3),
                surfaceAreaCm2: surfaceAreaM2 * 1e4,
                densityKgPerM3: densityKgM3,
                centreOfMassMm: new[]
                {
                    Units.LengthFromApi(com[0]),
                    Units.LengthFromApi(com[1]),
                    Units.LengthFromApi(com[2])
                },
                boundingBoxMm: box == null || box.Length < 6 ? null : new[]
                {
                    Units.LengthFromApi(box[0]), Units.LengthFromApi(box[1]), Units.LengthFromApi(box[2]),
                    Units.LengthFromApi(box[3]), Units.LengthFromApi(box[4]), Units.LengthFromApi(box[5])
                });
        }

        /// <summary>
        /// The material assigned to the active part, or null. Worth reporting
        /// because mass is meaningless until it is set.
        /// </summary>
        public static string ActiveMaterial(SwSession session)
        {
            try
            {
                var doc = session.RequirePart();
                var part = (IPartDoc)doc;
                string database;
                string name = part.GetMaterialPropertyName2("", out database);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Measurements in the units the tool boundary speaks: millimetres, grams,
    /// cubic centimetres.
    /// </summary>
    public sealed class PartMeasurements
    {
        public PartMeasurements(
            bool hasBody, double massGrams, double volumeCm3, double surfaceAreaCm2,
            double densityKgPerM3, double[] centreOfMassMm, double[] boundingBoxMm)
        {
            HasBody = hasBody;
            MassGrams = massGrams;
            VolumeCm3 = volumeCm3;
            SurfaceAreaCm2 = surfaceAreaCm2;
            DensityKgPerM3 = densityKgPerM3;
            CentreOfMassMm = centreOfMassMm;
            BoundingBoxMm = boundingBoxMm;
        }

        /// <summary>False when the part has no solid body yet.</summary>
        public bool HasBody { get; }

        public double MassGrams { get; }
        public double VolumeCm3 { get; }
        public double SurfaceAreaCm2 { get; }
        public double DensityKgPerM3 { get; }

        /// <summary>X, Y, Z in millimetres.</summary>
        public double[] CentreOfMassMm { get; }

        /// <summary>xmin, ymin, zmin, xmax, ymax, zmax in millimetres. Null if unavailable.</summary>
        public double[] BoundingBoxMm { get; }

        /// <summary>
        /// The three bounding box dimensions, largest first.
        ///
        /// Reference tests assert on these rather than on X/Y/Z, because which
        /// global axis a feature lands on depends on the user's part template -
        /// a template with rotated reference planes builds a dimensionally
        /// correct part on different axes. Asserting X==60 would fail on a
        /// perfectly good part, and asserting the dimensions catches the error
        /// that actually matters: wrong size.
        /// </summary>
        public double[] SortedDimsMm
        {
            get
            {
                var dims = new[] { BboxWidthMm, BboxHeightMm, BboxDepthMm };
                Array.Sort(dims);
                Array.Reverse(dims);
                return dims;
            }
        }

        public double BboxWidthMm => BoundingBoxMm == null ? 0 : BoundingBoxMm[3] - BoundingBoxMm[0];
        public double BboxHeightMm => BoundingBoxMm == null ? 0 : BoundingBoxMm[4] - BoundingBoxMm[1];
        public double BboxDepthMm => BoundingBoxMm == null ? 0 : BoundingBoxMm[5] - BoundingBoxMm[2];

        /// <summary>
        /// The one-line summary that accompanies every feature result. Kept
        /// terse on purpose: it is repeated after every operation and the
        /// context window is finite.
        /// </summary>
        public string Describe()
        {
            if (!HasBody) return "No solid body yet (volume is zero).";

            var sb = new StringBuilder();
            var c = CultureInfo.InvariantCulture;

            if (BoundingBoxMm != null)
            {
                sb.Append("Bbox ")
                  .Append(BboxWidthMm.ToString("0.###", c)).Append("x")
                  .Append(BboxHeightMm.ToString("0.###", c)).Append("x")
                  .Append(BboxDepthMm.ToString("0.###", c)).Append("mm. ");
            }

            sb.Append("Volume ").Append(VolumeCm3.ToString("0.###", c)).Append(" cm3. ");
            sb.Append("Mass ").Append(MassGrams.ToString("0.###", c)).Append(" g.");

            return sb.ToString();
        }
    }
}
