using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SwAgent.Core.Drawings;
using SwAgent.Core.Files;
using SwAgent.Core.Properties;
using SwAgent.Core.Session;

namespace SwAgent.Core.Tools.Builtin
{
    /// <summary>Read a custom property the way a BOM resolves it.</summary>
    public sealed class PropertyReadTool : SwTool
    {
        public override string Name => "sw_property_read";
        public override string Description =>
            "Read a custom property from the active document. Checks the active configuration first, " +
            "then the file level - the same order SOLIDWORKS uses for BOMs and title blocks. " +
            "Omit the name to list every property.";

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Text("name", "Property name, e.g. 'PartNo' or 'Description'. Omit to list all.",
                required: false),
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            string name = args.GetString("name");

            if (string.IsNullOrWhiteSpace(name))
            {
                var all = CustomProperties.ReadAll(session);
                if (all.Count == 0)
                    return ToolResult.Success("The document has no custom properties.");

                var sb = new StringBuilder($"{all.Count} custom propert(ies):");
                foreach (var p in all.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine();
                    sb.Append($"  {p.Name} = {p.ResolvedValue}");
                    if (p.RawValue != p.ResolvedValue) sb.Append($"  (stored as: {p.RawValue})");
                    sb.Append($"  [{p.FoundOn.ToString().ToLowerInvariant()} level]");
                }
                return ToolResult.Success(sb.ToString());
            }

            var value = CustomProperties.Read(session, name);
            if (!value.Exists)
                return ToolResult.Success($"There is no property called '{name}' on this document.");

            string detail = value.RawValue != value.ResolvedValue
                ? $"'{name}' = {value.ResolvedValue} (stored as '{value.RawValue}')"
                : $"'{name}' = {value.ResolvedValue}";

            return ToolResult.Success($"{detail}, on the {value.FoundOn.ToString().ToLowerInvariant()} level.");
        }
    }

    /// <summary>Write a custom property.</summary>
    public sealed class PropertyWriteTool : SwTool
    {
        public override string Name => "sw_property_write";
        public override string Description =>
            "Set a custom property on the active document. By default it updates the property where it " +
            "already lives, and creates new ones at the file level - which is what title blocks usually read.";

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Text("name", "Property name, e.g. 'PartNo', 'Description', 'Material', 'Revision'."),
            ToolParameter.Text("value", "The value to set."),
            ToolParameter.Choice("tier",
                "Which tier to write to. 'auto' updates where it already exists, else file level.",
                new[] { "auto", "file", "configuration" }, required: false, defaultValue: "auto"),
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            string name = args.GetString("name");
            string value = args.GetString("value");
            var tier = ParseTier(args.GetString("tier"));

            var written = CustomProperties.Write(session, name, value, tier);

            return ToolResult.Success(
                $"'{name}' set to '{value}' on the {written.ToString().ToLowerInvariant()} level.");
        }

        private static PropertyTier ParseTier(string value)
        {
            switch ((value ?? "auto").ToLowerInvariant())
            {
                case "file": return PropertyTier.File;
                case "configuration": return PropertyTier.Configuration;
                default: return PropertyTier.Auto;
            }
        }
    }

    /// <summary>Assign a material, which is what makes mass meaningful.</summary>
    public sealed class MaterialSetTool : SwTool
    {
        public override string Name => "sw_material_set";
        public override string Description =>
            "Assign a material to the active part. Mass depends on density, so set this before quoting " +
            "a mass. Use the exact SOLIDWORKS material name, e.g. '6061 Alloy', 'AISI 304', 'ABS'.";

        public override bool MutatesModel => true;

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Text("material", "Material name as it appears in the SOLIDWORKS material library."),
            ToolParameter.Text("database",
                "Material database name. Leave empty for the standard SOLIDWORKS materials.",
                required: false, defaultValue: ""),
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            string material = args.GetString("material");
            string database = args.GetString("database") ?? string.Empty;

            var doc = session.RequirePart();
            var part = (IPartDoc)doc;

            // An empty database name means the default library.
            part.SetMaterialPropertyName2("", database, material);
            session.Rebuild();

            string applied = Inspection.PartMeasure.ActiveMaterial(session);
            if (string.IsNullOrWhiteSpace(applied))
            {
                return ToolResult.Failure(
                    $"SOLIDWORKS did not apply a material called '{material}'. Check the exact name in " +
                    "the material library - they are case- and punctuation-sensitive, e.g. '6061 Alloy' " +
                    "rather than 'aluminium 6061'.",
                    "material_not_found");
            }

            var m = Inspection.PartMeasure.OfActivePart(session);
            return ToolResult.Success(
                $"Material set to '{applied}'. Mass is now {m.MassGrams:0.##} g " +
                $"(density {m.DensityKgPerM3:0.#} kg/m3, volume {m.VolumeCm3:0.###} cm3).");
        }
    }

    /// <summary>Save the active document to an explicit path.</summary>
    public sealed class SaveAsTool : SwTool
    {
        public override string Name => "sw_save_as";
        public override string Description =>
            "Save the active document to an explicit full path. Required before creating a drawing, " +
            "because a drawing view references a saved file. Will not overwrite unless told to.";

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Text("path", "Full path including extension, e.g. C:\\Parts\\bracket.sldprt"),
            ToolParameter.Flag("overwrite", "Replace the file if it already exists."),
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            string path = args.GetString("path");
            bool overwrite = args.GetBool("overwrite");

            var outcome = Exporter.Export(session, path, overwrite, Exporter.WriteKind.Native);

            return outcome.Succeeded
                ? ToolResult.Success(outcome.Message)
                : ToolResult.Failure(outcome.Message, "save_failed");
        }
    }

    /// <summary>Export to a neutral format.</summary>
    public sealed class ExportTool : SwTool
    {
        public override string Name => "sw_export";
        public override string Description =>
            "Export the active document. The format comes from the file extension: .step for machining, " +
            ".stl for 3D printing, .pdf for a drawing, .dxf for laser cutting. Will not overwrite unless told to.";

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Text("path", "Full output path including extension, e.g. C:\\Parts\\bracket.step"),
            ToolParameter.Flag("overwrite", "Replace the file if it already exists."),
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            string path = args.GetString("path");
            bool overwrite = args.GetBool("overwrite");

            var outcome = Exporter.Export(session, path, overwrite, Exporter.WriteKind.Neutral);

            return outcome.Succeeded
                ? ToolResult.Success(outcome.Message)
                : ToolResult.Failure(outcome.Message, "export_failed");
        }
    }

    /// <summary>Create a drawing with standard views.</summary>
    public sealed class DrawingCreateTool : SwTool
    {
        public override string Name => "sw_drawing_create";
        public override string Description =>
            "Create a drawing of the active part using the user's own drawing template and sheet format, " +
            "with standard views placed. The part must be saved first. The projection convention is read " +
            "from the template rather than assumed.";

        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            var outcome = DrawingOps.CreateWithStandardViews(session);

            if (!outcome.Succeeded)
                return ToolResult.Failure(outcome.Message, "drawing_failed");

            var sb = new StringBuilder(outcome.Message);
            if (!string.IsNullOrWhiteSpace(outcome.SheetFormat))
                sb.Append($" Sheet format: {outcome.SheetFormat}.");

            var fields = DrawingOps.ReadTitleBlockFields(session);
            if (fields.Count > 0)
            {
                sb.Append($" The title block reads these properties: {string.Join(", ", fields)}. ");
                sb.Append("Fill them with sw_drawing_fill_titleblock.");
            }

            return ToolResult.Success(sb.ToString());
        }
    }

    /// <summary>Insert model dimensions onto the drawing views.</summary>
    public sealed class DrawingDimensionsTool : SwTool
    {
        public override string Name => "sw_drawing_insert_dimensions";
        public override string Description =>
            "Insert the model's dimensions onto the drawing views. The result always needs a human to " +
            "arrange it - dimensions come out overlapping. Say so rather than claiming a finished drawing.";

        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            return ToolResult.Success(DrawingOps.InsertDimensions(session));
        }
    }

    /// <summary>Populate the title block, reporting fields the template does not have.</summary>
    public sealed class TitleBlockTool : SwTool
    {
        public override string Name => "sw_drawing_fill_titleblock";
        public override string Description =>
            "Fill the drawing's title block by setting custom properties on the drawing. Reports which " +
            "requested fields the template actually has and which it does not, rather than silently " +
            "dropping the ones it does not.";

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Text("field", "Title block field name, e.g. 'DrawnBy', 'PartNo', 'Revision'."),
            ToolParameter.Text("value", "The value to put in it."),
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            string field = args.GetString("field");
            string value = args.GetString("value");

            session.RequireDrawing();

            var templateFields = DrawingOps.ReadTitleBlockFields(session);
            var tier = CustomProperties.Write(session, field, value, PropertyTier.File);
            session.Rebuild();

            bool templateUsesIt = templateFields.Any(
                f => string.Equals(f, field, StringComparison.OrdinalIgnoreCase));

            if (templateUsesIt)
                return ToolResult.Success($"'{field}' set to '{value}'. The sheet format references this field, " +
                                          "so it will appear in the title block.");

            // Honest rather than silent: the value was stored, but this
            // template's title block will not display it.
            string known = templateFields.Count > 0
                ? $" The fields this sheet format actually uses are: {string.Join(", ", templateFields)}."
                : " This sheet format does not appear to reference any properties.";

            return ToolResult.Success(
                $"'{field}' was set to '{value}' as a document property, but this sheet format does not " +
                $"reference '{field}', so it will NOT appear in the title block.{known}");
        }
    }
}
