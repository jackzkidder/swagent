using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Properties;
using SwAgent.Core.Session;

namespace SwAgent.Core.Batch
{
    /// <summary>
    /// A batch request that cannot be planned at all, as opposed to one file in
    /// it that cannot be changed.
    /// </summary>
    public sealed class BatchRejectedException : ArgumentException
    {
        public BatchRejectedException(string message) : base(message) { }
    }

    /// <summary>
    /// The dry run: works out, file by file, what a batch would do, and writes
    /// nothing.
    ///
    /// A batch is the one place a mistake is multiplied. A wrong property on one
    /// part is a typo; on two hundred saved and closed parts it is an afternoon
    /// of someone's work, with no undo. So the plan is computed completely, shown
    /// file by file, and applied only when the user says so.
    ///
    /// "Writes nothing" is literal, and the harness checks it: every file's size
    /// and timestamp are identical after a preview. Files are opened read-only
    /// and closed without saving.
    /// </summary>
    public static class BatchPlanner
    {
        /// <summary>Exports only list files during the preview, so they can cover more.</summary>
        public const int MaxFilesPerExport = 200;

        /// <summary>
        /// Property and material previews open every file to read its current
        /// value, and SOLIDWORKS does not respond while that runs.
        /// </summary>
        public const int MaxFilesOpenedPerPreview = 50;

        private static readonly string[] ModelExportExtensions = { ".step", ".stp", ".x_t", ".igs", ".iges", ".stl", ".3mf" };
        private static readonly string[] DrawingExportExtensions = { ".pdf", ".dxf", ".dwg" };

        /// <summary>Export formats a batch accepts, without the dot.</summary>
        public static string[] ExportFormats =>
            ModelExportExtensions.Concat(DrawingExportExtensions).Select(e => e.Substring(1)).ToArray();

        private sealed class PreviewState
        {
            public bool MaterialVerified;
        }

        public static BatchPlan Preview(SwSession session, BatchRequest request)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (request == null) throw new ArgumentNullException(nameof(request));

            Validate(request);

            var index = FolderIndex.Scan(request.Folder, request.Pattern, request.Recursive);
            request.Folder = index.Folder;
            request.Pattern = index.Pattern;

            if (index.Files.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No SOLIDWORKS files in that folder match '{index.Pattern}'" +
                    (request.Recursive ? "." : " (subfolders were not searched).") +
                    " There is nothing to plan.");
            }

            bool opensFiles = request.Operation != BatchOperation.Export;
            int cap = opensFiles ? MaxFilesOpenedPerPreview : MaxFilesPerExport;

            if (index.Truncated || index.Files.Count > cap)
            {
                string count = index.Truncated ? $"more than {index.Files.Count}" : index.Files.Count.ToString();
                throw new BatchRejectedException(
                    $"That matches {count} files, and this kind of batch is limited to {cap}. " +
                    (opensFiles
                        ? "Its preview opens every file to read the current value, and SOLIDWORKS does not respond while it does. "
                        : "") +
                    "Narrow the pattern (e.g. 'BRK-*.sldprt') or work through the folder in parts.");
            }

            var plan = new BatchPlan(request);
            var exportTargets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var state = new PreviewState();

            for (int i = 0; i < index.Files.Count; i++)
            {
                var row = new BatchRow { File = index.Files[i], Status = BatchRowStatus.Ready };
                plan.Rows.Add(row);

                try
                {
                    switch (request.Operation)
                    {
                        case BatchOperation.SetProperty:
                            PlanProperty(session, request, row);
                            break;
                        case BatchOperation.SetMaterial:
                            PlanMaterial(session, request, row, state);
                            break;
                        default:
                            PlanExport(session, request, row, i, exportTargets);
                            break;
                    }
                }
                catch (Exception ex) when (!(ex is SwSessionLostException) && !(ex is BatchRejectedException))
                {
                    Skip(row, "it could not be read: " + ex.Message);
                }
            }

            return plan;
        }

        private static void Validate(BatchRequest r)
        {
            switch (r.Operation)
            {
                case BatchOperation.SetProperty:
                    if (string.IsNullOrWhiteSpace(r.PropertyName))
                        throw new ArgumentException("property_name is required for set_property.");
                    if (r.PropertyValue == null)
                        throw new ArgumentException(
                            "property_value is required for set_property. Pass an empty string to blank a property.");
                    break;

                case BatchOperation.SetMaterial:
                    if (string.IsNullOrWhiteSpace(r.Material))
                        throw new ArgumentException("material is required for set_material.");
                    r.MaterialDatabase = r.MaterialDatabase ?? "";
                    break;

                case BatchOperation.Export:
                    string extension = (r.ExportExtension ?? "").Trim().ToLowerInvariant();
                    if (extension.Length > 0 && extension[0] != '.') extension = "." + extension;

                    if (!ModelExportExtensions.Contains(extension) && !DrawingExportExtensions.Contains(extension))
                        throw new ArgumentException(
                            $"export_format must be one of: {string.Join(", ", ExportFormats)}.");

                    r.ExportExtension = extension;

                    if (string.IsNullOrWhiteSpace(r.OutputFolder))
                        throw new ArgumentException("output_folder is required for export.");

                    if (!Path.IsPathRooted(r.OutputFolder))
                        throw new ArgumentException($"output_folder '{r.OutputFolder}' is not a full path.");

                    string full = Path.GetFullPath(r.OutputFolder).TrimEnd('\\', '/');
                    if (!Directory.Exists(full))
                        throw new ArgumentException(
                            $"The output folder '{full}' does not exist. Create it first - a batch does not " +
                            "create folders it was not asked to create.");

                    r.OutputFolder = full;
                    break;
            }
        }

        private static void PlanProperty(SwSession session, BatchRequest r, BatchRow row)
        {
            row.Proposed = r.PropertyValue;

            if (SkipIfOpen(session, row) || SkipIfNotWritable(row) || SkipIfUpgradeRefused(session, r, row))
                return;

            using (var bg = BackgroundDocument.Open(session, row.File.FullPath, readOnly: true))
            {
                var current = CustomProperties.Read(bg.Doc, r.PropertyName);
                row.Current = current.Exists ? current.RawValue : null;

                row.Status = current.Exists && string.Equals(current.RawValue, r.PropertyValue, StringComparison.Ordinal)
                    ? BatchRowStatus.Unchanged
                    : BatchRowStatus.Ready;
            }
        }

        private static void PlanMaterial(SwSession session, BatchRequest r, BatchRow row, PreviewState state)
        {
            row.Proposed = r.Material;

            if (row.File.DocType != BatchDocType.Part)
            {
                Skip(row, "materials apply to parts only");
                return;
            }

            if (SkipIfOpen(session, row) || SkipIfNotWritable(row) || SkipIfUpgradeRefused(session, r, row))
                return;

            using (var bg = BackgroundDocument.Open(session, row.File.FullPath, readOnly: true))
            {
                var part = (IPartDoc)bg.Doc;
                string current = part.GetMaterialPropertyName2("", out string _);
                row.Current = string.IsNullOrWhiteSpace(current) ? null : current;

                if (!state.MaterialVerified)
                {
                    // Prove the name resolves before planning 50 files around it.
                    // Applied in memory to a read-only document that is closed
                    // without saving, so nothing reaches disk.
                    part.SetMaterialPropertyName2("", r.MaterialDatabase, r.Material);
                    string applied = part.GetMaterialPropertyName2("", out string _);

                    if (!string.Equals(applied, r.Material, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new BatchRejectedException(
                            $"SOLIDWORKS has no material called '{r.Material}'. Names are exact and " +
                            "punctuation-sensitive, e.g. '6061 Alloy' rather than 'aluminium 6061'. Nothing was planned.");
                    }

                    // Use the library's own spelling from here on.
                    r.Material = applied;
                    row.Proposed = applied;
                    state.MaterialVerified = true;
                }

                row.Status = row.Current != null && string.Equals(row.Current, r.Material, StringComparison.OrdinalIgnoreCase)
                    ? BatchRowStatus.Unchanged
                    : BatchRowStatus.Ready;
            }
        }

        private static void PlanExport(SwSession session, BatchRequest r, BatchRow row, int index,
                                       Dictionary<string, int> targets)
        {
            string extension = r.ExportExtension;
            string output = Path.Combine(r.OutputFolder,
                Path.GetFileNameWithoutExtension(row.File.FullPath) + extension);

            row.OutputPath = output;
            row.Proposed = Path.GetFileName(output);

            bool drawingFormat = DrawingExportExtensions.Contains(extension);

            if (drawingFormat && row.File.DocType != BatchDocType.Drawing)
            {
                Skip(row, $"only drawings export to {extension}");
                return;
            }

            if (!drawingFormat && row.File.DocType == BatchDocType.Drawing)
            {
                Skip(row, $"a drawing cannot be exported as {extension}; export its part instead");
                return;
            }

            if (SkipIfOpen(session, row)) return;

            if (targets.TryGetValue(output, out int first))
            {
                Skip(row, $"#{first + 1} in this batch already exports to a file of the same name");
                return;
            }

            targets[output] = index;

            if (File.Exists(output))
            {
                if (!r.Overwrite)
                {
                    Skip(row, "a file of that name already exists in the output folder. Preview again with overwrite to replace it");
                    return;
                }

                row.Warning = "replaces an existing file in the output folder";
            }

            row.Status = BatchRowStatus.Ready;
        }

        private static bool SkipIfOpen(SwSession session, BatchRow row)
        {
            if (!BackgroundDocument.IsOpenInSession(session, row.File.FullPath)) return false;

            Skip(row, "it is open in SOLIDWORKS. Close it first - a batch never touches an open file, because " +
                      "it could save or discard edits that have not been saved yet");
            return true;
        }

        private static bool SkipIfNotWritable(BatchRow row)
        {
            if (row.File.ReadOnlyAttribute)
            {
                Skip(row, "the file is marked read-only");
                return true;
            }

            string lockReason = WriteLockReason(row.File.FullPath);
            if (lockReason != null)
            {
                Skip(row, lockReason);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Null when the file could be written, otherwise why not. Opens the file
        /// for exclusive access and closes it again without writing a byte, which
        /// changes neither its contents nor its timestamp. Only meaningful for a
        /// file this session does not have open.
        /// </summary>
        internal static string WriteLockReason(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }

                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return "you do not have permission to write to it";
            }
            catch (IOException)
            {
                return "another program or user has it open";
            }
        }

        private static bool SkipIfUpgradeRefused(SwSession session, BatchRequest r, BatchRow row)
        {
            FormatCheck check;
            try
            {
                check = FileFormat.Check(session.App, row.File.FullPath);
            }
            catch (SwSessionLostException)
            {
                throw;
            }
            catch (Exception ex)
            {
                check = new FormatCheck { Known = false, Detail = "its save history could not be read: " + ex.Message };
            }

            if (!check.Known)
            {
                row.Warning = "SwAgent could not tell which SOLIDWORKS release last saved it (" + check.Detail +
                              "), so saving may upgrade it";
                return false;
            }

            if (!check.WouldUpgrade) return false;

            if (r.AllowVersionUpgrade)
            {
                row.Warning = check.Detail + "; saving upgrades it, and that release can no longer open it";
                return false;
            }

            Skip(row, check.Detail + ". Saving it here would upgrade it permanently, and anyone still on that " +
                      "release could no longer open it. Preview again with allow_version_upgrade to include it");
            return true;
        }

        private static void Skip(BatchRow row, string reason)
        {
            row.Status = BatchRowStatus.Skipped;
            row.Reason = reason;
        }
    }
}
