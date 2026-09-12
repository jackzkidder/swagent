using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SolidWorks.Interop.sldworks;
using SwAgent.Core.Session;
using SwAgent.Core.Tools;
using SwAgent.Core.Tools.Builtin;

namespace SwAgent.Harness
{
    /// <summary>
    /// The half of the product that is deterministic: material, properties,
    /// save, drawing, export.
    ///
    /// This is where "finishes the job" is either true or it isn't. The
    /// geometry half has a ceiling and a failure rate; this half either writes
    /// a STEP file to disk or it does not, so it is worth asserting on the
    /// actual bytes rather than on a success message.
    ///
    /// Everything is written to a scratch directory and deleted afterwards -
    /// the suite must not leave files in the user's folders.
    /// </summary>
    public static class DeliverableTests
    {
        private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;
        private static JsonElement NoArgs => Args("{}");

        public static void ProducesTheWholeDeliverable(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();

            string scratch = Path.Combine(Path.GetTempPath(), "swagent_deliverable_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);

            string partPath = Path.Combine(scratch, "plate.sldprt");
            string stepPath = Path.Combine(scratch, "plate.step");
            string pdfPath = Path.Combine(scratch, "plate.pdf");

            try
            {
                // ---- model ------------------------------------------------
                run.Step("model a 60 x 40 x 10 plate");
                Expect(run, registry, session, "sw_new_part", NoArgs);
                Expect(run, registry, session, "sw_sketch_open", Args(@"{""plane"":""front""}"));
                Expect(run, registry, session, "sw_sketch_rect", Args(@"{""width_mm"":60,""height_mm"":40}"));
                var closed = Expect(run, registry, session, "sw_sketch_close", NoArgs);
                string sketch = ExtractQuoted(closed.Text) ?? "Sketch1";
                Expect(run, registry, session, "sw_extrude",
                    Args($@"{{""sketch_name"":""{sketch}"",""depth_mm"":10}}"));

                // ---- material ---------------------------------------------
                run.Step("sw_material_set 6061 Alloy");
                var material = registry.Execute("sw_material_set",
                    Args(@"{""material"":""6061 Alloy""}"), session);
                run.Note(material.Text);

                if (material.Ok)
                {
                    // 24 cm3 of 6061 at 2700 kg/m3 is 64.8 g. Asserting the
                    // number is the only way to know the material actually
                    // applied rather than the call merely returning.
                    run.Assert(material.Text.Contains("64.8") || material.Text.Contains("64.7"),
                        "mass reflects aluminium density (expected ~64.8 g)");
                }
                else
                {
                    // Material libraries vary between installs; that is a
                    // legitimate difference, not a bug.
                    run.Note("6061 Alloy is not in this installation's material library - skipping mass check");
                }

                // ---- properties -------------------------------------------
                run.Step("sw_property_write and read back");
                Expect(run, registry, session, "sw_property_write",
                    Args(@"{""name"":""PartNo"",""value"":""SWA-0001""}"));
                Expect(run, registry, session, "sw_property_write",
                    Args(@"{""name"":""Description"",""value"":""Test plate""}"));

                var read = Expect(run, registry, session, "sw_property_read", Args(@"{""name"":""PartNo""}"));
                run.Assert(read.Text.Contains("SWA-0001"), "PartNo reads back as written");
                run.Note(read.Text);

                var all = Expect(run, registry, session, "sw_property_read", NoArgs);
                run.Assert(all.Text.Contains("Description"), "listing includes both properties");

                // ---- save --------------------------------------------------
                run.Step("sw_save_as");
                var saved = registry.Execute("sw_save_as",
                    Args(JsonSerializer.Serialize(new { path = partPath, overwrite = false })), session);
                run.Note(saved.Text);
                run.Assert(saved.Ok, "part saved");
                run.Assert(File.Exists(partPath), "the .sldprt actually exists on disk");

                run.Step("saving again without overwrite is refused");
                var refused = registry.Execute("sw_save_as",
                    Args(JsonSerializer.Serialize(new { path = partPath, overwrite = false })), session);
                run.Assert(!refused.Ok, "a second save without overwrite is refused rather than silent");
                run.Note($"  {refused.Text}");

                // ---- export ------------------------------------------------
                run.Step("sw_export STEP");
                var step = registry.Execute("sw_export",
                    Args(JsonSerializer.Serialize(new { path = stepPath, overwrite = false })), session);
                run.Note(step.Text);
                run.Assert(step.Ok, "STEP export reported success");
                run.Assert(File.Exists(stepPath), "the .step actually exists on disk");

                if (File.Exists(stepPath))
                {
                    var info = new FileInfo(stepPath);
                    run.Assert(info.Length > 500, $"STEP file has real content ({info.Length:N0} bytes)");

                    // A STEP file that is not a STEP file would still have a
                    // size; check it is what it claims to be.
                    string head = File.ReadAllText(stepPath).Substring(0, Math.Min(200, (int)info.Length));
                    run.Assert(head.Contains("ISO-10303"), "the file is genuinely ISO-10303 STEP");
                }

                run.Step("an unsupported extension is refused");
                var bogus = registry.Execute("sw_export",
                    Args(JsonSerializer.Serialize(new { path = Path.Combine(scratch, "x.docx"), overwrite = true })), session);
                run.Assert(!bogus.Ok, ".docx is rejected with a readable message");
                run.Note($"  {Truncate(bogus.Text, 110)}");

                run.Step("a non-existent folder is refused rather than created");
                var badDir = registry.Execute("sw_export",
                    Args(JsonSerializer.Serialize(new { path = Path.Combine(scratch, "nope", "x.step"), overwrite = true })), session);
                run.Assert(!badDir.Ok, "writing into a folder that does not exist is refused");

                // ---- drawing -----------------------------------------------
                run.Step("sw_drawing_create");
                var drawing = registry.Execute("sw_drawing_create", NoArgs, session);
                run.Note(drawing.Text);

                if (!drawing.Ok)
                {
                    // No drawing template configured is a legitimate state on a
                    // fresh install, and the message should say so.
                    run.Note("drawing not created - see message above");
                }
                else
                {
                    run.Assert(drawing.Text.Contains("angle projection"),
                        "the projection convention is reported, not assumed");
                    run.Assert(drawing.Text.Contains("view(s) placed"), "views were placed");

                    run.Step("sw_drawing_insert_dimensions");
                    var dims = Expect(run, registry, session, "sw_drawing_insert_dimensions", NoArgs);
                    run.Note(Truncate(dims.Text, 150));
                    run.Assert(dims.Text.IndexOf("tidy", StringComparison.OrdinalIgnoreCase) >= 0
                               || dims.Text.IndexOf("by hand", StringComparison.OrdinalIgnoreCase) >= 0,
                        "the result is honest about needing human cleanup");

                    run.Step("sw_drawing_fill_titleblock reports unmatched fields");
                    var title = Expect(run, registry, session, "sw_drawing_fill_titleblock",
                        Args(@"{""field"":""DrawnBy"",""value"":""SwAgent""}"));
                    run.Note(Truncate(title.Text, 190));
                    run.Assert(title.Text.Contains("title block") || title.Text.Contains("sheet format"),
                        "the result says whether the field will actually show");

                    run.Step("sw_export PDF of the drawing");
                    var pdf = registry.Execute("sw_export",
                        Args(JsonSerializer.Serialize(new { path = pdfPath, overwrite = false })), session);
                    run.Note(pdf.Text);
                    run.Assert(pdf.Ok, "PDF export reported success");
                    run.Assert(File.Exists(pdfPath), "the .pdf actually exists on disk");

                    if (File.Exists(pdfPath))
                    {
                        byte[] head = new byte[5];
                        using (var fs = File.OpenRead(pdfPath)) fs.Read(head, 0, 5);
                        run.Assert(System.Text.Encoding.ASCII.GetString(head) == "%PDF-",
                            "the file is genuinely a PDF");
                    }
                }
            }
            finally
            {
                CloseEverything(session);
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
                catch (Exception ex) { run.Note($"(could not clean {scratch}: {ex.Message})"); }
            }
        }

        /// <summary>
        /// Close whatever is open without saving. The drawing has to go before
        /// the part it references, or SOLIDWORKS keeps the part open.
        /// </summary>
        private static void CloseEverything(SwSession session)
        {
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    var doc = session.ActiveDoc;
                    if (doc == null) return;

                    // Discard changes so nothing prompts and nothing is written.
                    try { doc.SetSaveFlag(); } catch { }
                    session.App.CloseDoc(doc.GetTitle());
                }
                catch
                {
                    return;
                }
            }
        }

        private static ToolResult Expect(TestRun run, ToolRegistry registry, SwSession session,
                                         string tool, JsonElement args)
        {
            var result = registry.Execute(tool, args, session);
            if (!result.Ok) run.Fail($"{tool} failed: {result.Text}");
            return result;
        }

        private static string ExtractQuoted(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int a = text.IndexOf('\'');
            if (a < 0) return null;
            int b = text.IndexOf('\'', a + 1);
            return b < 0 ? null : text.Substring(a + 1, b - a - 1);
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
