// tools/facex_tools.ts
//
// ToolDefinition factory for the facex computer-vision pipeline. Port of
// CircleAI.Tools.FacexTools. Register FacexTools.faceExtract() in addition to
// TheGeekNetworkTools when the host platform has a camera available.
//
// The tool is STATELESS: it takes frame metadata and returns absolute landmark
// coordinates. Temporal delta computation is the caller's responsibility. The
// raw frame buffer is NOT passed through the tool call parameter bag — in
// production the host invokes the native facex library directly and maps the
// result to FacialMetricMatrix; the ToolDefinition describes the intent.

import type { ToolDefinition, ToolParameter } from "./index.js";

function param(type: string, description: string, enumValues?: string[]): ToolParameter {
  return { type, description, enum: enumValues };
}

/**
 * Tool definitions for the facex on-device computer-vision pipeline. Mirrors
 * `CircleAI.Tools.FacexTools`.
 */
export const FacexTools = {
  /**
   * Returns the `facex.extract_features` tool definition. Register this when the
   * host platform has a camera; do NOT include it in headless / server contexts.
   * The tool is stateless — it returns absolute, normalized landmark coordinates
   * for a single frame.
   */
  faceExtract(): readonly ToolDefinition[] {
    return [
      {
        name: "facex.extract_features",
        description:
          "Extract facial landmark coordinates, a bounding box, an expression " +
          "classification, and a detection confidence score from the current " +
          "camera frame. Returns a single FacialMetricMatrix snapshot. " +
          "Operates entirely on-device with no network calls. " +
          "This tool is stateless — it returns absolute coordinates for one frame; " +
          "call it on consecutive frames and subtract to obtain temporal deltas.",
        parameters: {
          frame_width: param("number", "Width of the source camera frame in pixels. Required."),
          frame_height: param("number", "Height of the source camera frame in pixels. Required."),
          format: param("string", "Pixel format of the frame buffer.", [
            "yuv420",
            "rgb24",
            "bgr24",
            "grayscale",
          ]),
          min_confidence: param(
            "number",
            "Minimum detection confidence threshold in [0.0, 1.0]. " +
              "Detections below this score are not returned. Default 0.5.",
          ),
        },
        requiredParameters: ["frame_width", "frame_height", "format"],
      },
    ];
  },
};
