using System;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Files;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Properties;
using SwAgent.Core.Session;

namespace SwAgent.Core.Batch
{
    /// <summary>
    /// Applies an approved plan, one file per call.
    ///
    /// One file per call so the caller can hand the SOLIDWORKS thread back
    /// between files: the panel dispatches each row separately, which keeps CAD
    /// responsive, lets progress reach the screen, and lets Stop take effect
    /// between files rather than after the last one.
    ///
    /// Every row is re-checked against disk before it is touched. The preview
    /// is what the user approved; if the file has moved on since, the approval
    /// does not cover it.
    /// </summary>
    public static class BatchRunner
    {
        /// <summary>
        /// Apply one row. Records a per-file problem on the row rather than
        /// throwing; only a lost session escapes.
        /// </summary>
        public static void ApplyRow(SwSession session, BatchPlan plan, BatchRow row)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (row == null) throw new ArgumentNullException(nameof(row));

            if (plan.State != BatchPlanState.Applying)
            {
                throw new InvalidOperationException(
                    $"Batch plan {plan.Id} has not been approved for applying (it is {plan.State.ToString().ToLowerInvariant()}). " +
                    "Only the user can apply a batch, from the SwAgent panel.");
            }

            if (row.Status != BatchRowStatus.Ready) return;

            try
            {
                if (!row.File.StillMatchesDisk(out string why))
                {
                    Skip(row, why);
                    return;
                }

                if (BackgroundDocument.IsOpenInSession(session, row.File.FullPath))
                {
                    Skip(row, "it was opened in SOLIDWORKS after the preview");
                    return;
                }

                switch (plan.Request.Operation)
                {
                    case BatchOperation.SetProperty:
                        ApplyProperty(session, plan.Request, row);
                        break;
                    case BatchOperation.SetMaterial:
                        ApplyMaterial(session, plan.Request, row);
                        break;
                    default:
                        ApplyExport(session, plan.Request, row);
                        break;
                }
            }
            catch (SwSessionLostException)
            {
                throw;
            }
            catch (Exception ex)
            {
                row.Status = BatchRowStatus.Failed;
                row.Result = ex.Message;
            }
        }

        /// <summary>Mark every row that never got its turn, after the user stops a batch part way.</summary>
        public static void MarkRemainingStopped(BatchPlan plan)
        {
            foreach (var row in plan.Rows)
                if (row.Status == BatchRowStatus.Ready)
                    Skip(row, "stopped before this file was reached");
        }

        private static void ApplyProperty(SwSession session, BatchRequest r, BatchRow row)
        {
            if (SkipIfLocked(row)) return;

            using (var bg = BackgroundDocument.Open(session, row.File.FullPath, readOnly: false))
            {
                RequireWritable(bg);

                PropertyTier tier = CustomProperties.Write(bg.Doc, r.PropertyName, r.PropertyValue, PropertyTier.Auto);

                // Read back before saving: a write that did not land must not be
                // saved and reported as done.
                var back = CustomProperties.Read(bg.Doc, r.PropertyName);
                if (!string.Equals(back.RawValue ?? "", r.PropertyValue, StringComparison.Ordinal))
                    throw new InvalidOperationException("the property did not read back as written, so the file was not saved");

                SaveInPlace(bg, row.File);

                row.Status = BatchRowStatus.Applied;
                row.Result = (row.Current == null ? "added" : "changed") +
                             $" on the {tier.ToString().ToLowerInvariant()} level and saved";
            }
        }

        private static void ApplyMaterial(SwSession session, BatchRequest r, BatchRow row)
        {
            if (SkipIfLocked(row)) return;

            using (var bg = BackgroundDocument.Open(session, row.File.FullPath, readOnly: false))
            {
                RequireWritable(bg);

                var part = (IPartDoc)bg.Doc;
                part.SetMaterialPropertyName2("", r.MaterialDatabase ?? "", r.Material);

                string applied = part.GetMaterialPropertyName2("", out string _);
                if (!string.Equals(applied, r.Material, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("SOLIDWORKS did not apply the material, so the file was not saved");

                // Mass properties follow the material only after a rebuild, and
                // a saved part should not carry stale ones.
                bg.Doc.ForceRebuild3(false);

                SaveInPlace(bg, row.File);

                row.Status = BatchRowStatus.Applied;
                row.Result = "material set and saved";
            }
        }

        private static void ApplyExport(SwSession session, BatchRequest r, BatchRow row)
        {
            if (File.Exists(row.OutputPath) && !r.Overwrite)
            {
                Skip(row, "a file of that name appeared in the output folder after the preview");
                return;
            }

            using (var bg = BackgroundDocument.Open(session, row.File.FullPath, readOnly: true))
            {
                ExportOutcome outcome = Exporter.Export(bg.Doc, row.OutputPath, r.Overwrite, Exporter.WriteKind.Neutral);

                if (!outcome.Succeeded)
                {
                    row.Status = BatchRowStatus.Failed;
                    row.Result = outcome.Message;
                    return;
                }

                row.Status = BatchRowStatus.Applied;
                row.Result = $"exported ({Exporter.FormatSize(outcome.SizeBytes)})";
            }
        }

        private static bool SkipIfLocked(BatchRow row)
        {
            string lockReason = BatchPlanner.WriteLockReason(row.File.FullPath);
            if (lockReason == null) return false;

            Skip(row, lockReason);
            return true;
        }

        private static void RequireWritable(BackgroundDocument bg)
        {
            if (bg.OpenedReadOnly)
                throw new InvalidOperationException(
                    "SOLIDWORKS would only open it read-only, most likely because someone else has it, so it was not changed");
        }

        /// <summary>
        /// Save over the original, and believe it only when the file on disk has
        /// actually changed - the same three-way check the exporter makes.
        /// </summary>
        private static void SaveInPlace(BackgroundDocument bg, IndexedFile file)
        {
            DateTime timeBefore = File.GetLastWriteTimeUtc(file.FullPath);
            long sizeBefore = new FileInfo(file.FullPath).Length;
            int errors = 0, warnings = 0;

            bool returned = SwGuard.Com("Save3",
                () => bg.Doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings));

            if (!returned || errors != 0)
                throw new InvalidOperationException($"SOLIDWORKS refused to save it: {Exporter.DescribeCode(errors)}");

            // Any change, not "newer": a file stamped in the future - by a
            // server with a skewed clock, say - would otherwise never look saved.
            var after = new FileInfo(file.FullPath);
            if (after.LastWriteTimeUtc == timeBefore && after.Length == sizeBefore)
                throw new InvalidOperationException("SOLIDWORKS reported a successful save, but the file on disk did not change");
        }

        private static void Skip(BatchRow row, string reason)
        {
            row.Status = BatchRowStatus.Skipped;
            row.Reason = reason;
        }
    }
}
