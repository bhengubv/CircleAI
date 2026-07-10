"""test_hybrid_logical_clock.py

Verifies the Hybrid Logical Clock: bit layout compose/decompose, monotonic
tick, logical-counter reset on physical advance, logical overflow bumping
physical, and observe() staying monotonic w.r.t. inbound versions.

Mirrors CircleAI.Memory.Sync.HybridLogicalClock (C# — the spec).
"""
from __future__ import annotations

import pytest

from circle_ai.memory.sync import HybridLogicalClock


# ── compose / decompose ───────────────────────────────────────────────────────


def test_compose_decompose_round_trips() -> None:
    v = HybridLogicalClock.compose(1_719_000_000_000, 5, 7)
    physical, logical, node = HybridLogicalClock.decompose(v)
    assert physical == 1_719_000_000_000
    assert logical == 5
    assert node == 7


def test_compose_masks_logical_to_10_bits_and_node_to_6_bits() -> None:
    # logical > 0x3FF and node > 0x3F should be masked, not overflow into
    # neighbouring fields.
    v = HybridLogicalClock.compose(0, 0x3FF + 1, 0x3F + 1)
    physical, logical, node = HybridLogicalClock.decompose(v)
    assert physical == 0
    assert logical == 0  # (0x400 & 0x3FF) == 0
    assert node == 0  # (0x40 & 0x3F) == 0


def test_layout_places_fields_at_correct_bit_offsets() -> None:
    assert HybridLogicalClock.compose(1, 0, 0) == (1 << 16)
    assert HybridLogicalClock.compose(0, 1, 0) == (1 << 6)
    assert HybridLogicalClock.compose(0, 0, 1) == 1


# ── construction validation ───────────────────────────────────────────────────


@pytest.mark.parametrize("bad", [-1, 64, 100])
def test_rejects_out_of_range_node_short_id(bad: int) -> None:
    with pytest.raises(ValueError):
        HybridLogicalClock(bad)


@pytest.mark.parametrize("ok", [0, 1, 63])
def test_accepts_in_range_node_short_id(ok: int) -> None:
    HybridLogicalClock(ok, physical_now_ms=lambda: 1000)


# ── tick ──────────────────────────────────────────────────────────────────────


def test_tick_is_strictly_monotonic_within_a_frozen_millisecond() -> None:
    clock = HybridLogicalClock(3, physical_now_ms=lambda: 1000)
    versions = [clock.tick() for _ in range(50)]
    assert versions == sorted(versions)
    assert len(set(versions)) == 50


def test_tick_increments_logical_when_physical_does_not_advance() -> None:
    # Ctor reads physical=1000 and seeds _last_physical=1000. Because the first
    # tick sees now == _last_physical (not strictly greater) it takes the
    # increment branch → logical 1, then 2, 3 ... (matches the C# reference).
    clock = HybridLogicalClock(0, physical_now_ms=lambda: 1000)
    v1 = clock.tick()
    v2 = clock.tick()
    v3 = clock.tick()
    assert HybridLogicalClock.decompose(v1)[1] == 1
    assert HybridLogicalClock.decompose(v2)[1] == 2
    assert HybridLogicalClock.decompose(v3)[1] == 3


def test_tick_resets_logical_when_physical_advances() -> None:
    now = {"t": 1000}
    clock = HybridLogicalClock(0, physical_now_ms=lambda: now["t"])
    clock.tick()  # logical 0
    clock.tick()  # logical 1
    now["t"] = 2000
    v = clock.tick()
    physical, logical, _ = HybridLogicalClock.decompose(v)
    assert physical == 2000
    assert logical == 0


def test_tick_overflowing_logical_counter_bumps_physical() -> None:
    clock = HybridLogicalClock(0, physical_now_ms=lambda: 1000)
    # Ticks 1..1023 walk logical 1..1023 (physical frozen at 1000). Tick 1024
    # would make logical 1024 (>= 1024), which overflows: physical bumps to 1001
    # and logical resets to 0.
    last = 0
    for _ in range(1024):
        last = clock.tick()
    physical, logical, _ = HybridLogicalClock.decompose(last)
    assert physical == 1001  # bumped past the frozen 1000
    assert logical == 0


# ── observe ───────────────────────────────────────────────────────────────────


def test_observe_advances_to_incoming_physical_when_it_is_ahead() -> None:
    clock = HybridLogicalClock(2, physical_now_ms=lambda: 1000)
    incoming = HybridLogicalClock.compose(5000, 9, 4)
    result = clock.observe(incoming)
    physical, logical, node = HybridLogicalClock.decompose(result)
    assert physical == 5000
    assert logical == 10  # incoming logical + 1
    assert node == 2  # stamped with OUR node id


def test_observe_then_tick_stays_greater_than_incoming() -> None:
    clock = HybridLogicalClock(1, physical_now_ms=lambda: 1000)
    incoming = HybridLogicalClock.compose(9000, 3, 0)
    clock.observe(incoming)
    nxt = clock.tick()
    assert nxt > incoming


def test_observe_with_stale_incoming_keeps_local_progress() -> None:
    now = {"t": 8000}
    clock = HybridLogicalClock(0, physical_now_ms=lambda: now["t"])
    clock.tick()  # local physical 8000, logical 0
    stale = HybridLogicalClock.compose(1000, 0, 0)
    result = clock.observe(stale)
    physical, _, _ = HybridLogicalClock.decompose(result)
    assert physical == 8000  # not dragged back to the stale value
