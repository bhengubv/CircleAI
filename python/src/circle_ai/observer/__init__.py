"""circle_ai.observer — port of the CircleAI.Observer assembly.

(2.6.0 contracts / 3.3.0 in-memory) Observation-loop surface: sensors, a tool
registry, and a perceive-reason-act loop that collects the latest sensor
readings, asks a host-supplied async reasoner for a decision, runs the chosen
tools, and fans out an ObservationTick — plus a sensor recorder, the in-memory
toolbox, and fail-safe null defaults. C# is the exact spec.
"""
from __future__ import annotations

from .contracts import (
    IDisposable,
    IObservationLoop,
    IObservationToolbox,
    ISensor,
    ObservationTick,
    ObservationTool,
    SensorReading,
)
from .in_memory_observer import (
    InMemoryObservationLoop,
    ObserverDecision,
    SensorRecorder,
)
from .null_implementations import (
    InMemoryObservationToolbox,
    NullObservationLoop,
    NullSensor,
)

__all__ = [
    "SensorReading",
    "ObservationTool",
    "ObservationTick",
    "ObserverDecision",
    "IDisposable",
    "ISensor",
    "IObservationToolbox",
    "IObservationLoop",
    "SensorRecorder",
    "InMemoryObservationLoop",
    "InMemoryObservationToolbox",
    "NullSensor",
    "NullObservationLoop",
]
