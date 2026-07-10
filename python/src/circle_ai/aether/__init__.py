"""circle_ai.aether — port of the CircleAI.Aether assembly.

The five contracts that define the Aether <-> BhenguAI boundary (C# is the exact
spec). Aether publishes telemetry; BhenguAI subscribes, reasons, and publishes
security directives back through a one-way sink. Aether never calls into
BhenguAI.

Public surface:

  * Contract 1 — Telemetry:
      IAetherTelemetry, IAetherTelemetryObserver, IDisposable,
      NullAetherTelemetry, InMemoryAetherTelemetry,
      and the five event families (+ their kind enums + AetherThreatLevel):
      AetherNodeEvent/Kind, AetherNodeHealth,
      AetherTransportEvent/Kind, AetherTransportKind,
      AetherRouteEvent/Kind,
      AetherSecurityEvent/Kind,
      AetherNetworkEvent/Kind.
  * Contract 2 — Presence and Capability:
      AetherInstallLevel, AetherVersion, IAetherContext, InMemoryAetherContext.
  * Contract 3 — Intelligence Output:
      NetworkHealthReport, ThreatAssessment, RoutingAdvice, TrustScoreUpdate,
      IAetherIntelligence, InMemoryAetherIntelligence.
  * Contract 4 — Security Layer:
      SecurityDirectiveKind, SecurityDirective, SecurityPosture,
      ISecurityDirectiveConsumer, IAISecurityLayer, InMemoryAISecurityLayer.
  * Contract 5 — Auth Challenge:
      AuthChallengeReason, AuthMethod, AuthChallengeResult,
      IAuthChallenge, InMemoryAuthChallenge.

The dead flat ``circle_ai/aether.py`` shadow (if any) is superseded by this
package.
"""
from __future__ import annotations

from .auth_challenge import (
    AuthChallengeReason,
    AuthChallengeResult,
    AuthMethod,
    IAuthChallenge,
    InMemoryAuthChallenge,
)
from .context import (
    AetherInstallLevel,
    AetherVersion,
    IAetherContext,
    InMemoryAetherContext,
)
from .events import (
    AetherNetworkEvent,
    AetherNetworkEventKind,
    AetherNodeEvent,
    AetherNodeEventKind,
    AetherNodeHealth,
    AetherRouteEvent,
    AetherRouteEventKind,
    AetherSecurityEvent,
    AetherSecurityEventKind,
    AetherThreatLevel,
    AetherTransportEvent,
    AetherTransportEventKind,
    AetherTransportKind,
)
from .intelligence import (
    IAetherIntelligence,
    InMemoryAetherIntelligence,
    NetworkHealthReport,
    RoutingAdvice,
    ThreatAssessment,
    TrustScoreUpdate,
)
from .security_layer import (
    IAISecurityLayer,
    ISecurityDirectiveConsumer,
    InMemoryAISecurityLayer,
    SecurityDirective,
    SecurityDirectiveKind,
    SecurityPosture,
)
from .telemetry import (
    IAetherTelemetry,
    IAetherTelemetryObserver,
    IDisposable,
    InMemoryAetherTelemetry,
    NullAetherTelemetry,
)

__all__ = [
    # Contract 1 — Telemetry
    "IAetherTelemetry",
    "IAetherTelemetryObserver",
    "IDisposable",
    "NullAetherTelemetry",
    "InMemoryAetherTelemetry",
    "AetherNodeEvent",
    "AetherNodeEventKind",
    "AetherNodeHealth",
    "AetherTransportEvent",
    "AetherTransportEventKind",
    "AetherTransportKind",
    "AetherRouteEvent",
    "AetherRouteEventKind",
    "AetherSecurityEvent",
    "AetherSecurityEventKind",
    "AetherThreatLevel",
    "AetherNetworkEvent",
    "AetherNetworkEventKind",
    # Contract 2 — Presence and Capability
    "AetherInstallLevel",
    "AetherVersion",
    "IAetherContext",
    "InMemoryAetherContext",
    # Contract 3 — Intelligence Output
    "NetworkHealthReport",
    "ThreatAssessment",
    "RoutingAdvice",
    "TrustScoreUpdate",
    "IAetherIntelligence",
    "InMemoryAetherIntelligence",
    # Contract 4 — Security Layer
    "SecurityDirectiveKind",
    "SecurityDirective",
    "SecurityPosture",
    "ISecurityDirectiveConsumer",
    "IAISecurityLayer",
    "InMemoryAISecurityLayer",
    # Contract 5 — Auth Challenge
    "AuthChallengeReason",
    "AuthMethod",
    "AuthChallengeResult",
    "IAuthChallenge",
    "InMemoryAuthChallenge",
]
