# bluetooth.py
#
# CircleAI.Networking.Bluetooth — BLE GATT network transport module.
#
# Ported faithfully from the C# spec:
#   BluetoothTransportCommons.cs -> BluetoothConnectionState (enum),
#       BluetoothEndpointDescriptor, BluetoothCapabilityProfile,
#       BluetoothThroughputSample (records), BluetoothCapabilityProfiles,
#       InMemoryBluetoothTransportRegistry
#   BluetoothNetworkTransport.cs -> IBleGattAdapter (interface),
#       BluetoothNetworkTransport (INetworkTransport over BLE GATT)
#
# The platform BLE stacks (Windows.Devices.Bluetooth, CoreBluetooth, Android
# BluetoothGatt, BlueZ) implement IBleGattAdapter; the transport wires the
# adapter to the channel-based receive loop. A working, deterministic
# InMemoryBleGattAdapter is provided as the injected in-memory adapter.

from __future__ import annotations

import statistics
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import AsyncIterator, Dict, List, Optional, Sequence

from .interfaces import INetworkTransport
from .network_types import NetworkPayload, TransportKind
from ._inbound import InboundChannel


class BluetoothConnectionState(IntEnum):
    """State of a BLE connection to one endpoint.

    Ordinals match the C# ``enum BluetoothConnectionState { Disconnected,
    Discovering, Connecting, Connected, Failed }``.
    """

    DISCONNECTED = 0
    DISCOVERING = 1
    CONNECTING = 2
    CONNECTED = 3
    FAILED = 4


@dataclass(frozen=True, slots=True)
class BluetoothEndpointDescriptor:
    """Describes a discovered BLE endpoint. Faithful port of the C# record."""

    device_id: str
    name: str
    mac_address: str
    advertised_services: Sequence[str]


@dataclass(frozen=True, slots=True)
class BluetoothCapabilityProfile:
    """MTU / feature profile of a BLE link. Faithful port of the C# record."""

    max_mtu_bytes: int
    supports_secure_connections: bool
    supports_high_speed: bool
    compatible_profiles: Sequence[str]


@dataclass(frozen=True, slots=True)
class BluetoothThroughputSample:
    """A read/write throughput measurement for one device. Faithful port of the
    C# record.
    """

    device_id: str
    kbps_read: float
    kbps_write: float
    at_utc: datetime


class BluetoothCapabilityProfiles:
    """Canonical capability profiles. Mirrors the C# static
    ``BluetoothCapabilityProfiles`` accessor properties (LE5 / LE4 / Classic).
    """

    LE5: BluetoothCapabilityProfile = BluetoothCapabilityProfile(
        247, True, True, ("GATT", "L2CAP")
    )
    LE4: BluetoothCapabilityProfile = BluetoothCapabilityProfile(
        23, True, False, ("GATT",)
    )
    CLASSIC: BluetoothCapabilityProfile = BluetoothCapabilityProfile(
        1024, True, False, ("SPP", "RFCOMM")
    )


class InMemoryBluetoothTransportRegistry:
    """In-memory registry of BLE endpoints, connection states, and throughput.
    Faithful port of the C# ``InMemoryBluetoothTransportRegistry``.
    """

    def __init__(self) -> None:
        self._endpoints: Dict[str, BluetoothEndpointDescriptor] = {}
        self._states: Dict[str, BluetoothConnectionState] = {}
        self._throughput: List[BluetoothThroughputSample] = []
        self._lock = threading.Lock()

    def register(self, e: BluetoothEndpointDescriptor) -> None:
        if e is None:
            raise ValueError("endpoint required")
        with self._lock:
            self._endpoints[e.device_id] = e

    def get_endpoint(
        self, device_id: str
    ) -> Optional[BluetoothEndpointDescriptor]:
        with self._lock:
            return self._endpoints.get(device_id)

    @property
    def all_endpoints(self) -> Sequence[BluetoothEndpointDescriptor]:
        """Endpoints ordered by name (C#: ``OrderBy(e => e.Name)``)."""
        with self._lock:
            return sorted(self._endpoints.values(), key=lambda e: e.name)

    def set_state(
        self, device_id: str, s: BluetoothConnectionState
    ) -> None:
        with self._lock:
            self._states[device_id] = s

    def state(self, device_id: str) -> BluetoothConnectionState:
        with self._lock:
            return self._states.get(
                device_id, BluetoothConnectionState.DISCONNECTED
            )

    def record_throughput(self, s: BluetoothThroughputSample) -> None:
        if s is None:
            raise ValueError("throughput sample required")
        with self._lock:
            self._throughput.append(s)

    def avg_kbps_read(self, device_id: str) -> float:
        """Mean read throughput for ``device_id``; 0.0 when no samples
        (C#: ``DefaultIfEmpty(0.0).Average()``).
        """
        with self._lock:
            reads = [
                t.kbps_read for t in self._throughput if t.device_id == device_id
            ]
        return statistics.fmean(reads) if reads else 0.0

    def avg_kbps_write(self, device_id: str) -> float:
        """Mean write throughput for ``device_id``; 0.0 when no samples
        (C#: ``DefaultIfEmpty(0.0).Average()``).
        """
        with self._lock:
            writes = [
                t.kbps_write for t in self._throughput if t.device_id == device_id
            ]
        return statistics.fmean(writes) if writes else 0.0

    def unregister(self, device_id: str) -> bool:
        """Drop a device: remove its endpoint descriptor and any tracked
        connection state. Returns True if an endpoint was actually removed
        (C#: ``Unregister``).
        """
        if not device_id:
            return False
        with self._lock:
            removed = self._endpoints.pop(device_id, None) is not None
            self._states.pop(device_id, None)
        return removed

    def endpoints_with_service(
        self, service: str
    ) -> Sequence[BluetoothEndpointDescriptor]:
        """Endpoints advertising ``service`` (matched case-insensitively),
        ordered by device name — the discovery view a service scanner needs
        (C#: ``EndpointsWithService``). Empty ``service`` yields nothing.
        """
        if not service:
            return []
        target = service.casefold()
        with self._lock:
            matches = [
                e
                for e in self._endpoints.values()
                if any(s.casefold() == target for s in e.advertised_services)
            ]
        return sorted(matches, key=lambda e: e.name)

    @property
    def connected_count(self) -> int:
        """Number of devices currently in the ``CONNECTED`` state
        (C#: ``ConnectedCount``).
        """
        with self._lock:
            return sum(
                1
                for s in self._states.values()
                if s == BluetoothConnectionState.CONNECTED
            )


class IBleGattAdapter(ABC):
    """Platform-specific BLE GATT operations. Implement per platform (MAUI,
    Windows, Linux). Faithful port of the C# ``IBleGattAdapter`` interface.

    ``start_async`` is handed the transport's inbound channel (the C#
    ``ChannelWriter<NetworkPayload>``); the adapter writes received frames into
    it. Here that writer is an :class:`InboundChannel`.
    """

    @property
    @abstractmethod
    def is_available(self) -> bool:
        """Whether the BLE radio/link is currently usable."""
        ...

    @abstractmethod
    async def start_async(
        self, inbound: "InboundChannel[NetworkPayload]", *, ct: Optional[object] = None
    ) -> None:
        """Begin the GATT session and route received frames into ``inbound``."""
        ...

    @abstractmethod
    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        """End the GATT session."""
        ...

    @abstractmethod
    async def write_async(
        self, payload: NetworkPayload, *, ct: Optional[object] = None
    ) -> None:
        """Transmit ``payload`` over the GATT link."""
        ...


class BluetoothNetworkTransport(INetworkTransport):
    """`INetworkTransport` over BLE GATT. Faithful port of the C#
    ``BluetoothNetworkTransport``.

    Wires the injected :class:`IBleGattAdapter` to an unbounded inbound channel;
    ``receive_async`` streams that channel (the C# ``reader.ReadAllAsync``).
    """

    def __init__(self, adapter: IBleGattAdapter) -> None:
        if adapter is None:
            raise ValueError("adapter required")
        self._adapter = adapter
        self._inbound: "InboundChannel[NetworkPayload]" = InboundChannel()

    @property
    def kind(self) -> TransportKind:
        return TransportKind.BLUETOOTH

    @property
    def is_available(self) -> bool:
        return self._adapter.is_available

    async def start_async(self, *, ct: Optional[object] = None) -> None:
        await self._adapter.start_async(self._inbound, ct=ct)

    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        await self._adapter.stop_async(ct=ct)
        self._inbound.try_complete()

    async def send_async(
        self, payload: NetworkPayload, *, ct: Optional[object] = None
    ) -> None:
        if payload is None:
            raise ValueError("payload required")
        await self._adapter.write_async(payload, ct=ct)

    def receive_async(
        self, *, ct: Optional[object] = None
    ) -> AsyncIterator[NetworkPayload]:
        return self._inbound.read_all()


class InMemoryBleGattAdapter(IBleGattAdapter):
    """A working, deterministic :class:`IBleGattAdapter`.

    ``write_async`` loops each sent payload straight back into the inbound
    channel (a local GATT echo), so a :class:`BluetoothNetworkTransport` over
    this adapter round-trips deterministically without a real radio. The
    injected loopback echo is the seam a real platform adapter replaces with
    actual GATT reads. :meth:`deliver` lets a host/test inject an inbound frame
    from a simulated remote peer.
    """

    def __init__(self, *, available: bool = True, loopback: bool = True) -> None:
        self._available = available
        self._loopback = loopback
        self._inbound: Optional["InboundChannel[NetworkPayload]"] = None
        self._started = False

    @property
    def is_available(self) -> bool:
        return self._available

    def set_available(self, value: bool) -> None:
        """Toggle radio availability (host seam)."""
        self._available = value

    async def start_async(
        self,
        inbound: "InboundChannel[NetworkPayload]",
        *,
        ct: Optional[object] = None,
    ) -> None:
        self._inbound = inbound
        self._started = True

    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        self._started = False
        self._inbound = None

    async def write_async(
        self, payload: NetworkPayload, *, ct: Optional[object] = None
    ) -> None:
        if not self._started or self._inbound is None:
            raise RuntimeError("BLE adapter is not started")
        if self._loopback:
            self._inbound.write(payload)

    def deliver(self, payload: NetworkPayload) -> None:
        """Inject an inbound frame from a simulated remote peer."""
        if self._inbound is None:
            raise RuntimeError("BLE adapter is not started")
        self._inbound.write(payload)
