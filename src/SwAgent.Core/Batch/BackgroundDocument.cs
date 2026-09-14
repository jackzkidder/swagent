using System;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Infrastructure;
using SwAgent.Core.Session;

namespace SwAgent.Core.Batch
{
    /// <summary>
    /// A file opened for batch work without being shown, and closed again.
    ///
    /// Three hazards this type exists for:
    ///
    /// 1. A file the user already has open may hold unsaved edits. Opening it
    ///    again hands back the same in-memory document, so saving it would
    ///    write their half-finished work and closing it would throw that work
    ///    away. An open file is refused, never touched.
    ///
    /// 2. Opening a document makes it the active one. The agent may be half way
    ///    through a part; if the previous document is not re-activated, the
    ///    next modelling tool lands in whatever the batch opened last.
    ///
    /// 3. DocumentVisible is application-wide state. It is restored in a
    ///    finally, or every file the user opens afterwards silently opens
    ///    invisible.
    /// </summary>
    public sealed class BackgroundDocument : IDisposable
    {
        private readonly SwSession _session;
        private readonly string _previousActiveTitle;
        private readonly string _title;
        private bool _closed;

        private BackgroundDocument(SwSession session, IModelDoc2 doc, string path, BatchDocType type, string previousActiveTitle)
        {
            _session = session;
            _previousActiveTitle = previousActiveTitle;
            Doc = doc;
            Path = path;
            DocType = type;
            _title = SafeTitle(doc);

            try
            {
                OpenedReadOnly = doc.IsOpenedReadOnly();
            }
            catch
            {
                OpenedReadOnly = true;
            }
        }

        public IModelDoc2 Doc { get; }
        public string Path { get; }
        public BatchDocType DocType { get; }

        /// <summary>True when SOLIDWORKS would not give write access, whether or not it was asked for.</summary>
        public bool OpenedReadOnly { get; }

        /// <summary>True when the file is open in this SOLIDWORKS session, visibly or not.</summary>
        public static bool IsOpenInSession(SwSession session, string path)
        {
            return SwGuard.Com("GetOpenDocumentByName", () => session.App.GetOpenDocumentByName(path)) != null;
        }

        public static BackgroundDocument Open(SwSession session, string path, bool readOnly)
        {
            if (!FolderIndex.TryGetDocType(path, out BatchDocType type))
                throw new ArgumentException("That is not a SOLIDWORKS part, assembly or drawing.");

            if (IsOpenInSession(session, path))
                throw new InvalidOperationException(AlreadyOpenMessage);

            var sw = session.App;
            int swType = (int)ToSwType(type);
            string previous = SafeTitle(session.ActiveDoc);

            int options = (int)swOpenDocOptions_e.swOpenDocOptions_Silent;
            if (readOnly) options |= (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly;

            bool wasVisible = true;
            try { wasVisible = sw.GetDocumentVisible(swType); } catch { }

            int errors = 0, warnings = 0;
            IModelDoc2 doc;

            try
            {
                sw.DocumentVisible(false, swType);
                doc = SwGuard.Com("OpenDoc6",
                    () => (IModelDoc2)sw.OpenDoc6(path, swType, options, "", ref errors, ref warnings));
            }
            finally
            {
                try
                {
                    sw.DocumentVisible(wasVisible, swType);
                }
                catch (Exception ex)
                {
                    session.Log.Error($"Could not restore document visibility: {ex.Message}");
                }
            }

            if (doc == null)
            {
                RestoreActive(session, previous);
                throw new InvalidOperationException("SOLIDWORKS could not open it: " + DescribeLoadError(errors));
            }

            if ((warnings & (int)swFileLoadWarning_e.swFileLoadWarning_AlreadyOpen) != 0)
            {
                // Not ours to close: it was already in memory.
                RestoreActive(session, previous);
                throw new InvalidOperationException(AlreadyOpenMessage);
            }

            return new BackgroundDocument(session, doc, path, type, previous);
        }

        /// <summary>Close without saving, and put the user's active document back.</summary>
        public void Dispose()
        {
            if (_closed) return;
            _closed = true;

            try
            {
                // Through the API, CloseDoc neither saves nor prompts. Anything
                // that was meant to reach disk was saved explicitly before this.
                _session.App.CloseDoc(_title ?? Path);

                if (_session.App.GetOpenDocumentByName(Path) != null)
                    _session.App.CloseDoc(Path);

                if (_session.App.GetOpenDocumentByName(Path) != null)
                    _session.Log.Error("A background document did not close and is still loaded.");
            }
            catch (Exception ex)
            {
                _session.Log.Error($"Could not close a background document: {ex.Message}");
            }

            RestoreActive(_session, _previousActiveTitle);
        }

        private const string AlreadyOpenMessage =
            "it is open in SOLIDWORKS. Close it first - a batch never touches an open file, because it " +
            "could save or discard edits that have not been saved yet";

        private static void RestoreActive(SwSession session, string previousTitle)
        {
            if (string.IsNullOrEmpty(previousTitle)) return;

            try
            {
                if (SafeTitle(session.ActiveDoc) == previousTitle) return;

                int error = 0;
                session.App.ActivateDoc3(previousTitle, false,
                    (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref error);
            }
            catch (Exception ex)
            {
                session.Log.Debug($"Could not re-activate '{previousTitle}': {ex.Message}");
            }
        }

        private static string SafeTitle(IModelDoc2 doc)
        {
            try
            {
                return doc?.GetTitle();
            }
            catch
            {
                return null;
            }
        }

        private static swDocumentTypes_e ToSwType(BatchDocType type)
        {
            switch (type)
            {
                case BatchDocType.Assembly: return swDocumentTypes_e.swDocASSEMBLY;
                case BatchDocType.Drawing: return swDocumentTypes_e.swDocDRAWING;
                default: return swDocumentTypes_e.swDocPART;
            }
        }

        private static string DescribeLoadError(int code)
        {
            if (code == 0) return "no reason was given";

            var known = new (swFileLoadError_e Flag, string Text)[]
            {
                (swFileLoadError_e.swFileNotFoundError, "the file was not found"),
                (swFileLoadError_e.swFutureVersion, "it was saved by a newer SOLIDWORKS than this one"),
                (swFileLoadError_e.swInvalidFileTypeError, "it is not a valid SOLIDWORKS file"),
                (swFileLoadError_e.swFileWithSameTitleAlreadyOpen,
                    "a different file with the same name is open, and SOLIDWORKS cannot hold two documents with the same name"),
                (swFileLoadError_e.swFileRequiresRepairError, "the file is damaged and needs repairing"),
                (swFileLoadError_e.swFileCriticalDataRepairError, "the file is damaged and needs repairing"),
                (swFileLoadError_e.swLowResourcesError, "SOLIDWORKS is low on memory"),
                (swFileLoadError_e.swApplicationBusy, "SOLIDWORKS is busy"),
                (swFileLoadError_e.swIdMatchError, "its internal ID does not match"),
                (swFileLoadError_e.swGenericError, "SOLIDWORKS reported a generic error"),
            };

            foreach (var (flag, text) in known)
                if ((code & (int)flag) != 0) return text;

            return $"error code {code}";
        }
    }
}
