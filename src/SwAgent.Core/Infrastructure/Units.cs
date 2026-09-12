using System;

namespace SwAgent.Core.Infrastructure
{
    /// <summary>
    /// The single conversion chokepoint between the tool boundary and the
    /// SOLIDWORKS API.
    ///
    /// Tools speak millimetres and degrees, because that is what the user and
    /// the model think in. The SOLIDWORKS API speaks metres and radians,
    /// always, regardless of the document's unit system. A 60mm plate is 0.06.
    ///
    /// Every length and angle crossing into a COM call goes through here. If
    /// you find yourself writing "/ 1000.0" anywhere else in this repo, that is
    /// the bug: the API returns success for a part built 1000x too large, so
    /// nothing downstream will catch it for you.
    /// </summary>
    public static class Units
    {
        /// <summary>Largest length we will hand to SOLIDWORKS, in mm (10 m).</summary>
        public const double MaxLengthMm = 10000.0;

        /// <summary>
        /// Smallest non-zero length, in mm. Below this SOLIDWORKS produces
        /// zero-thickness geometry and rebuild failures rather than an error.
        /// </summary>
        public const double MinLengthMm = 1e-4;

        /// <summary>Convert a length in millimetres to the API's metres.</summary>
        /// <param name="mm">Length in millimetres.</param>
        /// <param name="paramName">Tool parameter name, for the error message.</param>
        /// <param name="allowZero">
        /// True for offsets and coordinates, where zero is meaningful. False for
        /// extents (depth, radius, width), where zero is always a mistake.
        /// </param>
        public static double LengthToApi(double mm, string paramName, bool allowZero = false)
        {
            if (double.IsNaN(mm) || double.IsInfinity(mm))
                throw new UnitRangeException(paramName, mm, "must be a finite number");

            double magnitude = Math.Abs(mm);

            if (!allowZero && magnitude < MinLengthMm)
                throw new UnitRangeException(paramName, mm,
                    $"must be at least {MinLengthMm}mm; zero or near-zero produces degenerate geometry");

            if (magnitude > MaxLengthMm)
                throw new UnitRangeException(paramName, mm,
                    $"exceeds the {MaxLengthMm}mm limit. If you meant {mm}m, pass {mm * 1000.0} " +
                    "(this tool takes millimetres)");

            return mm / 1000.0;
        }

        /// <summary>
        /// Convert a coordinate in millimetres to metres. Zero is legal; a
        /// sketch point at the origin is ordinary.
        /// </summary>
        public static double CoordToApi(double mm, string paramName)
        {
            return LengthToApi(mm, paramName, allowZero: true);
        }

        /// <summary>Convert an angle in degrees to the API's radians.</summary>
        public static double AngleToApi(double degrees, string paramName, double maxAbsDegrees = 360.0)
        {
            if (double.IsNaN(degrees) || double.IsInfinity(degrees))
                throw new UnitRangeException(paramName, degrees, "must be a finite number");

            if (Math.Abs(degrees) > maxAbsDegrees)
                throw new UnitRangeException(paramName, degrees,
                    $"must be within +/-{maxAbsDegrees} degrees (this tool takes degrees, not radians)");

            return degrees * Math.PI / 180.0;
        }

        /// <summary>Convert a length coming back from the API into millimetres.</summary>
        public static double LengthFromApi(double metres) => metres * 1000.0;

        /// <summary>Convert an angle coming back from the API into degrees.</summary>
        public static double AngleFromApi(double radians) => radians * 180.0 / Math.PI;

        /// <summary>Convert a mass coming back from the API (kg) into grams.</summary>
        public static double MassFromApiGrams(double kilograms) => kilograms * 1000.0;

        /// <summary>Convert a volume coming back from the API (m^3) into mm^3.</summary>
        public static double VolumeFromApiMm3(double cubicMetres) => cubicMetres * 1e9;

        /// <summary>Convert a volume coming back from the API (m^3) into cm^3.</summary>
        public static double VolumeFromApiCm3(double cubicMetres) => cubicMetres * 1e6;
    }

    /// <summary>
    /// A tool argument was outside the range we are willing to model. Thrown at
    /// the boundary so the agent gets a readable message instead of SOLIDWORKS
    /// silently building something absurd.
    /// </summary>
    public class UnitRangeException : ArgumentException
    {
        public string Parameter { get; }
        public double Value { get; }

        public UnitRangeException(string parameter, double value, string requirement)
            : base($"'{parameter}' = {value}: {requirement}.")
        {
            Parameter = parameter;
            Value = value;
        }
    }
}
