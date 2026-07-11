# biosignal_affect_mapper.py
#
# Port of CircleAI.Wearable.Biosignals BiosignalAffectMapper.cs (C# — the EXACT spec).
#
# Deterministic projection of biosignal samples onto AffectState mutations.
# Pure function on (sample, state): mutates the state in place, no persistence,
# no side effects beyond the mutation. Same rule sheet ports across languages.
#
# Rule sheet (all mutations clamped to [0, 1]):
#   * HeartRate > 130 bpm (conf >= 0.5): Energy += 0.10, Uncertainty += 0.05.
#   * HeartRate > 100 bpm (conf >= 0.5): Energy += 0.05.
#   * HeartRate < 50  bpm (conf >= 0.5): Energy -= 0.05.
#   * HRV < 20 ms (conf >= 0.5): Uncertainty += 0.05, Rapport -= 0.02.
#   * HRV > 60 ms (conf >= 0.5): Engagement += 0.02.
#   * SpO2 < 90 % (conf >= 0.5): Uncertainty += 0.10.
#   * SleepStage: no mutation. Confidence < 0.5 on any signal: no mutation.
#
# C# uses ``float`` throughout; the constants (0.10f etc.) and the affect fields
# are 32-bit. To match the C# results exactly we do each add/clamp in float32.

from __future__ import annotations

import struct
from datetime import datetime, timezone

from ..memory.affect_state import AffectState
from .biosignal_kind import BiosignalKind
from .biosignal_sample import BiosignalSample

_MIN_CONFIDENCE = struct.unpack("<f", struct.pack("<f", 0.5))[0]


def _f32(x: float) -> float:
    """Round to IEEE-754 single precision (a C# ``float``)."""
    return struct.unpack("<f", struct.pack("<f", x))[0]


def _clamp01(v: float) -> float:
    """C# ``Math.Clamp(v, 0f, 1f)`` in float32."""
    return _f32(min(1.0, max(0.0, v)))


def _add(a: float, delta: float) -> float:
    """float32 ``a + delta`` then clamp to [0, 1] (C# ``Clamp01(a + d)``)."""
    return _clamp01(_f32(_f32(a) + _f32(delta)))


def apply(sample: BiosignalSample, affect: AffectState) -> None:
    """Apply the rule for ``sample`` to ``affect``, mutating it in place. Safe
    to call repeatedly — all field values stay clamped to [0, 1]. Mirrors
    ``BiosignalAffectMapper.Apply``.
    """
    if sample is None:
        raise ValueError("sample must not be None")
    if affect is None:
        raise ValueError("affect must not be None")

    # Confidence gate — low-confidence samples never mutate state.
    if sample.confidence < _MIN_CONFIDENCE:
        return

    if sample.kind == BiosignalKind.HEART_RATE:
        _apply_heart_rate(sample.value, affect)
    elif sample.kind == BiosignalKind.HEART_RATE_VARIABILITY:
        _apply_hrv(sample.value, affect)
    elif sample.kind == BiosignalKind.OXYGEN_SATURATION:
        _apply_spo2(sample.value, affect)
    elif sample.kind == BiosignalKind.SLEEP_STAGE:
        # Deep/REM/awake/light — sleep itself is not affect; do nothing.
        pass
    else:
        # Accelerometer, BodyTemperature, Steps, GSR, Unknown — no rule yet.
        pass

    affect.last_updated_utc = datetime.now(timezone.utc)


def _apply_heart_rate(bpm: float, a: AffectState) -> None:
    if bpm > 130.0:
        a.energy = _add(a.energy, 0.10)
        a.uncertainty = _add(a.uncertainty, 0.05)
    elif bpm > 100.0:
        a.energy = _add(a.energy, 0.05)
    elif bpm < 50.0:
        a.energy = _add(a.energy, -0.05)


def _apply_hrv(rmssd_ms: float, a: AffectState) -> None:
    if rmssd_ms < 20.0:
        a.uncertainty = _add(a.uncertainty, 0.05)
        a.rapport = _add(a.rapport, -0.02)
    elif rmssd_ms > 60.0:
        a.engagement = _add(a.engagement, 0.02)


def _apply_spo2(percent: float, a: AffectState) -> None:
    if percent < 90.0:
        a.uncertainty = _add(a.uncertainty, 0.10)
