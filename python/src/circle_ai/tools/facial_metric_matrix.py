from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import Enum


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class FaceExpressionClassification(Enum):
    """Classified facial expression from a facial metric matrix."""
    NEUTRAL   = "Neutral"
    HAPPY     = "Happy"
    SURPRISED = "Surprised"
    CONFUSED  = "Confused"
    STRESSED  = "Stressed"
    ANGRY     = "Angry"
    UNKNOWN   = "Unknown"


@dataclass(frozen=True)
class FaceBoundingBox:
    """Bounding box for a detected face, normalised to [0.0, 1.0]."""

    x: float
    y: float
    width: float
    height: float


@dataclass
class FacialMetricMatrix:
    """Facial landmark and expression data captured from a camera frame.

    landmarks is a flat list of length 136: x0, y0, x1, y1, ... x67, y67
    All (x, y) values are normalised to [0.0, 1.0] relative to the bounding box.
    """

    landmarks: list[float]    # length 136 — 68 (x, y) pairs
    bounding_box: FaceBoundingBox
    expression: FaceExpressionClassification = FaceExpressionClassification.UNKNOWN
    confidence_score: float = 0.0
    captured_at: datetime = field(default_factory=_utc_now)

    def get_landmark(self, i: int) -> tuple[float, float]:
        """Return the (x, y) pair for landmark index i (0-based, 0..67)."""
        if not 0 <= i <= 67:
            raise IndexError(f"Landmark index must be in [0, 67], got {i}")
        return (self.landmarks[i * 2], self.landmarks[i * 2 + 1])
