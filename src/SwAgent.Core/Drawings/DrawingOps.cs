using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Session;

namespace SwAgent.Core.Drawings
{
    /// <summary>What a drawing creation produced.</summary>
    public sealed class DrawingOutcome
    {
        public bool Succeeded { get; set; }
        public string Message { get; set; }
        public string TemplateUsed { get; set; }
        public string SheetFormat { get; set; }
        public bool FirstAngle { get; set; }
        public int ViewCount { get; set; }
    }

    /// <summary>
    /// Drawing creation, from the user's template and their sheet format.
    ///
    /// Two rules that are easy to get wrong and expensive to get wrong.
    ///
    /// First, the template is the user's. Shipping our own sheet format would
    /// mean every drawing comes out in our title block rather than their
    /// company's, which makes the output unusable no matter how good the views
    /// are.
    ///
    /// Second, projection convention is read, never assumed.
    /// CreateThirdAngleViews2 hardcodes the North American convention; first
    /// angle is standard across most of Europe and Asia. Assuming third angle
    /// means silently shipping drawings that are wrong in a way a machinist
    /// will not notice until they have cut the part backwards.
    /// </summary>
    public static class DrawingOps
    {
        /// <summary>
        /// Create a drawing of the active part, with standard views.
        /// </summary>
        public static DrawingOutcome CreateWithStandardViews(SwSession session)
        {
            var part = session.RequirePart();

            // A drawing view references the model by path, so the part must
            // exist on disk. An unsaved part produces an empty drawing and no
            // error worth reading.
            string modelPath = part.GetPathName();
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                return new DrawingOutcome
                {
                    Succeeded = false,
                    Message = "The part has not been saved yet, and a drawing has to reference a saved " +
                              "file. Save the part first (sw_save_as), then create the drawing.",
                };
            }

            string template = session.App.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing);

            if (string.IsNullOrWhiteSpace(template) || !File.Exists(template))
            {
                return new DrawingOutcome
                {
                    Succeeded = false,
                    Message = "No default drawing template is configured in this SOLIDWORKS installation " +
                              "(Tools > Options > Default Templates). SwAgent uses your template, and will " +
                              "not substitute one of its own.",
                };
            }

            var drawing = session.App.NewDocument(template, 0, 0, 0) as IModelDoc2;
            if (drawing == null)
            {
                return new DrawingOutcome
                {
                    Succeeded = false,
                    Message = $"SOLIDWORKS refused to create a drawing from '{template}'.",
                };
            }

            var drawingDoc = (IDrawingDoc)drawing;

            // Read the convention out of the sheet the template gave us.
            bool firstAngle = IsFirstAngle(drawingDoc, out string sheetFormat);

            bool created = firstAngle
                ? drawingDoc.Create1stAngleViews2(modelPath)
                : drawingDoc.Create3rdAngleViews2(modelPath);

            drawing.ViewZoomtofit2();
            session.Rebuild();

            int viewCount = CountViews(drawingDoc);

            return new DrawingOutcome
            {
                Succeeded = created && viewCount > 0,
                TemplateUsed = Path.GetFileName(template),
                SheetFormat = sheetFormat,
                FirstAngle = firstAngle,
                ViewCount = viewCount,
                Message = created && viewCount > 0
                    ? $"Drawing created from your template '{Path.GetFileName(template)}' " +
                      $"using {(firstAngle ? "FIRST" : "THIRD")} angle projection " +
                      $"(read from the sheet, not assumed). {viewCount} view(s) placed."
                    : "SOLIDWORKS did not place any views. The model may not be suitable for standard views.",
            };
        }

        /// <summary>
        /// Read the sheet's projection convention.
        ///
        /// ISheet.GetProperties returns the values SetProperties takes, in the
        /// same order: paper size, template, scale numerator, scale
        /// denominator, first-angle flag, width, height. Index 4 is the one
        /// that decides whether a machinist reads this drawing correctly.
        /// </summary>
        public static bool IsFirstAngle(IDrawingDoc drawingDoc, out string sheetFormat)
        {
            sheetFormat = null;

            try
            {
                var sheet = (ISheet)drawingDoc.GetCurrentSheet();
                if (sheet == null) return false;

                try { sheetFormat = sheet.GetSheetFormatName(); } catch { }

                var properties = sheet.GetProperties() as double[];
                if (properties != null && properties.Length > 4)
                    return Math.Abs(properties[4]) > 0.5;
            }
            catch
            {
                // Fall through to the North American default, which is what
                // SOLIDWORKS itself assumes.
            }

            return false;
        }

        /// <summary>
        /// Insert model dimensions onto the views.
        ///
        /// Be honest about what this produces. InsertModelAnnotations3 is
        /// "Insert Model Items": it dumps every dimension from the model onto
        /// whichever view it likes, overlapping, and the result needs a human
        /// to arrange it. We ask it to skip duplicates and respect sketch
        /// placement, which helps, but the output is a drawing that is most of
        /// the way there - not a finished one.
        /// </summary>
        public static string InsertDimensions(SwSession session)
        {
            var drawing = session.RequireDrawing();
            var drawingDoc = (IDrawingDoc)drawing;

            var inserted = drawingDoc.InsertModelAnnotations3(
                (int)swImportModelItemsSource_e.swImportModelItemsFromEntireModel,
                (int)swInsertAnnotation_e.swInsertDimensionsMarkedForDrawing,
                AllViews: true,
                DuplicateDims: false,       // skip duplicates across views
                HiddenFeatureDims: false,
                UsePlacementInSketch: true) as object[];

            int count = inserted?.Length ?? 0;
            session.Rebuild();

            if (count == 0)
            {
                return "No dimensions were inserted. The model's dimensions may not be marked for " +
                       "drawings, in which case they have to be added on the drawing by hand.";
            }

            return $"{count} dimension(s) inserted across the views. " +
                   "They will be overlapping and poorly placed - Insert Model Items always is. " +
                   "The drawing is most of the way there and needs a few minutes of tidying.";
        }

        /// <summary>Which property fields the user's sheet format actually references.</summary>
        public static List<string> ReadTitleBlockFields(SwSession session)
        {
            var drawing = session.RequireDrawing();
            var fields = new List<string>();

            try
            {
                var sheet = (ISheet)((IDrawingDoc)drawing).GetCurrentSheet();
                if (sheet == null) return fields;

                // The sheet format is a view like any other; its notes carry
                // the $PRP links that make a title block populate.
                var views = ((IDrawingDoc)drawing).GetViews() as object[];
                if (views == null) return fields;

                foreach (object sheetObj in views)
                {
                    var viewsOnSheet = sheetObj as object[];
                    if (viewsOnSheet == null) continue;

                    foreach (object viewObj in viewsOnSheet)
                    {
                        var view = viewObj as IView;
                        if (view == null) continue;

                        var notes = view.GetNotes() as object[];
                        if (notes == null) continue;

                        foreach (object noteObj in notes)
                        {
                            var note = noteObj as INote;
                            if (note == null) continue;

                            string text = null;
                            try { text = note.GetText(); } catch { }
                            if (string.IsNullOrEmpty(text)) continue;

                            foreach (string name in ExtractPropertyReferences(text))
                                if (!fields.Contains(name, StringComparer.OrdinalIgnoreCase))
                                    fields.Add(name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                session.Log.Debug($"Could not read title block fields: {ex.Message}");
            }

            return fields;
        }

        /// <summary>
        /// Pull property names out of note text.
        ///
        /// Title block notes link to properties as $PRP:"NAME" (a property on
        /// the drawing) or $PRPSHEET:"NAME" (a property on the model the sheet
        /// references). Both forms matter: writing to the wrong one leaves the
        /// field blank.
        /// </summary>
        internal static IEnumerable<string> ExtractPropertyReferences(string noteText)
        {
            var matches = Regex.Matches(
                noteText,
                @"\$PRP(?:SHEET)?\s*:\s*""([^""]+)""",
                RegexOptions.IgnoreCase);

            foreach (Match m in matches)
                if (m.Groups.Count > 1) yield return m.Groups[1].Value;
        }

        private static int CountViews(IDrawingDoc drawingDoc)
        {
            try
            {
                var sheets = drawingDoc.GetViews() as object[];
                if (sheets == null) return 0;

                int count = 0;
                foreach (object sheetObj in sheets)
                {
                    var views = sheetObj as object[];
                    if (views == null) continue;

                    // The first entry on each sheet is the sheet format itself,
                    // not a drawing view.
                    count += Math.Max(0, views.Length - 1);
                }

                return count;
            }
            catch
            {
                return 0;
            }
        }
    }
}
