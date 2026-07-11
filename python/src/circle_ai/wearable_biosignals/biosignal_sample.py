# biosignal_sample.py
#
# Port of CircleAI.Wearable.Biosignals BiosignalSample.cs (C# — the EXACT spec).
#
# A single measurement from a wearable sensor. C# ``float Value/Confidence`` are
# 32-bit floats; the ``create`` factory clamps confidence to [0, 1] and stamps a
# fresh Guid + UTC timestamp. To preserve C# ``float`` precision we round stored
# float fields through IEEE-754 single precision (struct.pack("<f", x)) at every
# C# ``float`` site.

from __future__ import annotations

import struct
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Union

from .biosignal_kind import BiosignalKind


def _f32(x: float) -> float:
    """Round a Python float to IEEE-754 single precision (a C# ``float``)."""
    return struct.unpack("<f", struct.pack("<f", x))[0]


def _clamp01_f32(x: float) -> float:
    """C# ``Math.Clamp(x, 0f, 1f)`` on a 32-bit float."""
    return _f32(min(1.0, max(0.0, x)))


@dataclass(frozen=True, slots=True)
class BiosignalSample:
    """Mirrors ``CircleAI.Wearable.Biosignals.BiosignalSample`` —
    ``record(Guid Id, BiosignalKind Kind, float Value, string Unit, float
    Confidence, bool IsCumulative, DateTimeOffset MeasuredAt)``.
    """

    id: uuid.UUID
    kind: BiosignalKind
    value: float
    unit: str
    confidence: float
    is_cumulative: bool
    measured_at: datetime

    @staticmethod
    def create(
        kind: BiosignalKind,
        value: float,
        unit: str,
        confidence: float = 1.0,
        is_cumulative: bool = False,
    ) -> "BiosignalSample":
        """Create a fresh sample with a new UUID, current UTC timestamp, and
        confidence clamped to [0, 1]. Mirrors ``BiosignalSample.Create``.
        """
        return BiosignalSample(
            uuid.uuid4(),
            kind,
            _f32(value),
            unit,
            _clamp01_f32(confidence),
            is_cumulative,
            datetime.now(timezone.utc),
        )
