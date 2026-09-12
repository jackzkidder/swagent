using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SwAgent.Core.Inspection;
using SwAgent.Core.Session;

namespace SwAgent.Core.Intent
{
    /// <summary>
    /// What the agent committed to before it started building.
    ///
    /// The problem this solves: the verification round-trip compares
    /// measurements against the request, and for anything open-ended - "a small
    /// house" - there is nothing to compare against. So a wrong part rebuilds
    /// cleanly, measures fine against no expectation, and gets reported as
    /// finished. That happened.
    ///
    /// A prediction made BEFORE the first feature turns any request, however
    /// vague, into a test with a pass condition. It also changes the incentive:
    /// the cheapest way to satisfy "make a house" is a block with holes in it,
    /// but the cheapest way to satisfy an envelope and a volume band the agent
    /// itself wrote down is to actually build the thing.
    ///
    /// EVERY TERM HERE MUST BE MACHINE-CHECKABLE. A contract clause nobody
    /// verifies is a compliance ritual, and a ritual is worse than nothing
    /// because it looks like rigour. Minimum wall thickness is deliberately
    /// absent for exactly that reason - we cannot measure it yet, so it is not
    /// something the agent gets to promise.
    /// </summary>
    public sealed class DesignIntent
    {
        /// <summary>Plain description of what is being built, for the report.</summary>
        public string Summary { get; set; }

        /// <summary>Target overall size in millimetres, largest dimension first.</summary>
        public double EnvelopeLongMm { get; set; }
        public double EnvelopeMidMm { get; set; }
        public double EnvelopeShortMm { get; set; }

        /// <summary>The band the finished volume must land in, cm3.</summary>
        public double VolumeMinCm3 { get; set; }
        public double VolumeMaxCm3 { get; set; }

        /// <summary>Fewest features the finished part should have. Zero to not check.</summary>
        public int MinFeatures { get; set; }

        public DateTime DeclaredUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// How far the envelope may drift before it counts as wrong. A couple
        /// of percent covers rounding and fillet effects; more than that is a
        /// different part.
        /// </summary>
        public const double EnvelopeTolerance = 0.02;

        /// <summary>
        /// Widest volume band we will accept, as a fraction of the midpoint.
        ///
        /// This is the anti-gaming rule and it is the whole reason the contract
        /// has any force. "Somewhere between 100 and 900 cm3" is trivially
        /// satisfiable and means nothing, so a band wider than +/-15% is
        /// rejected the same way a 60,000 mm rectangle is.
        /// </summary>
        public const double MaxVolumeBandFraction = 0.30;

        /// <summary>
        /// Check the contract is worth signing, before anything is built.
        /// Throws with a readable reason if not.
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Summary))
                throw new ArgumentException("The intent needs a one-line description of the part.");

            CheckDimension(EnvelopeLongMm, "envelope_long_mm");
            CheckDimension(EnvelopeMidMm, "envelope_mid_mm");
            CheckDimension(EnvelopeShortMm, "envelope_short_mm");

            if (EnvelopeLongMm < EnvelopeMidMm || EnvelopeMidMm < EnvelopeShortMm)
            {
                throw new ArgumentException(
                    $"The envelope must be given largest to smallest, got {EnvelopeLongMm} / " +
                    $"{EnvelopeMidMm} / {EnvelopeShortMm} mm.");
            }

            if (VolumeMinCm3 <= 0)
                throw new ArgumentException("volume_min_cm3 must be greater than zero.");

            if (VolumeMaxCm3 <= VolumeMinCm3)
                throw new ArgumentException(
                    $"volume_max_cm3 ({VolumeMaxCm3}) must be greater than volume_min_cm3 ({VolumeMinCm3}).");

            double midpoint = (VolumeMaxCm3 + VolumeMinCm3) / 2.0;
            double band = (VolumeMaxCm3 - VolumeMinCm3) / midpoint;

            if (band > MaxVolumeBandFraction)
            {
                throw new ArgumentException(
                    $"The volume band {VolumeMinCm3}-{VolumeMaxCm3} cm3 is +/-{band / 2 * 100:0.#}%, which is " +
                    $"too loose to mean anything. Tighten it to within +/-{MaxVolumeBandFraction / 2 * 100:0}% " +
                    "of your estimate. Work out the volume from the dimensions you intend to build, rather " +
                    "than guessing a wide range that anything would satisfy.");
            }

            // A bounding box can never be smaller than the volume inside it.
            double envelopeCm3 = EnvelopeLongMm * EnvelopeMidMm * EnvelopeShortMm / 1000.0;
            if (VolumeMinCm3 > envelopeCm3 * 1.001)
            {
                throw new ArgumentException(
                    $"volume_min_cm3 ({VolumeMinCm3}) exceeds the volume of the envelope itself " +
                    $"({envelopeCm3:0.#} cm3). One of the two numbers is wrong.");
            }
        }

        private static void CheckDimension(double mm, string name)
        {
            if (double.IsNaN(mm) || double.IsInfinity(mm) || mm <= 0)
                throw new ArgumentException($"{name} must be a positive number of millimetres.");

            if (mm > Infrastructure.Units.MaxLengthMm)
                throw new ArgumentException($"{name} of {mm} mm is beyond what this tool will model.");
        }

        public string Describe()
        {
            var c = CultureInfo.InvariantCulture;
            return $"{Summary} | envelope {EnvelopeLongMm.ToString("0.#", c)} x " +
                   $"{EnvelopeMidMm.ToString("0.#", c)} x {EnvelopeShortMm.ToString("0.#", c)} mm | " +
                   $"volume {VolumeMinCm3.ToString("0.##", c)}-{VolumeMaxCm3.ToString("0.##", c)} cm3" +
                   (MinFeatures > 0 ? $" | at least {MinFeatures} features" : "");
        }
    }

    /// <summary>One term of the contract, and whether reality honoured it.</summary>
    public sealed class IntentFinding
    {
        public string Term { get; set; }
        public bool Passed { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>The verdict.</summary>
    public sealed class IntentCheck
    {
        public bool Passed { get; set; }
        public List<IntentFinding> Findings { get; set; } = new List<IntentFinding>();
        public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Passed
                ? "The part matches the intent declared before building."
                : "THE PART DOES NOT MATCH THE INTENT DECLARED BEFORE BUILDING.");

            foreach (var f in Findings)
                sb.AppendLine($"  [{(f.Passed ? "ok" : "MISMATCH")}] {f.Term}: {f.Detail}");

            if (!Passed)
            {
                sb.Append("Fix the part, or explain to the user precisely which commitment was missed and ");
                sb.Append("why. Do not quietly restate the intent to match what was built.");
            }

            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Holds the contract for the part currently being built, and the last
    /// verdict on it.
    ///
    /// Deliberately locked once declared. Letting the agent revise its
    /// prediction after seeing the result turns a commitment into a
    /// post-hoc description, which is the failure mode this whole mechanism
    /// exists to prevent.
    /// </summary>
    public sealed class IntentStore
    {
        public DesignIntent Current { get; private set; }
        public IntentCheck LastCheck { get; private set; }

        public bool HasIntent => Current != null;

        /// <summary>True when an intent exists and its most recent check passed.</summary>
        public bool IsSatisfied => Current != null && LastCheck != null && LastCheck.Passed;

        /// <summary>True when an intent was declared but never verified.</summary>
        public bool IsUnverified => Current != null && LastCheck == null;

        public void Declare(DesignIntent intent)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            intent.Validate();

            if (Current != null)
            {
                throw new InvalidOperationException(
                    "An intent has already been declared for this part: " + Current.Describe() +
                    ". It cannot be revised after building has started - that would turn a prediction " +
                    "into a description of whatever happened to come out. Start a new part to declare " +
                    "a different intent, or build the part you committed to.");
            }

            Current = intent;
            LastCheck = null;
        }

        /// <summary>Forget everything. Called when a new part starts.</summary>
        public void Clear()
        {
            Current = null;
            LastCheck = null;
        }

        /// <summary>Measure the active part and compare it against the contract.</summary>
        public IntentCheck Check(SwSession session)
        {
            if (Current == null)
                throw new InvalidOperationException(
                    "No intent has been declared, so there is nothing to check against. " +
                    "Declare one with sw_declare_intent before building.");

            var m = PartMeasure.OfActivePart(session);
            var check = new IntentCheck();
            var c = CultureInfo.InvariantCulture;

            // --- envelope ---
            if (!m.HasBody || m.BoundingBoxMm == null)
            {
                check.Findings.Add(new IntentFinding
                {
                    Term = "envelope",
                    Passed = false,
                    Detail = "the part has no solid body yet",
                });
            }
            else
            {
                var actual = m.SortedDimsMm;
                var expected = new[] { EnvelopeSorted(0), EnvelopeSorted(1), EnvelopeSorted(2) };
                string[] labels = { "longest", "middle", "shortest" };

                for (int i = 0; i < 3; i++)
                {
                    double allowed = Math.Max(expected[i] * DesignIntent.EnvelopeTolerance, 0.5);
                    bool ok = Math.Abs(actual[i] - expected[i]) <= allowed;

                    check.Findings.Add(new IntentFinding
                    {
                        Term = $"envelope {labels[i]}",
                        Passed = ok,
                        Detail = $"declared {expected[i].ToString("0.#", c)} mm, built " +
                                 $"{actual[i].ToString("0.#", c)} mm",
                    });
                }
            }

            // --- volume ---
            bool volumeOk = m.HasBody
                && m.VolumeCm3 >= Current.VolumeMinCm3
                && m.VolumeCm3 <= Current.VolumeMaxCm3;

            check.Findings.Add(new IntentFinding
            {
                Term = "volume",
                Passed = volumeOk,
                Detail = $"declared {Current.VolumeMinCm3.ToString("0.##", c)}-" +
                         $"{Current.VolumeMaxCm3.ToString("0.##", c)} cm3, built " +
                         $"{m.VolumeCm3.ToString("0.##", c)} cm3" +
                         (volumeOk ? "" : DescribeVolumeMiss(m.VolumeCm3)),
            });

            // --- feature count ---
            if (Current.MinFeatures > 0)
            {
                int features = FeatureTree.Read(session).Count;
                check.Findings.Add(new IntentFinding
                {
                    Term = "features",
                    Passed = features >= Current.MinFeatures,
                    Detail = $"declared at least {Current.MinFeatures}, built {features}",
                });
            }

            check.Passed = check.Findings.TrueForAll(f => f.Passed);
            LastCheck = check;
            return check;
        }

        /// <summary>
        /// Say which way the volume went. "Too much material" and "too little"
        /// point at completely different mistakes - an uncut opening versus a
        /// cut that went through the far wall.
        /// </summary>
        private string DescribeVolumeMiss(double actual)
        {
            if (actual > Current.VolumeMaxCm3)
            {
                return ". Too much material: something that should have been removed was not, " +
                       "or an opening was never cut.";
            }

            return ". Too little material: a cut removed more than intended - check whether it went " +
                   "through more walls than it should have.";
        }

        private double EnvelopeSorted(int index)
        {
            var dims = new[] { Current.EnvelopeLongMm, Current.EnvelopeMidMm, Current.EnvelopeShortMm };
            Array.Sort(dims);
            Array.Reverse(dims);
            return dims[index];
        }
    }
}
