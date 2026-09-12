using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using SolidWorks.Interop.sldworks;
using SwAgent.Core.Session;

namespace SwAgent.Core.Inspection
{
    /// <summary>Named camera positions SOLIDWORKS understands.</summary>
    public enum NamedView
    {
        Isometric,
        Front,
        Back,
        Left,
        Right,
        Top,
        Bottom,
        Trimetric
    }

    /// <summary>
    /// Captures the viewport as a PNG for the verification round-trip.
    ///
    /// The model does not see 3D. It sees a 2D projection, a text tree and some
    /// scalars, and triangulates across the three - so the image is a sanity
    /// check, not ground truth, and it is worth exactly as much as it is
    /// legible. Two things follow from that:
    ///
    /// - Always zoom to fit and redraw before grabbing, or the capture shows
    ///   whatever the user last left on screen.
    /// - Downscale to roughly 1024px on the long edge. Full-resolution captures
    ///   on every call exhaust the context window and buy nothing; at this size
    ///   a wrong extrude direction is still obvious.
    ///
    /// A single fixed viewpoint has occlusion blind spots, which is why the
    /// caller can ask for a specific named view when checking a dimension.
    /// </summary>
    public static class Viewport
    {
        /// <summary>
        /// Target size of the longest edge, in pixels.
        ///
        /// 640, not 1024. An image costs roughly (width x height) / 750 tokens,
        /// so this is about 2.5x cheaper per capture - and the picture is only
        /// ever a sanity check on direction and gross placement, which does not
        /// need the pixels. Dimensions are judged from the numbers, which are
        /// exact and nearly free.
        ///
        /// Do not raise this without a measurement showing the extra detail
        /// changes an outcome.
        /// </summary>
        public const int MaxEdgePixels = 640;

        /// <summary>
        /// Point the camera at a named view, fit the model, redraw, and return
        /// the viewport as PNG bytes.
        /// </summary>
        /// <param name="scratchDirectory">
        /// Where the intermediate bitmap is written. Must be a directory we own
        /// - never the user's working folder.
        /// </param>
        public static byte[] Capture(SwSession session, NamedView view, string scratchDirectory)
        {
            var doc = session.RequireModel();

            // Orient, fit, redraw - in that order. Skipping the fit is how you
            // send the model a picture of empty space next to a part.
            doc.ShowNamedView2(ToSwName(view), -1);
            doc.ViewZoomtofit2();
            doc.GraphicsRedraw2();

            Directory.CreateDirectory(scratchDirectory);
            string bmpPath = Path.Combine(scratchDirectory, $"capture_{Guid.NewGuid():N}.bmp");

            try
            {
                // Width and height of zero mean "use the current viewport size".
                if (!doc.SaveBMP(bmpPath, 0, 0))
                    throw new InvalidOperationException("SOLIDWORKS refused to capture the viewport.");

                if (!File.Exists(bmpPath))
                    throw new InvalidOperationException(
                        "SOLIDWORKS reported a successful capture but wrote no file.");

                return DownscaleToPng(bmpPath, MaxEdgePixels);
            }
            finally
            {
                // Never leave scratch bitmaps behind; they are large and they
                // accumulate once per feature.
                try { if (File.Exists(bmpPath)) File.Delete(bmpPath); }
                catch (Exception ex) { session.Log.Debug($"Could not delete {bmpPath}: {ex.Message}"); }
            }
        }

        /// <summary>Load a bitmap, scale its long edge down, and encode as PNG.</summary>
        private static byte[] DownscaleToPng(string bmpPath, int maxEdge)
        {
            using (var source = new Bitmap(bmpPath))
            {
                int w = source.Width;
                int h = source.Height;

                // Only ever shrink. Enlarging a small viewport wastes tokens
                // without adding a single pixel of real detail.
                double scale = Math.Min(1.0, (double)maxEdge / Math.Max(w, h));
                int tw = Math.Max(1, (int)Math.Round(w * scale));
                int th = Math.Max(1, (int)Math.Round(h * scale));

                using (var resized = new Bitmap(tw, th, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(resized))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.DrawImage(source, 0, 0, tw, th);
                    }

                    using (var ms = new MemoryStream())
                    {
                        resized.Save(ms, ImageFormat.Png);
                        return ms.ToArray();
                    }
                }
            }
        }

        /// <summary>
        /// SOLIDWORKS named views are prefixed with an asterisk, and these
        /// names are API constants rather than localised strings, so they work
        /// on every language seat.
        /// </summary>
        private static string ToSwName(NamedView view)
        {
            switch (view)
            {
                case NamedView.Isometric: return "*Isometric";
                case NamedView.Front: return "*Front";
                case NamedView.Back: return "*Back";
                case NamedView.Left: return "*Left";
                case NamedView.Right: return "*Right";
                case NamedView.Top: return "*Top";
                case NamedView.Bottom: return "*Bottom";
                case NamedView.Trimetric: return "*Trimetric";
                default: throw new ArgumentOutOfRangeException(nameof(view));
            }
        }

        /// <summary>The scratch directory we own, under the user's local app data.</summary>
        public static string DefaultScratchDirectory => Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "SwAgent", "scratch");
    }
}
