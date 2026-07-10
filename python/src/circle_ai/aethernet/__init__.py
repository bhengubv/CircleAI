"""circle_ai.aethernet — port of the CircleAI.AetherNet assembly.

Binds CircleAI's Aether contracts to the live AetherNet mesh runtime, plus the
RT-12 mesh-capability discovery registry (C# is the exact spec).

Public surface:

  * Mesh capability discovery (RT-12 v1):
      MeshCapabilityAdvertisement,
      IMeshCapabilityRegistry, InMemoryMeshCapabilityRegistry,
      IMeshCapabilityBroadcaster, NullMeshCapabilityBroadcaster.
  * CircleAI <-> AetherNet bridge adapters:
      AetherNetContextAdapter        (IAetherContext over the mesh runtime),
      AetherNetTelemetryAdapter      (IAetherTelemetry over the mesh bus),
      AetherNetDirectiveSink         (CircleAI -> mesh directive forwarder),
      AetherNetInboundDirectiveBridge(mesh -> CircleAI directive bridge),
      CircleAiAetherNetAiProvider    (CircleAI brain into AetherNet's AI seat),
      AetherNetCompanionStateChannel (sync-envelope transport over messaging),
      EventTranslator.
  * AetherNet-runtime seam (the injected external dependency, with in-memory
    fakes) — AetherNet event vocabulary, telemetry bus, directive consumer, AI
    provider seat, and messaging service. See circle_ai.aethernet.extensibility.

DI wiring (C# ServiceCollectionExtensions.AddCircleAiAetherNetAdapter) is not
ported — the Python tree has no DI container; wire the adapters via their
constructors instead.
"""
from __future__ import annotations

from .adapters import (
    AetherNetContextAdapter,
    AetherNetDirectiveSink,
    AetherNetInboundDirectiveBridge,
    AetherNetTelemetryAdapter,
    CircleAiAetherNetAiProvider,
)
from .companion_state_channel import (
    SYNC_MESSAGE_TYPE,
    AetherNetCompanionStateChannel,
)
from .event_translator import EventTranslator
from .extensibility import (
    CURRENT_PROTOCOL_VERSION,
    AetherNetNetworkEvent,
    AetherNetNetworkEventKind,
    AetherNetNodeEvent,
    AetherNetNodeEventKind,
    AetherNetNodeHealth,
    AetherNetRouteEvent,
    AetherNetRouteEventKind,
    AetherNetSecurityDirective,
    AetherNetSecurityDirectiveKind,
    AetherNetSecurityEvent,
    AetherNetSecurityEventKind,
    AetherNetThreatLevel,
    AetherNetTransportEvent,
    AetherNetTransportEventKind,
    AetherNetTransportKind,
    AiNetworkHealthReport,
    AiRouteSuggestion,
    AiThreatLevel,
    IAetherNetAiProvider,
    IAetherNetSecurityDirectiveConsumer,
    IAetherNetTelemetry,
    IAetherNetTelemetryObserver,
    IMessagingService,
    InMemoryAetherNetTelemetry,
    InMemoryMessagingService,
    MeshMessage,
    MeshPacket,
    MessageStatus,
    RecordingAetherNetDirectiveConsumer,
)
from .mesh_capability_registry import (
    IMeshCapabilityBroadcaster,
    IMeshCapabilityRegistry,
    InMemoryMeshCapabilityRegistry,
    MeshCapabilityAdvertisement,
    NullMeshCapabilityBroadcaster,
)

__all__ = [
    # Mesh capability discovery
    "MeshCapabilityAdvertisement",
    "IMeshCapabilityRegistry",
    "InMemoryMeshCapabilityRegistry",
    "IMeshCapabilityBroadcaster",
    "NullMeshCapabilityBroadcaster",
    # Bridge adapters
    "AetherNetContextAdapter",
    "AetherNetTelemetryAdapter",
    "AetherNetDirectiveSink",
    "AetherNetInboundDirectiveBridge",
    "CircleAiAetherNetAiProvider",
    "AetherNetCompanionStateChannel",
    "SYNC_MESSAGE_TYPE",
    "EventTranslator",
    # AetherNet-runtime seam
    "CURRENT_PROTOCOL_VERSION",
    "AetherNetNodeEvent",
    "AetherNetNodeEventKind",
    "AetherNetNodeHealth",
    "AetherNetTransportEvent",
    "AetherNetTransportEventKind",
    "AetherNetTransportKind",
    "AetherNetRouteEvent",
    "AetherNetRouteEventKind",
    "AetherNetSecurityEvent",
    "AetherNetSecurityEventKind",
    "AetherNetThreatLevel",
    "AetherNetNetworkEvent",
    "AetherNetNetworkEventKind",
    "AetherNetSecurityDirective",
    "AetherNetSecurityDirectiveKind",
    "IAetherNetSecurityDirectiveConsumer",
    "RecordingAetherNetDirectiveConsumer",
    "IAetherNetTelemetry",
    "IAetherNetTelemetryObserver",
    "InMemoryAetherNetTelemetry",
    "IAetherNetAiProvider",
    "AiThreatLevel",
    "AiRouteSuggestion",
    "AiNetworkHealthReport",
    "MeshPacket",
    "IMessagingService",
    "InMemoryMessagingService",
    "MeshMessage",
    "MessageStatus",
]
