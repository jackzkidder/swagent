using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Infrastructure;

namespace SwAgent.Core.Session
{
    /// <summary>
    /// Owns the SOLIDWORKS connection for the life of the add-in, and the small
    /// amount of state the API refuses to track for us.
    ///
    /// Two rules this type enforces:
    ///
    /// 1. The ISldWorks pointer is the one we were handed in ConnectToSW. We
    ///    never Dispatch a new one - that can start a second SOLIDWORKS session
    ///    behind the user's back.
    ///
    /// 2. Selection is global mutable state. The API consumes whatever happens
    ///    to be selected, so a leftover selection from a previous call is why
    ///    the fillet landed on the wrong edge. Every operation begins with
    ///    ClearSelection.
    /// </summary>
    public sealed class SwSession
    {
        private readonly ISldWorks _sw;
        private readonly ISwLog _log;

        public SwSession(ISldWorks sw, ISwLog log = null)
        {
            _sw = sw ?? throw new ArgumentNullException(nameof(sw));
            _log = log ?? NullSwLog.Instance;
        }

        /// <summary>The pointer we were given. Never replaced, never re-acquired.</summary>
        public ISldWorks App => _sw;

        public ISwLog Log => _log;

        /// <summary>
        /// What the agent committed to before building, and whether the part
        /// honoured it. Lives on the session so every tool can reach it.
        /// </summary>
        public Intent.IntentStore Intent { get; } = new Intent.IntentStore();

        /// <summary>
        /// Batch plans previewed in this session. Applying one is the user's
        /// decision, made in the panel - see <see cref="Batch.BatchPlanStore"/>.
        /// </summary>
        public Batch.BatchPlanStore Batches { get; } = new Batch.BatchPlanStore();

        /// <summary>
        /// A mark in the feature tree taken when the user sends a message, so
        /// the agent can remove its own work cleanly instead of trusting undo.
        /// </summary>
        public CheckpointStore Checkpoints { get; } = new CheckpointStore();

        /// <summary>
        /// What the user had selected when they sent their message.
        ///
        /// Set by the panel BEFORE the agent runs, because every tool starts by
        /// clearing the selection - the first tool call of a run would otherwise
        /// destroy the thing "this face" refers to. Never read the live
        /// selection instead; by then it is ours, not theirs.
        /// </summary>
        public Inspection.SelectionSnapshot UserSelection { get; set; }

        /// <summary>
        /// True while a sketch is open for editing.
        ///
        /// InsertSketch toggles: the same call that opens a sketch closes it.
        /// The API will not tell us which state we are in, so we track it and
        /// reject sketch-entity calls when nothing is open rather than silently
        /// drawing into the previous sketch.
        /// </summary>
        public bool IsSketchOpen { get; private set; }

        internal void MarkSketchOpened() => IsSketchOpen = true;
        internal void MarkSketchClosed() => IsSketchOpen = false;

        /// <summary>
        /// The active document, or null. Callers that require one should use
        /// <see cref="RequireModel"/> so the failure message is uniform.
        /// </summary>
        public IModelDoc2 ActiveDoc => _sw.IActiveDoc2;

        /// <summary>Active document, or a readable failure if there is none.</summary>
        public IModelDoc2 RequireModel()
        {
            var doc = ActiveDoc;
            if (doc == null)
                throw new InvalidOperationException(
                    "No document is open. Create or open one first (sw_new_part).");
            return doc;
        }

        /// <summary>Active document, requiring it to be a part.</summary>
        public IModelDoc2 RequirePart()
        {
            var doc = RequireModel();
            if (doc.GetType() != (int)swDocumentTypes_e.swDocPART)
                throw new InvalidOperationException(
                    $"The active document is not a part (it is {DescribeDocType(doc)}). " +
                    "This operation only applies to parts.");
            return doc;
        }

        /// <summary>Active document, requiring it to be a drawing.</summary>
        public IModelDoc2 RequireDrawing()
        {
            var doc = RequireModel();
            if (doc.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
                throw new InvalidOperationException(
                    $"The active document is not a drawing (it is {DescribeDocType(doc)}).");
            return doc;
        }

        private static string DescribeDocType(IModelDoc2 doc)
        {
            switch ((swDocumentTypes_e)doc.GetType())
            {
                case swDocumentTypes_e.swDocPART: return "a part";
                case swDocumentTypes_e.swDocASSEMBLY: return "an assembly";
                case swDocumentTypes_e.swDocDRAWING: return "a drawing";
                default: return "an unknown document type";
            }
        }

        /// <summary>
        /// Drop every selection. Call this at the start of every operation, no
        /// exceptions - see the class remarks.
        /// </summary>
        public void ClearSelection()
        {
            var doc = ActiveDoc;
            doc?.ClearSelection2(true);
        }

        /// <summary>
        /// Force a rebuild and report whether it was clean.
        ///
        /// A returned feature object does not mean the feature is correct. The
        /// only way to know is to rebuild and look at the error state, so every
        /// feature-creating tool ends here.
        /// </summary>
        public RebuildOutcome Rebuild(bool topLevelOnly = false)
        {
            var doc = RequireModel();

            SuppressFeatureErrorDialogs(doc);

            // ForceRebuildAll rebuilds regardless of what SOLIDWORKS thinks is
            // dirty, which is what we want after programmatic feature creation.
            bool ok = doc.Extension.ForceRebuildAll();

            var problems = new List<FeatureProblem>();
            int errors = 0, warnings = 0;

            try
            {
                // GetWhatsWrong names the offending features. A bare count would
                // tell the agent that something is wrong; this tells it what, so
                // the recovery can be the right one rather than a blind undo.
                if (doc.Extension.GetWhatsWrongCount() > 0)
                {
                    object featuresObj, errorCodesObj, warningsObj;
                    if (doc.Extension.GetWhatsWrong(out featuresObj, out errorCodesObj, out warningsObj))
                    {
                        var names = featuresObj as object[];
                        var codes = errorCodesObj as int[];
                        var isWarning = warningsObj as bool[];

                        int count = names?.Length ?? 0;
                        for (int i = 0; i < count; i++)
                        {
                            bool warn = isWarning != null && i < isWarning.Length && isWarning[i];
                            problems.Add(new FeatureProblem(
                                name: names[i]?.ToString() ?? "(unnamed)",
                                errorCode: codes != null && i < codes.Length ? codes[i] : 0,
                                isWarning: warn));

                            if (warn) warnings++; else errors++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"Rebuild: could not read what is wrong: {ex.Message}");
            }

            return new RebuildOutcome(ok, errors, warnings, problems);
        }

        /// <summary>
        /// Stop SOLIDWORKS popping a modal "what's wrong" dialog on a rebuild
        /// error.
        ///
        /// This matters more than it looks. A modal dialog raised on the
        /// SOLIDWORKS thread blocks that thread until a human clicks it, and the
        /// agent is not a human. The loop would hang with no timeout and no
        /// error, which the user experiences as the add-in freezing their CAD
        /// session - the exact failure we promise cannot happen.
        /// </summary>
        private void SuppressFeatureErrorDialogs(IModelDoc2 doc)
        {
            try
            {
                doc.ShowFeatureErrorDialog = false;
            }
            catch (Exception ex)
            {
                _log.Debug($"Could not suppress the feature error dialog: {ex.Message}");
            }
        }
    }

    /// <summary>A feature SOLIDWORKS reported as failed or warned after a rebuild.</summary>
    public sealed class FeatureProblem
    {
        public FeatureProblem(string name, int errorCode, bool isWarning)
        {
            Name = name;
            ErrorCode = errorCode;
            IsWarning = isWarning;
        }

        public string Name { get; }
        public int ErrorCode { get; }
        public bool IsWarning { get; }

        public override string ToString()
            => $"{Name} ({(IsWarning ? "warning" : "error")} {ErrorCode})";
    }

    /// <summary>The result of a forced rebuild.</summary>
    public sealed class RebuildOutcome
    {
        public RebuildOutcome(bool succeeded, int errors, int warnings, IReadOnlyList<FeatureProblem> problems = null)
        {
            Succeeded = succeeded;
            Errors = errors;
            Warnings = warnings;
            Problems = problems ?? new List<FeatureProblem>();
        }

        public bool Succeeded { get; }
        public int Errors { get; }
        public int Warnings { get; }

        /// <summary>The specific features that failed, by name.</summary>
        public IReadOnlyList<FeatureProblem> Problems { get; }

        public bool IsClean => Succeeded && Errors == 0;

        public string Describe()
        {
            if (IsClean)
                return Warnings == 0 ? "Rebuild clean." : $"Rebuild clean ({Warnings} warning(s)).";

            var head = Succeeded
                ? $"Rebuild reported {Errors} error(s), {Warnings} warning(s)"
                : $"Rebuild FAILED ({Errors} error(s), {Warnings} warning(s))";

            if (Problems.Count == 0) return head + ".";

            return head + ": " + string.Join(", ", Problems.Select(p => p.ToString())) + ".";
        }
    }
}
