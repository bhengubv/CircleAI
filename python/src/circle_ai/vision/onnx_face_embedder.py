# vision/onnx_face_embedder.py
#
# Port of CircleAI.Vision/OnnxFaceEmbedder.cs (C# — the EXACT spec).
#
# (Phase C3) Real IFaceEmbedder backed by an ArcFace-family ONNX model. The C#
# reference does: ImageSharp decode -> clamp the detected region into image bounds
# -> crop + resize to 112x112 -> BGR mean-subtracted tensor ((px - 127.5)/128) ->
# ONNX InferenceSession.Run -> L2-normalise the raw vector (so cosine == dot).
#
# The native legs — image decode, crop/resize, BGR tensor build and the ONNX
# session — are injected behind the :class:`IFaceEmbedderModelRunner` seam. The
# deterministic parts — region clamping and L2 normalisation — are ported
# faithfully. On inference failure the C# returns a zero-vector at the configured
# dimension; we mirror that exactly.

from __future__ import annotations

import math
import os
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import List, Sequence, Tuple

from .contracts import IFaceEmbedder
from .primitives import BoundingBox, DetectedFace, FaceEmbedding


@dataclass(frozen=True, slots=True)
class OnnxFaceEmbedderOptions:
    """Mirrors ``CircleAI.Vision.OnnxFaceEmbedderOptions``.

    :param model_path: Path to an ArcFace-family ONNX model.
    :param input_size: Square input dimension (112 = ArcFace default).
    :param dimension: Output embedding dimension (typically 512).
    """

    model_path: str
    input_size: int = 112
    dimension: int = 512


class IFaceEmbedderModelRunner(ABC):
    """Injected native seam — image decode + crop/resize + ArcFace inference.

    Stands in for the C# ``Image.Load`` + ``Crop``/``Resize`` + BGR tensor +
    ``InferenceSession.Run`` chain.
    """

    @abstractmethod
    def decode_dimensions(self, image_bytes: bytes) -> Tuple[int, int]:
        """Decode just enough to report ``(width, height)`` in pixels — the input
        to the ported :func:`_clamp_region`."""
        ...

    @abstractmethod
    def run(self, image_bytes: bytes, region: BoundingBox, input_size: int) -> "Sequence[float] | None":
        """Crop the (already-clamped) ``region``, resize to ``input_size`` square,
        build the BGR mean-subtracted tensor and run the model. Return the raw
        (pre-normalisation) embedding, or ``None`` to signal inference failure."""
        ...


class OnnxFaceEmbedder(IFaceEmbedder):
    """(Phase C3) ArcFace-family embedder over an injected model runner.

    Mirrors ``CircleAI.Vision.OnnxFaceEmbedder`` — the native decode+ONNX legs are
    the :class:`IFaceEmbedderModelRunner` seam; region clamp + L2 normalise are a
    faithful port. ``require_model_file`` (default False) skips the C#
    ``File.Exists(ModelPath)`` guard for in-memory use.
    """

    def __init__(
        self,
        opts: OnnxFaceEmbedderOptions,
        runner: IFaceEmbedderModelRunner,
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

    @property
    def dimension(self) -> int:
        return self._opts.dimension

    async def embed_async(
        self, image_bytes: bytes, face: DetectedFace, ct: object = None
    ) -> FaceEmbedding:
        if face is None:
            raise ValueError("face")

        try:
            width, height = self._runner.decode_dimensions(image_bytes)
            region = _clamp_region(face.region, width, height)
            raw = self._runner.run(image_bytes, region, self._opts.input_size)
        except Exception:  # noqa: BLE001 — matches the C# inference try/catch -> zero vector
            return _zero_embedding(self._opts.dimension)
        if raw is None:
            return _zero_embedding(self._opts.dimension)

        vec: List[float] = [float(x) for x in raw]
        _l2_normalise(vec)
        return FaceEmbedding(vector=tuple(vec), dimension=len(vec))


def _clamp_region(region: BoundingBox, image_width: int, image_height: int) -> BoundingBox:
    """Faithful port of the C# ``ClampRegion`` — pull the box fully inside image
    bounds while keeping width/height >= 1."""
    x = _clamp(region.x, 0, image_width - 1)
    y = _clamp(region.y, 0, image_height - 1)
    w = _clamp(region.width, 1, image_width - x)
    h = _clamp(region.height, 1, image_height - y)
    return BoundingBox(x, y, w, h)


def _clamp(value: int, low: int, high: int) -> int:
    # Mirrors System.Math.Clamp (low <= high assumed by construction).
    if value < low:
        return low
    if value > high:
        return high
    return value


def _l2_normalise(v: List[float]) -> None:
    """Faithful port of the C# ``L2Normalise`` — in-place, no-op below 1e-9 norm.
    Accumulates the sum-of-squares in double precision as C# does (``double sumSq``)."""
    sum_sq = 0.0
    for x in v:
        sum_sq += x * x
    norm = math.sqrt(sum_sq)
    if norm < 1e-9:
        return
    for i in range(len(v)):
        v[i] = v[i] / norm


def _zero_embedding(dimension: int) -> FaceEmbedding:
    return FaceEmbedding(vector=tuple(0.0 for _ in range(dimension)), dimension=dimension)


__all__ = [
    "OnnxFaceEmbedderOptions",
    "IFaceEmbedderModelRunner",
    "OnnxFaceEmbedder",
]
