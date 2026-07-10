# security_options.py
#
# Port of CircleAI.Security.SecurityOptions (C# — the EXACT spec).
#
# Configuration model for the AI Security Layer.
#
# All threshold values are trust scores in the [0, 1] range.
# Lower score = more compromised. Thresholds must satisfy:
#   quarantine_threshold < avoid_node_threshold < elevate_monitoring_threshold

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import timedelta


@dataclass(slots=True)
class SecurityOptions:
    """Configures thresholds, decay rates, and event retention for the AI
    Security Layer. Pass to :class:`NodeTrustRegistry` and
    :class:`SecurityLayerService`.

    Mutable (matching the C# ``{ get; set; }`` options object) so hosts can tune
    values before wiring.
    """

    # Trust score below which monitoring is elevated for the node.
    # Default: 0.75 — a 25 % trust loss triggers closer observation.
    elevate_monitoring_threshold: float = 0.75

    # Trust score below which the node is excluded from routing.
    # Default: 0.50 — half trust lost -> route around the node.
    avoid_node_threshold: float = 0.50

    # Trust score at or below which the node is hard-blocked (quarantined).
    # Default: 0.25 — severe compromise -> no traffic to or from the node.
    quarantine_threshold: float = 0.25

    # Passive trust recovery per second when no adverse events occur.
    # Default: 0.001 ~ full recovery from zero in ~16 minutes of clean behaviour.
    recovery_rate_per_second: float = 0.001

    # Sliding window used for pattern-based indicator detection (e.g. repeated
    # auth attempts). Events outside this window are ignored for pattern
    # analysis. Default: 5 minutes.
    event_window: timedelta = field(default_factory=lambda: timedelta(minutes=5))

    # Maximum security events retained per node. Oldest are dropped first.
    # Default: 100.
    max_events_per_node: int = 100

    # Trust score assigned to nodes on first observation.
    # Default: 1.0 (full trust until evidence says otherwise).
    initial_trust_score: float = 1.0
