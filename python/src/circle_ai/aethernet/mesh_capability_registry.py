# mesh_capability_registry.py
#
# Port of CircleAI.AetherNet.MeshCapabilityRegistry (C# — the EXACT spec).
#
# (RT-12 v1) Mesh capability discovery — peers broadcast what they have loaded
# ("I have Qwen3-1.7B-MNN with 2048 tokens of free KV budget on a Tier=Phone
# device"). v1 ships the contracts + an in-memory registry; the AetherNet
# broadcast transport lands in 2.7.0 with RT-12 v2 actual offload.
#
# Type map:
#   MeshCapabilityAdvertisement    — one peer's advertisement (pure data)
#   IMeshCapabilityRegistry        — latest-per-peer store + filtered query
#   InMemoryMeshCapabilityRegistry — default thread-safe in-memory registry
#   IMeshCapabilityBroadcaster     — publishes OUR advertisement to the mesh
#   NullMeshCapabilityBroadcaster  — no-op default (no transport bound)

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Callable, List, Optional

from ..device.device_probe import DeviceTier


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass(frozen=True, slots=True)
class MeshCapabilityAdvertisement:
    """(RT-12 v1) One peer's advertisement of what it can serve right now. Pure
    data — no execution state.

    :param peer_id: Stable opaque identifier for the advertising peer.
    :param model_id: The model the peer has loaded, e.g. ``"Qwen3-1.7B-MNN"``.
    :param free_kv_tokens: How many tokens of KV-cache budget the peer has spare.
    :param tier: The peer's device tier (Wearable .. Workstation).
    :param context_window_tokens: The model's configured context window.
    :param advertised_at_utc: When the peer last published this advertisement.
    :param latency_hint_ms: Optional round-trip estimate; None when unknown.
    """

    peer_id: str
    model_id: str
    free_kv_tokens: int
    tier: DeviceTier
    context_window_tokens: int
    advertised_at_utc: datetime
    latency_hint_ms: Optional[int] = None


class IMeshCapabilityRegistry(ABC):
    """(RT-12 v1) Holds the latest advertisement per peer + supports filtered
    query. The AetherNet transport (v2, 2.7.0) feeds this registry as peers
    broadcast. v1 lets hosting layers query and reason about availability without
    yet routing.
    """

    @abstractmethod
    async def upsert_async(
        self, ad: MeshCapabilityAdvertisement, ct: Optional[object] = None
    ) -> None:
        """Publish or replace an advertisement. Called by the transport on
        receipt of a peer broadcast.
        """
        ...

    @abstractmethod
    async def remove_async(self, peer_id: str, ct: Optional[object] = None) -> bool:
        """Remove a peer (e.g. on explicit disconnect). Idempotent."""
        ...

    @abstractmethod
    def list(
        self, stale_after: Optional[timedelta] = None
    ) -> List[MeshCapabilityAdvertisement]:
        """Return every advertisement currently known. Use ``stale_after`` to
        filter out entries older than this duration. Default (None) returns all.
        """
        ...

    @abstractmethod
    def find(
        self,
        model_id: str,
        min_free_kv_tokens: int = 0,
        stale_after: Optional[timedelta] = None,
    ) -> List[MeshCapabilityAdvertisement]:
        """Find every peer that has loaded ``model_id`` with at least
        ``min_free_kv_tokens`` of spare KV budget. Sorted by spare budget
        descending — the most-capable peer comes first.
        """
        ...


class InMemoryMeshCapabilityRegistry(IMeshCapabilityRegistry):
    """(RT-12 v1) Default :class:`IMeshCapabilityRegistry` — in-memory,
    thread-safe. The AetherNet transport plugs into this; without a transport,
    the registry just stays empty (no peers).

    :param now_utc: Optional clock override for tests (mirrors the C# ``NowUtc``
        init property).
    """

    def __init__(self, now_utc: Optional[Callable[[], datetime]] = None) -> None:
        self._lock = threading.Lock()
        self._entries: dict[str, MeshCapabilityAdvertisement] = {}
        self.now_utc: Callable[[], datetime] = now_utc or _utc_now

    async def upsert_async(
        self, ad: MeshCapabilityAdvertisement, ct: Optional[object] = None
    ) -> None:
        if ad is None:
            raise ValueError("ad must not be None")
        if not ad.peer_id or not ad.peer_id.strip():
            raise ValueError("ad.peer_id must not be null or whitespace")
        with self._lock:
            self._entries[ad.peer_id] = ad

    async def remove_async(self, peer_id: str, ct: Optional[object] = None) -> bool:
        if not peer_id or not peer_id.strip():
            raise ValueError("peer_id must not be null or whitespace")
        with self._lock:
            return self._entries.pop(peer_id, None) is not None

    def list(
        self, stale_after: Optional[timedelta] = None
    ) -> List[MeshCapabilityAdvertisement]:
        with self._lock:
            values = list(self._entries.values())
        if stale_after is None:
            return values
        cutoff = self.now_utc() - stale_after
        return [a for a in values if a.advertised_at_utc >= cutoff]

    def find(
        self,
        model_id: str,
        min_free_kv_tokens: int = 0,
        stale_after: Optional[timedelta] = None,
    ) -> List[MeshCapabilityAdvertisement]:
        if not model_id or not model_id.strip():
            raise ValueError("model_id must not be null or whitespace")
        cutoff = (
            self.now_utc() - stale_after
            if stale_after is not None
            else datetime.min.replace(tzinfo=timezone.utc)
        )
        with self._lock:
            values = list(self._entries.values())
        matches = [
            a
            for a in values
            if a.model_id.lower() == model_id.lower()
            and a.free_kv_tokens >= min_free_kv_tokens
            and a.advertised_at_utc >= cutoff
        ]
        # Sort by spare budget descending — most-capable peer first.
        matches.sort(key=lambda a: a.free_kv_tokens, reverse=True)
        return matches


class IMeshCapabilityBroadcaster(ABC):
    """(RT-12 v1) Contract for the broadcaster that publishes OUR advertisement
    to the mesh. v1 ships a no-op default; the AetherNet transport binding (v2)
    supersedes it.
    """

    @abstractmethod
    async def broadcast_async(
        self, ad: MeshCapabilityAdvertisement, ct: Optional[object] = None
    ) -> None:
        """Publish our current advertisement to the mesh. v1 may be a no-op when
        no transport is registered.
        """
        ...


class NullMeshCapabilityBroadcaster(IMeshCapabilityBroadcaster):
    """Default broadcaster — does nothing. Used when no AetherNet transport is
    bound. Existing CircleAI deployments work unchanged.
    """

    #: Shared singleton instance, mirroring C# ``NullMeshCapabilityBroadcaster.Instance``.
    instance: "NullMeshCapabilityBroadcaster"

    async def broadcast_async(
        self, ad: MeshCapabilityAdvertisement, ct: Optional[object] = None
    ) -> None:
        return None


NullMeshCapabilityBroadcaster.instance = NullMeshCapabilityBroadcaster()
