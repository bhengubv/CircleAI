# model_delta.py
#
# Port of CircleAI.Federation ModelDelta.cs + FederationRound.cs (C# — the EXACT
# spec).
#
# One participant's signed contribution to a federation round, and the round
# lifecycle record. C# Guid maps to uuid.UUID; byte[] maps to bytes;
# DateTimeOffset maps to datetime.

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import Optional
from uuid import UUID


class RoundStatus(IntEnum):
    """Lifecycle state of a :class:`FederationRound`. Mirrors
    ``CircleAI.Federation.RoundStatus`` (declaration order)."""

    Open = 0
    Aggregating = 1
    Committed = 2
    Aborted = 3


@dataclass(frozen=True, slots=True)
class ModelDelta:
    """Mirrors ``CircleAI.Federation.ModelDelta`` — ``record(Guid Id,
    Guid RoundId, string ContributorUhid, string ModelId, string FromVersion,
    byte[] DeltaPayload, int SampleCount, byte[] Signature,
    DateTimeOffset SubmittedAt)``.

    NO raw training data leaves the device — only the delta payload.
    """

    id: UUID
    round_id: UUID
    contributor_uhid: str
    model_id: str
    from_version: str
    delta_payload: bytes
    sample_count: int
    signature: bytes
    submitted_at: datetime


@dataclass(frozen=True, slots=True)
class FederationRound:
    """Mirrors ``CircleAI.Federation.FederationRound`` — ``record(Guid Id,
    string ModelId, string FromVersion, string ToVersion, int MinParticipants,
    int MaxParticipants, int CurrentParticipantCount, RoundStatus Status,
    DateTimeOffset OpenedAt, DateTimeOffset? CommittedAt)``."""

    id: UUID
    model_id: str
    from_version: str
    to_version: str
    min_participants: int
    max_participants: int
    current_participant_count: int
    status: RoundStatus
    opened_at: datetime
    committed_at: Optional[datetime]
