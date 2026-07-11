"""circle_ai.federation — port of the CircleAI.Federation assembly.

Federated-learning primitives: the round + delta records, the participant /
aggregator / delta-dispatcher contracts, sample-size-weighted float averaging
(little-endian IEEE 754, struct "<f"), and an in-process reference aggregator
with an injected signature validator. C# is the exact spec.

Public surface:

  * RoundStatus / DeltaDispatchOutcome                    — enums.
  * ModelDelta / FederationRound                          — domain records.
  * IFederationParticipant / IFederationAggregator / IFederationDeltaDispatcher.
  * FederatedAveraging                                    — averaging math.
  * InMemoryFederationAggregator                          — reference impl.
"""
from __future__ import annotations

from .federated_averaging import FederatedAveraging
from .in_memory_federation_aggregator import InMemoryFederationAggregator
from .interfaces import (
    DeltaDispatchOutcome,
    IFederationAggregator,
    IFederationDeltaDispatcher,
    IFederationParticipant,
)
from .model_delta import FederationRound, ModelDelta, RoundStatus

__all__ = [
    "RoundStatus",
    "DeltaDispatchOutcome",
    "ModelDelta",
    "FederationRound",
    "IFederationParticipant",
    "IFederationAggregator",
    "IFederationDeltaDispatcher",
    "FederatedAveraging",
    "InMemoryFederationAggregator",
]
