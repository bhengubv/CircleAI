# in_memory_federation_aggregator.py
#
# Port of CircleAI.Federation InMemoryFederationAggregator.cs (C# — the EXACT
# spec).
#
# In-process reference IFederationAggregator. Stores all round + delta state in
# memory; performs sample-size-weighted averaging on commit. Signature
# verification is delegated to a caller-supplied validator (Func<ModelDelta,
# bool>) so this package stays engine-agnostic — pass `lambda _: True` in tests
# where signatures are not under test.
#
# The C# type derives from CircleAIComponentBase and wraps every operation in
# RunOperationAsync (telemetry/logging only). That wrapper carries NO domain
# behaviour, so the port inlines the operation bodies directly. C#
# KeyNotFoundException maps to KeyError; InvalidOperationException maps to
# RuntimeError; ArgumentException maps to ValueError.

from __future__ import annotations

import threading
from datetime import datetime, timezone
from typing import Callable, Dict, List, Optional
from uuid import UUID, uuid4

from .federated_averaging import FederatedAveraging
from .interfaces import IFederationAggregator
from .model_delta import FederationRound, ModelDelta, RoundStatus


class _RoundState:
    def __init__(self, initial: FederationRound) -> None:
        self.snapshot: FederationRound = initial
        self.deltas: List[ModelDelta] = []
        self.committed_payload: Optional[bytes] = None
        self.lock = threading.Lock()


def _replace(round: FederationRound, **changes: object) -> FederationRound:
    """`record with { ... }` — dataclasses.replace equivalent for FederationRound."""
    from dataclasses import replace

    return replace(round, **changes)


class InMemoryFederationAggregator(IFederationAggregator):
    """In-process reference aggregator. Not durable across process restarts."""

    def __init__(self, signature_validator: Callable[[ModelDelta], bool]) -> None:
        if signature_validator is None:
            raise ValueError("signatureValidator must not be None")
        self._rounds: Dict[UUID, _RoundState] = {}
        self._rounds_lock = threading.Lock()
        self._signature_validator = signature_validator

    @property
    def component_name(self) -> str:
        return "InMemoryFederationAggregator"

    async def open_round_async(
        self,
        model_id: str,
        from_version: str,
        to_version: str,
        min_participants: int,
        max_participants: int,
        ct: Optional[object] = None,
    ) -> FederationRound:
        if model_id is None or model_id == "":
            raise ValueError("modelId must not be null or empty")
        if from_version is None or from_version == "":
            raise ValueError("fromVersion must not be null or empty")
        if to_version is None or to_version == "":
            raise ValueError("toVersion must not be null or empty")
        if min_participants <= 0:
            raise ValueError("minParticipants must be positive.")
        if max_participants < min_participants:
            raise ValueError(
                f"maxParticipants ({max_participants}) must be >= minParticipants ({min_participants})."
            )

        round = FederationRound(
            id=uuid4(),
            model_id=model_id,
            from_version=from_version,
            to_version=to_version,
            min_participants=min_participants,
            max_participants=max_participants,
            current_participant_count=0,
            status=RoundStatus.Open,
            opened_at=datetime.now(timezone.utc),
            committed_at=None,
        )
        state = _RoundState(round)
        with self._rounds_lock:
            self._rounds[round.id] = state
        return state.snapshot

    async def submit_delta_async(self, delta: ModelDelta, ct: Optional[object] = None) -> None:
        if delta is None:
            raise ValueError("delta must not be None")

        with self._rounds_lock:
            state = self._rounds.get(delta.round_id)
        if state is None:
            raise KeyError(f"Round {delta.round_id} is not open.")

        if len(delta.delta_payload) == 0:
            # Empty payloads are invalid: do not store, do not count, do not
            # raise. The round stays viable.
            return

        with state.lock:
            if state.snapshot.status != RoundStatus.Open:
                raise RuntimeError(
                    f"Round {delta.round_id} is {state.snapshot.status.name}; not accepting deltas."
                )
            if len(state.deltas) >= state.snapshot.max_participants:
                raise RuntimeError(
                    f"Round {delta.round_id} has reached MaxParticipants ({state.snapshot.max_participants})."
                )
            state.deltas.append(delta)
            state.snapshot = _replace(state.snapshot, current_participant_count=len(state.deltas))

    async def try_commit_async(self, round_id: UUID, ct: Optional[object] = None) -> Optional[bytes]:
        with self._rounds_lock:
            state = self._rounds.get(round_id)
        if state is None:
            raise KeyError(f"Round {round_id} is unknown.")

        with state.lock:
            if state.snapshot.status == RoundStatus.Committed:
                # Idempotent: re-return the previously committed payload.
                return state.committed_payload
            if state.snapshot.status == RoundStatus.Aborted:
                return None

            valid_deltas = [d for d in state.deltas if self._signature_validator(d)]
            if len(valid_deltas) < state.snapshot.min_participants:
                return None

            state.snapshot = _replace(state.snapshot, status=RoundStatus.Aggregating)

            try:
                aggregated = FederatedAveraging.average(valid_deltas)
            except ValueError:
                # Payload encoding inconsistent — fall back to the median delta
                # by SampleCount, as documented in the contract.
                aggregated = self._fallback_median_payload(valid_deltas)

            state.committed_payload = aggregated
            state.snapshot = _replace(
                state.snapshot,
                status=RoundStatus.Committed,
                committed_at=datetime.now(timezone.utc),
            )
            return aggregated

    async def get_round_async(self, round_id: UUID, ct: Optional[object] = None) -> FederationRound:
        with self._rounds_lock:
            state = self._rounds.get(round_id)
        if state is None:
            raise KeyError(f"Round {round_id} is unknown.")
        with state.lock:
            return state.snapshot

    @property
    def round_count(self) -> int:
        with self._rounds_lock:
            return len(self._rounds)

    @staticmethod
    def _fallback_median_payload(deltas: List[ModelDelta]) -> bytes:
        ordered = sorted(deltas, key=lambda d: d.sample_count)
        median = ordered[len(ordered) // 2]
        return bytes(median.delta_payload)
