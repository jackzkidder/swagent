using System;
using System.Globalization;

namespace SwAgent.AddIn
{
    /// <summary>
    /// Decides whether the SOLIDWORKS we have been loaded into is one we
    /// support, and says so in words a person can act on.
    ///
    /// The alternative is worse than it sounds: without a gate, an unsupported
    /// seat does not fail at connect. It fails later, as a COM error from deep
    /// inside a feature call, in a message that names neither the add-in nor
    /// the version. That becomes a support ticket that is expensive to
    /// diagnose and trivially preventable.
    /// </summary>
    internal static class SwVersionGate
    {
        /// <summary>
        /// Interop major version to marketing year. SOLIDWORKS numbers its
        /// interops sequentially from an offset that has held for many years.
        /// </summary>
        private const int MajorVersionOfSw2018 = 26;
        private const int YearOfMajorVersion2018 = 2018;

        /// <summary>
        /// The oldest interop major version this build can talk to.
        ///
        /// This is 33 (SOLIDWORKS 2025) because that is the interop the add-in
        /// is compiled against, and an older SOLIDWORKS cannot load a newer
        /// interop. Newer seats are fine: newer SOLIDWORKS loads older
        /// interops, which is why there is no upper bound here.
        ///
        /// Lowering this is not a code change - it requires rebuilding against
        /// the older interop assemblies and re-running the reference parts
        /// against that seat. See the deviation note in README.
        /// </summary>
        public const int MinimumSupportedMajor = 33;

        /// <summary>
        /// The newest version the reference suite has actually been run
        /// against. Above this we still load, but we say so in the log rather
        /// than claiming tested support.
        /// </summary>
        public const int HighestVerifiedMajor = 33;

        public sealed class Result
        {
            public bool IsSupported { get; set; }
            public int Major { get; set; }
            public int Year { get; set; }
            public string RawRevision { get; set; }
            public string Message { get; set; }
            public bool IsVerified { get; set; }
        }

        /// <summary>
        /// Classify a revision string as returned by ISldWorks.RevisionNumber(),
        /// e.g. "33.5.0" for SOLIDWORKS 2025 SP5.
        /// </summary>
        public static Result Evaluate(string revisionNumber)
        {
            var result = new Result { RawRevision = revisionNumber };

            int major = ParseMajor(revisionNumber);
            if (major <= 0)
            {
                // Unreadable version. Refusing here would be worse than trying:
                // a version string we cannot parse is far more likely to be a
                // format change than an ancient seat.
                result.IsSupported = true;
                result.IsVerified = false;
                result.Major = 0;
                result.Message =
                    $"Could not read the SOLIDWORKS version ('{revisionNumber}'). Continuing anyway.";
                return result;
            }

            result.Major = major;
            result.Year = YearOf(major);

            if (major < MinimumSupportedMajor)
            {
                result.IsSupported = false;
                result.Message =
                    $"SwAgent requires SOLIDWORKS {YearOf(MinimumSupportedMajor)} or newer. " +
                    $"This is SOLIDWORKS {result.Year} (version {revisionNumber}). " +
                    "The add-in has not been loaded.";
                return result;
            }

            result.IsSupported = true;
            result.IsVerified = major <= HighestVerifiedMajor;
            result.Message = result.IsVerified
                ? $"SOLIDWORKS {result.Year} (version {revisionNumber})."
                : $"SOLIDWORKS {result.Year} (version {revisionNumber}). This is newer than the " +
                  $"version SwAgent has been tested against ({YearOf(HighestVerifiedMajor)}); " +
                  "it should work, but please report anything that does not.";

            return result;
        }

        private static int ParseMajor(string revision)
        {
            if (string.IsNullOrWhiteSpace(revision)) return 0;

            string head = revision.Split('.')[0].Trim();
            return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out int major)
                ? major
                : 0;
        }

        private static int YearOf(int major) => YearOfMajorVersion2018 + (major - MajorVersionOfSw2018);
    }
}
