# vision/primitives.py
#
# Port of CircleAI.Vision/Primitives.cs (C# — the EXACT spec).
#
# (2.2.0) Shared shapes used across the vision contract surface.
#
# C# -> Python type mapping used throughout the vision module:
#   ReadOnlyMemory<byte> / ReadOnlySpan<byte>  -> bytes
#   readonly record struct                     -> @dataclass(frozen=True, slots=True)
#   sealed record                              -> @dataclass(frozen=True, slots=True)
#   IReadOnlyList<T>                           -> tuple[T, ...]
#   float (C# System.Single)                   -> float
#   DateTimeOffset                             -> datetime (tz-aware, UTC)
#   ValueTask<T>                               -> async def -> T

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from typing import Optional, Tuple


@dataclass(frozen=True, slots=True)
class BoundingBox:
    """An axis-aligned rectangle in image-pixel coordinates.

    Mirrors ``CircleAI.Vision.BoundingBox`` —
    ``readonly record struct BoundingBox(int X, int Y, int Width, int Height)``.
    """

    x: int
    y: int
    width: int
    height: int


@dataclass(frozen=True, slots=True)
class LandmarkPoint:
    """A 2D point on a detected face — eye centre, mouth corner, etc.

    Coordinates are image-pixel space. Mirrors ``CircleAI.Vision.LandmarkPoint`` —
    ``readonly record struct LandmarkPoint(int X, int Y)``.
    """

    x: int
    y: int


@dataclass(frozen=True, slots=True)
class DetectedFace:
    """One detected face with optional landmark fallback.

    Mirrors ``CircleAI.Vision.DetectedFace``.
    """

    region: BoundingBox
    confidence: float
    landmarks: Optional[Tuple[LandmarkPoint, ...]] = None


@dataclass(frozen=True, slots=True)
class FaceEmbedding:
    """A face embedding suitable for similarity search.

    ``vector`` is normalised so cosine similarity reduces to a dot product.
    Mirrors ``CircleAI.Vision.FaceEmbedding``.
    """

    vector: Tuple[float, ...]
    dimension: int


@dataclass(frozen=True, slots=True)
class LivenessResult:
    """Outcome of liveness detection — is the camera seeing a real human, a
    printed photo, a screen replay, a 3D mask, …?

    Mirrors ``CircleAI.Vision.LivenessResult``.
    """

    is_live: bool
    confidence: float
    failure_reason: Optional[str] = None


@dataclass(frozen=True, slots=True)
class DocumentField:
    """One parsed field from an ID document.

    Mirrors ``CircleAI.Vision.DocumentField`` —
    ``record(string Key, string Value, float Confidence)``.
    """

    key: str
    value: str
    confidence: float


@dataclass(frozen=True, slots=True)
class DocumentVerificationResult:
    """Outcome of KYC document verification.

    Mirrors ``CircleAI.Vision.DocumentVerificationResult``.
    """

    is_valid: bool
    document_type: str
    issuing_country: str
    fields: Tuple[DocumentField, ...]
    overall_confidence: float
    warnings: Optional[Tuple[str, ...]] = None


@dataclass(frozen=True, slots=True)
class PlateRecognitionResult:
    """Outcome of license-plate recognition.

    Mirrors ``CircleAI.Vision.PlateRecognitionResult``.
    """

    plate_text: str
    country_hint: Optional[str]
    region: BoundingBox
    confidence: float


@dataclass(frozen=True, slots=True)
class BluetoothAnomaly:
    """One observed BLE / RF anomaly. Severity 0-1; higher = more concerning.

    Mirrors ``CircleAI.Vision.BluetoothAnomaly``.
    """

    source: str
    kind: str
    severity: float
    description: str
    observed_at_utc: datetime


__all__ = [
    "BoundingBox",
    "LandmarkPoint",
    "DetectedFace",
    "FaceEmbedding",
    "LivenessResult",
    "DocumentField",
    "DocumentVerificationResult",
    "PlateRecognitionResult",
    "BluetoothAnomaly",
]
