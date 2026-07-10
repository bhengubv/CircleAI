# mesh_security_gate.py
#
# Port of CircleAI.Security.AetherNet.MeshSecurityGate (C# — the EXACT spec).
#
# Read-only fast-path query surface over MeshDirectiveStore. The gate is the type
# CircleAI features inject when they want to consult mesh-issued directives before
# serving a request — e.g. chat refusing a blocked user.
#
# Separating the gate from the store lets callers depend on the query view
# without exposing the directive-write surface (the store) to every consumer.

from __future__ import annotations

from dataclasses import dataclass

from .mesh_directive_store import MeshDirectiveStore


@dataclass(frozen=True, slots=True)
class GateDecision:
    """Decision returned from :meth:`MeshSecurityGate.decide`."""

    is_blocked: bool
    reason: str

    @staticmethod
    def allowed() -> "GateDecision":
        """Convenience: allow with no reason text."""
        return _ALLOWED


_ALLOWED = GateDecision(False, "")


class MeshSecurityBlockedException(Exception):
    """Raised by :meth:`MeshSecurityGate.enforce` when the mesh has issued a
    block directive against the requesting id.
    """

    def __init__(self, blocked_id: str, reason: str) -> None:
        super().__init__(f"Mesh has blocked '{blocked_id}': {reason}")
        self.blocked_id = blocked_id


class MeshSecurityGate:
    """Query surface for asking "is this user/node currently blocked by the
    mesh?" Backed by a :class:`MeshDirectiveStore`.
    """

    def __init__(self, store: MeshDirectiveStore) -> None:
        if store is None:
            raise ValueError("store must not be None")
        self._store = store

    def decide(self, user_or_node_id: str) -> GateDecision:
        """Returns a single-shot decision for the given user/node id. The reason
        text comes from the most recent active block directive.
        """
        if not user_or_node_id or not user_or_node_id.strip():
            return GateDecision.allowed()
        blocked, reason = self._store.is_blocked(user_or_node_id)
        return GateDecision(True, reason) if blocked else GateDecision.allowed()

    def enforce(self, user_or_node_id: str) -> None:
        """Raises :class:`MeshSecurityBlockedException` when a request from a
        blocked id would proceed. Use in service code that wants a one-line guard
        at the top of a method.
        """
        decision = self.decide(user_or_node_id)
        if decision.is_blocked:
            raise MeshSecurityBlockedException(user_or_node_id, decision.reason)
