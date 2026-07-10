# vision/contracts.py
#
# Port of CircleAI.Vision/Contracts.cs + CircleAI.Vision/IVideoCapture.cs
# (C# — the EXACT spec).
#
# (2.2.0) The CircleAI.Vision contract surface. Null implementations ship out of
# the box (see null_implementations.py); real backends — compv (CV foundation),
# facex (face stack), FaceLivenessDetection-SDK, KYC-Documents-Verif-SDK,
# ultimateALPR-SDK, Bluehound — are injected dependencies.
#
# (Phase C2) IVideoCapture is the camera analogue of CircleAI.Voice.IAudioCapture:
# an async-stream of raw frame buffers with metadata (pixel format, dimensions).
#
# C# -> Python mapping:
#   ReadOnlyMemory<byte>          -> bytes
#   IAsyncEnumerable<T>           -> AsyncIterator[T]
#   IReadOnlyList<T>              -> tuple[T, ...]
#   ValueTask<T> / Task<T>        -> async def -> T
#   IAsyncDisposable              -> dispose_async() + async context manager
#   IDisposable                   -> dispose() + context manager
#   Func<T, ValueTask>            -> Callable[[T], Awaitable[None]]
#   enum                          -> IntEnum (stable ordinals = C# declaration order)
#   DateTimeOffset                -> datetime (tz-aware, UTC)

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import IntEnum
from typing import Awaitable, Callable, AsyncIterator, Optional, Tuple

from .primitives import (
    BluetoothAnomaly,
    DetectedFace,
    DocumentVerificationResult,
    FaceEmbedding,
    LivenessResult,
    PlateRecognitionResult,
)


# ── shared disposable handles ───────────────────────────────────────────────────


class IDisposable(ABC):
    """Subscription / resource handle mirroring C# ``IDisposable``."""

    @abstractmethod
    def dispose(self) -> None:
        ...

    def __enter__(self) -> "IDisposable":
        return self

    def __exit__(self, *exc_info: object) -> None:
        self.dispose()


# ── camera capture (IVideoCapture.cs) ───────────────────────────────────────────


class VideoPixelFormat(IntEnum):
    """(Phase C2) Pixel layout of a captured :class:`VideoFrame`.

    Mirrors ``CircleAI.Vision.VideoPixelFormat``. Ordinals are the C# declaration
    order and are stable across languages.
    """

    YUV420 = 0
    NV21 = 1
    RGBA32 = 2
    BGR24 = 3
    JPEG = 4


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass(frozen=True, slots=True)
class VideoFrame:
    """(Phase C2) One captured camera frame + metadata.

    Mirrors ``CircleAI.Vision.VideoFrame`` —
    ``record(ReadOnlyMemory<byte> Bytes, int Width, int Height,
    VideoPixelFormat PixelFormat, DateTimeOffset CapturedAtUtc,
    int? RotationDegrees = null)``.
    """

    bytes: bytes
    width: int
    height: int
    pixel_format: VideoPixelFormat
    captured_at_utc: datetime = field(default_factory=_utc_now)
    rotation_degrees: Optional[int] = None


class IVideoCapture(ABC):
    """(Phase C2) Async-stream of camera frames.

    Mirrors ``CircleAI.Vision.IVideoCapture`` (``IAsyncDisposable``).
    """

    @abstractmethod
    def capture_async(
        self, preferred_width: int, preferred_height: int, ct: object = None
    ) -> AsyncIterator[VideoFrame]:
        """Open the camera at the requested resolution and start streaming. The
        capture loop is bound to ``ct``."""
        ...

    @abstractmethod
    async def dispose_async(self) -> None:
        ...

    async def __aenter__(self) -> "IVideoCapture":
        return self

    async def __aexit__(self, *exc: object) -> None:
        await self.dispose_async()


# ── CV runtime + detectors (Contracts.cs) ───────────────────────────────────────


class IComputerVisionRuntime(ABC):
    """(2.2.0) Generic CV-runtime primitive. Consumers that need basic image
    decoding / resize / colour-space ops dispatch through this surface.

    Mirrors ``CircleAI.Vision.IComputerVisionRuntime``. The C# opaque
    backend-private image (``object?``) maps to ``Optional[object]``.
    """

    @abstractmethod
    async def decode_async(self, image_bytes: bytes, ct: object = None) -> Optional[object]:
        """Decode bytes into a backend-private opaque image."""
        ...

    @abstractmethod
    async def resize_async(
        self, image: object, width: int, height: int, ct: object = None
    ) -> Optional[object]:
        """Resize an opaque image. Returns a new opaque image."""
        ...

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification — "compv-3.x", "null", etc."""
        ...


class IFaceDetector(ABC):
    """(2.2.0) Find faces in an image. Mirrors ``CircleAI.Vision.IFaceDetector``."""

    @abstractmethod
    async def detect_async(self, image_bytes: bytes, ct: object = None) -> Tuple[DetectedFace, ...]:
        ...


class IFaceEmbedder(ABC):
    """(2.2.0) Convert a detected face into a similarity-search vector.

    Mirrors ``CircleAI.Vision.IFaceEmbedder``.
    """

    @property
    @abstractmethod
    def dimension(self) -> int:
        ...

    @abstractmethod
    async def embed_async(
        self, image_bytes: bytes, face: DetectedFace, ct: object = None
    ) -> FaceEmbedding:
        ...


class IFaceLivenessDetector(ABC):
    """(2.2.0) Decide whether the camera is looking at a real person.

    Mirrors ``CircleAI.Vision.IFaceLivenessDetector``.
    """

    @abstractmethod
    async def check_async(self, image_bytes: bytes, ct: object = None) -> LivenessResult:
        ...


class IDocumentVerifier(ABC):
    """(2.2.0) Parse + verify a KYC document image.

    Mirrors ``CircleAI.Vision.IDocumentVerifier``.
    """

    @abstractmethod
    async def verify_async(self, image_bytes: bytes, ct: object = None) -> DocumentVerificationResult:
        ...


class IPlateRecognizer(ABC):
    """(2.2.0) Read a license plate from an image.

    Mirrors ``CircleAI.Vision.IPlateRecognizer``.
    """

    @abstractmethod
    async def recognize_async(
        self, image_bytes: bytes, ct: object = None
    ) -> Tuple[PlateRecognitionResult, ...]:
        ...


# Handler for BLE-anomaly subscriptions: ``Func<BluetoothAnomaly, ValueTask>``.
BluetoothAnomalyHandler = Callable[[BluetoothAnomaly], Awaitable[None]]


class IBluetoothAnomalyDetector(ABC):
    """(2.2.0) Surface for AetherNet adversary detection — BLE / RF anomalies
    raised by the platform's Bluetooth radio. Implementations are long-running
    (``start_async`` / ``stop_async`` lifecycle).

    Mirrors ``CircleAI.Vision.IBluetoothAnomalyDetector`` (``IAsyncDisposable``).
    """

    @abstractmethod
    def subscribe(self, handler: BluetoothAnomalyHandler) -> IDisposable:
        """Subscribe to anomaly events. Returns an unsubscribe handle."""
        ...

    @abstractmethod
    async def start_async(self, ct: object = None) -> None:
        """Begin monitoring. Idempotent."""
        ...

    @abstractmethod
    async def stop_async(self, ct: object = None) -> None:
        """Stop monitoring. Idempotent."""
        ...

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification — "bluehound-1.x", "null", etc."""
        ...

    @abstractmethod
    async def dispose_async(self) -> None:
        ...

    async def __aenter__(self) -> "IBluetoothAnomalyDetector":
        return self

    async def __aexit__(self, *exc: object) -> None:
        await self.dispose_async()


__all__ = [
    # shared
    "IDisposable",
    # camera capture
    "VideoPixelFormat",
    "VideoFrame",
    "IVideoCapture",
    # CV runtime + detectors
    "IComputerVisionRuntime",
    "IFaceDetector",
    "IFaceEmbedder",
    "IFaceLivenessDetector",
    "IDocumentVerifier",
    "IPlateRecognizer",
    "IBluetoothAnomalyDetector",
    "BluetoothAnomalyHandler",
]
