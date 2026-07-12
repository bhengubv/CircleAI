# default_federation_delta_dispatcher.py
#
# Port of CircleAI.Federation DefaultFederationDeltaDispatcher.cs (C# — the EXACT
# spec).
#
# The safe-by-default composer: wraps an IFederationAggregator plus a signature
# validator so a production consumer cannot skip verify -> dedup -> submit. No
# exception is raised on rejection — the caller branches on the returned
# DeltaDispatchOutcome.
#
# C# ConcurrentDictionary<Guid, byte>.TryAdd maps to a lock-guarded set (atomic
# claim of the delta id; a replay loses the race). The aggregator's exceptions
# are translated into outcomes: KeyNotFoundException -> KeyError -> RoundUnknown;
# InvalidOperationException -> RuntimeError -> RoundClosed. On either, the id is
# released from the seen-set so a legitimate retry is not misread as a replay.

from __future__ import annotations

import threading
from typing import Callable, Optional, Set
from uuid import UUID

from .interfaces import DeltaDispatchOutcome, IFederationAggregator, IFederationDeltaDispatcher
from .model_delta import ModelDelta


class DefaultFederationDeltaDispatcher(IFederationDeltaDispatcher):
    """Reference :class:`IFederationDeltaDispatcher`. Composes signature
    verification, replay de-duplication, and submission over an
    :class:`IFederationAggregator` in a single call so no step can be skipped."""

    def __init__(
        self,
        aggregator: IFederationAggregator,
        signature_validator: Callable[[ModelDelta], bool],
    ) -> None:
        if aggregator is None:
            raise ValueError("aggregator must not be None")
        if signature_validator is None:
            raise ValueError("signatureValidator must not be None")
        self._aggregator = aggregator
        self._signature_validator = signature_validator
        self._seen: Set[UUID] = set()
        self._seen_lock = threading.Lock()

    async def verify_and_submit_async(
        self, delta: ModelDelta, ct: Optional[object] = None
    ) -> DeltaDispatchOutcome:
        if delta is None:
            raise ValueError("delta must not be None")

        # 1. Verify the signature first — a forged or unsigned delta never
        #    touches the round.
        if not self._signature_validator(delta):
            return DeltaDispatchOutcome.SignatureInvalid

        # 2. De-duplicate: atomically claim the delta id; a replay loses the race.
        with self._seen_lock:
            if delta.id in self._seen:
                return DeltaDispatchOutcome.Duplicate
            self._seen.add(delta.id)

        # 3. Submit, translating the aggregator's exceptions into outcomes so the
        #    caller can branch on the result without a try/except of its own.
        try:
            await self._aggregator.submit_delta_async(delta, ct)
            return DeltaDispatchOutcome.Accepted
        except KeyError:
            with self._seen_lock:
                self._seen.discard(delta.id)
            return DeltaDispatchOutcome.RoundUnknown
        except RuntimeError:
            with self._seen_lock:
                self._seen.discard(delta.id)
            return DeltaDispatchOutcome.RoundClosed
