# in_memory_agent_peer_protocol.py
#
# Port of CircleAI.Agents.Peer InMemoryAgentPeerProtocol.cs (C# — the EXACT
# spec).
#
# Reference implementation of IAgentPeerProtocol that uses an in-process
# AgentBus as its transport. Multiple instances sharing one bus simulate a small
# mesh of CircleAI devices.
#
# Real implementations (BLE, Wi-Fi Direct, Aether router) live elsewhere and
# follow the same contract.
#
# ─ Porting notes ────────────────────────────────────────────────────────────
#   * The C# ctor starts an inbox pump via `Task.Run(PumpInboxAsync)`. Python
#     asyncio has no ambient loop at construction time in general, so the pump
#     is started lazily on the first awaited operation (`_ensure_started`),
#     binding to the *running* loop. Registration on the bus still happens in
#     the ctor (synchronous, loop-free), so peers are discoverable immediately.
#   * `ConcurrentDictionary<Guid, TaskCompletionSource<AgentMessage>>` (pending
#     invocations) -> dict[UUID, asyncio.Future[AgentMessage]] guarded by a
#     lock. The correlation key is the *invoke* message's id.
#   * Correlation-prefix convention: RouteInvoke prepends the 16 bytes of the
#     invoke's id (`Guid.ToByteArray()` -> `UUID.bytes`) to the response/decline
#     payload; CompletePending reads those 16 bytes back to a UUID and resolves
#     the pending future. Byte-identical framing to the C#.
#   * Discovery window (50 ms) and invoke timeout (5 s) use asyncio.sleep /
#     asyncio.wait_for.
#   * `signer` -> Callable[[bytes], bytes]; `capability_handler` ->
#     Callable[[AgentCapability, bytes], Optional[bytes]] (returning None sends a
#     Decline, mirroring the C# nullable byte[] contract).
#   * `dispose()` cancels the pump, unregisters from the bus, and completes the
#     external inbox; it is idempotent (mirrors the Interlocked.Exchange guard).

from __future__ import annotations

import asyncio
import dataclasses
import threading
from datetime import datetime, timezone
from typing import AsyncIterator, Callable, Dict, List, Optional
from uuid import UUID, uuid4

from .agent_bus import AgentBus
from .agent_invocation_exception import AgentInvocationException
from .agent_message import AgentMessage, AgentMessageKind
from .agent_peer_protocol import IAgentPeerProtocol
from .peer_agent import AgentCapability, PeerAgent

# Match the C# static defaults exactly.
_DEFAULT_DISCOVERY_WINDOW_S = 0.050  # TimeSpan.FromMilliseconds(50)
_DEFAULT_INVOKE_TIMEOUT_S = 5.0      # TimeSpan.FromSeconds(5)

# Private completion sentinel for the external inbox queue.
_INBOX_COMPLETE = object()

Signer = Callable[[bytes], bytes]
CapabilityHandler = Callable[[AgentCapability, bytes], Optional[bytes]]


class InMemoryAgentPeerProtocol(IAgentPeerProtocol):
    """In-memory reference implementation of :class:`IAgentPeerProtocol`.

    Backed by an :class:`AgentBus` so multiple instances can simulate a mesh of
    CircleAI peers in tests and samples. Mirrors
    ``CircleAI.Agents.Peer.InMemoryAgentPeerProtocol``.
    """

    component_name = "InMemoryAgentPeerProtocol"

    def __init__(
        self,
        own_uhid: str,
        bus: AgentBus,
        own_capabilities: List[AgentCapability],
        own_public_key: bytes,
        signer: Optional[Signer] = None,
        capability_handler: Optional[CapabilityHandler] = None,
    ) -> None:
        if own_uhid is None or own_uhid.strip() == "":
            raise ValueError("own_uhid must be non-empty")
        if bus is None:
            raise ValueError("bus must not be None")
        if own_capabilities is None:
            raise ValueError("own_capabilities must not be None")
        if own_public_key is None:
            raise ValueError("own_public_key must not be None")

        self._own_uhid = own_uhid
        self._bus = bus
        self._own_capabilities = own_capabilities
        self._own_public_key = own_public_key
        self._signer = signer
        self._capability_handler = capability_handler

        self._last_seen: Dict[str, datetime] = {}
        self._pending: Dict[UUID, "asyncio.Future[AgentMessage]"] = {}
        self._pending_lock = threading.Lock()

        self._external_inbox: "asyncio.Queue[object]" = asyncio.Queue()
        self._pump_task: Optional["asyncio.Task[None]"] = None
        self._started = False
        self._disposed = False
        self._start_lock = threading.Lock()

        # Register on the bus immediately so peers are discoverable before the
        # first awaited operation (matches the C# ctor registration).
        self._bus.register(
            PeerAgent(
                id=uuid4(),
                uhid_identity_id=own_uhid,
                display_name=own_uhid,
                capabilities=self._own_capabilities,
                public_key_der=self._own_public_key,
                current_transport_id="in-memory",
                last_seen_at=datetime.now(timezone.utc),
            )
        )

        # Start the inbox pump eagerly when a running loop is present — mirrors
        # the C# ctor's `Task.Run(PumpInboxAsync)`, so a peer that only ever
        # RECEIVES (never awaits an outbound op of its own) still services its
        # inbox. When no loop is running at construction time, the pump starts
        # lazily on the first awaited operation instead (see _ensure_started).
        try:
            asyncio.get_running_loop()
        except RuntimeError:
            pass
        else:
            self._ensure_started()

    @property
    def own_uhid(self) -> str:
        """The UHID identity owned by this agent."""
        return self._own_uhid

    # ── Lifecycle ───────────────────────────────────────────────────────────

    def _ensure_started(self) -> None:
        """Start the inbox pump on first use, binding to the running loop."""
        with self._start_lock:
            if self._started or self._disposed:
                return
            self._started = True
            self._pump_task = asyncio.ensure_future(self._pump_inbox_async())

    def dispose(self) -> None:
        """Tear down the protocol, unregister from the bus, and stop the inbox
        pump. Idempotent (mirrors the C# ``Interlocked.Exchange`` guard).
        """
        with self._start_lock:
            if self._disposed:
                return
            self._disposed = True
            pump = self._pump_task

        if pump is not None:
            pump.cancel()
        self._bus.unregister(self._own_uhid)
        self._external_inbox.put_nowait(_INBOX_COMPLETE)

    def __enter__(self) -> "InMemoryAgentPeerProtocol":
        return self

    def __exit__(self, *exc: object) -> None:
        self.dispose()

    # ── IAgentPeerProtocol ──────────────────────────────────────────────────

    async def discover_peers_async(
        self, ct: Optional[object] = None
    ) -> List[PeerAgent]:
        self._ensure_started()

        # Broadcast a Discover so peers can refresh their view of us.
        announcement = AgentMessage.create(
            AgentMessageKind.DISCOVER,
            self._own_uhid,
            "*",
            "application/json",
            payload=b"",
            signature=self._sign(b""),
        )
        self._bus.send(announcement)

        # Brief listen window so any registered peer's responses can land.
        try:
            await asyncio.sleep(_DEFAULT_DISCOVERY_WINDOW_S)
        except asyncio.CancelledError:
            # Window cancelled — return whatever we can see now.
            pass

        return [
            self._with_last_seen(p)
            for p in self._bus.registered_peers
            if p.uhid_identity_id != self._own_uhid
        ]

    async def greet_async(
        self, target_uhid: str, ct: Optional[object] = None
    ) -> Optional[PeerAgent]:
        if target_uhid is None or target_uhid.strip() == "":
            raise ValueError("target_uhid must be non-empty")
        self._ensure_started()

        found, peer = self._bus.try_get_peer(target_uhid)
        if not found or peer is None:
            return None

        greet = AgentMessage.create(
            AgentMessageKind.GREET,
            self._own_uhid,
            target_uhid,
            "application/json",
            payload=b"",
            signature=self._sign(b""),
        )
        self._bus.send(greet)

        return self._with_last_seen(peer)

    async def query_capabilities_async(
        self, target_uhid: str, ct: Optional[object] = None
    ) -> List[AgentCapability]:
        if target_uhid is None or target_uhid.strip() == "":
            raise ValueError("target_uhid must be non-empty")
        self._ensure_started()

        found, peer = self._bus.try_get_peer(target_uhid)
        if not found or peer is None:
            return []
        return list(peer.capabilities)

    async def invoke_async(
        self,
        target_uhid: str,
        capability: AgentCapability,
        request_payload: bytes,
        ct: Optional[object] = None,
    ) -> AgentMessage:
        if target_uhid is None or target_uhid.strip() == "":
            raise ValueError("target_uhid must be non-empty")
        if capability is None:
            raise ValueError("capability must not be None")
        if request_payload is None:
            raise ValueError("request_payload must not be None")
        self._ensure_started()

        found, _ = self._bus.try_get_peer(target_uhid)
        if not found:
            raise AgentInvocationException(
                f"Peer '{target_uhid}' is not reachable on the current transport.",
                target_uhid,
            )

        invoke = AgentMessage.create(
            AgentMessageKind.INVOKE,
            self._own_uhid,
            target_uhid,
            "application/octet-stream",
            payload=request_payload,
            signature=self._sign(request_payload),
        )

        loop = asyncio.get_running_loop()
        future: "asyncio.Future[AgentMessage]" = loop.create_future()
        with self._pending_lock:
            self._pending[invoke.id] = future

        self._bus.send(invoke)

        try:
            reply = await asyncio.wait_for(future, timeout=_DEFAULT_INVOKE_TIMEOUT_S)
        except asyncio.TimeoutError:
            with self._pending_lock:
                self._pending.pop(invoke.id, None)
            raise AgentInvocationException(
                f"Invocation of '{capability.name}' on peer '{target_uhid}' "
                f"timed out.",
                target_uhid,
            )
        except asyncio.CancelledError:
            with self._pending_lock:
                self._pending.pop(invoke.id, None)
            raise

        with self._pending_lock:
            self._pending.pop(invoke.id, None)

        if reply.kind == AgentMessageKind.DECLINE:
            raise AgentInvocationException(
                f"Peer '{target_uhid}' declined '{capability.name}'.",
                target_uhid,
                reply,
            )

        return reply

    async def stream_inbox_async(
        self, ct: Optional[object] = None
    ) -> AsyncIterator[AgentMessage]:
        self._ensure_started()
        while True:
            item = await self._external_inbox.get()
            if item is _INBOX_COMPLETE:
                return
            yield item  # type: ignore[misc]

    # ── Inbox pump + routing ────────────────────────────────────────────────

    async def _pump_inbox_async(self) -> None:
        try:
            async for message in self._bus.receive(self._own_uhid):
                self._last_seen[message.from_uhid] = message.sent_at
                await self._handle_incoming_async(message)
        except asyncio.CancelledError:
            # Shutdown path.
            return

    async def _handle_incoming_async(self, message: AgentMessage) -> None:
        if message.kind in (AgentMessageKind.RESPONSE, AgentMessageKind.DECLINE):
            self._complete_pending(message)
        elif message.kind == AgentMessageKind.INVOKE:
            await self._route_invoke_async(message)

        # Every inbound message is also surfaced to external consumers.
        self._external_inbox.put_nowait(message)

    def _complete_pending(self, message: AgentMessage) -> None:
        # Convention: Response/Decline carry the original Invoke's id in the
        # first 16 bytes of the payload when generated by _route_invoke_async.
        if len(message.payload) < 16:
            return
        correlation_id = UUID(bytes=bytes(message.payload[:16]))
        with self._pending_lock:
            future = self._pending.get(correlation_id)
        if future is not None and not future.done():
            future.set_result(message)

    async def _route_invoke_async(self, invoke: AgentMessage) -> None:
        if self._capability_handler is None:
            return

        # Best-effort: a real implementation negotiates which capability is
        # being invoked by carrying its name in the payload. The in-memory mock
        # simply hands the first advertised capability to the handler.
        capability = (
            self._own_capabilities[0]
            if len(self._own_capabilities) > 0
            else AgentCapability("unknown", "0.0.0", 0, "SDPKT")
        )

        try:
            result = self._capability_handler(capability, invoke.payload)
        except Exception:
            result = None

        correlation_prefix = invoke.id.bytes  # 16 bytes, == Guid.ToByteArray()

        if result is None:
            decline = AgentMessage.create(
                AgentMessageKind.DECLINE,
                self._own_uhid,
                invoke.from_uhid,
                "application/octet-stream",
                payload=correlation_prefix,
                signature=self._sign(correlation_prefix),
            )
            self._bus.send(decline)
            return

        response_payload = correlation_prefix + bytes(result)
        response = AgentMessage.create(
            AgentMessageKind.RESPONSE,
            self._own_uhid,
            invoke.from_uhid,
            "application/octet-stream",
            payload=response_payload,
            signature=self._sign(response_payload),
        )
        self._bus.send(response)

    # ── Helpers ─────────────────────────────────────────────────────────────

    def _sign(self, data: bytes) -> bytes:
        return b"" if self._signer is None else self._signer(data)

    def _with_last_seen(self, peer: PeerAgent) -> PeerAgent:
        last_seen = self._last_seen.get(peer.uhid_identity_id, peer.last_seen_at)
        return dataclasses.replace(peer, last_seen_at=last_seen)
