# hybrid_logical_clock.py
#
# Hybrid Logical Clock (HLC) — monotonic version stamps that survive small
# clock skew between peers WITHOUT needing NTP. Composes a physical
# millisecond timestamp with a logical counter and the node's short ID so
# every emitted version is globally unique and monotonically increasing.
#
# Why HLC and not simple Lamport / vector clocks?
#   • Lamport is unique but loses wall-clock correlation, making debugging
#     and natural ordering awkward.
#   • Vector clocks scale with node count — wrong for a mesh that may have
#     a dozen+ devices per user.
#   • HLC is wall-clock-ish (within skew bounds), unique, and 64-bit small.
#
# Layout of the version:
#   high 48 bits — physical time in milliseconds (Unix epoch)
#   mid  10 bits — logical counter (resets when physical advances)
#   low   6 bits — node short ID (0..63)
# Total: 64 bits.
#
# Ported faithfully from CircleAI.Memory.Sync.HybridLogicalClock (C# — the spec).

from __future__ import annotations

import threading
from datetime import datetime, timezone
from typing import Callable, Optional, Tuple


def _default_now_ms() -> int:
    """System physical time, milliseconds since the Unix epoch (UTC)."""
    return int(datetime.now(timezone.utc).timestamp() * 1000)


class HybridLogicalClock:
    """Hybrid Logical Clock — produces monotonic, globally-unique version
    stamps for syncable entries. Thread-safe.
    """

    def __init__(
        self,
        node_short_id: int,
        physical_now_ms: Optional[Callable[[], int]] = None,
    ) -> None:
        """
        :param node_short_id: 0..63 — packs into the low 6 bits of every
            version. Each device a user has should pick a stable distinct value
            (any deterministic hash works).
        :param physical_now_ms: Source of physical time in milliseconds.
            Defaults to system time; override in tests for determinism.
        """
        if node_short_id < 0 or node_short_id > 63:
            raise ValueError("node_short_id must be in 0..63")
        self._node_short_id = node_short_id
        self._physical_now_ms = physical_now_ms or _default_now_ms
        self._last_physical = self._physical_now_ms()
        self._logical = 0
        self._lock = threading.Lock()

    def tick(self) -> int:
        """Produce the next outgoing version (for a write we originated)."""
        with self._lock:
            now = self._physical_now_ms()
            if now > self._last_physical:
                self._last_physical = now
                self._logical = 0
            else:
                self._logical += 1
                if self._logical >= 1024:
                    # Logical counter overflowed within the same ms — bump physical.
                    self._last_physical += 1
                    self._logical = 0
            return self.compose(self._last_physical, self._logical, self._node_short_id)

    def observe(self, incoming: int) -> int:
        """Update the clock from a received version.

        Must be called on every inbound apply so subsequent local ticks remain
        monotonic w.r.t. peers.
        """
        with self._lock:
            incoming_physical, _, _ = self.decompose(incoming)
            now = self._physical_now_ms()
            max_physical = max(max(self._last_physical, incoming_physical), now)

            if max_physical == self._last_physical and max_physical == incoming_physical:
                self._logical += 1
            elif max_physical == self._last_physical:
                self._logical += 1
            elif max_physical == incoming_physical:
                self._logical = self.decompose(incoming)[1] + 1
            else:
                self._logical = 0

            self._last_physical = max_physical
            return self.compose(self._last_physical, self._logical, self._node_short_id)

    @staticmethod
    def compose(physical_ms: int, logical: int, node_short_id: int) -> int:
        """Compose the three components into a 64-bit version."""
        return (physical_ms << 16) | ((logical & 0x3FF) << 6) | (node_short_id & 0x3F)

    @staticmethod
    def decompose(version: int) -> Tuple[int, int, int]:
        """Decompose a version into (physical_ms, logical, node_short_id)."""
        return (version >> 16, (version >> 6) & 0x3FF, version & 0x3F)
