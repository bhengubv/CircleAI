"""circle_ai.iot — port of the CircleAI.IoT assembly.

(3.3.0) Real domain types + in-memory board for the IoT vertical: devices,
telemetry samples, commands. C# is the exact spec.

Public surface:

  * IoTDevice / IoTTelemetry / IoTCommand — domain records.
  * IIoTBoard        — device / telemetry / command board.
  * InMemoryIoTBoard — thread-safe in-memory board.

Note: unlike most domain packs, CircleAI.IoT ships no DomainContext. Its
``IoTCompanionPipeline`` wires a ``CircleAI.Companion.ICompanionSession`` to the
voice pipeline (wake-word / transcriber / TTS), none of which are part of the
ported Python companion/voice surface, so it is intentionally not ported here.
"""
from __future__ import annotations

from .iot_primitives import (
    IIoTBoard,
    InMemoryIoTBoard,
    IoTCommand,
    IoTDevice,
    IoTTelemetry,
)

__all__ = [
    "IoTDevice",
    "IoTTelemetry",
    "IoTCommand",
    "IIoTBoard",
    "InMemoryIoTBoard",
]
