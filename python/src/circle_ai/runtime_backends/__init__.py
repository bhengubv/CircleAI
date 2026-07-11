"""circle_ai.runtime_backends — port of the CircleAI.Runtime.Backends assembly.

Backend-selection layer: the BackendKind + CapabilityTier enums, the
BackendSelection record, the IBackendSelector contract, and the default
deterministic table-style BackendSelector that maps a HostProfile + requested
tier to a runnable (backend, tier) combination — never refusing, downgrading the
tier when compute is short. C# is the exact spec.

Public surface:

  * BackendKind / CapabilityTier                          — enums.
  * BackendSelection                                      — record.
  * IBackendSelector / BackendSelector.
"""
from __future__ import annotations

from .backend_selector import BackendSelection, BackendSelector, IBackendSelector
from .enums import BackendKind, CapabilityTier

__all__ = [
    "BackendKind",
    "CapabilityTier",
    "BackendSelection",
    "IBackendSelector",
    "BackendSelector",
]
