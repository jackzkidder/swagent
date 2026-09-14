using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SwAgent.Core.Infrastructure;

namespace SwAgent.Core.Batch
{
    /// <summary>Whether saving a file on this seat would upgrade it.</summary>
    public sealed class FormatCheck
    {
        /// <summary>False when the file's history could not be read or understood.</summary>
        public bool Known { get; set; }

        public bool WouldUpgrade { get; set; }
        public int? LastSavedYear { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>
    /// Which SOLIDWORKS release last saved a file.
    ///
    /// This matters more in a batch than anywhere else. Saving a file upgrades
    /// it to the running release, permanently, and older releases cannot open
    /// it again. One part upgraded by accident is an annoyance; a folder of
    /// them locks out every colleague and supplier still on the older release.
    /// </summary>
    public static class FileFormat
    {
        // Each history entry reads "<format>[<release>/<build>,...]", oldest
        // entry first - e.g. "18000[2024/232,2025/268]" is file format 18000,
        // written by 2024 build 232 and by 2025 build 268. The releases in the
        // brackets are what we compare; see docs/findings.md.
        private static readonly Regex ReleaseStamp = new Regex(@"(\d{4})/\d+", RegexOptions.CultureInvariant);

        /// <summary>The file's save history, oldest first, read without opening it.</summary>
        public static string[] History(ISldWorks sw, string path)
        {
            object raw = SwGuard.Com("VersionHistory", () => sw.VersionHistory(path));

            if (raw is string[] strings) return strings;

            if (raw is object[] objects)
                return objects.Select(o => o?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

            return Array.Empty<string>();
        }

        /// <summary>The release year of the running seat: revision 33 is SOLIDWORKS 2025.</summary>
        public static int SeatYear(ISldWorks sw)
        {
            string revision = sw.RevisionNumber() ?? string.Empty;
            int dot = revision.IndexOf('.');
            string major = dot > 0 ? revision.Substring(0, dot) : revision;

            return int.TryParse(major, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                ? 1992 + n
                : 0;
        }

        /// <summary>The newest release named in one history entry, or null if it names none.</summary>
        public static int? LatestRelease(string entry)
        {
            if (string.IsNullOrEmpty(entry)) return null;

            int? newest = null;
            foreach (Match match in ReleaseStamp.Matches(entry))
            {
                int year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                if (year < 1995 || year > 2100) continue;
                if (newest == null || year > newest.Value) newest = year;
            }

            return newest;
        }

        public static FormatCheck Check(ISldWorks sw, string path)
        {
            string[] history = History(sw, path);
            if (history.Length == 0)
                return new FormatCheck { Known = false, Detail = "its save history could not be read" };

            string last = history[history.Length - 1];
            int? year = LatestRelease(last);
            if (year == null)
                return new FormatCheck { Known = false, Detail = $"its save history entry '{last}' was not understood" };

            int seat = SeatYear(sw);
            if (seat == 0)
            {
                return new FormatCheck
                {
                    Known = false,
                    LastSavedYear = year,
                    Detail = "the running SOLIDWORKS release could not be determined",
                };
            }

            return new FormatCheck
            {
                Known = true,
                LastSavedYear = year,
                WouldUpgrade = year.Value < seat,
                Detail = $"it was last saved by SOLIDWORKS {year.Value}",
            };
        }
    }
}
