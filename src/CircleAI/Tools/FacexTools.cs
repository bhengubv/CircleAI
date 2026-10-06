// FacexTools.cs
//
// ToolDefinition factory for the facex computer vision pipeline.
// Register FacexTools.FaceExtract() in addition to TheGeekNetworkTools
// when the host platform has a camera available.
//
// Design decisions:
//   - The tool is STATELESS: it takes frame metadata and returns absolute
//     landmark coordinates. Temporal delta computation is the caller's
//     responsibility — a stateless tool cannot hold a previous frame.
//   - The raw frame buffer (uint8_t*) is NOT passed through the tool call
//     parameter bag. In production the host invokes the native facex
//     library directly via IFacexBackend and maps the result to
//     FacialMetricMatrix. The ToolDefinition describes the intent to
//     the inference engine; the host dispatches the native call.

using System.Collections.Generic;

namespace CircleAI.Tools
{
    /// <summary>
    /// Tool definitions for the facex on-device computer vision pipeline.
    /// </summary>
    public static class FacexTools
    {
        private static ToolParameter Param(
            string type,
            string description,
            string[]? @enum = null) =>
            new() { Type = type, Description = description, Enum = @enum };

        /// <summary>
        /// Returns the <c>facex.extract_features</c> tool definition.
        /// Register this when the host platform has a camera. Do NOT include
        /// it in headless or server contexts where no camera is available.
        /// </summary>
        /// <remarks>
        /// The tool is stateless. It returns absolute, normalized landmark
        /// coordinates for a single frame. Callers that need temporal deltas
        /// (e.g. gaze velocity, blink rate) must subtract the previous frame's
        /// coordinates themselves.
        /// </remarks>
        public static IReadOnlyList<ToolDefinition> FaceExtract() => new[]
        {
            new ToolDefinition
            {
                Name = "facex.extract_features",
                Description =
                    "Extract facial landmark coordinates, a bounding box, an expression " +
                    "classification, and a detection confidence score from the current " +
                    "camera frame. Returns a single FacialMetricMatrix snapshot. " +
                    "Operates entirely on-device with no network calls. " +
                    "This tool is stateless — it returns absolute coordinates for one frame; " +
                    "call it on consecutive frames and subtract to obtain temporal deltas.",
                Parameters = new Dictionary<string, ToolParameter>
                {
                    ["frame_width"]  = Param(
                        "number",
                        "Width of the source camera frame in pixels. Required."),

                    ["frame_height"] = Param(
                        "number",
                        "Height of the source camera frame in pixels. Required."),

                    ["format"] = Param(
                        "string",
                        "Pixel format of the frame buffer.",
                        new[] { "yuv420", "rgb24", "bgr24", "grayscale" }),

                    ["min_confidence"] = Param(
                        "number",
                        "Minimum detection confidence threshold in [0.0, 1.0]. " +
                        "Detections below this score are not returned. Default 0.5."),
                },
                RequiredParameters = new[] { "frame_width", "frame_height", "format" }
            }
        };
    }
}
