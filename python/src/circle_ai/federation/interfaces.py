# interfaces.py
#
# Port of CircleAI.Federation IFederationParticipant.cs /
# IFederationAggregator.cs / IFederationDeltaDispatcher.cs (C# — the EXACT
# spec).
#
# Participant produces + applies deltas; aggregator owns the round lifecycle;
# the delta dispatcher composes verify+dedup+submit so a consumer cannot skip a
# step. C# Task<T> maps to async def -> T; Guid maps to uuid.UUID.

from __future__ import annotations

from abc import ABC, abstractmethod
from enum import IntEnum
from typing import Optional
from uuid import UUID

from .model_delta import FederationRound, ModelDelta


class DeltaDispatchOutcome(IntEnum):
    """Outcome of a :meth:`IFederationDeltaDispatcher.verify_and_submit_async`
    call. Mirrors ``CircleAI.Federation.DeltaDispatchOutcome`` ordinals."""

    Accepted = 0
    SignatureInvalid = 1
    Duplicate = 2
    RoundUnknown = 3
    RoundClosed = 4


class IFederationParticipant(ABC):
    """Contract for a device that contributes to federation rounds."""

    @abstractmethod
    async def produce_delta_async(self, round: FederationRound, ct: Optional[object] = None) -> ModelDelta:
        ...

    @abstractmethod
    async def apply_aggregated_model_async(
        self, model_id: str, new_version: str, aggregated_payload: bytes, ct: Optional[object] = None
    ) -> bool:
        ...


class IFederationAggregator(ABC):
    """Coordinator for federation rounds."""

    @abstractmethod
    async def open_round_async(
        self,
        model_id: str,
        from_version: str,
        to_version: str,
        min_participants: int,
        max_participants: int,
        ct: Optional[object] = None,
    ) -> FederationRound:
        ...

    @abstractmethod
    async def submit_delta_async(self, delta: ModelDelta, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def try_commit_async(self, round_id: UUID, ct: Optional[object] = None) -> Optional[bytes]:
        ...

    @abstractmethod
    async def get_round_async(self, round_id: UUID, ct: Optional[object] = None) -> FederationRound:
        ...


class IFederationDeltaDispatcher(ABC):
    """Safe-by-default federation delta dispatcher — verify, dedup, and submit
    in one call so consumers cannot skip a step."""

    @abstractmethod
    async def verify_and_submit_async(self, delta: ModelDelta, ct: Optional[object] = None) -> DeltaDispatchOutcome:
        ...
