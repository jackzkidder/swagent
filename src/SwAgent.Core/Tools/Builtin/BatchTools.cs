using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SwAgent.Core.Batch;
using SwAgent.Core.Session;

namespace SwAgent.Core.Tools.Builtin
{
    /// <summary>Count what a folder holds, without opening anything.</summary>
    public sealed class BatchIndexTool : SwTool
    {
        public override string Name => "sw_batch_index";
        public override string Description =>
            "Look at a folder of SOLIDWORKS files before planning a batch change: how many parts, assemblies " +
            "and drawings match a pattern, and how many are read-only. Opens nothing and changes nothing. " +
            "File names are not returned to you - they stay on the user's machine, and the user sees them in " +
            "the batch preview.";

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Text("folder", "Full path of the folder, exactly as the user gave it."),
            ToolParameter.Text("pattern",
                "File name wildcard, e.g. '*.sldprt' or 'BRK-*'. Omit for every SOLIDWORKS file.",
                required: false, defaultValue: "*"),
            ToolParameter.Flag("include_subfolders", "Also search folders inside it."),
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            var index = FolderIndex.Scan(
                args.GetString("folder"), args.GetString("pattern"), args.GetBool("include_subfolders"));

            string where = index.Recursive ? "that folder and its subfolders" : "that folder (subfolders not searched)";

            if (index.Files.Count == 0)
            {
                return ToolResult.Success(
                    $"No SOLIDWORKS files in {where} match '{index.Pattern}'." + Extras(index));
            }

            int parts = index.Files.Count(f => f.DocType == BatchDocType.Part);
            int assemblies = index.Files.Count(f => f.DocType == BatchDocType.Assembly);
            int drawings = index.Files.Count(f => f.DocType == BatchDocType.Drawing);
            int readOnly = index.Files.Count(f => f.ReadOnlyAttribute);
            long bytes = index.Files.Sum(f => f.SizeBytes);

            var sb = new StringBuilder();
            sb.Append($"{index.Files.Count} SOLIDWORKS file(s) in {where} match '{index.Pattern}': ");
            sb.Append($"{parts} part(s), {assemblies} assembl{(assemblies == 1 ? "y" : "ies")}, {drawings} drawing(s), ");
            sb.Append($"{Files.Exporter.FormatSize(bytes)} in total.");

            if (readOnly > 0)
                sb.Append($" {readOnly} are marked read-only, so a batch can export them but not change them.");

            if (index.Truncated)
                sb.Append($" The listing stopped at {index.Files.Count} files and there are more; narrow the pattern.");

            sb.Append(Extras(index));
            sb.Append(" File names are not sent to you. Next, plan a change with sw_batch_preview.");

            return ToolResult.Success(sb.ToString());
        }

        private static string Extras(FolderIndexResult index)
        {
            var sb = new StringBuilder();

            if (index.LockFilesIgnored > 0)
                sb.Append($" {index.LockFilesIgnored} SOLIDWORKS lock file(s) (~$) were ignored; each means a file " +
                          "is open, here or on another machine.");

            if (index.UnreadableFolders.Count > 0)
                sb.Append($" {index.UnreadableFolders.Count} folder(s) could not be read.");

            return sb.ToString();
        }
    }

    /// <summary>
    /// Plan a change across a folder. There is deliberately no tool that
    /// applies one: see <see cref="BatchPlanStore"/>.
    /// </summary>
    public sealed class BatchPreviewTool : SwTool
    {
        public override string Name => "sw_batch_preview";
        public override string Description =>
            "Dry-run one change across a folder of existing SOLIDWORKS files: set a custom property, set a " +
            "material, or export each file. Nothing is written. The plan is shown to the user file by file in " +
            "the SwAgent panel, and ONLY THE USER can apply it, with the Apply button - no tool applies a batch. " +
            "Files open in SOLIDWORKS are always skipped. Property and material previews are limited to " +
            $"{BatchPlanner.MaxFilesOpenedPerPreview} files, exports to {BatchPlanner.MaxFilesPerExport}.";

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Text("folder", "Full path of the folder, exactly as the user gave it."),
            ToolParameter.Text("pattern",
                "File name wildcard, e.g. '*.sldprt' or 'BRK-*'. Omit for every SOLIDWORKS file.",
                required: false, defaultValue: "*"),
            ToolParameter.Flag("include_subfolders", "Also include files in folders inside it."),

            ToolParameter.Choice("operation", "The one change this plan makes.",
                new[] { "set_property", "set_material", "export" }),

            ToolParameter.Text("property_name", "For set_property: the property, e.g. 'Revision'.",
                required: false),
            ToolParameter.Text("property_value",
                "For set_property: the value to set. An empty string blanks the property.", required: false),

            ToolParameter.Text("material", "For set_material: exact SOLIDWORKS material name, e.g. '6061 Alloy'.",
                required: false),
            ToolParameter.Text("material_database",
                "For set_material: material database. Leave empty for the standard SOLIDWORKS materials.",
                required: false, defaultValue: ""),

            ToolParameter.Choice("export_format",
                "For export: step, x_t, igs, stl or 3mf for parts and assemblies; pdf, dxf or dwg for drawings. " +
                "Files of the wrong kind are skipped.",
                BatchPlanner.ExportFormats, required: false),
            ToolParameter.Text("output_folder",
                "For export: full path of an existing folder to write into. Never invent one; ask the user.",
                required: false),
            ToolParameter.Flag("overwrite",
                "For export: replace files that already exist in the output folder. Only if the user said so."),

            ToolParameter.Flag("allow_version_upgrade",
                "Include files last saved by an older SOLIDWORKS release. Saving upgrades them permanently and " +
                "that release can no longer open them. Only if the user has explicitly accepted that."),
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            var request = new BatchRequest
            {
                Operation = ParseOperation(args.GetString("operation")),
                Folder = args.GetString("folder"),
                Pattern = args.GetString("pattern"),
                Recursive = args.GetBool("include_subfolders"),
                PropertyName = args.GetString("property_name"),
                PropertyValue = args.GetString("property_value"),
                Material = args.GetString("material"),
                MaterialDatabase = args.GetString("material_database") ?? "",
                ExportExtension = args.GetString("export_format"),
                OutputFolder = args.GetString("output_folder"),
                Overwrite = args.GetBool("overwrite"),
                AllowVersionUpgrade = args.GetBool("allow_version_upgrade"),
            };

            BatchPlan plan = BatchPlanner.Preview(session, request);
            session.Batches.Add(plan, session.Log);

            return ToolResult.Success(plan.DescribeForModel());
        }

        private static BatchOperation ParseOperation(string value)
        {
            switch (value)
            {
                case "set_property": return BatchOperation.SetProperty;
                case "set_material": return BatchOperation.SetMaterial;
                default: return BatchOperation.Export;
            }
        }
    }
}
