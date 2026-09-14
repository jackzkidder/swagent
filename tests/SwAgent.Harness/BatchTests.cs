using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Batch;
using SwAgent.Core.Properties;
using SwAgent.Core.Session;
using SwAgent.Core.Tools;
using SwAgent.Core.Tools.Builtin;

namespace SwAgent.Harness
{
    /// <summary>
    /// Batch operations against real files on disk.
    ///
    /// The assertions are about the disk, not about messages. A dry run is
    /// proven to write nothing by comparing every file's size and timestamp; an
    /// apply is proven by reopening each file and reading the value back.
    ///
    /// Everything lives in a scratch folder that is deleted afterwards.
    /// </summary>
    public static class BatchTests
    {
        // Distinctive names, so the check that no file name reaches the model
        // cannot pass by accident.
        private const string Alpha = "zebra_bracket";
        private const string Beta = "okapi_flange";
        private const string Gamma = "quokka_spacer";
        private const string Locked = "narwhal_plate";

        private static readonly string[] Names = { Alpha, Beta, Gamma, Locked };

        private static JsonElement Args(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

        public static void IndexPreviewApply(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();

            string scratch = Path.Combine(Path.GetTempPath(), "swagent_batch_" + Guid.NewGuid().ToString("N"));
            string sub = Path.Combine(scratch, "sub");
            string output = Path.Combine(scratch, "out");
            Directory.CreateDirectory(sub);
            Directory.CreateDirectory(output);

            string pathA = Path.Combine(scratch, Alpha + ".sldprt");
            string pathB = Path.Combine(scratch, Beta + ".sldprt");
            string pathC = Path.Combine(scratch, Gamma + ".sldprt");
            string pathLocked = Path.Combine(scratch, Locked + ".sldprt");
            string pathSub = Path.Combine(sub, Alpha + ".sldprt");

            var created = new List<BatchPlan>();
            Action<BatchPlan> onCreated = p => created.Add(p);
            session.Batches.PlanCreated += onCreated;

            try
            {
                // ---- fixture ------------------------------------------------
                run.Step("build and save five parts");
                SavePlate(run, registry, session, pathA, 60, "A");
                SavePlate(run, registry, session, pathB, 70, "B");
                SavePlate(run, registry, session, pathC, 80, null);
                SavePlate(run, registry, session, pathLocked, 90, "A");
                SavePlate(run, registry, session, pathSub, 100, "A");

                if (!new[] { pathA, pathB, pathC, pathLocked, pathSub }.All(File.Exists))
                {
                    run.Fail("the fixture parts were not all saved, so the batch checks cannot run");
                    return;
                }

                File.SetAttributes(pathLocked, File.GetAttributes(pathLocked) | FileAttributes.ReadOnly);
                File.WriteAllBytes(Path.Combine(scratch, "~$" + Alpha + ".sldprt"), new byte[0]);
                File.WriteAllText(Path.Combine(scratch, "notes.txt"), "not a SOLIDWORKS file");

                int baselineDocs = session.App.GetDocumentCount();

                // ---- index --------------------------------------------------
                run.Step("sw_batch_index counts without naming");
                var index = registry.Execute("sw_batch_index", Args(new { folder = scratch }), session);
                run.Note(index.Text);
                run.Assert(index.Ok, "index succeeded");
                run.Assert(index.Text.StartsWith("4 SOLIDWORKS file(s)"),
                    "four parts at the top level; the lock file and the .txt are not counted");
                run.Assert(index.Text.Contains("1 are marked read-only"), "the read-only part is reported");
                run.Assert(index.Text.Contains("1 SOLIDWORKS lock file"), "the ~$ lock file is ignored, and said to be");
                AssertNoNames(run, index.Text, scratch, "index");

                var deep = registry.Execute("sw_batch_index", Args(new { folder = scratch, include_subfolders = true }), session);
                run.Assert(deep.Ok && deep.Text.StartsWith("5 SOLIDWORKS file(s)"), "including subfolders finds the fifth");

                var none = registry.Execute("sw_batch_index", Args(new { folder = scratch, pattern = "*.slddrw" }), session);
                run.Assert(none.Ok && none.Text.StartsWith("No SOLIDWORKS files"), "a pattern with no matches says so");

                run.Step("the index stays inside its folder");
                var escape = registry.Execute("sw_batch_index",
                    Args(new { folder = scratch, pattern = "..\\*.sldprt" }), session);
                run.Assert(!escape.Ok && escape.ErrorKind == "invalid_argument", "a pattern containing a path is rejected");

                var drive = registry.Execute("sw_batch_index",
                    Args(new { folder = Path.GetPathRoot(scratch), include_subfolders = true }), session);
                run.Assert(!drive.Ok, "a whole drive is refused");

                // ---- the gate -----------------------------------------------
                run.Step("no tool can apply a batch");
                run.Assert(!registry.Tools.Any(t => t.Name.IndexOf("apply", StringComparison.OrdinalIgnoreCase) >= 0),
                    "no registered tool name mentions apply");
                var invented = registry.Execute("sw_batch_apply", Args(new { id = "B1" }), session);
                run.Assert(invented.ErrorKind == "unknown_tool", "an invented sw_batch_apply is an unknown tool");

                // ---- dry run ------------------------------------------------
                run.Step("preview Revision=B while an unrelated part is active");
                Expect(run, registry, session, "sw_new_part", new { });
                string activeTitle = session.ActiveDoc?.GetTitle();
                int docsWithActive = session.App.GetDocumentCount();
                var before = Snapshot(scratch);

                var previewResult = registry.Execute("sw_batch_preview", Args(new
                {
                    folder = scratch,
                    pattern = "*.sldprt",
                    operation = "set_property",
                    property_name = "Revision",
                    property_value = "B",
                }), session);

                run.Note(previewResult.Text);
                run.Assert(previewResult.Ok, "preview succeeded");
                run.Assert(created.Count == 1, "exactly one plan was raised to the panel");
                if (!previewResult.Ok || created.Count != 1) return;

                var plan = created[0];
                run.Assert(Row(plan, pathA).Status == BatchRowStatus.Ready && Row(plan, pathA).Current == "A",
                    "A: will change, from A");
                run.Assert(Row(plan, pathB).Status == BatchRowStatus.Unchanged, "B: already B, so left alone");
                run.Assert(Row(plan, pathC).Status == BatchRowStatus.Ready && Row(plan, pathC).Current == null,
                    "C: will gain the property");
                run.Assert(Row(plan, pathLocked).Status == BatchRowStatus.Skipped
                           && (Row(plan, pathLocked).Reason ?? "").Contains("read-only"),
                    "the read-only part is skipped, and says why");

                run.Note("save history of a part saved on this seat: " +
                         string.Join(" | ", FileFormat.History(session.App, pathA)));
                // Real entries, from --versionprobe on installed sample parts.
                run.Assert(FileFormat.LatestRelease("18000[2024/232,2025/268]") == 2025
                           && FileFormat.LatestRelease("17000[2023/241]") == 2023
                           && FileFormat.LatestRelease("629[1997/218]") == 1997
                           && FileFormat.LatestRelease("not a history entry") == null,
                    "a save history entry is read by the newest release it names");
                run.Assert(plan.Rows.All(r => r.Warning == null),
                    "files saved by this seat carry no version warning");

                run.Assert(SameAs(before, Snapshot(scratch)),
                    "the dry run wrote nothing: every size and timestamp is unchanged");
                run.Assert(session.ActiveDoc?.GetTitle() == activeTitle, "the part that was active is active again");
                run.Assert(session.App.GetDocumentCount() == docsWithActive, "every background document was closed");
                run.Assert(session.App.GetDocumentVisible((int)swDocumentTypes_e.swDocPART),
                    "parts still open visibly afterwards");
                AssertNoNames(run, previewResult.Text, scratch, "preview");
                CloseAll(session);

                run.Step("nothing applies without the user's approval");
                run.AssertThrows(() => BatchRunner.ApplyRow(session, plan, Row(plan, pathA)),
                    "applying a row of a plan that is still pending");

                // ---- apply --------------------------------------------------
                run.Step("apply, exactly as the panel's Apply button does");
                DateTime bTime = File.GetLastWriteTimeUtc(pathB);
                Apply(session, plan);
                run.Note(plan.DescribeOutcome());

                run.Assert(Row(plan, pathA).Status == BatchRowStatus.Applied, $"A applied ({Row(plan, pathA).Result})");
                run.Assert(Row(plan, pathC).Status == BatchRowStatus.Applied, $"C applied ({Row(plan, pathC).Result})");
                run.Assert(File.GetLastWriteTimeUtc(pathB) == bTime, "B was not rewritten for nothing");
                run.Assert(ReadProperty(session, pathA, "Revision") == "B", "A reads back Revision = B from disk");
                run.Assert(ReadProperty(session, pathC, "Revision") == "B", "C reads back Revision = B from disk");
                run.Assert(ReadProperty(session, pathLocked, "Revision") == "A", "the read-only part still says A");
                run.Assert(session.App.GetDocumentCount() == baselineDocs, "nothing is left open after applying");
                run.AssertThrows(() => session.Batches.BeginApply(plan.Id), "applying the same plan twice");

                // ---- an open file is never touched --------------------------
                run.Step("a file open in SOLIDWORKS is skipped, and stays open");
                int loadErrors = 0, loadWarnings = 0;
                var openC = session.App.OpenDoc6(pathC, (int)swDocumentTypes_e.swDocPART,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref loadErrors, ref loadWarnings);
                run.Assert(openC != null, "C opened, as the user would open it");

                var withOpen = Preview(run, registry, session, created, new
                {
                    folder = scratch, pattern = "*.sldprt", operation = "set_property",
                    property_name = "Revision", property_value = "C",
                });

                if (withOpen != null)
                {
                    var rowC = Row(withOpen, pathC);
                    run.Assert(rowC.Status == BatchRowStatus.Skipped && (rowC.Reason ?? "").Contains("open in SOLIDWORKS"),
                        "C is skipped because it is open");
                    run.Assert(BackgroundDocument.IsOpenInSession(session, pathC),
                        "C is still open: the preview did not close the user's document");
                }

                CloseAll(session);

                // ---- stale --------------------------------------------------
                run.Step("a file changed after the preview is not applied");
                var stale = Preview(run, registry, session, created, new
                {
                    folder = scratch, pattern = "*.sldprt", operation = "set_property",
                    property_name = "Revision", property_value = "C",
                });

                if (stale != null)
                {
                    File.SetLastWriteTimeUtc(pathA, DateTime.UtcNow.AddMinutes(5));
                    Apply(session, stale);

                    run.Assert(Row(stale, pathA).Status == BatchRowStatus.Skipped
                               && (Row(stale, pathA).Reason ?? "").Contains("modified after the preview"),
                        "A is skipped: it was modified after the preview");
                    run.Assert(Row(stale, pathC).Status == BatchRowStatus.Applied, "C is still applied");
                    run.Assert(ReadProperty(session, pathA, "Revision") == "B", "A kept its value");
                }

                // ---- discard ------------------------------------------------
                run.Step("a discarded plan cannot be applied");
                var discarded = Preview(run, registry, session, created, new
                {
                    folder = scratch, pattern = "*.sldprt", operation = "set_property",
                    property_name = "Revision", property_value = "D",
                });

                if (discarded != null)
                {
                    run.Assert(session.Batches.Discard(discarded.Id), "discard accepted");
                    run.AssertThrows(() => session.Batches.BeginApply(discarded.Id), "applying a discarded plan");
                }

                // ---- material -----------------------------------------------
                run.Step("an unknown material rejects the whole plan");
                before = Snapshot(scratch);
                var bogus = registry.Execute("sw_batch_preview", Args(new
                {
                    folder = scratch, pattern = "*.sldprt", operation = "set_material", material = "Unobtainium 9000",
                }), session);
                run.Note(bogus.Text);
                run.Assert(!bogus.Ok && bogus.Text.Contains("no material called"), "rejected with a readable message");
                run.Assert(SameAs(before, Snapshot(scratch)), "and the in-memory trial wrote nothing");

                run.Step("set_material 6061 Alloy");
                var material = Preview(run, registry, session, created, new
                {
                    folder = scratch, pattern = "*.sldprt", operation = "set_material", material = "6061 Alloy",
                });

                if (material == null)
                {
                    run.Note("6061 Alloy is not in this installation's material library - skipping");
                }
                else
                {
                    Apply(session, material);
                    run.Note(material.DescribeOutcome());
                    run.Assert(Row(material, pathA).Status == BatchRowStatus.Applied,
                        $"A applied, despite its timestamp having been set in the future ({Row(material, pathA).Result ?? Row(material, pathA).Reason})");
                    run.Assert(ReadMaterial(session, pathB) == "6061 Alloy", "B reads back 6061 Alloy from disk");
                    run.Assert(Row(material, pathLocked).Status == BatchRowStatus.Skipped, "the read-only part is skipped");
                }

                // ---- export -------------------------------------------------
                run.Step("export STEP, including subfolders");
                var export = Preview(run, registry, session, created, new
                {
                    folder = scratch, include_subfolders = true, operation = "export",
                    export_format = "step", output_folder = output,
                });

                if (export != null)
                {
                    var zebras = export.Rows
                        .Where(r => Path.GetFileNameWithoutExtension(r.File.FullPath)
                            .Equals(Alpha, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    run.Assert(zebras.Count == 2
                               && zebras.Count(r => r.Status == BatchRowStatus.Skipped
                                                    && (r.Reason ?? "").Contains("same name")) == 1,
                        "two files that would export to the same name: the second is skipped");
                    run.Assert(Row(export, pathLocked).Status == BatchRowStatus.Ready,
                        "the read-only part can still be exported");
                    AssertNoNames(run, export.DescribeForModel(), null, "export preview");

                    Apply(session, export);
                    run.Note(export.DescribeOutcome());

                    var written = export.Rows.Where(r => r.Status == BatchRowStatus.Applied).ToList();
                    run.Assert(written.Count == 4, $"four STEP files written (got {written.Count})");
                    run.Assert(written.All(r => IsStep(r.OutputPath)), "each one is genuinely ISO-10303 STEP");

                    foreach (var failed in export.Rows.Where(r => r.Status == BatchRowStatus.Failed))
                        run.Note($"  failed: {failed.File.RelativePath}: {failed.Result}");

                    run.Step("exporting again without overwrite replaces nothing");
                    var again = Preview(run, registry, session, created, new
                    {
                        folder = scratch, include_subfolders = true, operation = "export",
                        export_format = "step", output_folder = output,
                    });
                    run.Assert(again != null && again.CountOf(BatchRowStatus.Ready) == 0, "every existing output is skipped");

                    var pdf = Preview(run, registry, session, created, new
                    {
                        folder = scratch, operation = "export", export_format = "pdf", output_folder = output,
                    });
                    run.Assert(pdf != null && pdf.Rows.All(r => r.Status == BatchRowStatus.Skipped),
                        "parts are not exported to PDF");
                }

                run.Assert(session.App.GetDocumentCount() == 0, "no documents left open by the batch");
            }
            finally
            {
                session.Batches.PlanCreated -= onCreated;
                CloseAll(session);

                try
                {
                    if (File.Exists(pathLocked)) File.SetAttributes(pathLocked, FileAttributes.Normal);
                    if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
                }
                catch (Exception ex)
                {
                    run.Note($"(could not clean {scratch}: {ex.Message})");
                }
            }
        }

        /// <summary>
        /// Diagnostic: print what SOLIDWORKS reports as each file's save history.
        /// <c>SwAgent.Harness.exe --versionprobe file1.sldprt file2.sldprt</c>
        /// </summary>
        public static void VersionProbe(SwSession session, string[] args, int from)
        {
            Console.WriteLine($"Seat: revision {session.App.RevisionNumber()}, release {FileFormat.SeatYear(session.App)}");

            for (int i = from; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal)) continue;

                Console.WriteLine(args[i]);
                try
                {
                    string[] history = FileFormat.History(session.App, args[i]);
                    Console.WriteLine($"  history ({history.Length}): {string.Join(" | ", history)}");

                    var check = FileFormat.Check(session.App, args[i]);
                    Console.WriteLine($"  known={check.Known} upgrade={check.WouldUpgrade} detail={check.Detail}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  failed: {ex.Message}");
                }
            }
        }

        // ---- helpers ---------------------------------------------------------

        private static void SavePlate(TestRun run, ToolRegistry registry, SwSession session,
                                      string path, double width, string revision)
        {
            Expect(run, registry, session, "sw_new_part", new { });
            Expect(run, registry, session, "sw_sketch_open", new { plane = "front" });
            Expect(run, registry, session, "sw_sketch_rect", new { width_mm = width, height_mm = 40 });
            var closed = Expect(run, registry, session, "sw_sketch_close", new { });
            string sketch = ExtractQuoted(closed.Text) ?? "Sketch1";
            Expect(run, registry, session, "sw_extrude", new { sketch_name = sketch, depth_mm = 10 });

            if (revision != null)
                Expect(run, registry, session, "sw_property_write", new { name = "Revision", value = revision });

            Expect(run, registry, session, "sw_save_as", new { path, overwrite = false });
            CloseAll(session);
        }

        private static BatchPlan Preview(TestRun run, ToolRegistry registry, SwSession session,
                                         List<BatchPlan> created, object args)
        {
            int count = created.Count;
            var result = registry.Execute("sw_batch_preview", Args(args), session);

            if (!result.Ok)
            {
                run.Note("preview refused: " + result.Text);
                return null;
            }

            run.Note(result.Text.Split('\n')[1].Trim());
            return created.Count > count ? created[created.Count - 1] : null;
        }

        private static void Apply(SwSession session, BatchPlan plan)
        {
            session.Batches.BeginApply(plan.Id);
            foreach (var row in plan.Rows)
                BatchRunner.ApplyRow(session, plan, row);
            session.Batches.Finish(plan);
        }

        private static BatchRow Row(BatchPlan plan, string path)
        {
            string full = Path.GetFullPath(path);
            return plan.Rows.FirstOrDefault(r => string.Equals(r.File.FullPath, full, StringComparison.OrdinalIgnoreCase))
                   ?? new BatchRow { Status = (BatchRowStatus)(-1), File = new IndexedFile { FullPath = full } };
        }

        private static string ReadProperty(SwSession session, string path, string name)
        {
            using (var bg = BackgroundDocument.Open(session, path, readOnly: true))
            {
                var value = CustomProperties.Read(bg.Doc, name);
                return value.Exists ? value.RawValue : null;
            }
        }

        private static string ReadMaterial(SwSession session, string path)
        {
            using (var bg = BackgroundDocument.Open(session, path, readOnly: true))
                return ((IPartDoc)bg.Doc).GetMaterialPropertyName2("", out string _);
        }

        private static bool IsStep(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            using (var reader = new StreamReader(path))
            {
                char[] head = new char[200];
                int read = reader.Read(head, 0, head.Length);
                return new string(head, 0, read).Contains("ISO-10303");
            }
        }

        private static Dictionary<string, (long Size, DateTime Written)> Snapshot(string folder)
        {
            return Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(
                f => f,
                f =>
                {
                    var info = new FileInfo(f);
                    return (info.Length, info.LastWriteTimeUtc);
                },
                StringComparer.OrdinalIgnoreCase);
        }

        private static bool SameAs(Dictionary<string, (long Size, DateTime Written)> a,
                                   Dictionary<string, (long Size, DateTime Written)> b)
        {
            return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v.Equals(kv.Value));
        }

        /// <param name="folder">Also check the folder path is absent. Null when the model supplied the path itself.</param>
        private static void AssertNoNames(TestRun run, string text, string folder, string what)
        {
            bool leaked = (folder != null && text.IndexOf(folder, StringComparison.OrdinalIgnoreCase) >= 0)
                          || Names.Any(n => text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            run.Assert(!leaked, $"the {what} result names no file" + (folder != null ? " or folder" : ""));
        }

        private static ToolResult Expect(TestRun run, ToolRegistry registry, SwSession session, string tool, object args)
        {
            var result = registry.Execute(tool, Args(args), session);
            if (!result.Ok) run.Fail($"{tool} failed: {result.Text}");
            return result;
        }

        /// <summary>Close whatever is open without saving.</summary>
        private static void CloseAll(SwSession session)
        {
            for (int i = 0; i < 12; i++)
            {
                try
                {
                    var doc = session.ActiveDoc;
                    if (doc == null) return;
                    session.App.CloseDoc(doc.GetTitle());
                }
                catch
                {
                    return;
                }
            }
        }

        private static string ExtractQuoted(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int a = text.IndexOf('\'');
            if (a < 0) return null;
            int b = text.IndexOf('\'', a + 1);
            return b < 0 ? null : text.Substring(a + 1, b - a - 1);
        }
    }
}
