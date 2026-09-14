using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SwAgent.Core.Infrastructure;

namespace SwAgent.Core.Batch
{
    public enum BatchOperation
    {
        SetProperty,
        SetMaterial,
        Export
    }

    public enum BatchRowStatus
    {
        /// <summary>Will change when applied.</summary>
        Ready,

        /// <summary>Already in the requested state. Applying leaves the file alone rather than rewriting it for nothing.</summary>
        Unchanged,

        /// <summary>Cannot or must not be changed. Reason says why.</summary>
        Skipped,

        Applied,
        Failed
    }

    public enum BatchPlanState
    {
        Pending,
        Applying,
        Applied,
        Discarded
    }

    /// <summary>What the agent asked to plan.</summary>
    public sealed class BatchRequest
    {
        public BatchOperation Operation { get; set; }
        public string Folder { get; set; }
        public string Pattern { get; set; } = "*";
        public bool Recursive { get; set; }

        public string PropertyName { get; set; }
        public string PropertyValue { get; set; }

        public string Material { get; set; }
        public string MaterialDatabase { get; set; } = "";

        /// <summary>With the dot, lower case, once validated.</summary>
        public string ExportExtension { get; set; }
        public string OutputFolder { get; set; }
        public bool Overwrite { get; set; }

        public bool AllowVersionUpgrade { get; set; }
    }

    /// <summary>One file in a plan.</summary>
    public sealed class BatchRow
    {
        public IndexedFile File { get; set; }
        public BatchRowStatus Status { get; set; }

        /// <summary>The value now, or null when there is none. For display.</summary>
        public string Current { get; set; }

        /// <summary>The value or output file name it will get. For display.</summary>
        public string Proposed { get; set; }

        public string OutputPath { get; set; }

        /// <summary>Why the file is skipped.</summary>
        public string Reason { get; set; }

        /// <summary>Something the user should know before applying, on a row that will still change.</summary>
        public string Warning { get; set; }

        /// <summary>What applying actually did.</summary>
        public string Result { get; set; }
    }

    /// <summary>
    /// A previewed batch change, file by file.
    ///
    /// Two audiences, two renderings. The user sees every file name and value
    /// in the panel. The model gets counts and reasons by row number only: the
    /// setup screen promises that no file names are sent to Anthropic, and a
    /// folder listing is exactly the kind of thing that promise is about.
    /// </summary>
    public sealed class BatchPlan
    {
        internal BatchPlan(BatchRequest request)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
        }

        public string Id { get; internal set; }
        public BatchRequest Request { get; }
        public List<BatchRow> Rows { get; } = new List<BatchRow>();
        public DateTime CreatedUtc { get; internal set; } = DateTime.UtcNow;
        public BatchPlanState State { get; internal set; } = BatchPlanState.Pending;

        /// <summary>Property and material changes save the source files; exports only read them.</summary>
        public bool WritesSourceFiles => Request.Operation != BatchOperation.Export;

        public int CountOf(BatchRowStatus status) => Rows.Count(r => r.Status == status);

        public string Title
        {
            get
            {
                switch (Request.Operation)
                {
                    case BatchOperation.SetProperty:
                        return $"Set property '{Request.PropertyName}' to '{Request.PropertyValue}'";
                    case BatchOperation.SetMaterial:
                        return $"Set material to '{Request.Material}'";
                    default:
                        return $"Export {Request.ExportExtension?.TrimStart('.').ToUpperInvariant()} files to {Request.OutputFolder}";
                }
            }
        }

        /// <summary>The dry run as the model sees it. No file names, no current values.</summary>
        public string DescribeForModel(int maxListed = 40)
        {
            int ready = CountOf(BatchRowStatus.Ready);
            int unchanged = CountOf(BatchRowStatus.Unchanged);
            int skipped = CountOf(BatchRowStatus.Skipped);
            int warned = Rows.Count(r => r.Status == BatchRowStatus.Ready && r.Warning != null);

            var sb = new StringBuilder();
            sb.AppendLine($"Batch plan {Id} (dry run): {Title}.");
            sb.Append($"{Rows.Count} matching file(s): {ready} will change, {unchanged} already correct, {skipped} skipped");
            sb.AppendLine(warned > 0 ? $", {warned} with a warning." : ".");

            if (WritesSourceFiles && ready > 0)
                sb.AppendLine("Applying saves each changed file in place. Saved files cannot be undone from here.");

            var notable = Rows
                .Select((row, i) => (row, i))
                .Where(x => x.row.Status == BatchRowStatus.Skipped || x.row.Warning != null)
                .ToList();

            foreach (var (row, i) in notable.Take(maxListed))
            {
                string what = row.Status == BatchRowStatus.Skipped
                    ? "skipped - " + Redact(row.Reason, row.File)
                    : "warning - " + Redact(row.Warning, row.File);
                sb.AppendLine($"  #{i + 1} {row.File.DocType.ToString().ToLowerInvariant()}: {what}");
            }

            if (notable.Count > maxListed)
                sb.AppendLine($"  ... and {notable.Count - maxListed} more.");

            sb.AppendLine();
            sb.Append(
                "Nothing has been written. The plan is now in front of the user in the SwAgent panel, file by " +
                "file, with Apply and Discard buttons. Only the user can apply it - you cannot, and no tool " +
                "does. Tell the user in a sentence or two what it will do and what was skipped or warned " +
                "about, then stop and let them decide. File names are deliberately not shown to you; refer to " +
                "files by their # number.");

            return sb.ToString();
        }

        /// <summary>What applying did, for the panel and for the model's next turn. No file names.</summary>
        public string DescribeOutcome()
        {
            int applied = CountOf(BatchRowStatus.Applied);
            int unchanged = CountOf(BatchRowStatus.Unchanged);
            int skipped = CountOf(BatchRowStatus.Skipped);
            int failed = CountOf(BatchRowStatus.Failed);

            string verb = Request.Operation == BatchOperation.Export ? "exported" : "changed and saved";

            return $"Batch plan {Id} ({Title}) was applied by the user: {applied} file(s) {verb}, " +
                   $"{unchanged} already correct, {skipped} skipped, {failed} failed.";
        }

        /// <summary>
        /// Belt and braces for the privacy promise: reasons are written without
        /// names, but an exception message from the OS can carry a path.
        /// </summary>
        internal static string Redact(string text, IndexedFile file)
        {
            if (string.IsNullOrEmpty(text) || file == null) return text;

            string result = text;
            foreach (string sensitive in new[]
                     {
                         file.FullPath,
                         SafeDirectory(file.FullPath),
                         Path.GetFileName(file.FullPath),
                         Path.GetFileNameWithoutExtension(file.FullPath),
                     })
            {
                if (string.IsNullOrEmpty(sensitive)) continue;
                result = ReplaceIgnoreCase(result, sensitive, "[file]");
            }

            return result;
        }

        private static string SafeDirectory(string path)
        {
            try { return Path.GetDirectoryName(path); }
            catch { return null; }
        }

        private static string ReplaceIgnoreCase(string text, string find, string replacement)
        {
            var sb = new StringBuilder();
            int start = 0;
            int at;
            while ((at = text.IndexOf(find, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                sb.Append(text, start, at - start).Append(replacement);
                start = at + find.Length;
            }

            return sb.Append(text.Substring(start)).ToString();
        }
    }

    /// <summary>
    /// The previewed plans in this session, and the only gate to applying one.
    ///
    /// The gate is the point. Nothing in the tool registry calls
    /// <see cref="BeginApply"/>; the panel's Apply button does. So a model that
    /// has misunderstood the request can describe a bad batch, but cannot run
    /// one - and an instruction in the system prompt saying "ask before
    /// applying" would be advice, where this is enforcement.
    /// </summary>
    public sealed class BatchPlanStore
    {
        /// <summary>
        /// Past this age a plan must be previewed again. Fingerprints catch a
        /// changed file; this catches an Apply button pressed on a card from an
        /// hour ago, long scrolled out of mind.
        /// </summary>
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

        private readonly Dictionary<string, BatchPlan> _plans =
            new Dictionary<string, BatchPlan>(StringComparer.OrdinalIgnoreCase);

        private int _nextId = 1;

        /// <summary>Raised on the SOLIDWORKS thread when a preview produces a plan.</summary>
        public event Action<BatchPlan> PlanCreated;

        public BatchPlan Add(BatchPlan plan, ISwLog log = null)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            plan.Id = "B" + _nextId++;
            _plans[plan.Id] = plan;

            try
            {
                PlanCreated?.Invoke(plan);
            }
            catch (Exception ex)
            {
                // A broken listener must not turn a good preview into a failed tool call.
                log?.Error($"A batch plan listener failed: {ex.Message}");
            }

            return plan;
        }

        public bool TryGet(string id, out BatchPlan plan)
        {
            plan = null;
            return !string.IsNullOrEmpty(id) && _plans.TryGetValue(id, out plan);
        }

        /// <summary>Move a pending plan to applying, or explain why it cannot be.</summary>
        public BatchPlan BeginApply(string id)
        {
            if (!TryGet(id, out BatchPlan plan))
                throw new InvalidOperationException($"There is no batch plan '{id}'.");

            switch (plan.State)
            {
                case BatchPlanState.Applying:
                    throw new InvalidOperationException($"Batch plan {plan.Id} is already being applied.");
                case BatchPlanState.Applied:
                    throw new InvalidOperationException(
                        $"Batch plan {plan.Id} has already been applied. Preview it again to run it a second time.");
                case BatchPlanState.Discarded:
                    throw new InvalidOperationException($"Batch plan {plan.Id} was discarded.");
            }

            if (DateTime.UtcNow - plan.CreatedUtc > Lifetime)
            {
                plan.State = BatchPlanState.Discarded;
                throw new InvalidOperationException(
                    $"Batch plan {plan.Id} is more than {Lifetime.TotalMinutes:0} minutes old. Preview it again, " +
                    "so what you approve describes the files as they are now.");
            }

            plan.State = BatchPlanState.Applying;
            return plan;
        }

        public void Finish(BatchPlan plan)
        {
            if (plan != null && plan.State == BatchPlanState.Applying)
                plan.State = BatchPlanState.Applied;
        }

        public bool Discard(string id)
        {
            if (!TryGet(id, out BatchPlan plan) || plan.State != BatchPlanState.Pending) return false;
            plan.State = BatchPlanState.Discarded;
            return true;
        }
    }
}
