"""circle_ai.wearable_biosignals — port of the CircleAI.Wearable.Biosignals assembly.

The wearable biosignal layer: a canonical signal taxonomy, samples, streaming
sources (null + recorded), a sliding-window aggregator, and a deterministic
biosignal->affect mapper. C# is the exact spec.

Public surface:

  * BiosignalKind                     — canonical signal taxonomy (stable ints).
  * BiosignalSample                   — a single measurement (+ ``create`` factory).
  * IBiosignalSource                  — streaming source contract.
  * NullBiosignalSource               — no-op "no wearable" source.
  * RecordedBiosignalSource           — replays a fixed sample list.
  * BiosignalStats / BiosignalSnapshot — aggregate stats.
  * BiosignalAggregator               — sliding-window snapshot.
  * biosignal_affect_mapper (module)  — ``apply(sample, affect)``.

The affect mapper is exposed both as the submodule ``biosignal_affect_mapper``
(call ``biosignal_affect_mapper.apply(...)``) and via the convenience alias
``apply_biosignal_to_affect``.
"""
from __future__ import annotations

from . import biosignal_affect_mapper
from .biosignal_affect_mapper import apply as apply_biosignal_to_affect
from .biosignal_aggregator import (
    BiosignalAggregator,
    BiosignalSnapshot,
    BiosignalStats,
)
from .biosignal_kind import BiosignalKind
from .biosignal_sample import BiosignalSample
from .biosignal_source import (
    IBiosignalSource,
    NullBiosignalSource,
    RecordedBiosignalSource,
)

__all__ = [
    "BiosignalKind",
    "BiosignalSample",
    "IBiosignalSource",
    "NullBiosignalSource",
    "RecordedBiosignalSource",
    "BiosignalStats",
    "BiosignalSnapshot",
    "BiosignalAggregator",
    "biosignal_affect_mapper",
    "apply_biosignal_to_affect",
]
