# vision/onnx_face_detector.py
#
# Port of CircleAI.Vision/OnnxFaceDetector.cs (C# — the EXACT spec).
#
# (Phase C3) Real IFaceDetector designed against YOLOv8-face / YOLOv5-face /
# RetinaFace family models — all share the same boxes+score+landmarks output
# shape. The C# reference does: ImageSharp decode -> letterbox pad to a square ->
# CHW float tensor (R/G/B / 255) -> ONNX InferenceSession.Run -> YOLO postprocess
# (back-project boxes from letterbox space, threshold, NMS).
#
# The native legs — image decode + resize + the ONNX session — are injected here
# behind the :class:`IFaceDetectorModelRunner` seam (the same seam boundary the
# voice ONNX ports use for InferenceSession.Run). Everything deterministic —
# the letterbox scalar arithmetic, YOLO box decode, coordinate back-projection,
# confidence threshold and NMS — is ported faithfully so the behaviour is exact
# given a runner. ``struct.pack("<f", x)`` is not needed here: the model output is
# consumed as Python floats, never re-serialised to a wire format.

from __future__ import annotations

import math
import os
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import List, Sequence, Tuple

from ._geometry import Candidate, non_max_suppression
from .contracts import IFaceDetector
from .primitives import BoundingBox, DetectedFace


@dataclass(frozen=True, slots=True)
class OnnxFaceDetectorOptions:
    """Mirrors ``CircleAI.Vision.OnnxFaceDetectorOptions``.

    :param model_path: Path to a YOLO-family ONNX face-detection model.
    :param input_size: Square input dimension (640 = YOLOv8 default).
    :param confidence_threshold: Skip detections under this score (0..1).
    :param iou_threshold: NMS IoU cutoff (0..1).
    """

    model_path: str
    input_size: int = 640
    confidence_threshold: float = 0.5
    iou_threshold: float = 0.45


@dataclass(frozen=True, slots=True)
class FaceDetectorModelOutput:
    """Raw output of one detector inference — the injected-seam return value.

    Carries the flattened model output tensor (laid out ``[batch, channel, box]``
    exactly like the C# ``output.ToArray()``) plus its channel/box dims and the
    decoded image's original pixel dimensions. The C# side gets ``origW``/``origH``
    from the decoded ``Image`` and the tensor from the session; both are native, so
    both are supplied by the runner here.

    :param data: Flattened output, index ``= channel * boxes + box``.
    :param channels: ``dims[1]`` — must be >= 5 for cx,cy,w,h,score.
    :param boxes: ``dims[2]`` — number of candidate boxes.
    :param original_width: Decoded source-image width in pixels.
    :param original_height: Decoded source-image height in pixels.
    """

    data: Sequence[float]
    channels: int
    boxes: int
    original_width: int
    original_height: int


class IFaceDetectorModelRunner(ABC):
    """Injected native seam — image decode + letterbox + ONNX inference.

    Stands in for the C# ``Image.Load`` + ``LetterboxResize`` + ``ToTensor`` +
    ``InferenceSession.Run`` chain. Given the raw encoded image bytes and the
    square ``input_size``, decode + letterbox + run the model and return the raw
    output tensor plus the decoded original dimensions.

    Returning ``None`` models the C# inference ``try/catch`` that degrades to an
    empty detection list.
    """

    @abstractmethod
    def run(self, image_bytes: bytes, input_size: int) -> "FaceDetectorModelOutput | None":
        ...


class OnnxFaceDetector(IFaceDetector):
    """(Phase C3) YOLO-family face detector over an injected model runner.

    Mirrors ``CircleAI.Vision.OnnxFaceDetector`` — the native decode+ONNX legs are
    the :class:`IFaceDetectorModelRunner` seam; the deterministic YOLO postprocess
    is a faithful port. ``require_model_file`` (default False) skips the C#
    ``File.Exists(ModelPath)`` guard so an in-memory runner can be used without a
    real file on disk.
    """

    def __init__(
        self,
        opts: OnnxFaceDetectorOptions,
        runner: IFaceDetectorModelRunner,
        require_model_file: bool = False,
    ) -> None:
        if opts is None:
            raise ValueError("opts")
        if runner is None:
            raise ValueError("runner")
        if require_model_file and not os.path.isfile(opts.model_path):
            raise FileNotFoundError(f"ONNX model not found: {opts.model_path}")
        self._opts = opts
        self._runner = runner

    async def detect_async(self, image_bytes: bytes, ct: object = None) -> Tuple[DetectedFace, ...]:
        # C#: ct.ThrowIfCancellationRequested(); if (imageBytes.IsEmpty) return empty.
        if len(image_bytes) == 0:
            return ()

        try:
            output = self._runner.run(image_bytes, self._opts.input_size)
        except Exception:  # noqa: BLE001 — matches the C# inference try/catch -> empty
            return ()
        if output is None:
            return ()

        return self._postprocess_yolo(output)

    # ── Helpers ──────────────────────────────────────────────────────────────────

    def _postprocess_yolo(self, output: FaceDetectorModelOutput) -> Tuple[DetectedFace, ...]:
        """Faithful port of ``PostprocessYolo`` (+ the letterbox scalar math it
        depends on). YOLOv8 output layout: ``[1, 4+1+K, N]``; we read the first 5
        channels per box (cx, cy, w, h, score)."""
        channels = output.channels
        boxes = output.boxes
        # C# guards dims.Length != 3; here the seam already yields (channels, boxes).
        if channels < 5 or boxes <= 0:
            return ()

        orig_w = output.original_width
        orig_h = output.original_height
        pad_x, pad_y, scale = _letterbox_params(orig_w, orig_h, self._opts.input_size)
        if scale <= 0:
            return ()

        arr = output.data
        candidates: List[Candidate] = []
        # arr laid out [batch, channel, box] flattened. Index = c*boxes + n.
        for n in range(boxes):
            cx = arr[0 * boxes + n]
            cy = arr[1 * boxes + n]
            bw = arr[2 * boxes + n]
            bh = arr[3 * boxes + n]
            score = arr[4 * boxes + n]
            if score < self._opts.confidence_threshold:
                continue

            # Convert back from letterbox space to original pixel space.
            x1 = (cx - bw / 2 - pad_x) / scale
            y1 = (cy - bh / 2 - pad_y) / scale
            x2 = (cx + bw / 2 - pad_x) / scale
            y2 = (cy + bh / 2 - pad_y) / scale
            bx = max(0, int(math.floor(x1)))
            by = max(0, int(math.floor(y1)))
            bxw = min(orig_w - bx, int(math.ceil(x2 - x1)))
            bxh = min(orig_h - by, int(math.ceil(y2 - y1)))
            if bxw <= 0 or bxh <= 0:
                continue
            candidates.append((float(score), BoundingBox(bx, by, bxw, bxh)))

        kept = non_max_suppression(candidates, self._opts.iou_threshold)
        return tuple(DetectedFace(c[1], c[0], None) for c in kept)


def _letterbox_params(width: int, height: int, input_size: int) -> Tuple[int, int, float]:
    """Scalar half of the C# ``LetterboxResize`` — scale + symmetric padding. The
    pixel resize/pad itself is native and lives behind the runner seam."""
    if width <= 0 or height <= 0:
        return 0, 0, 0.0
    scale = min(input_size / width, input_size / height)
    new_w = int(round(width * scale))
    new_h = int(round(height * scale))
    pad_x = (input_size - new_w) // 2
    pad_y = (input_size - new_h) // 2
    return pad_x, pad_y, scale


__all__ = [
    "OnnxFaceDetectorOptions",
    "FaceDetectorModelOutput",
    "IFaceDetectorModelRunner",
    "OnnxFaceDetector",
]
