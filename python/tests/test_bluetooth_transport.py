"""test_bluetooth_transport.py

Verifies the Bluetooth (BLE GATT) transport module:
BluetoothConnectionState ordinals, the endpoint/capability/throughput records,
BluetoothCapabilityProfiles constants, InMemoryBluetoothTransportRegistry, the
IBleGattAdapter seam, and BluetoothNetworkTransport start/stop/send/receive
wired to an InMemoryBleGattAdapter (with the Wave-1 concurrency guarantees).

Mirrors CircleAI.Networking.Bluetooth BluetoothTransportCommons.cs /
BluetoothNetworkTransport.cs (C# — the spec).
"""
from __future__ import annotations

import asyncio
import dataclasses
from datetime import datetime, timezone

import pytest

from circle_ai.networking import (
    BluetoothCapabilityProfile,
    BluetoothCapabilityProfiles,
    BluetoothConnectionState,
    BluetoothEndpointDescriptor,
    BluetoothNetworkTransport,
    BluetoothThroughputSample,
    InMemoryBleGattAdapter,
    InMemoryBluetoothTransportRegistry,
    NetworkPayload,
    TransportKind,
)


def _now() -> datetime:
    return datetime.now(timezone.utc)


async def _next(it, timeout: float = 1.0):
    return await asyncio.wait_for(it.__anext__(), timeout=timeout)


# ── BluetoothConnectionState ─────────────────────────────────────────────────


def test_connection_state_ordinals_match_csharp() -> None:
    assert int(BluetoothConnectionState.DISCONNECTED) == 0
    assert int(BluetoothConnectionState.DISCOVERING) == 1
    assert int(BluetoothConnectionState.CONNECTING) == 2
    assert int(BluetoothConnectionState.CONNECTED) == 3
    assert int(BluetoothConnectionState.FAILED) == 4


# ── records + capability profiles ────────────────────────────────────────────


def test_endpoint_descriptor_is_frozen() -> None:
    e = BluetoothEndpointDescriptor("d1", "Watch", "AA:BB", ("GATT",))
    assert e.device_id == "d1"
    assert list(e.advertised_services) == ["GATT"]
    with pytest.raises(dataclasses.FrozenInstanceError):
        e.name = "x"  # type: ignore[misc]


def test_capability_profiles_match_csharp_constants() -> None:
    le5 = BluetoothCapabilityProfiles.LE5
    assert le5.max_mtu_bytes == 247
    assert le5.supports_secure_connections is True
    assert le5.supports_high_speed is True
    assert list(le5.compatible_profiles) == ["GATT", "L2CAP"]

    le4 = BluetoothCapabilityProfiles.LE4
    assert le4.max_mtu_bytes == 23
    assert le4.supports_high_speed is False
    assert list(le4.compatible_profiles) == ["GATT"]

    classic = BluetoothCapabilityProfiles.CLASSIC
    assert classic.max_mtu_bytes == 1024
    assert list(classic.compatible_profiles) == ["SPP", "RFCOMM"]


def test_capability_profile_is_frozen() -> None:
    p = BluetoothCapabilityProfile(100, True, False, ("GATT",))
    with pytest.raises(dataclasses.FrozenInstanceError):
        p.max_mtu_bytes = 1  # type: ignore[misc]


# ── InMemoryBluetoothTransportRegistry ───────────────────────────────────────


def test_registry_register_get_and_ordered_all() -> None:
    reg = InMemoryBluetoothTransportRegistry()
    reg.register(BluetoothEndpointDescriptor("d2", "Zeta", "00:02", ()))
    reg.register(BluetoothEndpointDescriptor("d1", "Alpha", "00:01", ()))
    assert reg.get_endpoint("d1").name == "Alpha"
    assert reg.get_endpoint("missing") is None
    # all_endpoints ordered by name (C#: OrderBy(e => e.Name))
    assert [e.name for e in reg.all_endpoints] == ["Alpha", "Zeta"]


def test_registry_rejects_none_endpoint() -> None:
    reg = InMemoryBluetoothTransportRegistry()
    with pytest.raises(ValueError):
        reg.register(None)  # type: ignore[arg-type]


def test_registry_state_defaults_to_disconnected() -> None:
    reg = InMemoryBluetoothTransportRegistry()
    assert reg.state("unknown") is BluetoothConnectionState.DISCONNECTED
    reg.set_state("d1", BluetoothConnectionState.CONNECTED)
    assert reg.state("d1") is BluetoothConnectionState.CONNECTED


def test_registry_avg_kbps_read_empty_is_zero() -> None:
    reg = InMemoryBluetoothTransportRegistry()
    assert reg.avg_kbps_read("d1") == 0.0


def test_registry_avg_kbps_read_averages_samples() -> None:
    reg = InMemoryBluetoothTransportRegistry()
    reg.record_throughput(BluetoothThroughputSample("d1", 100.0, 50.0, _now()))
    reg.record_throughput(BluetoothThroughputSample("d1", 200.0, 60.0, _now()))
    reg.record_throughput(BluetoothThroughputSample("d2", 999.0, 0.0, _now()))
    assert reg.avg_kbps_read("d1") == 150.0  # mean(100, 200)


# ── BluetoothNetworkTransport ────────────────────────────────────────────────


def test_transport_kind_is_bluetooth() -> None:
    t = BluetoothNetworkTransport(InMemoryBleGattAdapter())
    assert t.kind is TransportKind.BLUETOOTH


def test_transport_rejects_none_adapter() -> None:
    with pytest.raises(ValueError):
        BluetoothNetworkTransport(None)  # type: ignore[arg-type]


def test_transport_is_available_reflects_adapter() -> None:
    adapter = InMemoryBleGattAdapter(available=False)
    t = BluetoothNetworkTransport(adapter)
    assert t.is_available is False
    adapter.set_available(True)
    assert t.is_available is True


async def test_send_loops_back_through_adapter_to_receiver() -> None:
    adapter = InMemoryBleGattAdapter(loopback=True)
    t = BluetoothNetworkTransport(adapter)
    await t.start_async()
    rx = t.receive_async()
    await t.send_async(NetworkPayload.create(b"ble-frame", destination_id="peer"))
    got = await _next(rx)
    assert got.data == b"ble-frame"


async def test_send_before_start_raises() -> None:
    t = BluetoothNetworkTransport(InMemoryBleGattAdapter())
    with pytest.raises(RuntimeError):
        await t.send_async(NetworkPayload.create(b"x"))


async def test_adapter_deliver_injects_inbound_frame() -> None:
    adapter = InMemoryBleGattAdapter(loopback=False)
    t = BluetoothNetworkTransport(adapter)
    await t.start_async()
    rx = t.receive_async()
    # Simulate a frame arriving from a remote peer (not a loopback of a send).
    adapter.deliver(NetworkPayload.create(b"from-remote"))
    got = await _next(rx)
    assert got.data == b"from-remote"


async def test_message_sent_immediately_after_subscribe_not_lost() -> None:
    adapter = InMemoryBleGattAdapter(loopback=True)
    t = BluetoothNetworkTransport(adapter)
    await t.start_async()
    rx = t.receive_async()  # registers synchronously
    await t.send_async(NetworkPayload.create(b"race"))
    assert (await _next(rx)).data == b"race"


async def test_unbounded_buffering_retains_predrain_frames() -> None:
    adapter = InMemoryBleGattAdapter(loopback=True)
    t = BluetoothNetworkTransport(adapter)
    await t.start_async()
    rx = t.receive_async()
    for i in range(15):
        await t.send_async(NetworkPayload.create(str(i).encode()))
    received = [(await _next(rx)).data for _ in range(15)]
    assert received == [str(i).encode() for i in range(15)]


async def test_stop_completes_receive_loop_without_deadlock() -> None:
    adapter = InMemoryBleGattAdapter(loopback=True)
    t = BluetoothNetworkTransport(adapter)
    await t.start_async()
    rx = t.receive_async()
    await t.stop_async()
    collected = [item async for item in rx]
    assert collected == []


async def test_fan_out_to_multiple_receivers() -> None:
    adapter = InMemoryBleGattAdapter(loopback=True)
    t = BluetoothNetworkTransport(adapter)
    await t.start_async()
    rx1 = t.receive_async()
    rx2 = t.receive_async()
    await t.send_async(NetworkPayload.create(b"dup"))
    assert (await _next(rx1)).data == b"dup"
    assert (await _next(rx2)).data == b"dup"
