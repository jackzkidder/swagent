using System;
using System.Linq;
using System.Text.Json;
using SwAgent.Core.Inspection;
using SwAgent.Core.Modeling;
using SwAgent.Core.Session;
using SwAgent.Core.Tools;
using SwAgent.Core.Tools.Builtin;

namespace SwAgent.Harness
{
    /// <summary>
    /// The three additions that came out of looking at what other CAD agents do:
    /// topology counts in the round-trip, the user's selection, and a checkpoint
    /// that does what undo cannot.
    ///
    /// The assertions are relationships rather than absolute counts wherever the
    /// absolute depends on the user's template - the same reason the reference
    /// parts assert sorted bounding-box dimensions rather than X, Y and Z.
    /// </summary>
    public static class SelectionCheckpointTests
    {
        private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;
        private static JsonElement NoArgs => Args("{}");

        /// <summary>
        /// Counts are the structural half of the verification round-trip. A box
        /// is 6 faces and 12 edges, and putting a hole through it must add
        /// faces without adding bodies - which is exactly the signal that
        /// catches a pattern that placed nothing (finding #13).
        /// </summary>
        public static void TopologyCountsReachTheModel(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();

            run.Step("build a 60 x 40 x 10 plate");
            BuildPlate(run, registry, session);

            run.Step("a plain box is one body, six faces, twelve edges");
            var box = BodyTopology.Read(session);
            run.Assert(box.IsKnown, "topology could be read");
            run.Assert(box.SolidBodies == 1, $"one solid body (got {box.SolidBodies})");
            run.Assert(box.SheetBodies == 0, $"no surface bodies (got {box.SheetBodies})");
            run.Assert(box.Faces == 6, $"six faces (got {box.Faces})");
            run.Assert(box.Edges == 12, $"twelve edges (got {box.Edges})");

            run.Step("the counts reach the model in the measurement line");
            var measured = registry.Execute("sw_mass_properties", NoArgs, session);
            run.Assert(measured.Ok, "sw_mass_properties succeeded");
            run.Assert(measured.Text.Contains("1 solid body"), "measurement text names the body count");
            run.Assert(measured.Text.Contains("6 faces"), "measurement text names the face count");
            run.Note(measured.Text.Split('\n').FirstOrDefault(l => l.Contains("faces")));

            run.Step("a through hole adds faces without adding bodies");
            var hole = CutAHole(run, registry, session);
            if (!hole) return;

            var drilled = BodyTopology.Read(session);
            run.Assert(drilled.SolidBodies == 1, $"still one solid body (got {drilled.SolidBodies})");
            run.Assert(drilled.Faces > box.Faces,
                $"face count rose from {box.Faces} to {drilled.Faces}");
            run.Assert(drilled.Edges > box.Edges,
                $"edge count rose from {box.Edges} to {drilled.Edges}");
        }

        /// <summary>
        /// Revert must remove everything built since the mark, take absorbed
        /// sketches with it, and leave what the user had before it alone.
        /// </summary>
        public static void RevertRemovesOnlyThisRequestsWork(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();

            run.Step("the user's own work: a plate");
            BuildPlate(run, registry, session);

            var beforeNames = FeatureTree.Read(session).Select(n => n.Name).ToList();
            int beforeCount = beforeNames.Count;
            run.Note($"tree at the mark: {string.Join(", ", beforeNames)}");

            run.Step("mark the tree, as the panel does when a message arrives");
            session.Checkpoints.Mark(beforeNames);
            run.Assert(session.Checkpoints.HasMark, "a mark was taken");

            run.Step("the agent then builds two features of its own");
            if (!CutAHole(run, registry, session)) return;

            var withWork = FeatureTree.Read(session).Select(n => n.Name).ToList();
            run.Assert(withWork.Count > beforeCount,
                $"tree grew from {beforeCount} to {withWork.Count} features");

            run.Step("sw_revert_to_request_start");
            var reverted = registry.Execute("sw_revert_to_request_start", NoArgs, session);
            run.Assert(reverted.Ok, $"revert succeeded: {FirstLine(reverted.Text)}");
            run.Note(FirstLine(reverted.Text));

            run.Step("the tree is back to exactly what it was at the mark");
            var afterNames = FeatureTree.Read(session).Select(n => n.Name).ToList();
            run.Assert(afterNames.Count == beforeCount,
                $"feature count back to {beforeCount} (got {afterNames.Count})");
            run.Assert(beforeNames.All(afterNames.Contains),
                "every feature the user had before the mark survived");

            run.Step("no orphan sketch was left behind");
            // This is the failure that makes undo untrustworthy: the feature
            // goes, its sketch stays, and the count does not move.
            var tree = registry.Execute("sw_feature_tree", NoArgs, session);
            run.Assert(tree.Ok, "feature tree readable after revert");
            run.Assert(!tree.Text.Contains("Cut-Extrude"), "the cut is gone from the tree");

            run.Step("the part still measures as the original plate");
            var measured = registry.Execute("sw_mass_properties", NoArgs, session);
            run.Assert(measured.Text.Contains("24"), "volume is 24 cm3 again");
            run.Note(FirstLine(measured.Text));

            session.Checkpoints.Clear();
        }

        /// <summary>
        /// The selection the user made must survive into the run, describe
        /// itself usefully, and still be usable after a tool has cleared the
        /// live selection - which every tool does on entry.
        /// </summary>
        public static void SelectionSurvivesIntoTheRun(TestRun run, SwSession session)
        {
            var registry = BuiltinTools.CreateRegistry();

            run.Step("build a plate and select one of its faces, as a user would");
            BuildPlate(run, registry, session);

            var measured = PartMeasure.OfActivePart(session);
            if (measured.BoundingBoxMm == null)
            {
                run.Fail("no bounding box, so the ray cannot be aimed");
                return;
            }

            // Fire down the +Z axis from outside the part at its centre.
            double cx = (measured.BoundingBoxMm[0] + measured.BoundingBoxMm[3]) / 2;
            double cy = (measured.BoundingBoxMm[1] + measured.BoundingBoxMm[4]) / 2;
            double above = measured.BoundingBoxMm[5] + 20;

            var hit = FaceSelector.SelectByRay(session, cx, cy, above, RayDirection.MinusZ);
            run.Assert(hit.Found, $"a face was selected by ray: {hit.Description}");
            if (!hit.Found) return;

            run.Step("the panel captures the selection before the agent runs");
            var snapshot = SelectionReader.Capture(session);
            session.UserSelection = snapshot;

            run.Assert(!snapshot.IsEmpty, $"the snapshot holds {snapshot.Count} item(s)");
            run.Assert(snapshot.Items.Count > 0 && snapshot.Items[0].IsFace, "the first item is a face");
            run.Assert(snapshot.Describe().Contains("area"), "the description reports the face's area");
            run.Note(snapshot.Describe());

            run.Step("sw_selection reports it to the model");
            var reported = registry.Execute("sw_selection", NoArgs, session);
            run.Assert(reported.Ok, "sw_selection succeeded");
            run.Assert(reported.Text.Contains("face"), "the result mentions a face");

            run.Step("the live selection is gone by now, because every tool clears it");
            var live = SelectionReader.Capture(session);
            run.Assert(live.IsEmpty, "SOLIDWORKS' own selection was cleared by the tool call");

            run.Step("sw_sketch_on_selected_face re-selects the captured face anyway");
            var sketched = registry.Execute("sw_sketch_on_selected_face", NoArgs, session);
            run.Assert(sketched.Ok, $"a sketch opened on the user's face: {FirstLine(sketched.Text)}");
            run.Note(FirstLine(sketched.Text));

            if (sketched.Ok)
            {
                var closed = registry.Execute("sw_sketch_close", NoArgs, session);
                run.Assert(closed.Ok, "the sketch closed again");
            }

            run.Step("with nothing selected, the tool says so rather than guessing");
            session.UserSelection = SelectionSnapshot.Empty;
            var none = registry.Execute("sw_sketch_on_selected_face", NoArgs, session);
            run.Assert(!none.Ok, "refused when nothing was selected");
            run.Assert(none.ErrorKind == "no_selection", $"classified as no_selection (got {none.ErrorKind})");
        }

        // -----------------------------------------------------------------

        private static void BuildPlate(TestRun run, ToolRegistry registry, SwSession session)
        {
            Expect(run, registry, session, "sw_new_part", NoArgs);
            Expect(run, registry, session, "sw_sketch_open", Args(@"{""plane"":""front""}"));
            Expect(run, registry, session, "sw_sketch_rect", Args(@"{""width_mm"":60,""height_mm"":40}"));
            var closed = Expect(run, registry, session, "sw_sketch_close", NoArgs);
            string sketch = ExtractQuoted(closed.Text) ?? "Sketch1";
            Expect(run, registry, session, "sw_extrude",
                Args($@"{{""sketch_name"":""{sketch}"",""end_condition"":""blind"",""depth_mm"":10}}"));
        }

        private static bool CutAHole(TestRun run, ToolRegistry registry, SwSession session)
        {
            var opened = registry.Execute("sw_sketch_open", Args(@"{""plane"":""front""}"), session);
            if (!opened.Ok) { run.Fail($"could not open a sketch: {opened.Text}"); return false; }

            registry.Execute("sw_sketch_circle", Args(@"{""radius_mm"":5}"), session);
            var closed = registry.Execute("sw_sketch_close", NoArgs, session);
            string sketch = ExtractQuoted(closed.Text) ?? "Sketch2";

            var cut = registry.Execute("sw_cut",
                Args($@"{{""sketch_name"":""{sketch}"",""end_condition"":""through_all_both""}}"), session);

            if (!cut.Ok) { run.Fail($"the cut failed: {FirstLine(cut.Text)}"); return false; }
            return true;
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

        private static string FirstLine(string text)
            => (text ?? string.Empty).Split('\n').FirstOrDefault()?.TrimEnd();
    }
}
