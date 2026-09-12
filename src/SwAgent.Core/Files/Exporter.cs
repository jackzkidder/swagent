using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Session;

namespace SwAgent.Core.Files
{
    /// <summary>The outcome of an export, including the parts SOLIDWORKS reports by reference.</summary>
    public sealed class ExportOutcome
    {
        public bool Succeeded { get; set; }
        public string Path { get; set; }
        public long SizeBytes { get; set; }
        public int ErrorCode { get; set; }
        public int WarningCode { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Exports through IModelDocExtension.SaveAs.
    ///
    /// The errors and warnings come back through byref parameters, and they
    /// must be checked, because a failed export otherwise looks identical to a
    /// successful one - the call returns and nothing throws. We check the
    /// return value, the error code, AND whether a file actually appeared on
    /// disk, because those three can disagree.
    ///
    /// Scope discipline: an export only ever writes to a path the caller named,
    /// and refuses to overwrite unless explicitly told to.
    /// </summary>
    public static class Exporter
    {
        /// <summary>
        /// Native SOLIDWORKS formats. Saving to one of these keeps a live,
        /// editable document; it is not an export, and a drawing can only
        /// reference a model saved this way.
        /// </summary>
        private static readonly Dictionary<string, string> NativeFormats =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".sldprt"] = "SOLIDWORKS part",
                [".slddrw"] = "SOLIDWORKS drawing",
                [".sldasm"] = "SOLIDWORKS assembly",
            };

        /// <summary>Neutral formats, for handing work to someone else.</summary>
        private static readonly Dictionary<string, string> NeutralFormats =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".step"] = "STEP AP214 solid model, for machining and other CAD systems",
                [".stp"] = "STEP AP214 solid model",
                [".iges"] = "IGES surfaces",
                [".igs"] = "IGES surfaces",
                [".stl"] = "STL mesh, for 3D printing",
                [".pdf"] = "PDF, for sending a drawing to someone",
                [".dxf"] = "DXF, for laser and waterjet cutting",
                [".dwg"] = "DWG",
                [".x_t"] = "Parasolid",
                [".3mf"] = "3MF mesh",
            };

        public static IEnumerable<string> NeutralExtensions => NeutralFormats.Keys;
        public static IEnumerable<string> NativeExtensions => NativeFormats.Keys;

        /// <summary>Which family a write belongs to.</summary>
        public enum WriteKind
        {
            /// <summary>Accept either family.</summary>
            Any,

            /// <summary>Only .sldprt / .slddrw / .sldasm.</summary>
            Native,

            /// <summary>Only the neutral interchange formats.</summary>
            Neutral
        }

        /// <summary>
        /// Export the active document to an explicit path.
        /// </summary>
        public static ExportOutcome Export(SwSession session, string path, bool overwrite,
                                           WriteKind kind = WriteKind.Any)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("An output path is required.", nameof(path));

            var doc = session.RequireModel();

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"'{path}' is not a usable file path: {ex.Message}");
            }

            string extension = Path.GetExtension(fullPath);
            if (string.IsNullOrEmpty(extension))
                throw new ArgumentException(
                    $"'{path}' has no file extension, so the format is ambiguous. " +
                    $"Use one of: {string.Join(", ", NativeFormats.Keys.Concat(NeutralFormats.Keys).OrderBy(k => k))}.");

            bool isNative = NativeFormats.ContainsKey(extension);
            bool isNeutral = NeutralFormats.ContainsKey(extension);

            if (!isNative && !isNeutral)
            {
                throw new ArgumentException(
                    $"'{extension}' is not a format this tool writes. Native: " +
                    $"{string.Join(", ", NativeFormats.Keys.OrderBy(k => k))}. Neutral: " +
                    $"{string.Join(", ", NeutralFormats.Keys.OrderBy(k => k))}.");
            }

            // Saving and exporting are different intents, and mixing them up is
            // how you get a "saved" part that is really a STEP file and cannot
            // back a drawing.
            if (kind == WriteKind.Native && !isNative)
                throw new ArgumentException(
                    $"'{extension}' is an interchange format, not a SOLIDWORKS document. " +
                    $"Use sw_export for that, or save as one of: " +
                    $"{string.Join(", ", NativeFormats.Keys.OrderBy(k => k))}.");

            if (kind == WriteKind.Neutral && !isNeutral)
                throw new ArgumentException(
                    $"'{extension}' is a SOLIDWORKS document format, not an export. " +
                    $"Use sw_save_as for that, or export as one of: " +
                    $"{string.Join(", ", NeutralFormats.Keys.OrderBy(k => k))}.");

            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                throw new ArgumentException(
                    $"The folder '{directory}' does not exist. Create it first, or export somewhere else. " +
                    "This tool does not create folders it was not asked to create.");

            // No silent overwrites, ever.
            if (File.Exists(fullPath) && !overwrite)
            {
                return new ExportOutcome
                {
                    Succeeded = false,
                    Path = fullPath,
                    Message = $"'{fullPath}' already exists. Pass overwrite to replace it.",
                };
            }

            long sizeBefore = File.Exists(fullPath) ? new FileInfo(fullPath).Length : -1;
            DateTime timeBefore = File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath) : DateTime.MinValue;

            int errors = 0, warnings = 0;

            bool returned = doc.Extension.SaveAs(
                fullPath,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                null,
                ref errors,
                ref warnings);

            var outcome = new ExportOutcome
            {
                Path = fullPath,
                ErrorCode = errors,
                WarningCode = warnings,
            };

            bool fileExists = File.Exists(fullPath);
            bool fileChanged = fileExists
                && (sizeBefore < 0 || File.GetLastWriteTimeUtc(fullPath) > timeBefore);

            // All three must agree before we tell the agent this worked.
            if (returned && errors == 0 && fileExists && fileChanged)
            {
                outcome.Succeeded = true;
                outcome.SizeBytes = new FileInfo(fullPath).Length;
                outcome.Message = warnings == 0
                    ? $"Exported to {fullPath} ({FormatSize(outcome.SizeBytes)})."
                    : $"Exported to {fullPath} ({FormatSize(outcome.SizeBytes)}) with warnings ({DescribeCode(warnings)}).";
                return outcome;
            }

            outcome.Succeeded = false;

            if (!returned || errors != 0)
                outcome.Message = $"SOLIDWORKS refused the export: {DescribeCode(errors)}.";
            else if (!fileExists)
                outcome.Message = "SOLIDWORKS reported success but no file was written.";
            else
                outcome.Message = "SOLIDWORKS reported success but the existing file was not updated.";

            return outcome;
        }

        /// <summary>Turn a SOLIDWORKS save error bitmask into something a person can act on.</summary>
        private static string DescribeCode(int code)
        {
            if (code == 0) return "no error reported";

            var parts = new List<string>();
            var known = new (swFileSaveError_e Flag, string Text)[]
            {
                (swFileSaveError_e.swGenericSaveError, "generic save error"),
                (swFileSaveError_e.swReadOnlySaveError, "the file is read-only"),
                (swFileSaveError_e.swFileNameEmpty, "the file name was empty"),
                (swFileSaveError_e.swFileNameContainsAtSign, "the file name contains an '@'"),
                (swFileSaveError_e.swFileLockError, "the file is locked, most likely open in another program"),
                (swFileSaveError_e.swFileSaveFormatNotAvailable, "that format is not available for this document type"),
                (swFileSaveError_e.swFileSaveAsDoNotOverwrite, "the file already exists and overwriting was not permitted"),
                (swFileSaveError_e.swFileSaveAsInvalidFileExtension, "the file extension is not valid for this document"),
                (swFileSaveError_e.swFileSaveAsNameExceedsMaxPathLength, "the path is too long"),
                (swFileSaveError_e.swFileSaveAsNotSupported, "saving this document to that format is not supported"),
            };

            foreach (var (flag, text) in known)
                if ((code & (int)flag) != 0) parts.Add(text);

            return parts.Count > 0
                ? string.Join("; ", parts)
                : $"error code {code}";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " bytes";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
        }
    }
}
