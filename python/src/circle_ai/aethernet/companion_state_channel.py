# companion_state_channel.py
#
# Port of CircleAI.AetherNet.AetherNetCompanionStateChannel (C# — the EXACT spec).
#
# The production transport for CircleAI.Memory.Sync.CompanionStateSyncEngine.
# Marshals SyncEnvelopes onto AetherNet.Messaging's MeshMessage pipeline.
#
#   ICompanionStateChannel.send_async(envelope)
#        -> JSON-serialize
#        -> wrap in MeshMessage with message_type = "circleai.sync.v1"
#        -> for each peer UHID configured at construction:
#        -> IMessagingService.send_async(mesh_message, plaintext)
#
#   IMessagingService message received
#        -> filter message_type == "circleai.sync.v1"
#        -> skip self-loopback (sender_uhid == local_node_id)
#        -> JSON-deserialize MeshMessage.encrypted_content
#        -> fire every subscribe handler
#
# The plaintext crossing the bus is JSON. AetherNet.Messaging applies the usual
# Signal-Protocol E2E layer on top — this channel does not need to know about
# encryption.

from __future__ import annotations

import json
import threading
from datetime import datetime
from typing import Any, Dict, List, Optional

from ..memory.sync.companion_state_channel import (
    EnvelopeHandler,
    ICompanionStateChannel,
    IDisposable,
)
from ..memory.sync.sync_envelope import (
    RequestItem,
    StateVectorEntry,
    SyncEnvelope,
    SyncEnvelopeKind,
)
from ..memory.sync.syncable_entry import SyncableEntry
from .extensibility import (
    IMessagingService,
    MeshMessage,
    MessageStatus,
)


#: message_type used to distinguish CircleAI sync envelopes from other mesh traffic.
SYNC_MESSAGE_TYPE = "circleai.sync.v1"


# ── SyncEnvelope <-> JSON codec ───────────────────────────────────────────────
# Mirrors what System.Text.Json does with the C# SyncEnvelope record graph.


def _envelope_to_json(envelope: SyncEnvelope) -> str:
    def entry(e: SyncableEntry) -> Dict[str, Any]:
        return {
            "entityType": e.entity_type,
            "entityId": e.entity_id,
            "version": e.version,
            "isTombstone": e.is_tombstone,
            "contentHash": e.content_hash,
            "payload": e.payload,
            "sourceNodeId": e.source_node_id,
            "authoredAt": e.authored_at.isoformat(),
        }

    obj: Dict[str, Any] = {
        "kind": int(envelope.kind),
        "fromNodeId": envelope.from_node_id,
        "stateVector": (
            None
            if envelope.state_vector is None
            else [
                {"entityType": s.entity_type, "maxKnownVersion": s.max_known_version}
                for s in envelope.state_vector
            ]
        ),
        "requests": (
            None
            if envelope.requests is None
            else [
                {"entityType": r.entity_type, "sinceVersion": r.since_version}
                for r in envelope.requests
            ]
        ),
        "entries": (
            None
            if envelope.entries is None
            else [entry(e) for e in envelope.entries]
        ),
    }
    return json.dumps(obj, separators=(",", ":"))


def _envelope_from_json(text: str) -> SyncEnvelope:
    obj = json.loads(text)

    state_vector = obj.get("stateVector")
    requests = obj.get("requests")
    entries = obj.get("entries")

    return SyncEnvelope(
        kind=SyncEnvelopeKind(obj["kind"]),
        from_node_id=obj["fromNodeId"],
        state_vector=(
            None
            if state_vector is None
            else [
                StateVectorEntry(s["entityType"], s["maxKnownVersion"])
                for s in state_vector
            ]
        ),
        requests=(
            None
            if requests is None
            else [RequestItem(r["entityType"], r["sinceVersion"]) for r in requests]
        ),
        entries=(
            None
            if entries is None
            else [
                SyncableEntry(
                    entity_type=e["entityType"],
                    entity_id=e["entityId"],
                    version=e["version"],
                    is_tombstone=e["isTombstone"],
                    content_hash=e["contentHash"],
                    payload=e["payload"],
                    source_node_id=e["sourceNodeId"],
                    authored_at=datetime.fromisoformat(e["authoredAt"]),
                )
                for e in entries
            ]
        ),
    )


# ── The channel ───────────────────────────────────────────────────────────────


class AetherNetCompanionStateChannel(ICompanionStateChannel):
    """AetherNet-backed implementation of :class:`ICompanionStateChannel`.

    Subscribes immediately to the messaging service's inbound feed so a message
    published right after construction is never lost.

    :param messaging: Live AetherNet messaging service (injected).
    :param local_uhid: This node's mesh UHID.
    :param peer_uhids: UHIDs the channel should broadcast to. The sync engine
        converges via announce/request/push so the list does NOT need to include
        every peer on the mesh — only the user's own paired devices. An empty
        list is allowed; :meth:`send_async` is then a no-op.
    """

    def __init__(
        self,
        messaging: IMessagingService,
        local_uhid: str,
        peer_uhids: "list[str] | tuple[str, ...]",
    ) -> None:
        if messaging is None:
            raise ValueError("messaging must not be None")
        if not local_uhid or not local_uhid.strip():
            raise ValueError("local_uhid is required.")
        if peer_uhids is None:
            raise ValueError("peer_uhids must not be None")

        self._messaging = messaging
        self._local_node_id = local_uhid
        # Distinct, non-blank, preserving first-seen order (mirrors C# Distinct()).
        seen: set[str] = set()
        deduped: List[str] = []
        for p in peer_uhids:
            if p and p.strip() and p not in seen:
                seen.add(p)
                deduped.append(p)
        self._peer_uhids: List[str] = deduped

        self._handlers: List[EnvelopeHandler] = []
        self._lock = threading.Lock()
        self._disposed = False

        # Subscribe synchronously BEFORE returning so no inbound message races
        # the subscription.
        self._messaging.add_message_received(self._on_inbound)

    @property
    def local_node_id(self) -> str:
        return self._local_node_id

    async def send_async(
        self, envelope: SyncEnvelope, *, ct: Optional[object] = None
    ) -> None:
        if envelope is None:
            raise ValueError("envelope must not be None")
        if self._disposed:
            raise RuntimeError("AetherNetCompanionStateChannel is disposed")
        if len(self._peer_uhids) == 0:
            return  # no peers configured

        js = _envelope_to_json(envelope)
        plaintext = js.encode("utf-8")

        for peer in self._peer_uhids:
            mesh_message = MeshMessage(
                sender_uhid=self._local_node_id,
                recipient_uhid=peer,
                message_type=SYNC_MESSAGE_TYPE,
                priority=5,
                encrypted_content=b"",  # service encrypts the plaintext arg
                status=MessageStatus.PENDING,
                created_at=_utc_now(),
            )
            await self._messaging.send_async(mesh_message, plaintext, ct)

    def subscribe(self, handler: EnvelopeHandler) -> IDisposable:
        if handler is None:
            raise ValueError("handler must not be None")
        if self._disposed:
            raise RuntimeError("AetherNetCompanionStateChannel is disposed")
        with self._lock:
            self._handlers.append(handler)
        return _Subscription(self, handler)

    def _on_inbound(self, sender: object, msg: MeshMessage) -> None:
        # Fire-and-forget async dispatch, mirroring the C# `async void OnInbound`.
        if msg is None:
            return
        if msg.message_type != SYNC_MESSAGE_TYPE:
            return
        if msg.sender_uhid == self._local_node_id:
            return
        if msg.encrypted_content is None or len(msg.encrypted_content) == 0:
            return

        try:
            js = msg.encrypted_content.decode("utf-8")
            envelope = _envelope_from_json(js)
        except Exception:
            # Malformed payload — drop silently. Sync converges next round.
            return
        if envelope is None:
            return

        with self._lock:
            snapshot = list(self._handlers)

        # Dispatch handlers. Handlers are async; schedule them on the running loop
        # if one exists, else run each to completion synchronously. A single
        # handler's failure must not stop the others.
        import asyncio

        try:
            loop = asyncio.get_running_loop()
        except RuntimeError:
            loop = None

        for h in snapshot:
            coro = h(envelope, None)
            if loop is not None:
                loop.create_task(_guard(coro))
            else:
                try:
                    asyncio.run(coro)
                except Exception:
                    pass

    def dispose(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        self._messaging.remove_message_received(self._on_inbound)
        with self._lock:
            self._handlers.clear()

    def __enter__(self) -> "AetherNetCompanionStateChannel":
        return self

    def __exit__(self, *exc_info: object) -> None:
        self.dispose()


async def _guard(coro: Any) -> None:
    try:
        await coro
    except Exception:
        # One handler's failure must not stop the others.
        pass


class _Subscription(IDisposable):
    def __init__(
        self, owner: AetherNetCompanionStateChannel, handler: EnvelopeHandler
    ) -> None:
        self._owner = owner
        self._handler = handler

    def dispose(self) -> None:
        with self._owner._lock:
            try:
                self._owner._handlers.remove(self._handler)
            except ValueError:
                pass


def _utc_now() -> datetime:
    from datetime import timezone

    return datetime.now(timezone.utc)
