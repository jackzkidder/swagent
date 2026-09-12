using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SwAgent.Core.Session;

namespace SwAgent.Core.Modeling
{
    /// <summary>Which of the three default reference planes to sketch on.</summary>
    public enum StandardPlane
    {
        Front = 0,
        Top = 1,
        Right = 2
    }

    /// <summary>
    /// Selects one of the three default reference planes.
    ///
    /// SelectByID2("Front Plane", ...) works only on an English install. On a
    /// German seat the plane is "Vorne", on a French one "Face". Hardcoding the
    /// English name means the tool fails on a customer's machine in a way we
    /// will never reproduce locally, so the name is only ever the fast path:
    /// the fallback walks the feature tree and takes reference planes by
    /// position, which is language-independent because the first three
    /// RefPlanes in a part template are always Front, Top, Right in that order.
    /// </summary>
    public static class PlaneSelector
    {
        private static readonly string[] EnglishNames = { "Front Plane", "Top Plane", "Right Plane" };

        /// <summary>
        /// Select the given standard plane. Returns the name actually selected,
        /// for the tool's result text.
        /// </summary>
        public static string Select(SwSession session, StandardPlane plane)
        {
            var doc = session.RequirePart();
            session.ClearSelection();

            int index = (int)plane;

            // Fast path: the English name, which covers most seats.
            string englishName = EnglishNames[index];
            if (doc.Extension.SelectByID2(englishName, "PLANE", 0, 0, 0, false, 0, null, 0))
                return englishName;

            // Fallback: tree position. Language-independent.
            var planeNames = ListReferencePlanes(doc);
            if (planeNames.Count <= index)
            {
                throw new InvalidOperationException(
                    $"Could not find the {plane} plane. The part template has only " +
                    $"{planeNames.Count} reference plane(s); a standard template has three.");
            }

            string actual = planeNames[index];
            session.ClearSelection();
            if (!doc.Extension.SelectByID2(actual, "PLANE", 0, 0, 0, false, 0, null, 0))
            {
                throw new InvalidOperationException(
                    $"Could not select the {plane} plane (tried '{englishName}' and '{actual}').");
            }

            return actual;
        }

        /// <summary>
        /// The names of the reference planes in the feature tree, in tree order.
        /// </summary>
        public static List<string> ListReferencePlanes(IModelDoc2 doc)
        {
            var names = new List<string>();
            var feature = (IFeature)doc.FirstFeature();

            while (feature != null)
            {
                // The type name is an API constant, not a localised string, so
                // this comparison is safe on every language.
                if (string.Equals(feature.GetTypeName2(), "RefPlane", StringComparison.Ordinal))
                    names.Add(feature.Name);

                feature = (IFeature)feature.GetNextFeature();
            }

            return names;
        }
    }
}
