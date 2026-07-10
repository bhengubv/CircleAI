# node_trust_registry.py
#
# Port of CircleAI.Security.NodeTrustRegistry + NodeTrustEntry
# (C# — the EXACT spec).
#
# Thread-safe, per-peer trust store.
#
# - Each peer gets a score in [0, 1]. 1.0 = fully trusted; 0.0 = fully lost.
# - apply_degradation drops the score and records the triggering event.
# - apply_recovery heals all peers passively (called by a background timer).
# - trust_score_updates is an unbounded channel; readers receive every change.
#
# Transport-agnostic: stores PeerSecurityEvent, emits PeerTrustScoreUpdate.
# No dependency on any transport package.

from __future__ import annotations

import asyncio
import threading
from datetime import datetime, timedelta, timezone
from typing import Dict, List, Optional, Tuple

from .peer_security_types import PeerSecurityEvent, PeerTrustScoreUpdate
from .security_options import SecurityOptions


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class NodeTrustEntry:
    """Per-peer mutable trust state. Exposed for diagnostics and tests."""

    def __init__(self, node_id: str, trust_score: float) -> None:
        self.node_id: str = node_id
        self.trust_score: float = trust_score
        self.last_updated: datetime = _utc_now()
        # Bounded history of security events (oldest-first).
        self.recent_events: List[PeerSecurityEvent] = []
        # Per-entry lock (mirrors C# `lock (entry)`).
        self._lock = threading.RLock()


class _UnboundedChannel:
    """Minimal unbounded single-queue channel mirroring the C# ``Channel``
    ``.CreateUnbounded`` used by ``NodeTrustRegistry``.

    - ``try_write`` NEVER blocks and buffers even when no reader is attached
      (writes sent before a subscriber reads are retained — matching the
      unbounded C# channel).
    - ``read_all_async`` is an async generator; multiple concurrent readers are
      competing consumers (each update delivered to exactly one reader), exactly
      like ``ChannelReader.ReadAllAsync`` on a multi-reader channel.
    """

    def __init__(self) -> None:
        self._queue: "asyncio.Queue[PeerTrustScoreUpdate]" = asyncio.Queue()

    def try_write(self, update: PeerTrustScoreUpdate) -> bool:
        # put_nowait on an unbounded queue only appends to the internal buffer;
        # it does not require a running loop and never raises QueueFull.
        self._queue.put_nowait(update)
        return True

    async def read_all_async(self, ct: Optional[object] = None):
        while True:
            update = await self._queue.get()
            yield update


class NodeTrustRegistry:
    """Maintains per-peer trust scores, event history, and a live channel of
    trust score changes consumed by :class:`PeerIntelligenceService`.
    """

    def __init__(self, options: SecurityOptions) -> None:
        self._options = options
        self._nodes: Dict[str, NodeTrustEntry] = {}
        self._nodes_lock = threading.Lock()
        self._channel = _UnboundedChannel()

    @property
    def trust_score_updates(self) -> _UnboundedChannel:
        """Stream of trust score changes; never completes during normal
        operation. Callers should pass a cancellation token to break out.
        """
        return self._channel

    # ── Peer access ──────────────────────────────────────────────────────────

    def get_or_create(self, node_id: str) -> NodeTrustEntry:
        """Return the existing entry for ``node_id``, or create a new one
        initialised to :attr:`SecurityOptions.initial_trust_score`.
        """
        with self._nodes_lock:
            entry = self._nodes.get(node_id)
            if entry is None:
                entry = NodeTrustEntry(node_id, self._options.initial_trust_score)
                self._nodes[node_id] = entry
            return entry

    @property
    def all_node_ids(self) -> List[str]:
        """All peer IDs currently tracked."""
        with self._nodes_lock:
            return list(self._nodes.keys())

    def get_trust_score(self, node_id: str) -> float:
        """Return the current trust score for ``node_id``, or
        :attr:`SecurityOptions.initial_trust_score` for unknown peers.
        """
        with self._nodes_lock:
            entry = self._nodes.get(node_id)
        if entry is not None:
            with entry._lock:
                return entry.trust_score
        return self._options.initial_trust_score

    # ── Mutations ────────────────────────────────────────────────────────────

    def apply_degradation(
        self, security_event: PeerSecurityEvent, degradation_amount: float
    ) -> Tuple[float, float]:
        """Apply trust degradation for a security event.

        Score is clamped to ``[0, 1]``; the event is appended to the per-peer
        history; a :class:`PeerTrustScoreUpdate` is published on the channel.
        Returns ``(previous_score, new_score)``.
        """
        entry = self.get_or_create(security_event.node_id)

        with entry._lock:
            previous = entry.trust_score
            entry.trust_score = max(0.0, min(1.0, previous - degradation_amount))
            entry.last_updated = security_event.occurred_at

            # Maintain bounded event list (oldest dropped first).
            entry.recent_events.append(security_event)
            while len(entry.recent_events) > self._options.max_events_per_node:
                entry.recent_events.pop(0)

            current = entry.trust_score

            if abs(current - previous) > 0.0001:
                self._publish(
                    entry.node_id,
                    previous,
                    current,
                    security_event.description,
                    security_event.occurred_at,
                )

            return (previous, current)

    def apply_recovery(self, elapsed: timedelta) -> None:
        """Passively heal all tracked peers by
        ``recovery_rate_per_second * elapsed``. Peers already at 1.0 are
        skipped. Called by the background recovery timer.
        """
        amount = self._options.recovery_rate_per_second * elapsed.total_seconds()
        if amount <= 0:
            return

        with self._nodes_lock:
            entries = list(self._nodes.values())

        for entry in entries:
            with entry._lock:
                if entry.trust_score >= 1.0:
                    continue

                previous = entry.trust_score
                entry.trust_score = min(1.0, previous + amount)
                entry.last_updated = _utc_now()

                self._publish(
                    entry.node_id,
                    previous,
                    entry.trust_score,
                    "passive-recovery",
                    _utc_now(),
                )

    # ── History queries ──────────────────────────────────────────────────────

    def get_recent_events(self, node_id: str) -> List[PeerSecurityEvent]:
        """Return events for ``node_id`` that fall within
        :attr:`SecurityOptions.event_window` of now. Returns an empty list for
        unknown peers.
        """
        with self._nodes_lock:
            entry = self._nodes.get(node_id)
        if entry is None:
            return []

        cutoff = _utc_now() - self._options.event_window
        with entry._lock:
            return [e for e in entry.recent_events if e.occurred_at >= cutoff]

    # ── Private ──────────────────────────────────────────────────────────────

    def _publish(
        self,
        node_id: str,
        previous: float,
        current: float,
        reason: str,
        at: datetime,
    ) -> None:
        self._channel.try_write(
            PeerTrustScoreUpdate(node_id, previous, current, reason, at)
        )
