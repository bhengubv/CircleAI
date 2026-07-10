# vision/onnx_plate_recognizer.py
#
# Port of CircleAI.Vision/OnnxPlateRecognizer.cs (C# — the EXACT spec).
#
# (Phase C3) IPlateRecognizer backed by an ONNX detector model. Follows the same
# letterbox + YOLO-postprocess pattern as OnnxFaceDetector but emits
# PlateRecognitionResult records. The plate-text OCR is a separate model — the C#
# leaves PlateText empty for a downstream OCR stage, and so do we.
#
# The native legs — image decode + resize + the ONNX session — are injected behind
# the :class:`IPlateModelRunner` seam. The deterministic YOLO box decode,
# coordinate back-projection, confidence threshold and NMS are ported faithfully.
# NB: the plate back-projection derives box W/H straight from ``bw/scale`` /
# ``bh/scale`` (NOT ``x2-x1``) — matching the C# exactly.

from __future__ import annotations

import math
import os
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import List, Optional, Sequence, Tuple

from ._geometry import Candidate, non_max_suppression
from .contracts import IPlateRecognizer
from .onnx_face_detector import FaceDetectorModelOutput as _ModelOutput  # identical shape
from .primitives import BoundingBox, PlateRecognitionResult


@dataclass(frozen=True, slots=True)
class OnnxPlateRecognizerOptions:
    """Mirrors ``CircleAI.Vision.OnnxPlateRecognizerOptions``."""

    model_path: str
    input_size: int = 640
    confidence_threshold: float = 0.5
    iou_threshold: float = 0.45
    country_hint: Optional[str] = None


# The plate model output has the same [batch, channel, box] + decoded-dims shape as
# the face-detector output; reuse that record verbatim rather than declaring a twin.
PlateModelOutput = _ModelOutput


class IPlateModelRunner(ABC):
    """Injected native seam — image decode + letterbox + ONNX inference.

    Stands in for the C# ``Image.Load`` + letterbox + ``ToTensor`` +
    ``InferenceSession.Run`` chain. Returns the raw output tensor (flattened
    ``[batch, channel, box]``) with its dims and the decoded original dimensions,
    or ``None`` to model the C# inference ``try/catch`` degrading to no plates.
    """

    @abstractmethod
    def run(self, image_bytes: bytes, input_size: int) -> "PlateModelOutput | None":
        ...


class OnnxPlateRecognizer(IPlateRecognizer):
    """(Phase C3) YOLO-family plate detector over an injected model runner.

    Mirrors ``CircleAI.Vision.OnnxPlateRecognizer``. ``require_model_file``
    (default False) skips the C# ``File.Exists(ModelPath)`` guard for in-memory use.
    """

    def __init__(
        self,
        opts: OnnxPlateRecognizerOptions,
        runner: IPlateModelRunner,
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

    async def recognize_async(
        self, image_bytes: bytes, ct: object = None
    ) -> Tuple[PlateRecognitionResult, ...]:
        if len(image_bytes) == 0:
            return ()

        try:
            output = self._runner.run(image_bytes, self._opts.input_size)
        except Exception:  # noqa: BLE001 — matches the C# inference try/catch -> empty
            return ()
        if output is None:
            return ()

        channels = output.channels
        boxes = output.boxes
        if channels < 5 or boxes <= 0:
            return ()

        orig_w = output.original_width
        orig_h = output.original_height
        scale, pad_x, pad_y = _letterbox(orig_w, orig_h, self._opts.input_size)
        if scale <= 0:
            return ()

        arr = output.data
        hits: List[Candidate] = []
        for n in range(boxes):
            cx = arr[0 * boxes + n]
            cy = arr[1 * boxes + n]
            bw = arr[2 * boxes + n]
            bh = arr[3 * boxes + n]
            score = arr[4 * boxes + n]
            if score < self._opts.confidence_threshold:
                continue
            x1 = (cx - bw / 2 - pad_x) / scale
            y1 = (cy - bh / 2 - pad_y) / scale
            bx = max(0, int(math.floor(x1)))
            by = max(0, int(math.floor(y1)))
            bxw = min(orig_w - bx, int(math.ceil(bw / scale)))
            bxh = min(orig_h - by, int(math.ceil(bh / scale)))
            if bxw <= 0 or bxh <= 0:
                continue
            hits.append((float(score), BoundingBox(bx, by, bxw, bxh)))

        kept = non_max_suppression(hits, self._opts.iou_threshold)
        return tuple(
            PlateRecognitionResult(
                plate_text="",  # OCR pass is a separate model — left to a follow-up
                country_hint=self._opts.country_hint,
                region=k[1],
                confidence=k[0],
            )
            for k in kept
        )


def _letterbox(width: int, height: int, input_size: int) -> Tuple[float, int, int]:
    """Scalar half of the C# inline letterbox — scale + symmetric padding.
    Returns ``(scale, pad_x, pad_y)`` (plate order); pixels are native."""
    if width <= 0 or height <= 0:
        return 0.0, 0, 0
    scale = min(input_size / width, input_size / height)
    new_w = int(round(width * scale))
    new_h = int(round(height * scale))
    pad_x = (input_size - new_w) // 2
    pad_y = (input_size - new_h) // 2
    return scale, pad_x, pad_y


__all__ = [
    "OnnxPlateRecognizerOptions",
    "PlateModelOutput",
    "IPlateModelRunner",
    "OnnxPlateRecognizer",
]
