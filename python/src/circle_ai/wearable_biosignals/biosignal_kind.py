# biosignal_kind.py
#
# Port of CircleAI.Wearable.Biosignals BiosignalKind.cs (C# — the EXACT spec).
#
# Canonical taxonomy of biosignals ingested by Circle AI's wearable layer.
# Integer values are stable across language ports — do not renumber.

from __future__ import annotations

from enum import IntEnum


class BiosignalKind(IntEnum):
    """Canonical kinds of biosignal samples Circle AI consumes from wearables.

    Mirrors ``CircleAI.Wearable.Biosignals.BiosignalKind``.
    """

    #: Heart rate, beats per minute.
    HEART_RATE = 0
    #: Heart rate variability, RMSSD in milliseconds.
    HEART_RATE_VARIABILITY = 1
    #: Peripheral oxygen saturation, percent (0-100).
    OXYGEN_SATURATION = 2
    #: Accelerometer magnitude, m/s^2.
    ACCELEROMETER = 3
    #: Body temperature, degrees Celsius.
    BODY_TEMPERATURE = 4
    #: Sleep stage encoded as a float: 0=awake, 1=light, 2=deep, 3=REM.
    SLEEP_STAGE = 5
    #: Step count (cumulative or delta — see BiosignalSample.is_cumulative).
    STEPS = 6
    #: Galvanic skin response, microsiemens.
    GALVANIC_SKIN_RESPONSE = 7
    #: Catch-all for vendor-specific or future signals.
    UNKNOWN = 8
