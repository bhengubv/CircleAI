# adapters.py
#
# Ports of the CircleAI <-> AetherNet bridge adapters (C# — the EXACT spec):
#   AetherNetContextAdapter.cs          -> AetherNetContextAdapter
#   AetherNetTelemetryAdapter.cs        -> AetherNetTelemetryAdapter (+ ObserverBridge)
#   AetherNetDirectiveSink.cs           -> AetherNetDirectiveSink
#   AetherNetInboundDirectiveBridge.cs  -> AetherNetInboundDirectiveBridge
#   CircleAiAetherNetAiProvider.cs      -> CircleAiAetherNetAiProvider
#
# Each bridges CircleAI.Aether contracts to the AetherNet runtime seam
# (circle_ai.aethernet.extensibility). The translation is the load-bearing
# behaviour; the runtime itself is injected.

from __future__ import annotations

from typing import List, Mapping, Optional, Sequence

from ..aether.context import (
    AetherInstallLevel,
    AetherVersion,
    IAetherContext,
    VersionLike,
)
from ..aether.intelligence import IAetherIntelligence
from ..aether.security_layer import (
    ISecurityDirectiveConsumer,
    SecurityDirective,
)
from ..aether.telemetry import (
    IAetherTelemetry,
    IAetherTelemetryObserver,
    IDisposable,
)
from .event_translator import EventTranslator
from .extensibility import (
    AiNetworkHealthReport,
    AiRouteSuggestion,
    AiThreatLevel,
    AetherNetNetworkEvent,
    AetherNetNodeEvent,
    AetherNetRouteEvent,
    AetherNetSecurityDirective,
    AetherNetSecurityEvent,
    AetherNetTransportEvent,
    CURRENT_PROTOCOL_VERSION,
    IAetherNetAiProvider,
    IAetherNetSecurityDirectiveConsumer,
    IAetherNetTelemetry,
    IAetherNetTelemetryObserver,
    MeshPacket,
)
from ..aether.events import AetherThreatLevel


# ── AetherNetContextAdapter ───────────────────────────────────────────────────


class AetherNetContextAdapter(IAetherContext):
    """Reports the presence and capability of AetherNet to CircleAI consumers via
    the :class:`IAetherContext` contract.

    Install level is fixed at App for this adapter — AetherNet runs as an
    in-process library; OS-managed instances are surfaced by a separate
    platform-specific adapter.

    :param minimum_required: Minimum AetherNet protocol version the consuming app
        requires. When None, any installed version is considered sufficient.
    :param is_enabled: Whether AetherNet is currently enabled in this process.
        Defaults to True.
    :param protocol_version: The AetherNet wire-protocol version to report as the
        runtime version. Defaults to the runtime's
        ``ProtocolConstants.CurrentProtocolVersion`` (injected here as
        :data:`CURRENT_PROTOCOL_VERSION`), matching the C#
        ``new Version(CurrentProtocolVersion, 0, 0, 0)``.
    """

    def __init__(
        self,
        minimum_required: VersionLike = None,
        is_enabled: bool = True,
        protocol_version: int = CURRENT_PROTOCOL_VERSION,
    ) -> None:
        self._minimum_required = (
            AetherVersion.parse(minimum_required)
            if isinstance(minimum_required, str)
            else minimum_required
        )
        self._is_enabled = is_enabled
        self._runtime_version = AetherVersion(protocol_version, 0, 0, 0)

    @property
    def install_level(self) -> AetherInstallLevel:
        return AetherInstallLevel.APP

    @property
    def is_available(self) -> bool:
        return True

    @property
    def runtime_version(self) -> Optional[AetherVersion]:
        return self._runtime_version

    @property
    def minimum_required(self) -> Optional[AetherVersion]:
        return self._minimum_required

    @property
    def is_sufficient(self) -> bool:
        return self._minimum_required is None or (
            self._runtime_version is not None
            and self._runtime_version >= self._minimum_required
        )

    @property
    def requires_auth(self) -> bool:
        return self.install_level is AetherInstallLevel.OS

    @property
    def is_enabled(self) -> bool:
        return self._is_enabled


# ── AetherNetTelemetryAdapter ─────────────────────────────────────────────────


class AetherNetTelemetryAdapter(IAetherTelemetry):
    """Bridges AetherNet's telemetry bus to CircleAI's :class:`IAetherTelemetry`
    contract. Each subscriber gets an independent AetherNet subscription, so
    disposal cleans up exactly one downstream handle.
    """

    def __init__(self, mesh_telemetry: IAetherNetTelemetry) -> None:
        if mesh_telemetry is None:
            raise ValueError("mesh_telemetry must not be None")
        self._mesh_telemetry = mesh_telemetry

    def subscribe(self, observer: IAetherTelemetryObserver) -> IDisposable:
        if observer is None:
            raise ValueError("observer must not be None")
        bridge = _ObserverBridge(observer)
        return self._mesh_telemetry.subscribe(bridge)


class _ObserverBridge(IAetherNetTelemetryObserver):
    """Receives AetherNet events and forwards them to a CircleAI observer after
    type translation.
    """

    def __init__(self, target: IAetherTelemetryObserver) -> None:
        self._target = target

    def on_node_event(self, e: AetherNetNodeEvent) -> None:
        self._target.on_node_event(EventTranslator.translate_node(e))

    def on_transport_event(self, e: AetherNetTransportEvent) -> None:
        self._target.on_transport_event(EventTranslator.translate_transport(e))

    def on_route_event(self, e: AetherNetRouteEvent) -> None:
        self._target.on_route_event(EventTranslator.translate_route(e))

    def on_security_event(self, e: AetherNetSecurityEvent) -> None:
        self._target.on_security_event(EventTranslator.translate_security(e))

    def on_network_event(self, e: AetherNetNetworkEvent) -> None:
        self._target.on_network_event(EventTranslator.translate_network(e))


# ── AetherNetDirectiveSink (CircleAI -> AetherNet, outbound) ──────────────────


class AetherNetDirectiveSink(ISecurityDirectiveConsumer):
    """Forwards CircleAI security directives to the AetherNet policy engine.
    Implements CircleAI's :class:`ISecurityDirectiveConsumer` so it can be
    registered as a directive sink on the CircleAI side.
    """

    def __init__(self, mesh_consumer: IAetherNetSecurityDirectiveConsumer) -> None:
        if mesh_consumer is None:
            raise ValueError("mesh_consumer must not be None")
        self._mesh_consumer = mesh_consumer

    def on_directive(self, directive: SecurityDirective) -> None:
        if directive is None:
            raise ValueError("directive must not be None")
        mesh_directive = AetherNetSecurityDirective(
            kind=EventTranslator.map_directive_kind_to_mesh(directive.kind),
            target_node_id=directive.target_node_id,
            trust_score_override=directive.trust_score_override,
            threat_level=EventTranslator.map_threat_level_to_mesh(
                directive.threat_level
            ),
            reason=directive.reason,
            duration=directive.duration,
            issued_at=directive.issued_at,
        )
        self._mesh_consumer.on_directive(mesh_directive)


# ── AetherNetInboundDirectiveBridge (AetherNet -> CircleAI, inbound) ──────────


class AetherNetInboundDirectiveBridge(IAetherNetSecurityDirectiveConsumer):
    """Receives AetherNet-side directives and forwards them into CircleAI's
    :class:`ISecurityDirectiveConsumer`. The other half of the bidirectional
    directive pipeline: :class:`AetherNetDirectiveSink` handles outbound, this
    handles inbound.
    """

    def __init__(self, circle_consumer: ISecurityDirectiveConsumer) -> None:
        if circle_consumer is None:
            raise ValueError("circle_consumer must not be None")
        self._circle_consumer = circle_consumer

    def on_directive(self, mesh_directive: AetherNetSecurityDirective) -> None:
        if mesh_directive is None:
            raise ValueError("mesh_directive must not be None")
        circle_directive = SecurityDirective(
            kind=EventTranslator.map_directive_kind_from_mesh(mesh_directive.kind),
            target_node_id=mesh_directive.target_node_id,
            trust_score_override=mesh_directive.trust_score_override,
            threat_level=EventTranslator.map_threat_level(mesh_directive.threat_level),
            reason=mesh_directive.reason,
            duration=mesh_directive.duration,
            issued_at=mesh_directive.issued_at,
        )
        self._circle_consumer.on_directive(circle_directive)


# ── CircleAiAetherNetAiProvider ───────────────────────────────────────────────

_EMPTY_BIASES: Mapping[str, float] = {}
_EMPTY_ROUTES: Sequence[AiRouteSuggestion] = ()


class CircleAiAetherNetAiProvider(IAetherNetAiProvider):
    """Bridges CircleAI's :class:`IAetherIntelligence` to AetherNet's
    :class:`IAetherNetAiProvider` extension seat.

    What CircleAI doesn't yet produce (transport biases, structured route
    suggestions) returns a sensible default that lets the mesh fall back to its
    own logic — no false claim of intelligence we don't have.
    """

    def __init__(self, intelligence: IAetherIntelligence) -> None:
        if intelligence is None:
            raise ValueError("intelligence must not be None")
        self._intelligence = intelligence

    @property
    def is_available(self) -> bool:
        return True

    async def suggest_routes_async(
        self,
        destination_uhid: str,
        payload_bytes: int,
        cancellation_token: object = None,
    ) -> Sequence[AiRouteSuggestion]:
        if not destination_uhid or not destination_uhid.strip():
            return _EMPTY_ROUTES

        advice = await self._intelligence.get_routing_advice_async(
            destination_uhid, cancellation_token
        )
        if advice is None or len(advice.recommended_path) == 0:
            return _EMPTY_ROUTES
        return [AiRouteSuggestion(list(advice.recommended_path), advice.confidence)]

    async def get_transport_biases_async(
        self, payload_bytes: int, cancellation_token: object = None
    ) -> Mapping[str, float]:
        # CircleAI does not yet model per-transport biases. An empty mapping tells
        # AetherNet to use its built-in transport selector without AI adjustment.
        return _EMPTY_BIASES

    async def assess_threat_async(
        self, packet: MeshPacket, cancellation_token: object = None
    ) -> AiThreatLevel:
        if packet is None or not packet.source_uhid or not packet.source_uhid.strip():
            return AiThreatLevel.NONE

        assessment = await self._intelligence.assess_threat_async(
            packet.source_uhid, cancellation_token
        )
        return _map_to_mesh_threat_level(assessment.level)

    async def get_network_health_async(
        self, cancellation_token: object = None
    ) -> AiNetworkHealthReport:
        health = await self._intelligence.get_network_health_async(cancellation_token)
        return AiNetworkHealthReport(
            health.overall_score,
            health.trusted_node_count,
            health.suspicious_node_count,
            health.summary,
            health.generated_at,
        )


def _map_to_mesh_threat_level(level: AetherThreatLevel) -> AiThreatLevel:
    # AetherNet's AiThreatLevel has 4 values (None, Low, Medium, High). CircleAI's
    # AetherThreatLevel has Critical. Fold Critical -> High — the strongest signal
    # the AI seat can carry.
    return {
        AetherThreatLevel.NONE: AiThreatLevel.NONE,
        AetherThreatLevel.LOW: AiThreatLevel.LOW,
        AetherThreatLevel.MEDIUM: AiThreatLevel.MEDIUM,
        AetherThreatLevel.HIGH: AiThreatLevel.HIGH,
        AetherThreatLevel.CRITICAL: AiThreatLevel.HIGH,
    }.get(level, AiThreatLevel.NONE)
