# facex_tools.py
#
# Port of CircleAI.Tools FacexTools.cs (C# — the EXACT spec).
#
# ToolDefinition factory for the facex computer vision pipeline. Register
# FacexTools.face_extract() in addition to TheGeekNetworkTools when the host
# platform has a camera available.
#
# Design decisions (carried over from the C#):
#   - The tool is STATELESS: it takes frame metadata and returns absolute
#     landmark coordinates. Temporal delta computation is the caller's
#     responsibility — a stateless tool cannot hold a previous frame.
#   - The raw frame buffer is NOT passed through the tool call parameter bag. In
#     production the host invokes the native facex library directly and maps the
#     result to FacialMetricMatrix. The ToolDefinition describes the intent to
#     the inference engine; the host dispatches the native call.

from __future__ import annotations

from typing import List, Optional

from .tool_types import ToolDefinition, ToolParameter


def _param(type: str, description: str, enum: Optional[List[str]] = None) -> ToolParameter:
    return ToolParameter(type=type, description=description, enum=enum)


class FacexTools:
    """Tool definitions for the facex on-device computer vision pipeline.
    Mirrors ``CircleAI.Tools.FacexTools`` (a static class).
    """

    @staticmethod
    def face_extract() -> List[ToolDefinition]:
        """Return the ``facex.extract_features`` tool definition.

        Register this when the host platform has a camera. Do NOT include it in
        headless or server contexts where no camera is available.

        The tool is stateless. It returns absolute, normalized landmark
        coordinates for a single frame. Callers that need temporal deltas
        (e.g. gaze velocity, blink rate) must subtract the previous frame's
        coordinates themselves.
        """
        return [
            ToolDefinition(
                name="facex.extract_features",
                description=(
                    "Extract facial landmark coordinates, a bounding box, an "
                    "expression classification, and a detection confidence score "
                    "from the current camera frame. Returns a single "
                    "FacialMetricMatrix snapshot. Operates entirely on-device "
                    "with no network calls. This tool is stateless — it returns "
                    "absolute coordinates for one frame; call it on consecutive "
                    "frames and subtract to obtain temporal deltas."
                ),
                parameters={
                    "frame_width": _param(
                        "number",
                        "Width of the source camera frame in pixels. Required.",
                    ),
                    "frame_height": _param(
                        "number",
                        "Height of the source camera frame in pixels. Required.",
                    ),
                    "format": _param(
                        "string",
                        "Pixel format of the frame buffer.",
                        ["yuv420", "rgb24", "bgr24", "grayscale"],
                    ),
                    "min_confidence": _param(
                        "number",
                        "Minimum detection confidence threshold in [0.0, 1.0]. "
                        "Detections below this score are not returned. Default 0.5.",
                    ),
                },
                required_parameters=["frame_width", "frame_height", "format"],
            )
        ]
