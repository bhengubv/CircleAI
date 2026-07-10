# nearlink.py
#
# CircleAI.Networking.NearLink — Huawei SLE / NearLink network transport module.
#
# Ported faithfully from the C# spec:
#   NearLinkTransportCommons.cs -> NearLinkPairingState, NearLinkPowerProfile
#       (enums), NearLinkDevice, NearLinkSession, NearLinkThroughputSample
#       (records), InMemoryNearLinkRegistry
#   NearLinkTransport.cs -> NearLinkTransport (INetworkTransport for SLE /
#       NearLink), INearLinkAdapter (the injected platform-ops seam)
#
# NearLink operates up to 600 m / 12 Mbps, bridging BLE and WiFi Direct. The
# real transport needs the Huawei DevEco NearLink SDK; here the platform ops are
# injected behind :class:`INearLinkAdapter` (in-memory, no radio). A working,
# deterministic :class:`InMemoryNearLinkAdapter` loops sent payloads back into the
# inbound channel so the transport round-trips without hardware.

from __future__ import annotations

import statistics
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import AsyncIterator, Dict, List, Optional, Sequence

from ._inbound import InboundChannel
from .interfaces import INetworkTransport
from .network_types import NetworkPayload, TransportKind


class NearLinkPairingState(IntEnum):
    """Pairing state of a NearLink device.

    Ordinals match the C# ``enum NearLinkPairingState { Unpaired, Pairing,
    Paired, PairingFailed }``.
    """

    UNPAIRED = 0
    PAIRING = 1
    PAIRED = 2
    PAIRING_FAILED = 3


class NearLinkPowerProfile(IntEnum):
    """Power/throughput profile of a NearLink session.

    Ordinals match the C# ``enum NearLinkPowerProfile { LowEnergy, Balanced,
    HighThroughput }``.
    """

    LOW_ENERGY = 0
    BALANCED = 1
    HIGH_THROUGHPUT = 2


@dataclass(frozen=True, slots=True)
class NearLinkDevice:
    """A discovered NearLink device. Faithful port of the C# record."""

    device_id: str
    friendly_name: str
    manufacturer_id: str
    firmware_version: str


@dataclass(frozen=True, slots=True)
class NearLinkSession:
    """An open NearLink session to a device. Faithful port of the C# record."""

    session_id: str
    device_id: str
    power_profile: NearLinkPowerProfile
    started_utc: datetime


@dataclass(frozen=True, slots=True)
class NearLinkThroughputSample:
    """A read/write throughput + RSSI measurement. Faithful port of the C#
    record.
    """

    device_id: str
    kbps_read: float
    kbps_write: float
    rssi_dbm: int
    at_utc: datetime


class InMemoryNearLinkRegistry:
    """In-memory registry of NearLink devices, pairing states, sessions, and
    throughput. Faithful port of the C# ``InMemoryNearLinkRegistry``.
    """

    def __init__(self) -> None:
        self._devices: Dict[str, NearLinkDevice] = {}
        self._states: Dict[str, NearLinkPairingState] = {}
        self._sessions: Dict[str, NearLinkSession] = {}
        self._throughput: List[NearLinkThroughputSample] = []
        self._lock = threading.Lock()

    def register(self, d: NearLinkDevice) -> None:
        if d is None:
            raise ValueError("device required")
        with self._lock:
            self._devices[d.device_id] = d

    def get_device(self, device_id: str) -> Optional[NearLinkDevice]:
        with self._lock:
            return self._devices.get(device_id)

    @property
    def devices(self) -> Sequence[NearLinkDevice]:
        """Devices ordered by friendly name (C#: ``OrderBy(d => d.FriendlyName)``)."""
        with self._lock:
            return sorted(self._devices.values(), key=lambda d: d.friendly_name)

    def set_pairing_state(
        self, device_id: str, s: NearLinkPairingState
    ) -> None:
        with self._lock:
            self._states[device_id] = s

    def pairing_state(self, device_id: str) -> NearLinkPairingState:
        with self._lock:
            return self._states.get(device_id, NearLinkPairingState.UNPAIRED)

    def open_session(self, s: NearLinkSession) -> None:
        if s is None:
            raise ValueError("session required")
        with self._lock:
            self._sessions[s.session_id] = s

    def get_session(self, session_id: str) -> Optional[NearLinkSession]:
        with self._lock:
            return self._sessions.get(session_id)

    def close_session(self, session_id: str) -> None:
        with self._lock:
            self._sessions.pop(session_id, None)

    @property
    def active_sessions(self) -> Sequence[NearLinkSession]:
        with self._lock:
            return list(self._sessions.values())

    def record_throughput(self, s: NearLinkThroughputSample) -> None:
        if s is None:
            raise ValueError("throughput sample required")
        with self._lock:
            self._throughput.append(s)

    def avg_rssi(self, device_id: str) -> float:
        """Mean RSSI (dBm) for ``device_id``; -127 when no samples
        (C#: ``DefaultIfEmpty(-127).Average()``).
        """
        with self._lock:
            samples = [
                float(t.rssi_dbm)
                for t in self._throughput
                if t.device_id == device_id
            ]
        return statistics.fmean(samples) if samples else -127.0


class INearLinkAdapter(ABC):
    """Platform-level NearLink / SLE operations. Implement with the Huawei
    DevEco NearLink SDK on HarmonyOS, or the NearLink HAL on compatible Android.
    Faithful port of the C# ``INearLinkAdapter`` interface.

    ``start_async`` is handed the transport's inbound channel (the C#
    ``ChannelWriter<NetworkPayload>``); the adapter writes received frames into
    it. Here that writer is an :class:`InboundChannel`.
    """

    @property
    @abstractmethod
    def is_available(self) -> bool:
        """Whether the NearLink radio/link is currently usable."""
        ...

    @abstractmethod
    async def start_async(
        self,
        inbound: "InboundChannel[NetworkPayload]",
        *,
        ct: Optional[object] = None,
    ) -> None:
        """Begin the SLE session and route received frames into ``inbound``."""
        ...

    @abstractmethod
    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        """End the SLE session."""
        ...

    @abstractmethod
    async def send_async(
        self, payload: NetworkPayload, *, ct: Optional[object] = None
    ) -> None:
        """Transmit ``payload`` over the NearLink link."""
        ...


class NearLinkTransport(INetworkTransport):
    """`INetworkTransport` for Huawei SLE / NearLink. Faithful port of the C#
    ``NearLinkTransport``.

    Delegates all platform work to the injected :class:`INearLinkAdapter` and
    wires it to an unbounded inbound channel; ``receive_async`` streams that
    channel (the C# ``reader.ReadAllAsync``). ``send_async`` forwards straight to
    the adapter (the C# ``SendAsync`` is a direct passthrough — note it does NOT
    complete the channel, unlike ``stop_async``).
    """

    def __init__(self, adapter: INearLinkAdapter) -> None:
        if adapter is None:
            raise ValueError("adapter required")
        self._adapter = adapter
        self._inbound: "InboundChannel[NetworkPayload]" = InboundChannel()

    @property
    def kind(self) -> TransportKind:
        return TransportKind.NEAR_LINK

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
        # Direct passthrough (the C# `=> _adapter.SendAsync(...)`).
        await self._adapter.send_async(payload, ct=ct)

    def receive_async(
        self, *, ct: Optional[object] = None
    ) -> AsyncIterator[NetworkPayload]:
        return self._inbound.read_all()


class InMemoryNearLinkAdapter(INearLinkAdapter):
    """A working, deterministic :class:`INearLinkAdapter`.

    ``send_async`` loops each sent payload straight back into the inbound
    channel (a local SLE echo) when ``loopback`` is set, so a
    :class:`NearLinkTransport` over this adapter round-trips deterministically
    without a real radio. :meth:`deliver` lets a host/test inject an inbound
    frame from a simulated remote device.
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

    async def send_async(
        self, payload: NetworkPayload, *, ct: Optional[object] = None
    ) -> None:
        if not self._started or self._inbound is None:
            raise RuntimeError("NearLink adapter is not started")
        if self._loopback:
            self._inbound.write(payload)

    def deliver(self, payload: NetworkPayload) -> None:
        """Inject an inbound frame from a simulated remote device."""
        if self._inbound is None:
            raise RuntimeError("NearLink adapter is not started")
        self._inbound.write(payload)
