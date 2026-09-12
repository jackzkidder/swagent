using System;
using System.Collections.Generic;

namespace SwAgent.Core.Tools
{
    /// <summary>
    /// What a tool hands back to the agent loop.
    ///
    /// Shaped to become an Anthropic tool_result block directly, including the
    /// image case: a tool_result may carry image content, which is how the
    /// viewport gets back to the model inside the tool's own result.
    ///
    /// Ordering matters and is deliberate. The model does not see 3D; it sees a
    /// 2D projection, a text tree and some scalars, and triangulates. Text goes
    /// first because numbers are ground truth and the image is only a sanity
    /// check.
    /// </summary>
    public sealed class ToolResult
    {
        private ToolResult(bool ok, string text, IReadOnlyList<ToolImage> images, string errorKind)
        {
            Ok = ok;
            Text = text ?? string.Empty;
            Images = images ?? Array.Empty<ToolImage>();
            ErrorKind = errorKind;
        }

        /// <summary>False when the operation failed. The agent must not build on top of a failure.</summary>
        public bool Ok { get; }

        /// <summary>Human- and model-readable summary. For a feature: what was made, rebuild state, bbox, mass.</summary>
        public string Text { get; }

        /// <summary>Viewport captures, already downscaled. Usually zero or one.</summary>
        public IReadOnlyList<ToolImage> Images { get; }

        /// <summary>
        /// A short machine-readable classification on failure, so the agent loop
        /// can distinguish "your arguments were wrong, try different ones" from
        /// "SOLIDWORKS is gone, stop". Null when <see cref="Ok"/>.
        /// </summary>
        public string ErrorKind { get; }

        public static ToolResult Success(string text, params ToolImage[] images)
            => new ToolResult(true, text, images, null);

        /// <summary>
        /// The operation failed but the session is fine and the agent may try
        /// something else. Bad arguments, a rebuild error, a missing face.
        /// </summary>
        public static ToolResult Failure(string text, string errorKind = "operation_failed")
            => new ToolResult(false, text, null, errorKind);

        /// <summary>
        /// The session is no longer usable: SOLIDWORKS exited, the pointer is
        /// dead, the document was closed underneath us. The loop should stop
        /// rather than retry.
        /// </summary>
        public static ToolResult Fatal(string text)
            => new ToolResult(false, text, null, "session_lost");

        public override string ToString()
            => (Ok ? "OK: " : "FAIL(" + ErrorKind + "): ") + Text;
    }

    /// <summary>A PNG capture bound for a tool_result image block.</summary>
    public sealed class ToolImage
    {
        public ToolImage(byte[] pngBytes, string caption = null)
        {
            PngBytes = pngBytes ?? throw new ArgumentNullException(nameof(pngBytes));
            Caption = caption;
        }

        public byte[] PngBytes { get; }
        public string MediaType => "image/png";
        public string Caption { get; }

        public string ToBase64() => Convert.ToBase64String(PngBytes);
    }
}
