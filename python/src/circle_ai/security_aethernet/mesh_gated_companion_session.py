# mesh_gated_companion_session.py
#
# Port of CircleAI.Security.AetherNet.MeshGatedCompanionSession (C# — the spec).
#
# Decorator over a companion session that consults MeshSecurityGate before EVERY
# message-producing call (send_async, stream_async, agent_async). When the gate
# says the session's identity_id is blocked by an active mesh directive, the
# decorator raises MeshSecurityBlockedException instead of reaching the
# underlying generator.
#
# This is the "chat path consults mesh" wire-up. The decorator never modifies or
# impersonates the inner session; it strictly adds the gate check.
#
# Context / history / feedback are diagnostic / metadata calls and pass through
# UNGATED — gating them would prevent a blocked user from even seeing their own
# state, which goes beyond "stop the chat" into "punish".

from __future__ import annotations

from typing import Any, AsyncGenerator, Optional

from ..companion.companion_types import (
    CompanionContext,
    CompanionTurn,
    InterfaceKind,
)
from ..companion.session import CompanionSession
from .mesh_security_gate import MeshSecurityGate


class MeshGatedCompanionSession:
    """Wraps an inner companion session and enforces the mesh's "block this user"
    directives via :class:`MeshSecurityGate` on every message-producing call.

    The inner object need only present the companion-session surface
    (:class:`circle_ai.companion.session.CompanionSession` or a compatible duck
    type): ``session_id``, ``identity_id``, ``interface``, ``history``,
    ``on_proactive_message_ready``, ``send_async``, ``stream_async``,
    ``agent_async``, ``get_context``, ``refresh_context_async``,
    ``signal_feedback_async``.
    """

    def __init__(self, inner: CompanionSession, gate: MeshSecurityGate) -> None:
        if inner is None:
            raise ValueError("inner must not be None")
        if gate is None:
            raise ValueError("gate must not be None")
        self._inner = inner
        self._gate = gate

    # ── Pass-through identity / properties ────────────────────────────────────

    @property
    def session_id(self) -> str:
        return self._inner.session_id

    @property
    def identity_id(self) -> str:
        return self._inner.identity_id

    @property
    def interface(self) -> InterfaceKind:
        return self._inner.interface

    @property
    def history(self) -> "list[CompanionTurn]":
        return self._inner.history

    @property
    def on_proactive_message_ready(self) -> Any:
        return self._inner.on_proactive_message_ready

    @on_proactive_message_ready.setter
    def on_proactive_message_ready(self, value: Any) -> None:
        self._inner.on_proactive_message_ready = value

    # ── Guarded entry points ──────────────────────────────────────────────────

    async def send_async(self, message: str, *, ct: Optional[object] = None) -> str:
        self._gate.enforce(self.identity_id)
        return await self._inner.send_async(message, ct=ct)

    async def stream_async(
        self, message: str, *, ct: Optional[object] = None
    ) -> AsyncGenerator[str, None]:
        self._gate.enforce(self.identity_id)
        async for chunk in self._inner.stream_async(message, ct=ct):
            yield chunk

    async def agent_async(
        self, instruction: str, *, ct: Optional[object] = None
    ) -> str:
        self._gate.enforce(self.identity_id)
        return await self._inner.agent_async(instruction, ct=ct)

    # ── Unguarded pass-through ────────────────────────────────────────────────

    def get_context(self) -> CompanionContext:
        return self._inner.get_context()

    async def refresh_context_async(self, *, ct: Optional[object] = None) -> None:
        return await self._inner.refresh_context_async(ct=ct)

    async def signal_feedback_async(
        self, positive: bool, note: Optional[str] = None, *, ct: Optional[object] = None
    ) -> None:
        return await self._inner.signal_feedback_async(positive, note, ct=ct)

    async def dispose_async(self) -> None:
        # Forward disposal only if the inner session supports it (the Python
        # CompanionSession has no DisposeAsync; a duck-typed session may).
        inner_dispose = getattr(self._inner, "dispose_async", None)
        if inner_dispose is not None:
            await inner_dispose()
