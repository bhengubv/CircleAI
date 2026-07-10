# aether_security_bridge.py
#
# Port of CircleAI.Security.AetherNet.AetherSecurityBridge (C# — the EXACT spec).
#
# Bridges the Aether telemetry feed (IAetherTelemetry / IAetherTelemetryObserver)
# into the transport-agnostic CircleAI.Security layer (SecurityLayerService).
#
# Responsibilities:
#   1. Implements IAISecurityLayer so existing Aether callers can wire this up
#      without code changes.
#   2. Subscribes to IAetherTelemetry on start_async, translates each
#      AetherSecurityEvent into a PeerSecurityEvent and calls
#      SecurityLayerService.handle_peer_event().
#   3. Adapts ISecurityDirectiveConsumer (Aether contract) <->
#      IPeerDirectiveConsumer (transport-agnostic contract).
#   4. Maps SecurityPosture <-> PeerSecurityPosture.
#
# The SecurityLayerService does all the reasoning; this class is pure translation.

from __future__ import annotations

from typing import Optional

from ..aether.events import (
    AetherNetworkEvent,
    AetherNodeEvent,
    AetherRouteEvent,
    AetherSecurityEvent,
    AetherTransportEvent,
)
from ..aether.security_layer import (
    IAISecurityLayer,
    ISecurityDirectiveConsumer,
    SecurityDirective,
    SecurityPosture,
)
from ..aether.telemetry import (
    IAetherTelemetry,
    IAetherTelemetryObserver,
    IDisposable,
)
from ..security.peer_security_types import (
    IPeerDirectiveConsumer,
    PeerDirective,
    PeerSecurityEvent,
)
from ..security.security_layer_service import SecurityLayerService
from .aether_mapper import AetherMapper


class AetherSecurityBridge(IAISecurityLayer):
    """Connects an Aether mesh telemetry feed to the transport-agnostic
    :class:`SecurityLayerService`. Implements :class:`IAISecurityLayer` so it can
    be used as a drop-in replacement for the old Aether-coupled layer.
    """

    def __init__(self, layer: SecurityLayerService) -> None:
        if layer is None:
            raise ValueError("layer must not be None")
        self._layer = layer
        self._telemetry_subscription: Optional[IDisposable] = None

    # ── IAISecurityLayer ──────────────────────────────────────────────────────

    async def start_async(
        self, telemetry: IAetherTelemetry, ct: Optional[object] = None
    ) -> None:
        if telemetry is None:
            raise ValueError("telemetry must not be None")
        # Subscribe synchronously before starting the recovery loop so no event
        # published right after start is lost.
        self._telemetry_subscription = telemetry.subscribe(_Observer(self))
        await self._layer.start_async(ct)

    async def stop_async(self, ct: Optional[object] = None) -> None:
        if self._telemetry_subscription is not None:
            self._telemetry_subscription.dispose()
            self._telemetry_subscription = None
        await self._layer.stop_async(ct)

    def subscribe_to_directives(
        self, consumer: ISecurityDirectiveConsumer
    ) -> IDisposable:
        if consumer is None:
            raise ValueError("consumer must not be None")
        return self._layer.subscribe_to_directives(_DirectiveAdapter(consumer))

    async def get_posture_async(
        self, ct: Optional[object] = None
    ) -> SecurityPosture:
        posture = await self._layer.get_posture_async(ct)
        return SecurityPosture(
            overall_threat_level=AetherMapper.to_aether_threat_level(
                posture.overall_threat_level
            ),
            quarantined_node_count=posture.quarantined_peer_count,
            monitored_node_count=posture.monitored_peer_count,
            is_active=posture.is_active,
            assessed_at=posture.generated_at,
        )


class _Observer(IAetherTelemetryObserver):
    """Translates Aether telemetry into the transport-agnostic security layer.
    Only security and node-departure events matter to scoring; the rest are
    ignored.
    """

    def __init__(self, bridge: AetherSecurityBridge) -> None:
        self._bridge = bridge

    def on_security_event(self, e: AetherSecurityEvent) -> None:
        peer = PeerSecurityEvent(
            node_id=e.node_id,
            kind=AetherMapper.to_peer_event_kind(e.kind),
            threat_level=AetherMapper.to_peer_threat_level(e.threat_level),
            description=e.description,
            transport_id="aether",
            occurred_at=e.occurred_at,
        )
        self._bridge._layer.handle_peer_event(peer)

    def on_node_event(self, e: AetherNodeEvent) -> None:
        if e.is_exit:
            self._bridge._layer.handle_peer_left(e.node_id)

    # Not relevant to security scoring — ignore.
    def on_transport_event(self, e: AetherTransportEvent) -> None:
        pass

    def on_route_event(self, e: AetherRouteEvent) -> None:
        pass

    def on_network_event(self, e: AetherNetworkEvent) -> None:
        pass


class _DirectiveAdapter(IPeerDirectiveConsumer):
    """Adapts an Aether :class:`ISecurityDirectiveConsumer` so it can receive
    :class:`PeerDirective` instances from the transport-agnostic layer,
    translating them back to :class:`SecurityDirective` before delivery.
    """

    def __init__(self, consumer: ISecurityDirectiveConsumer) -> None:
        self._consumer = consumer

    def on_directive(self, directive: PeerDirective) -> None:
        aether = SecurityDirective(
            kind=AetherMapper.to_security_directive_kind(directive.kind),
            target_node_id=directive.target_node_id,
            trust_score_override=directive.trust_score,
            threat_level=AetherMapper.to_aether_threat_level(directive.threat_level),
            reason=directive.reason,
            duration=directive.duration,
            issued_at=directive.issued_at,
        )
        self._consumer.on_directive(aether)
