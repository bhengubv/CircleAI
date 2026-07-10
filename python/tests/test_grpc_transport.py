"""test_grpc_transport.py

Verifies the gRPC transport module: GrpcChannelState ordinals, the
channel-descriptor / retry-policy / call-summary records, GrpcRetryPolicies
constants, InMemoryGrpcCallMetrics, the IGrpcChannel seam +
InMemoryGrpcChannel, and GrpcNetworkTransport (start/stop, the
NotSupported send path, the empty receive stream, channel exposure + dispose).

Mirrors CircleAI.Networking.Grpc GrpcTransportCommons.cs /
GrpcNetworkTransport.cs (C# — the spec).
"""
from __future__ import annotations

import dataclasses
from datetime import datetime, timezone

import pytest

from circle_ai.networking import (
    GrpcCallSummary,
    GrpcChannelDescriptor,
    GrpcChannelState,
    GrpcNetworkTransport,
    GrpcRetryPolicies,
    GrpcRetryPolicy,
    GrpcSendNotSupportedError,
    InMemoryGrpcCallMetrics,
    InMemoryGrpcChannel,
    NetworkPayload,
    TransportKind,
)


def _now() -> datetime:
    return datetime.now(timezone.utc)


# ── GrpcChannelState ─────────────────────────────────────────────────────────


def test_channel_state_ordinals_match_csharp() -> None:
    assert int(GrpcChannelState.IDLE) == 0
    assert int(GrpcChannelState.CONNECTING) == 1
    assert int(GrpcChannelState.READY) == 2
    assert int(GrpcChannelState.TRANSIENT_FAILURE) == 3
    assert int(GrpcChannelState.SHUTDOWN) == 4


# ── records + retry policies ─────────────────────────────────────────────────


def test_channel_descriptor_is_frozen() -> None:
    d = GrpcChannelDescriptor("dns:///svc:443", True, 4_000_000, 2_000_000, 30.0)
    assert d.target == "dns:///svc:443"
    assert d.use_tls is True
    with pytest.raises(dataclasses.FrozenInstanceError):
        d.target = "x"  # type: ignore[misc]


def test_retry_policies_default_matches_csharp() -> None:
    p = GrpcRetryPolicies.DEFAULT
    assert p.max_attempts == 3
    assert abs(p.initial_backoff - 0.1) < 1e-9
    assert abs(p.max_backoff - 2.0) < 1e-9
    assert p.multiplier == 2.0
    assert list(p.retryable_status_codes) == ["UNAVAILABLE", "DEADLINE_EXCEEDED"]


def test_retry_policies_aggressive_and_no_retry() -> None:
    agg = GrpcRetryPolicies.AGGRESSIVE
    assert agg.max_attempts == 6
    assert "RESOURCE_EXHAUSTED" in agg.retryable_status_codes
    nr = GrpcRetryPolicies.NO_RETRY
    assert nr.max_attempts == 1
    assert nr.initial_backoff == 0.0
    assert list(nr.retryable_status_codes) == []


# ── InMemoryGrpcCallMetrics ──────────────────────────────────────────────────


def test_metrics_register_get_and_state_default() -> None:
    m = InMemoryGrpcCallMetrics()
    d = GrpcChannelDescriptor("t", False, 1, 1, 10.0)
    m.register_channel("c1", d)
    assert m.get_channel("c1") is d
    assert m.get_channel("missing") is None
    assert m.state("c1") is GrpcChannelState.IDLE  # default
    m.set_state("c1", GrpcChannelState.READY)
    assert m.state("c1") is GrpcChannelState.READY


def test_metrics_log_call_returns_monotonic_ids() -> None:
    m = InMemoryGrpcCallMetrics()
    id1 = m.log_call(GrpcCallSummary("M", 1, 0.01, "OK", _now()))
    id2 = m.log_call(GrpcCallSummary("M", 2, 0.02, "OK", _now()))
    assert id1 == "grpc-1"
    assert id2 == "grpc-2"


def test_metrics_recent_calls_newest_first_and_limited() -> None:
    m = InMemoryGrpcCallMetrics()
    base = _now()
    from datetime import timedelta

    for i in range(5):
        m.log_call(
            GrpcCallSummary(f"M{i}", 1, 0.0, "OK", base + timedelta(seconds=i))
        )
    recent = m.recent_calls(limit=3)
    assert len(recent) == 3
    # newest first (largest at_utc)
    assert recent[0].method == "M4"
    assert recent[1].method == "M3"
    assert recent[2].method == "M2"


# ── InMemoryGrpcChannel ──────────────────────────────────────────────────────


def test_channel_rejects_blank_target() -> None:
    with pytest.raises(ValueError):
        InMemoryGrpcChannel("   ")


def test_channel_dispose_transitions_to_shutdown() -> None:
    ch = InMemoryGrpcChannel("svc:443", state=GrpcChannelState.READY)
    assert ch.target == "svc:443"
    assert ch.state is GrpcChannelState.READY
    ch.dispose()
    assert ch.is_disposed is True
    assert ch.state is GrpcChannelState.SHUTDOWN
    # set_state is a no-op once disposed
    ch.set_state(GrpcChannelState.READY)
    assert ch.state is GrpcChannelState.SHUTDOWN


# ── GrpcNetworkTransport ─────────────────────────────────────────────────────


def test_transport_kind_is_grpc() -> None:
    t = GrpcNetworkTransport(InMemoryGrpcChannel("svc"))
    assert t.kind is TransportKind.GRPC


def test_transport_rejects_none_channel() -> None:
    with pytest.raises(ValueError):
        GrpcNetworkTransport(None)  # type: ignore[arg-type]


async def test_start_stop_toggles_availability() -> None:
    t = GrpcNetworkTransport(InMemoryGrpcChannel("svc"))
    assert t.is_available is False
    await t.start_async()
    assert t.is_available is True
    await t.stop_async()
    assert t.is_available is False


async def test_send_raises_not_supported() -> None:
    t = GrpcNetworkTransport(InMemoryGrpcChannel("svc"))
    await t.start_async()
    with pytest.raises(GrpcSendNotSupportedError):
        await t.send_async(NetworkPayload.create(b"x"))


async def test_receive_yields_nothing() -> None:
    t = GrpcNetworkTransport(InMemoryGrpcChannel("svc"))
    await t.start_async()
    collected = [item async for item in t.receive_async()]
    assert collected == []


def test_channel_property_exposes_underlying_channel() -> None:
    ch = InMemoryGrpcChannel("svc")
    t = GrpcNetworkTransport(ch)
    assert t.channel is ch


def test_dispose_disposes_channel() -> None:
    ch = InMemoryGrpcChannel("svc", state=GrpcChannelState.READY)
    t = GrpcNetworkTransport(ch)
    t.dispose()
    assert ch.is_disposed is True
    assert ch.state is GrpcChannelState.SHUTDOWN
