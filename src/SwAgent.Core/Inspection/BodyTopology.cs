using System;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Session;

namespace SwAgent.Core.Inspection
{
    /// <summary>
    /// How many bodies, faces and edges the part has.
    ///
    /// Volume and bounding box are ground truth about SIZE. They say nothing
    /// about STRUCTURE, and structure is where this API lies to us:
    ///
    ///   Finding #11 - an impossible shell reports success and leaves a nearly
    ///                 solid body. Volume barely moves; the face count does not
    ///                 gain the inner faces a real shell would.
    ///   Finding #13 - a pattern whose instances miss the material rebuilds
    ///                 CLEAN. The face count does not change either, which is
    ///                 exactly the tell.
    ///
    /// And a boolean that quietly splits the part into two disconnected lumps
    /// changes no volume at all, only the body count.
    ///
    /// Three integers cost about twenty tokens after each feature, which is the
    /// cheapest verification in the whole round-trip.
    ///
    /// Deliberately NOT reported: IBody2.Check2(). It exists, but what its
    /// return value means is not documented in our reflection dump, and a number
    /// nobody can interpret is worse in a tool result than no number at all.
    /// </summary>
    public static class BodyTopology
    {
        public static BodyCounts Read(SwSession session)
        {
            IPartDoc part;
            try
            {
                part = (IPartDoc)session.RequirePart();
            }
            catch (Exception ex)
            {
                session.Log.Debug($"Topology: no part to read: {ex.Message}");
                return BodyCounts.Unavailable;
            }

            int solids = 0, sheets = 0, faces = 0, edges = 0;

            try
            {
                // swAllBodies, not swSolidBody: a stray surface body is itself
                // worth reporting, and it is invisible in mass properties.
                var bodies = part.GetBodies2((int)swBodyType_e.swAllBodies, false) as object[];
                if (bodies == null) return BodyCounts.Unavailable;

                foreach (var entry in bodies)
                {
                    var body = entry as IBody2;
                    if (body == null) continue;

                    if (body.GetType() == (int)swBodyType_e.swSheetBody) sheets++;
                    else solids++;

                    faces += body.GetFaceCount();
                    edges += body.GetEdgeCount();
                }
            }
            catch (Exception ex)
            {
                session.Log.Debug($"Topology unavailable: {ex.Message}");
                return BodyCounts.Unavailable;
            }

            return new BodyCounts(solids, sheets, faces, edges);
        }
    }

    /// <summary>Body, face and edge counts for the active part.</summary>
    public sealed class BodyCounts
    {
        public static readonly BodyCounts Unavailable = new BodyCounts(-1, 0, 0, 0);

        public BodyCounts(int solidBodies, int sheetBodies, int faces, int edges)
        {
            SolidBodies = solidBodies;
            SheetBodies = sheetBodies;
            Faces = faces;
            Edges = edges;
        }

        public int SolidBodies { get; }
        public int SheetBodies { get; }
        public int Faces { get; }
        public int Edges { get; }

        public bool IsKnown => SolidBodies >= 0;

        /// <summary>
        /// One short clause for the measurement line. Says nothing when the
        /// part is a single ordinary solid, beyond the counts themselves; calls
        /// out the two states that are usually a mistake.
        /// </summary>
        public string Describe()
        {
            if (!IsKnown) return null;
            if (SolidBodies == 0 && SheetBodies == 0) return "No bodies.";

            string text = SolidBodies == 1
                ? $"1 solid body, {Faces} faces, {Edges} edges."
                : $"{SolidBodies} SEPARATE solid bodies, {Faces} faces, {Edges} edges.";

            if (SheetBodies > 0)
                text += $" Also {SheetBodies} surface body(s) - unusual in a machined part.";

            return text;
        }
    }
}
