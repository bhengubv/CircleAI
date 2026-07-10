"""circle_ai.security_aethernet — port of the CircleAI.Security.AetherNet assembly.

The AetherNet-specific security bindings: they connect the Aether mesh vocabulary
(circle_ai.aether) to the transport-agnostic security layer (circle_ai.security),
and expose the mesh-directive block pipeline that CircleAI features consult before
serving a request (C# is the exact spec).

Public surface:

  * AetherMapper                 — Aether <-> Peer type translation helpers.
  * AetherSecurityBridge         — IAISecurityLayer over the transport-agnostic
                                   SecurityLayerService (telemetry -> peer events).
  * AetherIntelligenceAdapter    — IAetherIntelligence over PeerIntelligenceService.
  * MeshDirectiveStore           — ISecurityDirectiveConsumer sink + query view,
                                   lazy-expiry, Release lifts blocks.
  * MeshSecurityGate             — read-only "is this id blocked?" gate,
                                   GateDecision + MeshSecurityBlockedException.
  * MeshGatedCompanionSession    — companion-session decorator that enforces the
                                   gate on every message-producing call.

DI wiring (C# ServiceCollectionExtensions.AddCircleAiMeshSecurity) is not ported —
the Python tree has no DI container; wire via constructors instead. The store is
the directive sink; the gate is the read-only query view over it.
"""
from __future__ import annotations

from .aether_intelligence_adapter import AetherIntelligenceAdapter
from .aether_mapper import AetherMapper
from .aether_security_bridge import AetherSecurityBridge
from .mesh_directive_store import MeshDirectiveStore
from .mesh_gated_companion_session import MeshGatedCompanionSession
from .mesh_security_gate import (
    GateDecision,
    MeshSecurityBlockedException,
    MeshSecurityGate,
)

__all__ = [
    "AetherMapper",
    "AetherSecurityBridge",
    "AetherIntelligenceAdapter",
    "MeshDirectiveStore",
    "MeshSecurityGate",
    "GateDecision",
    "MeshSecurityBlockedException",
    "MeshGatedCompanionSession",
]
