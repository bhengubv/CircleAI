"""test_node_trust_registry.py — per-peer trust store + score-change channel.

Covers get_or_create, degradation clamping, bounded event history, passive
recovery, windowed event queries, and the unbounded trust-score-update channel.
"""
from __future__ import annotations

import asyncio
from datetime import datetime, timedelta, timezone

import pytest

from circle_ai.security import (
    NodeTrustRegistry,
    PeerSecurityEvent,
    PeerSecurityEventKind,
    PeerThreatLevel,
    SecurityOptions,
)


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _event(node="n1", when=None, kind=PeerSecurityEventKind.INTRUSION_SIGNAL):
    return PeerSecurityEvent(
        node_id=node,
        kind=kind,
        threat_level=PeerThreatLevel.HIGH,
        description="evt",
        transport_id="wifi",
        occurred_at=when or _now(),
    )


def test_get_or_create_initialises_to_initial_trust():
    opts = SecurityOptions()
    reg = NodeTrustRegistry(opts)
    entry = reg.get_or_create("n1")
    assert entry.node_id == "n1"
    assert entry.trust_score == opts.initial_trust_score == 1.0
    # Idempotent — same instance returned.
    assert reg.get_or_create("n1") is entry


def test_unknown_node_returns_initial_trust():
    reg = NodeTrustRegistry(SecurityOptions())
    assert reg.get_trust_score("never-seen") == 1.0


def test_apply_degradation_clamps_and_returns_prev_current():
    reg = NodeTrustRegistry(SecurityOptions())
    prev, cur = reg.apply_degradation(_event(), 0.3)
    assert prev == 1.0
    assert abs(cur - 0.7) < 1e-9
    # Over-degrade clamps at 0.
    prev2, cur2 = reg.apply_degradation(_event(), 5.0)
    assert cur2 == 0.0


def test_all_node_ids_tracks_every_peer():
    reg = NodeTrustRegistry(SecurityOptions())
    reg.apply_degradation(_event("a"), 0.1)
    reg.apply_degradation(_event("b"), 0.1)
    assert set(reg.all_node_ids) == {"a", "b"}


def test_event_history_is_bounded():
    opts = SecurityOptions()
    opts.max_events_per_node = 3
    reg = NodeTrustRegistry(opts)
    for _ in range(10):
        reg.apply_degradation(_event("a"), 0.01)
    entry = reg.get_or_create("a")
    assert len(entry.recent_events) == 3


def test_apply_recovery_heals_but_caps_at_one():
    opts = SecurityOptions()
    opts.recovery_rate_per_second = 0.001
    reg = NodeTrustRegistry(opts)
    reg.apply_degradation(_event("a"), 0.5)  # -> 0.5
    reg.apply_recovery(timedelta(seconds=100))  # +0.1 -> 0.6
    assert abs(reg.get_trust_score("a") - 0.6) < 1e-9
    reg.apply_recovery(timedelta(seconds=100000))  # huge -> capped at 1.0
    assert reg.get_trust_score("a") == 1.0


def test_apply_recovery_skips_fully_trusted_and_zero_amount():
    reg = NodeTrustRegistry(SecurityOptions())
    reg.get_or_create("a")  # score 1.0
    reg.apply_recovery(timedelta(seconds=0))  # amount 0 -> no-op
    assert reg.get_trust_score("a") == 1.0


def test_get_recent_events_respects_window():
    opts = SecurityOptions()
    opts.event_window = timedelta(minutes=5)
    reg = NodeTrustRegistry(opts)
    reg.apply_degradation(_event("a", when=_now() - timedelta(minutes=10)), 0.01)
    reg.apply_degradation(_event("a", when=_now()), 0.01)
    recent = reg.get_recent_events("a")
    assert len(recent) == 1  # old event filtered out


def test_get_recent_events_unknown_node_empty():
    reg = NodeTrustRegistry(SecurityOptions())
    assert reg.get_recent_events("nope") == []


async def test_trust_score_updates_channel_publishes():
    reg = NodeTrustRegistry(SecurityOptions())
    seen = []

    async def consume(n):
        async for u in reg.trust_score_updates.read_all_async():
            seen.append(u)
            if len(seen) >= n:
                break

    task = asyncio.create_task(consume(1))
    await asyncio.sleep(0.01)
    reg.apply_degradation(_event("a"), 0.3)
    await asyncio.wait_for(task, timeout=2)
    u = seen[0]
    assert u.node_id == "a"
    assert u.previous_score == 1.0
    assert abs(u.new_score - 0.7) < 1e-9
    assert u.reason == "evt"


async def test_recovery_update_reason_is_passive_recovery():
    reg = NodeTrustRegistry(SecurityOptions())
    reg.apply_degradation(_event("a"), 0.5)  # buffered update #1
    reg.apply_recovery(timedelta(seconds=100))  # buffered update #2

    seen = []

    async def consume(n):
        async for u in reg.trust_score_updates.read_all_async():
            seen.append(u)
            if len(seen) >= n:
                break

    await asyncio.wait_for(consume(2), timeout=2)
    assert seen[0].reason == "evt"
    assert seen[1].reason == "passive-recovery"


def test_tiny_change_below_epsilon_not_published_but_still_applied():
    # A degradation smaller than 0.0001 must not emit an update (matches the
    # C# `Math.Abs(current - previous) > 0.0001` gate) but still records the event.
    reg = NodeTrustRegistry(SecurityOptions())
    prev, cur = reg.apply_degradation(_event("a"), 0.00001)
    assert prev == 1.0
    assert cur == pytest.approx(0.99999)
    entry = reg.get_or_create("a")
    assert len(entry.recent_events) == 1
