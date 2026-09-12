using System;
using System.Globalization;
using System.Threading;

namespace SwAgent.Agent
{
    /// <summary>
    /// Tracks what the session has cost so far, in dollars.
    ///
    /// This exists because of a specific anxiety: BYOK means unbounded spend
    /// against the user's own card, and an engineer watching an agent work with
    /// no idea what it is costing will stop using it. A running number kills
    /// that anxiety outright, and it costs us nothing to display, because the
    /// usage figures come back on every response.
    ///
    /// The rates are Anthropic's published first-party API rates and are
    /// therefore an estimate: they are not a bill, and cached reads in
    /// particular are cheaper than the input rate used here. Present it as an
    /// estimate, never as an invoice.
    /// </summary>
    public sealed class CostMeter
    {
        private long _inputTokens;
        private long _outputTokens;
        private long _cacheReadTokens;
        private int _requests;

        public CostMeter(ModelPricing pricing)
        {
            Pricing = pricing ?? throw new ArgumentNullException(nameof(pricing));
        }

        public ModelPricing Pricing { get; }

        public long InputTokens => Interlocked.Read(ref _inputTokens);
        public long OutputTokens => Interlocked.Read(ref _outputTokens);
        public long CacheReadTokens => Interlocked.Read(ref _cacheReadTokens);
        public int Requests => _requests;

        public void Record(long inputTokens, long outputTokens, long cacheReadTokens = 0)
        {
            Interlocked.Add(ref _inputTokens, inputTokens);
            Interlocked.Add(ref _outputTokens, outputTokens);
            Interlocked.Add(ref _cacheReadTokens, cacheReadTokens);
            Interlocked.Increment(ref _requests);
        }

        /// <summary>Estimated cost of the session so far, in US dollars.</summary>
        public decimal EstimatedCostUsd
        {
            get
            {
                decimal input = InputTokens / 1_000_000m * Pricing.InputPerMillionUsd;
                decimal output = OutputTokens / 1_000_000m * Pricing.OutputPerMillionUsd;
                decimal cached = CacheReadTokens / 1_000_000m * Pricing.CacheReadPerMillionUsd;
                return input + output + cached;
            }
        }

        /// <summary>A short string for the status line.</summary>
        public string Describe()
        {
            decimal cost = EstimatedCostUsd;
            var c = CultureInfo.InvariantCulture;

            // Below a cent, a dollar figure reads as "free" and is not
            // informative. Say so in words instead.
            string money = cost < 0.01m
                ? "under $0.01"
                : "$" + cost.ToString("0.00", c);

            return $"{money} estimated this session " +
                   $"({Requests} request(s), {InputTokens:N0} in / {OutputTokens:N0} out)";
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _inputTokens, 0);
            Interlocked.Exchange(ref _outputTokens, 0);
            Interlocked.Exchange(ref _cacheReadTokens, 0);
            _requests = 0;
        }
    }

    /// <summary>Published per-million-token rates for a model.</summary>
    public sealed class ModelPricing
    {
        public ModelPricing(string modelId, decimal inputPerMillionUsd, decimal outputPerMillionUsd, decimal cacheReadPerMillionUsd)
        {
            ModelId = modelId;
            InputPerMillionUsd = inputPerMillionUsd;
            OutputPerMillionUsd = outputPerMillionUsd;
            CacheReadPerMillionUsd = cacheReadPerMillionUsd;
        }

        public string ModelId { get; }
        public decimal InputPerMillionUsd { get; }
        public decimal OutputPerMillionUsd { get; }
        public decimal CacheReadPerMillionUsd { get; }

        /// <summary>
        /// Claude Opus 5 - the default. Rates are Anthropic's published
        /// first-party API pricing; re-check them before showing anything that
        /// looks like a bill.
        /// </summary>
        public static readonly ModelPricing Opus5 =
            new ModelPricing(ModelIds.Opus5, inputPerMillionUsd: 5.00m, outputPerMillionUsd: 25.00m, cacheReadPerMillionUsd: 0.50m);

        /// <summary>Claude Sonnet 5 - cheaper, for users who ask for it.</summary>
        public static readonly ModelPricing Sonnet5 =
            new ModelPricing(ModelIds.Sonnet5, inputPerMillionUsd: 2.00m, outputPerMillionUsd: 10.00m, cacheReadPerMillionUsd: 0.20m);

        public static ModelPricing For(string modelId)
        {
            if (string.Equals(modelId, ModelIds.Sonnet5, StringComparison.OrdinalIgnoreCase)) return Sonnet5;
            if (string.Equals(modelId, ModelIds.Opus5, StringComparison.OrdinalIgnoreCase)) return Opus5;

            // Unknown model: price it as the most expensive one we know, so the
            // estimate errs toward over-reporting rather than under-reporting.
            return new ModelPricing(modelId, Opus5.InputPerMillionUsd, Opus5.OutputPerMillionUsd, Opus5.CacheReadPerMillionUsd);
        }
    }

    /// <summary>Model identifiers, in one place.</summary>
    public static class ModelIds
    {
        /// <summary>The default. Capable enough to reason about geometry it cannot directly see.</summary>
        public const string Opus5 = "claude-opus-5";

        /// <summary>Cheaper alternative, for users who ask for it.</summary>
        public const string Sonnet5 = "claude-sonnet-5";
    }
}
