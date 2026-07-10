# security_response.py
#
# Port of CircleAI.Security.SecurityResponse + SecurityResponseKind
# (C# — the EXACT spec).
#
# Describes the action taken by ISecurityWatchdog in response to an
# AnomalySignal. Returned from on_anomaly_detected_async so calling code
# (e.g. ops-security agent, host application) knows what was done.

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import IntEnum
from typing import List, Optional, Sequence
from uuid import UUID

from .security_checkpoint import SecurityCheckpoint


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class SecurityResponseKind(IntEnum):
    """The type of protective action taken in response to an
    :class:`AnomalySignal`.
    """

    # No action — confidence below threshold or vector is informational.
    NO_ACTION = 0

    # The session's ephemeral UHID key ring was regenerated; prior session
    # keys are revoked and all in-flight requests using old keys will fail.
    KEY_ROTATION = 1

    # The affected session or execution sandbox was marked untrusted and
    # isolated from the rest of the runtime.
    SESSION_REVOCATION = 2

    # A PeerDirective was issued to surrounding mesh nodes to isolate the
    # suspected attack origin.
    MESH_ISOLATION_SIGNAL = 3

    # State was rolled back to the most recent verified SecurityCheckpoint.
    STATE_ROLLBACK = 4

    # A combination of responses was applied (e.g. key rotation + mesh
    # isolation). See :attr:`SecurityResponse.applied_actions` for the full list.
    COMPOSITE = 5


@dataclass(frozen=True, slots=True)
class SecurityResponse:
    """Describes the protective action taken by ``ISecurityWatchdog`` in
    response to an :class:`AnomalySignal`.

    :param signal_id: Identifier of the :class:`AnomalySignal` that triggered
        this response.
    :param kind: Primary response kind.
    :param applied_actions: When :attr:`kind` is
        :attr:`SecurityResponseKind.COMPOSITE`, lists each individual action
        applied. Empty for single-action responses.
    :param description: Human-readable description of what was done and why.
    :param restored_checkpoint: The :class:`SecurityCheckpoint` that was
        restored, if any. ``None`` when :attr:`kind` is not
        :attr:`SecurityResponseKind.STATE_ROLLBACK`.
    :param responded_at: UTC timestamp of the response.
    """

    signal_id: UUID
    kind: SecurityResponseKind
    applied_actions: Sequence[SecurityResponseKind]
    description: str
    restored_checkpoint: Optional[SecurityCheckpoint]
    responded_at: datetime

    @classmethod
    def no_action(cls, signal_id: UUID, reason: str) -> "SecurityResponse":
        """Create a no-action response for low-confidence or informational
        signals.
        """
        return cls(
            signal_id, SecurityResponseKind.NO_ACTION, [], reason, None, _utc_now()
        )

    @classmethod
    def for_key_rotation(cls, signal_id: UUID, description: str) -> "SecurityResponse":
        """Create a key-rotation response."""
        return cls(
            signal_id,
            SecurityResponseKind.KEY_ROTATION,
            [],
            description,
            None,
            _utc_now(),
        )

    @classmethod
    def for_rollback(
        cls, signal_id: UUID, restored: SecurityCheckpoint
    ) -> "SecurityResponse":
        """Create a state-rollback response, recording the restored checkpoint."""
        return cls(
            signal_id,
            SecurityResponseKind.STATE_ROLLBACK,
            [],
            f"State rolled back to checkpoint {restored.id} ({restored.module_label}).",
            restored,
            _utc_now(),
        )

    @classmethod
    def composite(
        cls,
        signal_id: UUID,
        actions: Sequence[SecurityResponseKind],
        description: str,
        restored_checkpoint: Optional[SecurityCheckpoint] = None,
    ) -> "SecurityResponse":
        """Create a composite response from multiple individual actions."""
        return cls(
            signal_id,
            SecurityResponseKind.COMPOSITE,
            list(actions),
            description,
            restored_checkpoint,
            _utc_now(),
        )
