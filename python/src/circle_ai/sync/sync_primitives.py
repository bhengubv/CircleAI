# sync_primitives.py
#
# (3.3.0) Shared sync-state helpers — version-vector merge, dominance test,
# last-writer-wins reconciliation.
#
# Ported faithfully from CircleAI.Sync.SyncPrimitives (C# — the spec):
# VersionVector + SyncReconciliation.

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from typing import Mapping, Tuple, TypeVar

T = TypeVar("T")


@dataclass(frozen=True, slots=True)
class VersionVector:
    """A per-key logical clock — key -> monotonically increasing counter."""

    clocks: Mapping[str, int]


class SyncReconciliation:
    """Static helpers for reconciling :class:`VersionVector` state."""

    @staticmethod
    def merge(a: VersionVector, b: VersionVector) -> VersionVector:
        """Element-wise max of two version vectors (their least upper bound)."""
        if a is None:
            raise ValueError("a required")
        if b is None:
            raise ValueError("b required")
        keys = set(a.clocks.keys()) | set(b.clocks.keys())
        merged = {k: max(a.clocks.get(k, 0), b.clocks.get(k, 0)) for k in keys}
        return VersionVector(merged)

    @staticmethod
    def a_dominates_b(a: VersionVector, b: VersionVector) -> bool:
        """True when ``a`` is >= ``b`` on every key AND strictly greater on at
        least one (i.e. ``a`` causally dominates ``b``).
        """
        if a is None:
            raise ValueError("a required")
        if b is None:
            raise ValueError("b required")
        keys = set(a.clocks.keys()) | set(b.clocks.keys())
        any_strictly_greater = False
        for k in keys:
            av = a.clocks.get(k, 0)
            bv = b.clocks.get(k, 0)
            if av < bv:
                return False
            if av > bv:
                any_strictly_greater = True
        return any_strictly_greater

    @staticmethod
    def last_writer_wins(
        a: Tuple[datetime, T], b: Tuple[datetime, T]
    ) -> Tuple[datetime, T]:
        """Pick the later-timestamped (at, val) pair; ties favour ``a``."""
        return a if a[0] >= b[0] else b
