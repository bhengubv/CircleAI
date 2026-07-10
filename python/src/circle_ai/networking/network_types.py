# network_types.py
#
# Core enums + immutable records for the networking transport abstraction.
#
# Ported faithfully from CircleAI.Networking (C# — the spec):
#   NetworkTypes.cs    -> TransportKind, ConnectivityState, MessagePriority,
#                         PeerRole  (SyncDeliveryMode is reused from
#                         circle_ai.sync — NOT redefined here)
#   NetworkPayload.cs  -> NetworkPayload (+ .create factory)
#   NetworkContext.cs  -> NetworkContext (+ .offline factory)
#   PeerInfo.cs        -> PeerInfo
#
# Enums are IntEnum with the same ordinals as the C# enum declaration order, so
# a wire/index that stores the ordinal round-trips byte-identically with C#.

from __future__ import annotations

import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from enum import IntEnum
from typing import Mapping, Optional, Sequence


class TransportKind(IntEnum):
    """A single transport a payload can traverse.

    Ordinals match the C# ``enum TransportKind`` declaration order exactly.
    """

    HTTP = 0
    WEB_SOCKET = 1
    GRPC = 2
    MQTT = 3
    TCP = 4
    UDP = 5
    WIFI = 6          # WiFi Direct / mDNS / LAN — no Aether required
    BLUETOOTH = 7     # raw BLE GATT — no Aether required
    NEAR_LINK = 8     # Huawei SLE / HarmonyOS — no Aether required
    AETHER = 9        # full Aether mesh (Signal E2E + AODV + SOS)
    DTN = 10          # 72hr store-and-forward over any transport
    LOCAL_STORE = 11  # offline queue — no live path at all


class ConnectivityState(IntEnum):
    """Coarse reachability of the device. Ordinals match C#."""

    ONLINE = 0
    LOCAL_ONLY = 1
    MESH_ONLY = 2
    OFFLINE = 3


class MessagePriority(IntEnum):
    """Delivery urgency of a payload. Ordinals match C#."""

    LOW = 0
    NORMAL = 1
    HIGH = 2
    URGENT = 3
    EMERGENCY = 4


class PeerRole(IntEnum):
    """The role a discovered peer plays in the mesh. Ordinals match C#."""

    PEER = 0
    RELAY = 1
    BRIDGE = 2
    SINK = 3


def _new_id() -> str:
    """Guid.NewGuid().ToString("N") — 32 lowercase hex chars, no dashes."""
    return uuid.uuid4().hex


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass(frozen=True, slots=True)
class NetworkPayload:
    """Immutable envelope for a single message or data unit traversing any
    transport. Transports must not mutate it — create a new payload instead.

    Faithful port of ``CircleAI.Networking.NetworkPayload`` (a C# ``record``).
    ``data`` is ``bytes`` (the C# ``ReadOnlyMemory<byte>``); ``ttl`` is seconds
    (the C# ``TimeSpan?``).
    """

    id: str
    source_id: Optional[str]
    destination_id: Optional[str]
    data: bytes
    priority: MessagePriority
    ttl: Optional[float]  # seconds; None = no expiry
    content_type: str
    metadata: Mapping[str, str]
    created_at: datetime

    @staticmethod
    def create(
        data: bytes,
        destination_id: Optional[str] = None,
        priority: MessagePriority = MessagePriority.NORMAL,
        content_type: str = "application/octet-stream",
        ttl: Optional[float] = None,
    ) -> "NetworkPayload":
        """Mirror of the C# ``NetworkPayload.Create`` static factory.

        Generates a fresh 32-hex id, no source, empty metadata, and stamps
        ``created_at`` with the current UTC time.
        """
        return NetworkPayload(
            id=_new_id(),
            source_id=None,
            destination_id=destination_id,
            data=data,
            priority=priority,
            ttl=ttl,
            content_type=content_type,
            metadata={},
            created_at=_utc_now(),
        )


@dataclass(frozen=True, slots=True)
class NetworkContext:
    """Snapshot of current connectivity state.

    Faithful port of ``CircleAI.Networking.NetworkContext`` (a C# ``record``).
    """

    state: ConnectivityState
    preferred_transport: TransportKind
    available_transports: Sequence[TransportKind]
    signal_strength_dbm: Optional[int]
    estimated_bandwidth_bps: Optional[int]
    latency_ms: Optional[int]
    nearby_peer_count: int
    snapshot_at: datetime

    @staticmethod
    def offline() -> "NetworkContext":
        """The canonical fully-offline context.

        Mirrors the C# ``NetworkContext.Offline`` static instance: state
        Offline, preferred transport LocalStore, no available transports, no
        signal/bandwidth/latency, zero peers. Unlike the C# ``static readonly``
        (which freezes one timestamp at type-init), this stamps a fresh
        ``snapshot_at`` per call so the snapshot time is never stale.
        """
        return NetworkContext(
            state=ConnectivityState.OFFLINE,
            preferred_transport=TransportKind.LOCAL_STORE,
            available_transports=(),
            signal_strength_dbm=None,
            estimated_bandwidth_bps=None,
            latency_ms=None,
            nearby_peer_count=0,
            snapshot_at=_utc_now(),
        )


@dataclass(frozen=True, slots=True)
class PeerInfo:
    """Describes a discovered peer on any transport.

    Faithful port of ``CircleAI.Networking.PeerInfo`` (a C# ``record``).
    """

    node_id: str
    display_name: Optional[str]
    supported_transports: Sequence[TransportKind]
    role: PeerRole
    signal_strength_dbm: Optional[int]
    last_seen: datetime
