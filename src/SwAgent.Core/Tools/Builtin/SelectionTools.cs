using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Inspection;
using SwAgent.Core.Modeling;
using SwAgent.Core.Session;

namespace SwAgent.Core.Tools.Builtin
{
    /// <summary>
    /// Read what the user had selected when they sent their message.
    ///
    /// Picking geometry is the thing the model is worst at: it works out where
    /// the face probably is and fires a ray at it. A person clicking the face
    /// they mean is exact and costs nothing, and "fillet this edge" is how
    /// engineers actually talk.
    /// </summary>
    public sealed class SelectionTool : SwTool
    {
        public override string Name => "sw_selection";
        public override string Description =>
            "See what the user had selected in SOLIDWORKS when they sent their message - faces, edges, " +
            "vertices, planes or features, with their sizes and positions. Call this whenever the request " +
            "says 'this face', 'that edge' or 'here'. It is exact, unlike aiming a ray.";

        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            var snapshot = session.UserSelection;

            if (snapshot == null || snapshot.IsEmpty)
            {
                return ToolResult.Success(
                    "Nothing was selected in SOLIDWORKS when the user sent this message. " +
                    "If the request refers to a particular face or edge, ask them to click it and say so, " +
                    "or locate it yourself with sw_mass_properties and sw_sketch_open_on_face.");
            }

            return ToolResult.Success(
                snapshot.Describe() +
                "\nTo sketch on a selected face, call sw_sketch_on_selected_face with its number.");
        }
    }

    /// <summary>
    /// Open a sketch on a face the USER selected, rather than one found by ray.
    ///
    /// The selection itself was captured before this run started, because every
    /// tool clears the selection on entry - see SelectionReader.
    /// </summary>
    public sealed class SketchOnSelectedFaceTool : SwTool
    {
        public override string Name => "sw_sketch_on_selected_face";
        public override string Description =>
            "Open a sketch on a face the user had selected when they sent their message. Use sw_selection " +
            "first to see what is available. Reports where the sketch origin landed and which way its axes " +
            "run, exactly like sw_sketch_open_on_face.";

        public override IReadOnlyList<ToolParameter> Parameters => new[]
        {
            ToolParameter.Integer("number",
                "Which selected item to use, as numbered by sw_selection. Defaults to the first selected face.",
                1, 12, required: false, defaultValue: 0)
        };

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            var snapshot = session.UserSelection;
            if (snapshot == null || snapshot.IsEmpty)
            {
                return ToolResult.Failure(
                    "Nothing was selected when the user sent this message, so there is no face to sketch on. " +
                    "Use sw_sketch_open_on_face and aim a ray instead.",
                    "no_selection");
            }

            int number = args.GetInt("number");
            SelectedEntity item;

            if (number > 0)
            {
                if (number > snapshot.Items.Count)
                    return ToolResult.Failure(
                        $"There is no selected item {number}; the user selected {snapshot.Items.Count}.",
                        "no_such_selection");

                item = snapshot.Items[number - 1];
                if (!item.IsFace)
                    return ToolResult.Failure(
                        $"Selected item {number} is {item.Description}, not a face. A sketch needs a face.",
                        "not_a_face");
            }
            else
            {
                item = snapshot.Items.FirstOrDefault(e => e.IsFace);
                if (item == null)
                    return ToolResult.Failure(
                        "None of what the user selected is a face. " + snapshot.Describe(),
                        "not_a_face");
            }

            // SwTool.Execute cleared the selection on entry, as it does for every
            // tool. Put the user's face back before asking for a sketch on it.
            if (!SelectionReader.Reselect(item))
            {
                return ToolResult.Failure(
                    "That face could no longer be selected. The model has probably changed since the user " +
                    "clicked it - ask them to select it again, or use sw_sketch_open_on_face.",
                    "stale_selection");
            }

            string frame = FaceSelector.OpenSketchOnSelectedFace(session);
            return ToolResult.Success($"Sketch opened on the face the user selected ({item.Description}). {frame}");
        }
    }

    /// <summary>
    /// Delete everything built since the user's message, and nothing else.
    ///
    /// The recovery path undo should have been. EditUndo2 returns void, so it
    /// cannot say whether it did anything, and it has been seen leaving a stray
    /// sketch behind with the feature count unmoved. This works from a mark
    /// taken when the request arrived: features not on that list are ours, and
    /// they go, newest first, each taking its absorbed sketch with it.
    /// </summary>
    public sealed class RevertToRequestStartTool : SwTool
    {
        public override string Name => "sw_revert_to_request_start";
        public override string Description =>
            "Delete every feature created since the user's current message, returning the part to how it " +
            "was when they sent it. Anything the user built earlier is untouched. Use this when several " +
            "features are wrong, or when sw_undo reports that nothing changed.";

        public override bool MutatesModel => true;
        public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

        protected override ToolResult Run(ToolArgs args, SwSession session)
        {
            var checkpoints = session.Checkpoints;
            if (!checkpoints.HasMark)
            {
                return ToolResult.Failure(
                    "There is no mark for this request, so there is nothing to revert to. Use sw_undo.",
                    "no_checkpoint");
            }

            var doc = session.RequireModel();

            var currentNames = FeatureTree.Read(session).Select(n => n.Name).ToList();
            var added = checkpoints.AddedSince(currentNames);

            if (added.Count == 0)
            {
                return ToolResult.Success(
                    "Nothing has been added since the user's message, so there is nothing to revert.");
            }

            // A sketch open for editing blocks deletion, and our own bookkeeping
            // is about to stop being true either way.
            try
            {
                if (SketchOps.IsSketchActuallyOpen(doc)) SketchOps.Close(session);
            }
            catch (Exception ex)
            {
                session.Log.Debug($"Revert: could not close the open sketch: {ex.Message}");
            }
            session.MarkSketchClosed();

            // Newest first, so nothing is deleted before the feature built on
            // top of it. swDelete_Absorbed takes the sketch a feature consumed;
            // swDelete_Children takes anything that depended on it. Without the
            // first, reverting leaves exactly the orphan sketches that make undo
            // untrustworthy.
            const int deleteOptions =
                (int)(swDeleteSelectionOptions_e.swDelete_Absorbed | swDeleteSelectionOptions_e.swDelete_Children);

            int deleted = 0;
            var failed = new List<string>();

            for (int i = added.Count - 1; i >= 0; i--)
            {
                string name = added[i];
                try
                {
                    session.ClearSelection();

                    if (!doc.Extension.SelectByID2(name, "BODYFEATURE", 0, 0, 0, false, 0, null, 0))
                    {
                        // Already gone with a parent, most likely.
                        continue;
                    }

                    if (doc.Extension.DeleteSelection2(deleteOptions)) deleted++;
                    else failed.Add(name);
                }
                catch (Exception ex)
                {
                    session.Log.Debug($"Revert: {name} could not be deleted: {ex.Message}");
                    failed.Add(name);
                }
            }

            session.ClearSelection();

            // The contract described a part that no longer exists.
            session.Intent.Clear();

            int remaining = FeatureTree.Read(session).Count;
            string headline = failed.Count == 0
                ? $"Reverted to the state when the user sent their message: {deleted} feature(s) deleted, " +
                  $"{remaining} left in the tree."
                : $"Reverted partially: {deleted} feature(s) deleted, {remaining} left. These could not be " +
                  $"deleted: {string.Join(", ", failed)}. Tell the user rather than building on top of them.";

            return Observation.AfterChange(session, headline);
        }
    }
}
