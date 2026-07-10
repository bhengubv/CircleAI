"""circle_ai.security — port of the CircleAI.Security assembly.

Security peer-intelligence pipeline. Public surface mirrors the C#
CircleAI.Security assembly (C# is the exact spec):

  * local runtime immune system:
      ThreatVector, AnomalySignal, RedactedEvidenceJsonConverter,
      SecurityCheckpoint, SecurityResponse (+ SecurityResponseKind),
      ISecurityWatchdog / DefaultSecurityWatchdog,
      IAnomalyEventDispatcher / DefaultAnomalyEventDispatcher
      (+ AnomalyDispatchOutcome, AnomalyDispatchResult),
      UhidKeyRing,
  * transport-agnostic peer security:
      PeerSecurityEventKind, PeerThreatLevel, PeerDirectiveKind,
      PeerSecurityEvent, PeerDirective, PeerTrustScoreUpdate,
      PeerSecurityPosture, PeerNetworkHealthReport, PeerThreatAssessment,
      PeerRoutingAdvice,
      IPeerDirectiveConsumer, IPeerSecurityLayer, IPeerIntelligence,
      IPeerSecurityEventFeed, IDisposable,
      SecurityOptions, ThreatDetector, NodeTrustRegistry / NodeTrustEntry,
      DirectivePublisher,
      SecurityLayerService (a.k.a. AISecurityLayerService),
      PeerIntelligenceService (a.k.a. AetherIntelligenceService).

The dead flat ``circle_ai/security.py`` shadow is superseded by this package.
"""
from __future__ import annotations

from .anomaly_event_dispatcher import (
    AnomalyDispatchOutcome,
    AnomalyDispatchResult,
    DefaultAnomalyEventDispatcher,
    IAnomalyEventDispatcher,
)
from .anomaly_signal import AnomalySignal
from .directive_publisher import DirectivePublisher
from .node_trust_registry import NodeTrustEntry, NodeTrustRegistry
from .peer_intelligence_service import PeerIntelligenceService
from .peer_security_types import (
    IDisposable,
    IPeerDirectiveConsumer,
    IPeerIntelligence,
    IPeerSecurityEventFeed,
    IPeerSecurityLayer,
    PeerDirective,
    PeerDirectiveKind,
    PeerEventHandler,
    PeerNetworkHealthReport,
    PeerRoutingAdvice,
    PeerSecurityEvent,
    PeerSecurityEventKind,
    PeerSecurityPosture,
    PeerThreatAssessment,
    PeerThreatLevel,
    PeerTrustScoreUpdate,
)
from .redacted_evidence_json_converter import RedactedEvidenceJsonConverter
from .security_checkpoint import SecurityCheckpoint
from .security_layer_service import SecurityLayerService
from .security_options import SecurityOptions
from .security_response import SecurityResponse, SecurityResponseKind
from .security_watchdog import DefaultSecurityWatchdog, ISecurityWatchdog
from .threat_detector import ThreatDetector
from .threat_vector import ThreatVector
from .uhid_key_ring import UhidKeyRing

# ── C#-filename aliases ───────────────────────────────────────────────────────
# The C# tree names two files AISecurityLayerService.cs / AetherIntelligenceService.cs
# but the public types they declare are SecurityLayerService / PeerIntelligenceService.
# Expose the file-derived names as aliases so either import resolves.
AISecurityLayerService = SecurityLayerService
AetherIntelligenceService = PeerIntelligenceService

__all__ = [
    # local runtime immune system
    "ThreatVector",
    "AnomalySignal",
    "RedactedEvidenceJsonConverter",
    "SecurityCheckpoint",
    "SecurityResponse",
    "SecurityResponseKind",
    "ISecurityWatchdog",
    "DefaultSecurityWatchdog",
    "IAnomalyEventDispatcher",
    "DefaultAnomalyEventDispatcher",
    "AnomalyDispatchOutcome",
    "AnomalyDispatchResult",
    "UhidKeyRing",
    # enums
    "PeerSecurityEventKind",
    "PeerThreatLevel",
    "PeerDirectiveKind",
    # records
    "PeerSecurityEvent",
    "PeerDirective",
    "PeerTrustScoreUpdate",
    "PeerSecurityPosture",
    "PeerNetworkHealthReport",
    "PeerThreatAssessment",
    "PeerRoutingAdvice",
    # interfaces
    "IPeerDirectiveConsumer",
    "IPeerSecurityLayer",
    "IPeerIntelligence",
    "IPeerSecurityEventFeed",
    "IDisposable",
    "PeerEventHandler",
    # options + logic + services
    "SecurityOptions",
    "ThreatDetector",
    "NodeTrustRegistry",
    "NodeTrustEntry",
    "DirectivePublisher",
    "SecurityLayerService",
    "PeerIntelligenceService",
    "AISecurityLayerService",
    "AetherIntelligenceService",
]
