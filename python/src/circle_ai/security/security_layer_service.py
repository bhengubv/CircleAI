# security_layer_service.py
#
# Port of CircleAI.Security.SecurityLayerService (C# — the EXACT spec).
#
# Transport-agnostic AI Security Layer — full implementation of
# IPeerSecurityLayer.
#
# Lifecycle:
#   start_async  -> launches the background trust-recovery loop.
#   (running)    -> security events arrive via handle_peer_event(event).
#                   Each event degrades the peer's trust score; threshold
#                   evaluation decides which PeerDirective (if any) to issue.
#   stop_async   -> cancels the recovery loop, cleans up.
#
# Directives issued (most-severe wins per event):
#   QUARANTINE_NODE     trust <= quarantine_threshold
#   AVOID_NODE          trust <= avoid_node_threshold
#   ELEVATE_MONITORING  trust <= elevate_monitoring_threshold
#   RELEASE_NODE        not issued automatically — requires operator action

from __future__ import annotations

import asyncio
from datetime import datetime, timedelta, timezone
from typing import Optional

from .directive_publisher import DirectivePublisher
from .node_trust_registry import NodeTrustRegistry
from .peer_security_types import (
    IDisposable,
    IPeerDirectiveConsumer,
    IPeerSecurityLayer,
    PeerDirective,
    PeerDirectiveKind,
    PeerSecurityEvent,
    PeerSecurityPosture,
    PeerThreatLevel,
)
from .security_options import SecurityOptions
from .threat_detector import ThreatDetector

# Background recovery cadence — matches the C# `TimeSpan.FromSeconds(30)`.
_RECOVERY_INTERVAL = timedelta(seconds=30)


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class SecurityLayerService(IPeerSecurityLayer):
    """Transport-agnostic AI Security Layer. Degrades per-peer trust scores via
    :class:`ThreatDetector` and issues :class:`PeerDirective` recommendations to
    all registered :class:`IPeerDirectiveConsumer` subscribers.
    """

    def __init__(
        self,
        registry: NodeTrustRegistry,
        options: SecurityOptions,
        publisher: DirectivePublisher,
    ) -> None:
        self._registry = registry
        self._options = options
        self._publisher = publisher

        self._recovery_loop: Optional[asyncio.Task] = None
        self._stop_event: Optional[asyncio.Event] = None
        self._active = False

    # ── IPeerSecurityLayer ────────────────────────────────────────────────────

    async def start_async(self, ct: Optional[object] = None) -> None:
        if self._active:
            return
        self._stop_event = asyncio.Event()
        self._recovery_loop = asyncio.create_task(
            self._run_recovery_loop_async(self._stop_event)
        )
        self._active = True

    async def stop_async(self, ct: Optional[object] = None) -> None:
        self._active = False

        if self._stop_event is not None:
            self._stop_event.set()

        if self._recovery_loop is not None:
            self._recovery_loop.cancel()
            try:
                await self._recovery_loop
            except asyncio.CancelledError:
                pass
            self._recovery_loop = None

        self._stop_event = None

    def handle_peer_event(self, e: PeerSecurityEvent) -> None:
        """Call this from any transport adapter after translating its native
        event type to :class:`PeerSecurityEvent`. Thread-safe.
        """
        degradation = ThreatDetector.compute_degradation(e)
        if degradation <= 0:
            return  # PeerThreatLevel.NONE — no trust impact

        previous, current = self._registry.apply_degradation(e, degradation)
        self._evaluate_thresholds(e.node_id, previous, current, e.description)

    def handle_peer_left(self, node_id: str) -> None:
        """Notify the security layer that a peer has left. Trust entry is
        preserved for historical queries; no directive is issued.
        """
        # Trust entry retained for forensic queries; no action on departure.
        return

    def subscribe_to_directives(
        self, consumer: IPeerDirectiveConsumer
    ) -> IDisposable:
        return self._publisher.subscribe(consumer)

    async def get_posture_async(
        self, ct: Optional[object] = None
    ) -> PeerSecurityPosture:
        node_ids = self._registry.all_node_ids

        quarantined = sum(
            1
            for nid in node_ids
            if self._registry.get_trust_score(nid) <= self._options.quarantine_threshold
        )
        monitored = 0
        for nid in node_ids:
            s = self._registry.get_trust_score(nid)
            if (
                s <= self._options.elevate_monitoring_threshold
                and s > self._options.quarantine_threshold
            ):
                monitored += 1

        if len(node_ids) == 0:
            worst_score = 1.0
        else:
            worst_score = min(self._registry.get_trust_score(nid) for nid in node_ids)
        overall_threat = self._score_to_threat_level(worst_score)

        return PeerSecurityPosture(
            overall_threat,
            quarantined,
            monitored,
            self._active,
            _utc_now(),
        )

    # ── Threshold evaluation ──────────────────────────────────────────────────

    def _evaluate_thresholds(
        self, node_id: str, previous: float, current: float, reason: str
    ) -> None:
        # Evaluate from most-severe to least; issue at most one directive per
        # event.

        if (
            previous > self._options.quarantine_threshold
            and current <= self._options.quarantine_threshold
        ):
            self._issue_directive(
                PeerDirectiveKind.QUARANTINE_NODE,
                node_id,
                current,
                reason,
                PeerThreatLevel.CRITICAL,
            )
            return

        if (
            previous > self._options.avoid_node_threshold
            and current <= self._options.avoid_node_threshold
        ):
            self._issue_directive(
                PeerDirectiveKind.AVOID_NODE,
                node_id,
                current,
                reason,
                PeerThreatLevel.HIGH,
            )
            return

        if (
            previous > self._options.elevate_monitoring_threshold
            and current <= self._options.elevate_monitoring_threshold
        ):
            self._issue_directive(
                PeerDirectiveKind.ELEVATE_MONITORING,
                node_id,
                current,
                reason,
                PeerThreatLevel.MEDIUM,
            )

    def _issue_directive(
        self,
        kind: PeerDirectiveKind,
        node_id: str,
        trust_score: float,
        reason: str,
        threat_level: PeerThreatLevel,
    ) -> None:
        self._publisher.publish(
            PeerDirective(
                kind=kind,
                target_node_id=node_id,
                trust_score=trust_score,
                threat_level=threat_level,
                reason=reason,
                duration=None,  # permanent until ReleaseNode
                issued_at=_utc_now(),
            )
        )

    # ── Background recovery loop ──────────────────────────────────────────────

    async def _run_recovery_loop_async(self, stop_event: asyncio.Event) -> None:
        interval_seconds = _RECOVERY_INTERVAL.total_seconds()
        while not stop_event.is_set():
            try:
                # Wait for the interval OR an early stop signal, whichever first.
                await asyncio.wait_for(stop_event.wait(), timeout=interval_seconds)
                # stop_event fired — exit the loop.
                break
            except asyncio.TimeoutError:
                # Interval elapsed with no stop — run the recovery pass.
                self._registry.apply_recovery(_RECOVERY_INTERVAL)
            except asyncio.CancelledError:
                break

    # ── Helpers ───────────────────────────────────────────────────────────────

    @staticmethod
    def _score_to_threat_level(score: float) -> PeerThreatLevel:
        if score <= 0.25:
            return PeerThreatLevel.CRITICAL
        if score <= 0.50:
            return PeerThreatLevel.HIGH
        if score <= 0.75:
            return PeerThreatLevel.MEDIUM
        if score <= 0.90:
            return PeerThreatLevel.LOW
        return PeerThreatLevel.NONE
