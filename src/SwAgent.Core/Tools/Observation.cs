using System;
using System.Text;
using SwAgent.Core.Inspection;
using SwAgent.Core.Session;

namespace SwAgent.Core.Tools
{
    /// <summary>
    /// Packages what the agent sees after an operation that changed the model.
    ///
    /// This is the verification round-trip, and the ordering is the whole
    /// point. The model does not see 3D; it sees a 2D projection, a text tree
    /// and some scalars, and triangulates across the three. So they are ranked:
    ///
    ///   numbers are ground truth  - mass and bounding box are unambiguous, and
    ///                               catch a 1000x unit error instantly
    ///   the tree is structure     - catches a wrong history that looks right
    ///   the image is a sanity check - catches a wrong direction, and little else
    ///
    /// Text first, image last, always. An agent that leads with the picture will
    /// talk itself into "looks about right" over a part that is 24,000 cm3.
    /// </summary>
    public static class Observation
    {
        /// <summary>
        /// Build the standard post-operation result: what happened, whether the
        /// rebuild was clean, the numbers, the tree, and a viewport capture.
        /// </summary>
        /// <param name="headline">What the tool just did, in one line.</param>
        /// <param name="includeImage">
        /// False for operations where a picture adds nothing, to keep the
        /// context window from filling with near-identical screenshots.
        /// </param>
        public static ToolResult AfterChange(
            SwSession session,
            string headline,
            bool includeImage = true,
            NamedView view = NamedView.Isometric)
        {
            var sb = new StringBuilder();
            sb.AppendLine(headline);

            // 1. Rebuild state. A returned feature object does not mean the
            //    feature is correct, so this is not optional.
            RebuildOutcome rebuild = null;
            try
            {
                rebuild = session.Rebuild();
                sb.AppendLine(rebuild.Describe());
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Rebuild could not be evaluated: {ex.Message}");
            }

            // 2. The numbers.
            try
            {
                var measurements = PartMeasure.OfActivePart(session);
                sb.AppendLine(measurements.Describe());

                string material = PartMeasure.ActiveMaterial(session);
                sb.AppendLine(material == null
                    ? "No material set, so mass assumes the default 1000 kg/m3."
                    : $"Material: {material}.");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Measurements unavailable: {ex.Message}");
            }

            // 3. The structure.
            try
            {
                var nodes = FeatureTree.Read(session);
                sb.AppendLine(FeatureTree.Render(nodes));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Feature tree unavailable: {ex.Message}");
            }

            // 4. The sanity check. A capture failure must not fail the
            //    operation - the operation already succeeded, and the numbers
            //    above are the ground truth anyway.
            if (!includeImage)
                return ToolResult.Success(sb.ToString().TrimEnd());

            try
            {
                byte[] png = Viewport.Capture(session, view, Viewport.DefaultScratchDirectory);
                return ToolResult.Success(
                    sb.ToString().TrimEnd(),
                    new ToolImage(png, $"{view} view"));
            }
            catch (Exception ex)
            {
                session.Log.Debug($"Viewport capture failed: {ex.Message}");
                sb.AppendLine($"(Viewport capture unavailable: {ex.Message})");
                return ToolResult.Success(sb.ToString().TrimEnd());
            }
        }
    }
}
