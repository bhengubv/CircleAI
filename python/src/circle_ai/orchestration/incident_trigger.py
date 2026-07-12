# incident_trigger.py
#
# Port of CircleAI.Orchestration IncidentTrigger.cs (C# — the EXACT spec).
#
# Maps a recorded EpisodicMemoryEntry (and, separately, a confirmed
# AnomalySignal) to the set of agent tasks that should be triggered when the
# input represents a crash or security incident.
#
# Cross-cluster references:
#   * EpisodicMemoryEntry  -> circle_ai.memory.episodic_memory
#   * AnomalySignal / ThreatVector -> circle_ai.security
# Both are imported lazily inside the methods so importing this module never
# forces the memory/security packages to load (keeps the import graph acyclic
# and the compile-gate cheap).

from __future__ import annotations

from typing import List, Optional

from .contracts import AgentPriority, AgentRole, AgentTask

# Tag keys that identify an episodic entry as a crash / unhandled-error incident
# (case-insensitive — mirrors the C# StringComparer.OrdinalIgnoreCase set).
_CRASH_TAGS = {"crash", "exception", "unhandled_error", "oom", "null_reference"}

# Tag keys that, in addition to a crash signal, indicate a security
# investigation is warranted.
_SECURITY_TAGS = {"auth_failure", "permission_denied", "token_expired", "injection", "overflow"}


class IncidentTrigger:
    """Maps recorded incidents to the agent tasks that should be triggered.
    Mirrors ``CircleAI.Orchestration.IncidentTrigger`` (a static class).
    """

    @staticmethod
    def from_memory_entry(entry: object) -> List[AgentTask]:
        """Inspect an episodic memory entry and return the agent tasks that
        should be triggered. Returns an empty list when the entry is not an
        incident.

        Always dispatches one :attr:`AgentRole.Operations` task for a crash tag;
        additionally dispatches one :attr:`AgentRole.Security` task when a
        security tag is also present.
        """
        if entry is None:
            raise ValueError("entry must not be None")

        tags = getattr(entry, "tags", None) or {}
        tag_keys = list(tags.keys())
        is_crash = any(k.casefold() in _CRASH_TAGS for k in tag_keys)
        if not is_crash:
            return []

        recorded_at = getattr(entry, "recorded_at_utc", None)
        recorded_at_str = recorded_at.isoformat() if recorded_at is not None else ""
        entry_id = getattr(entry, "id", "")
        user_text = getattr(entry, "user_text", "") or ""
        assistant_text = getattr(entry, "assistant_text", "") or ""
        app_context = getattr(entry, "app_context", None) or ""

        tasks: List[AgentTask] = []

        # Always dispatch an ops-incident task for every crash entry.
        tasks.append(
            AgentTask.create(
                AgentRole.Operations,
                f"ops-incident: diagnose crash recorded at {recorded_at_str}",
                AgentPriority.High,
                {
                    "episode_id": str(entry_id),
                    "user_text": user_text,
                    "assistant_text": assistant_text,
                    "app_context": app_context,
                },
            )
        )

        # When security indicators are also present, escalate to a security agent.
        is_security = any(k.casefold() in _SECURITY_TAGS for k in tag_keys)
        if is_security:
            tasks.append(
                AgentTask.create(
                    AgentRole.Security,
                    f"ops-security: investigate security incident from episode {entry_id}",
                    AgentPriority.Critical,
                    {
                        "episode_id": str(entry_id),
                        "app_context": app_context,
                        "tags": ",".join(tag_keys),
                    },
                )
            )

        return tasks

    @staticmethod
    def from_anomaly_signal(
        signal: object, dispatch_threshold: float = 0.30
    ) -> Optional[AgentTask]:
        """Map a confirmed ``AnomalySignal`` from the local immune system into an
        :class:`AgentTask` for an ops-security agent. Returns ``None`` for
        signals below the dispatch threshold.

        Confidence drives priority; high-severity vectors are bumped one rank
        (Critical=0 < High=1 < Normal=2 < Low=3, so "bumping" decreases the
        numeric value).
        """
        if signal is None:
            raise ValueError("signal must not be None")

        # Imported lazily to avoid forcing the security package at module load.
        from ..security.threat_vector import ThreatVector

        confidence = float(signal.confidence)
        if confidence < dispatch_threshold:
            return None

        if confidence >= 0.85:
            priority = AgentPriority.Critical
        elif confidence >= 0.60:
            priority = AgentPriority.High
        else:
            priority = AgentPriority.Normal

        is_high_severity_vector = signal.vector in (
            ThreatVector.CONTROL_FLOW_DRIFT,
            ThreatVector.PRIVILEGE_ESCALATION,
            ThreatVector.NETWORK_PIVOT,
            ThreatVector.STATE_CORRUPTION,
        )

        # priority ordering: Critical=0 < High=1 < Normal=2 < Low=3. "Bumping one
        # rank" means decreasing the numeric value, floored at Critical.
        if is_high_severity_vector and int(priority) > int(AgentPriority.Critical):
            priority = AgentPriority(max(int(AgentPriority.Critical), int(priority) - 1))

        evidence = dict(getattr(signal, "evidence", {}) or {})
        detected_at = getattr(signal, "detected_at", None)
        inputs = dict(evidence)
        inputs.update(
            {
                "signal_id": str(signal.id),
                "vector": signal.vector.name,
                "confidence": f"{confidence:.3f}",
                "affected_module": signal.affected_module,
                "description": signal.description,
                "detected_at": detected_at.isoformat() if detected_at is not None else "",
            }
        )

        # C# formats confidence as a whole-number percentage (P0) in the label.
        confidence_pct = f"{round(confidence * 100)}%"
        return AgentTask.create(
            AgentRole.Security,
            f"ops-security: anomaly {signal.vector.name} in {signal.affected_module} "
            f"(confidence {confidence_pct})",
            priority,
            inputs,
        )
