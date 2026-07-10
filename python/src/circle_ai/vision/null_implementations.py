# vision/null_implementations.py
#
# Port of CircleAI.Vision/NullImplementations.cs + the NullVideoCapture from
# CircleAI.Vision/IVideoCapture.cs (C# — the EXACT spec).
#
# (2.2.0) Safe null defaults — every interface has a working implementation that
# returns empty / no-op / fail-closed results. Lets the hosting layer wire
# CircleAI.Vision optionally; absence of a real backend degrades to deterministic
# empty answers, never a crash.

from __future__ import annotations

from typing import AsyncIterator, Optional, Tuple

from .contracts import (
    BluetoothAnomalyHandler,
    IBluetoothAnomalyDetector,
    IComputerVisionRuntime,
    IDisposable,
    IDocumentVerifier,
    IFaceDetector,
    IFaceEmbedder,
    IFaceLivenessDetector,
    IPlateRecognizer,
    IVideoCapture,
    VideoFrame,
)
from .primitives import (
    DetectedFace,
    DocumentVerificationResult,
    FaceEmbedding,
    LivenessResult,
    PlateRecognitionResult,
)


class NullVideoCapture(IVideoCapture):
    """(Phase C2) Headless / no-camera fallback — yields nothing.

    Mirrors ``CircleAI.Vision.NullVideoCapture``.
    """

    async def capture_async(
        self, preferred_width: int, preferred_height: int, ct: object = None
    ) -> AsyncIterator[VideoFrame]:
        # ct.ThrowIfCancellationRequested(); yield break — completes immediately.
        return
        yield  # pragma: no cover — makes this an async generator


    async def dispose_async(self) -> None:
        return None


class NullComputerVisionRuntime(IComputerVisionRuntime):
    """(2.2.0) No-op vision runtime. Mirrors ``CircleAI.Vision.NullComputerVisionRuntime``."""

    _instance: "NullComputerVisionRuntime | None" = None

    @classmethod
    def instance(cls) -> "NullComputerVisionRuntime":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def backend_id(self) -> str:
        return "null"

    async def decode_async(self, image_bytes: bytes, ct: object = None) -> Optional[object]:
        return None

    async def resize_async(
        self, image: object, width: int, height: int, ct: object = None
    ) -> Optional[object]:
        return None


class NullFaceDetector(IFaceDetector):
    """(2.2.0) Returns no faces. Useful as the default DI registration.

    Mirrors ``CircleAI.Vision.NullFaceDetector``.
    """

    _instance: "NullFaceDetector | None" = None

    @classmethod
    def instance(cls) -> "NullFaceDetector":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    async def detect_async(self, image_bytes: bytes, ct: object = None) -> Tuple[DetectedFace, ...]:
        return ()


class NullFaceEmbedder(IFaceEmbedder):
    """(2.2.0) Returns a zero-vector at the configured dimension.

    Mirrors ``CircleAI.Vision.NullFaceEmbedder`` (``NullFaceEmbedder(int dimension = 512)``).
    """

    def __init__(self, dimension: int = 512) -> None:
        self._dimension = dimension

    @property
    def dimension(self) -> int:
        return self._dimension

    async def embed_async(
        self, image_bytes: bytes, face: DetectedFace, ct: object = None
    ) -> FaceEmbedding:
        return FaceEmbedding(vector=tuple(0.0 for _ in range(self._dimension)), dimension=self._dimension)


class NullFaceLivenessDetector(IFaceLivenessDetector):
    """(2.2.0) Reports "no liveness backend" — fail-closed default.

    Mirrors ``CircleAI.Vision.NullFaceLivenessDetector``.
    """

    _instance: "NullFaceLivenessDetector | None" = None

    @classmethod
    def instance(cls) -> "NullFaceLivenessDetector":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    async def check_async(self, image_bytes: bytes, ct: object = None) -> LivenessResult:
        return LivenessResult(
            is_live=False, confidence=0.0, failure_reason="no liveness backend registered"
        )


class NullDocumentVerifier(IDocumentVerifier):
    """(2.2.0) Reports unverified — fail-closed default.

    Mirrors ``CircleAI.Vision.NullDocumentVerifier``.
    """

    _instance: "NullDocumentVerifier | None" = None

    @classmethod
    def instance(cls) -> "NullDocumentVerifier":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    async def verify_async(self, image_bytes: bytes, ct: object = None) -> DocumentVerificationResult:
        return DocumentVerificationResult(
            is_valid=False,
            document_type="unknown",
            issuing_country="unknown",
            fields=(),
            overall_confidence=0.0,
            warnings=("no document verifier backend registered",),
        )


class NullPlateRecognizer(IPlateRecognizer):
    """(2.2.0) Returns no plates. Mirrors ``CircleAI.Vision.NullPlateRecognizer``."""

    _instance: "NullPlateRecognizer | None" = None

    @classmethod
    def instance(cls) -> "NullPlateRecognizer":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    async def recognize_async(
        self, image_bytes: bytes, ct: object = None
    ) -> Tuple[PlateRecognitionResult, ...]:
        return ()


class _EmptyDisposable(IDisposable):
    _instance: "_EmptyDisposable | None" = None

    @classmethod
    def instance(cls) -> "_EmptyDisposable":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    def dispose(self) -> None:
        pass


class NullBluetoothAnomalyDetector(IBluetoothAnomalyDetector):
    """(2.2.0) Reports no anomalies; subscribers never fire.

    Mirrors ``CircleAI.Vision.NullBluetoothAnomalyDetector``.
    """

    @property
    def backend_id(self) -> str:
        return "null"

    def subscribe(self, handler: BluetoothAnomalyHandler) -> IDisposable:
        return _EmptyDisposable.instance()

    async def start_async(self, ct: object = None) -> None:
        return None

    async def stop_async(self, ct: object = None) -> None:
        return None

    async def dispose_async(self) -> None:
        return None


__all__ = [
    "NullVideoCapture",
    "NullComputerVisionRuntime",
    "NullFaceDetector",
    "NullFaceEmbedder",
    "NullFaceLivenessDetector",
    "NullDocumentVerifier",
    "NullPlateRecognizer",
    "NullBluetoothAnomalyDetector",
]
