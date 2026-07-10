"""test_nearlink_transport.py

Verifies the NearLink (Huawei SLE) transport module: NearLinkPairingState /
NearLinkPowerProfile ordinals, the device / session / throughput records,
InMemoryNearLinkRegistry (ordering, pairing-state default, session lifecycle,
AvgRssi default -127), the INearLinkAdapter seam, and NearLinkTransport
start/stop/send/receive wired to an InMemoryNearLinkAdapter (with the Wave-1
concurrency guarantees).

Mirrors CircleAI.Networking.NearLink NearLinkTransportCommons.cs /
NearLinkTransport.cs (C# — the spec).
"""
from __future__ import annotations

import asyncio
import dataclasses
from datetime import datetime, timezone

import pytest

from circle_ai.networking import (
    INearLinkAdapter,
    InMemoryNearLinkAdapter,
    InMemoryNearLinkRegistry,
    NearLinkDevice,
    NearLinkPairingState,
    NearLinkPowerProfile,
    NearLinkSession,
    NearLinkThroughputSample,
    NearLinkTransport,
    NetworkPayload,
    TransportKind,
)


def _now() -> datetime:
    return datetime.now(timezone.utc)


async def _next(it, timeout: float = 1.0):
    return await asyncio.wait_for(it.__anext__(), timeout=timeout)


# ── enums ────────────────────────────────────────────────────────────────────


def test_pairing_state_ordinals_match_csharp() -> None:
    assert int(NearLinkPairingState.UNPAIRED) == 0
    assert int(NearLinkPairingState.PAIRING) == 1
    assert int(NearLinkPairingState.PAIRED) == 2
    assert int(NearLinkPairingState.PAIRING_FAILED) == 3


def test_power_profile_ordinals_match_csharp() -> None:
    assert int(NearLinkPowerProfile.LOW_ENERGY) == 0
    assert int(NearLinkPowerProfile.BALANCED) == 1
    assert int(NearLinkPowerProfile.HIGH_THROUGHPUT) == 2


# ── records ──────────────────────────────────────────────────────────────────


def test_device_record_is_frozen() -> None:
    d = NearLinkDevice("d1", "Watch", "huawei", "1.0.0")
    assert d.friendly_name == "Watch"
    assert d.manufacturer_id == "huawei"
    with pytest.raises(dataclasses.FrozenInstanceError):
        d.firmware_version = "x"  # type: ignore[misc]


def test_session_and_throughput_records() -> None:
    s = NearLinkSession("s1", "d1", NearLinkPowerProfile.BALANCED, _now())
    assert s.power_profile is NearLinkPowerProfile.BALANCED
    t = NearLinkThroughputSample("d1", 100.0, 50.0, -60, _now())
    assert t.rssi_dbm == -60
    with pytest.raises(dataclasses.FrozenInstanceError):
        t.rssi_dbm = 0  # type: ignore[misc]


# ── InMemoryNearLinkRegistry ─────────────────────────────────────────────────


def test_registry_register_get_and_ordered_devices() -> None:
    reg = InMemoryNearLinkRegistry()
    reg.register(NearLinkDevice("d2", "Zeta", "m", "1"))
    reg.register(NearLinkDevice("d1", "Alpha", "m", "1"))
    assert reg.get_device("d1").friendly_name == "Alpha"
    assert reg.get_device("missing") is None
    # devices ordered by friendly name (C#: OrderBy(d => d.FriendlyName))
    assert [d.friendly_name for d in reg.devices] == ["Alpha", "Zeta"]


def test_registry_rejects_none_device() -> None:
    reg = InMemoryNearLinkRegistry()
    with pytest.raises(ValueError):
        reg.register(None)  # type: ignore[arg-type]


def test_registry_pairing_state_defaults_to_unpaired() -> None:
    reg = InMemoryNearLinkRegistry()
    assert reg.pairing_state("unknown") is NearLinkPairingState.UNPAIRED
    reg.set_pairing_state("d1", NearLinkPairingState.PAIRED)
    assert reg.pairing_state("d1") is NearLinkPairingState.PAIRED


def test_registry_session_lifecycle() -> None:
    reg = InMemoryNearLinkRegistry()
    s = NearLinkSession("s1", "d1", NearLinkPowerProfile.HIGH_THROUGHPUT, _now())
    reg.open_session(s)
    assert reg.get_session("s1") is s
    assert [x.session_id for x in reg.active_sessions] == ["s1"]
    reg.close_session("s1")
    assert reg.get_session("s1") is None
    assert list(reg.active_sessions) == []
    with pytest.raises(ValueError):
        reg.open_session(None)  # type: ignore[arg-type]


def test_registry_avg_rssi_empty_is_minus_127() -> None:
    reg = InMemoryNearLinkRegistry()
    assert reg.avg_rssi("d1") == -127.0


def test_registry_avg_rssi_averages_samples() -> None:
    reg = InMemoryNearLinkRegistry()
    reg.record_throughput(NearLinkThroughputSample("d1", 0, 0, -50, _now()))
    reg.record_throughput(NearLinkThroughputSample("d1", 0, 0, -70, _now()))
    reg.record_throughput(NearLinkThroughputSample("d2", 0, 0, -10, _now()))
    assert reg.avg_rssi("d1") == -60.0  # mean(-50, -70)


# ── NearLinkTransport ────────────────────────────────────────────────────────


def test_transport_kind_is_nearlink() -> None:
    t = NearLinkTransport(InMemoryNearLinkAdapter())
    assert t.kind is TransportKind.NEAR_LINK


def test_transport_rejects_none_adapter() -> None:
    with pytest.raises(ValueError):
        NearLinkTransport(None)  # type: ignore[arg-type]


def test_transport_is_available_reflects_adapter() -> None:
    adapter = InMemoryNearLinkAdapter(available=False)
    t = NearLinkTransport(adapter)
    assert t.is_available is False
    adapter.set_available(True)
    assert t.is_available is True


async def test_send_loops_back_through_adapter() -> None:
    adapter = InMemoryNearLinkAdapter(loopback=True)
    t = NearLinkTransport(adapter)
    await t.start_async()
    rx = t.receive_async()
    await t.send_async(NetworkPayload.create(b"sle-frame", destination_id="peer"))
    assert (await _next(rx)).data == b"sle-frame"


async def test_send_before_start_raises() -> None:
    t = NearLinkTransport(InMemoryNearLinkAdapter())
    with pytest.raises(RuntimeError):
        await t.send_async(NetworkPayload.create(b"x"))


async def test_adapter_deliver_injects_inbound_frame() -> None:
    adapter = InMemoryNearLinkAdapter(loopback=False)
    t = NearLinkTransport(adapter)
    await t.start_async()
    rx = t.receive_async()
    adapter.deliver(NetworkPayload.create(b"from-remote"))
    assert (await _next(rx)).data == b"from-remote"


async def test_message_immediately_after_subscribe_not_lost() -> None:
    adapter = InMemoryNearLinkAdapter(loopback=True)
    t = NearLinkTransport(adapter)
    await t.start_async()
    rx = t.receive_async()
    await t.send_async(NetworkPayload.create(b"race"))
    assert (await _next(rx)).data == b"race"


async def test_unbounded_buffering_retains_predrain_frames() -> None:
    adapter = InMemoryNearLinkAdapter(loopback=True)
    t = NearLinkTransport(adapter)
    await t.start_async()
    rx = t.receive_async()
    for i in range(15):
        await t.send_async(NetworkPayload.create(str(i).encode()))
    received = [(await _next(rx)).data for _ in range(15)]
    assert received == [str(i).encode() for i in range(15)]


async def test_stop_completes_receive_without_deadlock() -> None:
    adapter = InMemoryNearLinkAdapter(loopback=True)
    t = NearLinkTransport(adapter)
    await t.start_async()
    rx = t.receive_async()
    await t.stop_async()
    collected = [item async for item in rx]
    assert collected == []


async def test_fan_out_to_multiple_receivers() -> None:
    adapter = InMemoryNearLinkAdapter(loopback=True)
    t = NearLinkTransport(adapter)
    await t.start_async()
    rx1 = t.receive_async()
    rx2 = t.receive_async()
    await t.send_async(NetworkPayload.create(b"dup"))
    assert (await _next(rx1)).data == b"dup"
    assert (await _next(rx2)).data == b"dup"
