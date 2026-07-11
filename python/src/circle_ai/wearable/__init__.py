"""circle_ai.wearable — port of the CircleAI.Wearable assembly.

(3.3.0) Real domain types + in-memory board for the Wearable vertical: device
descriptors and telemetry samples (latest / windowed average), plus the
:class:`WearableContext` biometric snapshot and the WearableKind /
WearableTelemetryKind enums. C# is the exact spec.

The C# assembly has no ``WearableDomainContext``; its ``WearableCompanionAdapter``
(an ICompanionSession decorator) is intentionally not ported.
"""
from __future__ import annotations

from .wearable_context import WearableContext
from .wearable_primitives import (
    IWearableBoard,
    InMemoryWearableBoard,
    WearableDevice,
    WearableKind,
    WearableSample,
    WearableTelemetryKind,
)

__all__ = [
    "WearableKind",
    "WearableTelemetryKind",
    "WearableDevice",
    "WearableSample",
    "IWearableBoard",
    "InMemoryWearableBoard",
    "WearableContext",
]
