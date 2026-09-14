using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace SwAgent.Core.Batch
{
    /// <summary>The SOLIDWORKS document families a batch can touch.</summary>
    public enum BatchDocType
    {
        Part,
        Assembly,
        Drawing
    }

    /// <summary>One SOLIDWORKS file found on disk, as it was when it was found.</summary>
    public sealed class IndexedFile
    {
        public string FullPath { get; set; }

        /// <summary>Path below the indexed folder, for display.</summary>
        public string RelativePath { get; set; }

        public BatchDocType DocType { get; set; }
        public long SizeBytes { get; set; }

        /// <summary>
        /// Size and last write time when indexed are the file's fingerprint. If
        /// either has moved by the time a batch is applied, someone changed the
        /// file after the user approved the preview, and what they approved no
        /// longer describes it.
        /// </summary>
        public DateTime LastWriteUtc { get; set; }

        public bool ReadOnlyAttribute { get; set; }

        /// <summary>True when the file is still exactly as it was indexed.</summary>
        public bool StillMatchesDisk(out string why)
        {
            why = null;

            FileInfo info;
            try
            {
                info = new FileInfo(FullPath);
                info.Refresh();
            }
            catch (Exception ex)
            {
                why = "it can no longer be read: " + ex.Message;
                return false;
            }

            if (!info.Exists)
            {
                why = "it has been deleted or moved since the preview";
                return false;
            }

            if (info.Length != SizeBytes || info.LastWriteTimeUtc != LastWriteUtc)
            {
                why = "it was modified after the preview, so the preview no longer describes it";
                return false;
            }

            return true;
        }

        internal static IndexedFile FromDisk(string fullPath, string root, BatchDocType type)
        {
            var info = new FileInfo(fullPath);

            string relative = info.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? info.FullName.Substring(root.Length).TrimStart('\\', '/')
                : info.Name;

            return new IndexedFile
            {
                FullPath = info.FullName,
                RelativePath = relative,
                DocType = type,
                SizeBytes = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
                ReadOnlyAttribute = (info.Attributes & FileAttributes.ReadOnly) != 0,
            };
        }
    }

    /// <summary>What a folder scan found, including what it deliberately left out.</summary>
    public sealed class FolderIndexResult
    {
        public string Folder { get; set; }
        public string Pattern { get; set; }
        public bool Recursive { get; set; }
        public List<IndexedFile> Files { get; } = new List<IndexedFile>();

        /// <summary>True when the scan stopped at a limit and there is more.</summary>
        public bool Truncated { get; set; }

        /// <summary>
        /// SOLIDWORKS "~$" lock files skipped. Each one means the file it names
        /// is open somewhere - here or on a colleague's machine.
        /// </summary>
        public int LockFilesIgnored { get; set; }

        public List<string> UnreadableFolders { get; } = new List<string>();
    }

    /// <summary>
    /// Finds SOLIDWORKS files in a folder. Reads directory listings only; it
    /// opens nothing, so it is safe and fast on any folder.
    ///
    /// Bounded on purpose. A recursive scan of the wrong folder - a drive root,
    /// a network share - would otherwise run for minutes on the SOLIDWORKS
    /// thread, which the user experiences as CAD hanging.
    /// </summary>
    public static class FolderIndex
    {
        public const int MaxFiles = 500;
        public const int MaxFolders = 2000;

        private static readonly Dictionary<string, BatchDocType> Extensions =
            new Dictionary<string, BatchDocType>(StringComparer.OrdinalIgnoreCase)
            {
                [".sldprt"] = BatchDocType.Part,
                [".sldasm"] = BatchDocType.Assembly,
                [".slddrw"] = BatchDocType.Drawing,
            };

        public static bool TryGetDocType(string path, out BatchDocType type)
        {
            type = BatchDocType.Part;

            string extension;
            try
            {
                extension = Path.GetExtension(path);
            }
            catch (ArgumentException)
            {
                return false;
            }

            return !string.IsNullOrEmpty(extension) && Extensions.TryGetValue(extension, out type);
        }

        public static FolderIndexResult Scan(string folder, string pattern = "*", bool recursive = false)
        {
            string root = RequireFolder(folder);
            string normalisedPattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
            Regex matcher = CompilePattern(normalisedPattern);

            var result = new FolderIndexResult
            {
                Folder = root,
                Pattern = normalisedPattern,
                Recursive = recursive,
            };

            var pending = new Queue<string>();
            pending.Enqueue(root);
            int foldersVisited = 0;

            while (pending.Count > 0 && !result.Truncated)
            {
                string directory = pending.Dequeue();

                if (++foldersVisited > MaxFolders)
                {
                    result.Truncated = true;
                    break;
                }

                string[] files;
                try
                {
                    files = Directory.GetFiles(directory);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    result.UnreadableFolders.Add(directory);
                    continue;
                }

                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                foreach (string path in files)
                {
                    if (!TryGetDocType(path, out BatchDocType type)) continue;

                    string name = Path.GetFileName(path);

                    // SOLIDWORKS writes "~$name.sldprt" beside a file while it is
                    // open. It has a SOLIDWORKS extension and is not a document.
                    if (name.StartsWith("~$", StringComparison.Ordinal))
                    {
                        result.LockFilesIgnored++;
                        continue;
                    }

                    if (!matcher.IsMatch(name)) continue;

                    if (result.Files.Count >= MaxFiles)
                    {
                        result.Truncated = true;
                        break;
                    }

                    try
                    {
                        result.Files.Add(IndexedFile.FromDisk(path, root, type));
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                    {
                        // Deleted or locked between the listing and the stat.
                    }
                }

                if (!recursive || result.Truncated) continue;

                try
                {
                    string[] subfolders = Directory.GetDirectories(directory);
                    Array.Sort(subfolders, StringComparer.OrdinalIgnoreCase);

                    foreach (string sub in subfolders)
                    {
                        // A junction can point back up the tree, and following it
                        // loops until the folder limit.
                        if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        pending.Enqueue(sub);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    result.UnreadableFolders.Add(directory);
                }
            }

            result.Files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
            return result;
        }

        private static string RequireFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                throw new ArgumentException("A folder is required.");

            if (!Path.IsPathRooted(folder))
                throw new ArgumentException(
                    $"'{folder}' is not a full path. Give the whole folder path, e.g. C:\\Jobs\\1234\\Parts.");

            string full;
            try
            {
                full = Path.GetFullPath(folder).TrimEnd('\\', '/');
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"'{folder}' is not a usable folder path: {ex.Message}");
            }

            string driveRoot = Path.GetPathRoot(full)?.TrimEnd('\\', '/');
            if (string.Equals(full, driveRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "A batch will not run over a whole drive. Point it at the folder that holds the files.");

            if (!Directory.Exists(full))
                throw new ArgumentException($"The folder '{full}' does not exist.");

            return full;
        }

        /// <summary>
        /// Wildcards against the file name only: '*' and '?'. No path parts, so
        /// a pattern cannot reach outside the folder it was given.
        /// </summary>
        private static Regex CompilePattern(string pattern)
        {
            if (pattern.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
                throw new ArgumentException(
                    $"The pattern '{pattern}' may only match file names, e.g. 'BRK-*.sldprt'. " +
                    "Put folders in the folder argument instead.");

            string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
