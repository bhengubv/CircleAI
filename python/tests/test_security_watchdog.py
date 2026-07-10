"""test_security_watchdog.py — DefaultSecurityWatchdog graduated responses + signal stream.

Confidence bands: <0.30 NO_ACTION, 0.30-0.60 KEY_ROTATION,
>0.60 COMPOSITE (+STATE_ROLLBACK when a verified checkpoint on a high-severity
vector is supplied). Also covers the async signal stream.
"""
from __future__ import annotations

import asyncio

import pytest

from circle_ai.security import (
    AnomalySignal,
    DefaultSecurityWatchdog,
    SecurityCheckpoint,
    SecurityResponseKind,
    ThreatVector,
)


async def test_low_confidence_no_action():
    w = DefaultSecurityWatchdog()
    sig = AnomalySignal.create(ThreatVector.MEMORY_ANOMALY, 0.1, "M", "d")
    resp = await w.on_anomaly_detected_async(sig)
    assert resp.kind == SecurityResponseKind.NO_ACTION
    assert resp.applied_actions == []
    assert resp.restored_checkpoint is None
    assert resp.signal_id == sig.id


async def test_boundary_below_rotation_is_no_action():
    w = DefaultSecurityWatchdog()
    sig = AnomalySignal.create(ThreatVector.MEMORY_ANOMALY, 0.2999, "M", "d")
    resp = await w.on_anomaly_detected_async(sig)
    assert resp.kind == SecurityResponseKind.NO_ACTION


async def test_mid_confidence_key_rotation():
    w = DefaultSecurityWatchdog()
    sig = AnomalySignal.create(ThreatVector.MEMORY_ANOMALY, 0.45, "M", "d")
    resp = await w.on_anomaly_detected_async(sig)
    assert resp.kind == SecurityResponseKind.KEY_ROTATION
    assert resp.applied_actions == []


async def test_exact_composite_boundary_is_key_rotation():
    # 0.60 is NOT > 0.60, so it stays key-rotation (matches C# strict >).
    w = DefaultSecurityWatchdog()
    sig = AnomalySignal.create(ThreatVector.NETWORK_PIVOT, 0.60, "M", "d")
    resp = await w.on_anomaly_detected_async(sig)
    assert resp.kind == SecurityResponseKind.KEY_ROTATION


async def test_high_confidence_composite_without_checkpoint():
    w = DefaultSecurityWatchdog()
    sig = AnomalySignal.create(ThreatVector.NETWORK_PIVOT, 0.9, "M", "d")
    resp = await w.on_anomaly_detected_async(sig)
    assert resp.kind == SecurityResponseKind.COMPOSITE
    assert SecurityResponseKind.KEY_ROTATION in resp.applied_actions
    assert SecurityResponseKind.MESH_ISOLATION_SIGNAL in resp.applied_actions
    assert SecurityResponseKind.STATE_ROLLBACK not in resp.applied_actions
    assert resp.restored_checkpoint is None


async def test_high_confidence_high_severity_adds_rollback():
    w = DefaultSecurityWatchdog()
    cp = SecurityCheckpoint.create("uhid-1", "CircleAI.Memory", b"snap")
    sig = AnomalySignal.create(ThreatVector.STATE_CORRUPTION, 0.95, "M", "d")
    resp = await w.on_anomaly_detected_async(sig, cp)
    assert resp.kind == SecurityResponseKind.COMPOSITE
    assert SecurityResponseKind.STATE_ROLLBACK in resp.applied_actions
    assert resp.restored_checkpoint is cp


async def test_high_confidence_low_severity_vector_skips_rollback():
    w = DefaultSecurityWatchdog()
    cp = SecurityCheckpoint.create("uhid-1", "CircleAI.Memory", b"snap")
    # MEMORY_ANOMALY is not in the high-severity set.
    sig = AnomalySignal.create(ThreatVector.MEMORY_ANOMALY, 0.95, "M", "d")
    resp = await w.on_anomaly_detected_async(sig, cp)
    assert SecurityResponseKind.STATE_ROLLBACK not in resp.applied_actions
    assert resp.restored_checkpoint is None


async def test_rollback_skipped_when_checkpoint_fails_verification():
    w = DefaultSecurityWatchdog()
    cp = SecurityCheckpoint.create("uhid-1", "CircleAI.Memory", b"snap")
    # Corrupt the payload so verify() fails.
    broken = SecurityCheckpoint(
        cp.id, cp.uhid_identity_id, cp.module_label, b"CORRUPT",
        cp.payload_hash, cp.created_at,
    )
    sig = AnomalySignal.create(ThreatVector.PRIVILEGE_ESCALATION, 0.95, "M", "d")
    resp = await w.on_anomaly_detected_async(sig, broken)
    assert SecurityResponseKind.STATE_ROLLBACK not in resp.applied_actions
    assert resp.restored_checkpoint is None


async def test_none_signal_raises():
    w = DefaultSecurityWatchdog()
    with pytest.raises(ValueError):
        await w.on_anomaly_detected_async(None)  # type: ignore[arg-type]


async def test_stream_receives_dispatched_signals():
    w = DefaultSecurityWatchdog()
    seen = []

    async def consume(n: int):
        async for s in w.stream_signals_async():
            seen.append(s)
            if len(seen) >= n:
                break

    task = asyncio.create_task(consume(2))
    await asyncio.sleep(0.01)  # let the consumer subscribe
    a = AnomalySignal.create(ThreatVector.MEMORY_ANOMALY, 0.5, "M", "a")
    b = AnomalySignal.create(ThreatVector.NETWORK_PIVOT, 0.9, "M", "b")
    await w.on_anomaly_detected_async(a)
    await w.on_anomaly_detected_async(b)
    await asyncio.wait_for(task, timeout=2)
    assert [s.id for s in seen] == [a.id, b.id]


async def test_stream_buffers_signal_emitted_before_subscription():
    # Unbounded channel retains writes made before any reader attaches.
    w = DefaultSecurityWatchdog()
    a = AnomalySignal.create(ThreatVector.MEMORY_ANOMALY, 0.5, "M", "a")
    await w.on_anomaly_detected_async(a)

    seen = []

    async def consume():
        async for s in w.stream_signals_async():
            seen.append(s)
            break

    await asyncio.wait_for(consume(), timeout=2)
    assert seen[0].id == a.id
