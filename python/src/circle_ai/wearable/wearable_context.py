# wearable_context.py
#
# Port of CircleAI.Wearable WearableContext.cs (C# — the EXACT spec).
#
# Biometric snapshot injected into the Companion context on wearable surfaces.
# Every value is optional — only populated when the sensor is available and
# consented. C# ``double?`` -> Optional[float], ``int?`` -> Optional[int],
# DateTimeOffset -> datetime.

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from typing import Optional


@dataclass(frozen=True, slots=True)
class WearableContext:
    """Mirrors ``CircleAI.Wearable.WearableContext`` — ``record(double?
    HeartRateBpm, int? StepCountToday, double? SpO2Percent, double?
    SkinTempCelsius, bool IsWorkoutActive, DateTimeOffset CapturedAt)``.
    """

    heart_rate_bpm: Optional[float]
    step_count_today: Optional[int]
    sp_o2_percent: Optional[float]
    skin_temp_celsius: Optional[float]
    is_workout_active: bool
    captured_at: datetime
