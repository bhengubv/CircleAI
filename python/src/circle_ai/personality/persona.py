# persona.py
#
# Port of CircleAI.Personality Persona.cs (C# — the EXACT spec).
#
# The user-DECLARED persona artefact. Distinct from CircleAI.Memory.PersonaState
# (the AI's LEARNED model of the user). Persona is the user's structured,
# editable, exportable identity declaration — a document the user owns.
#
# C# record -> frozen slotted dataclass. Guid -> uuid.UUID. DateTimeOffset ->
# datetime. C# enum PrivacyLevel -> IntEnum (Strict=0, Balanced=1, Open=2 —
# declaration order). Persona.Create -> Persona.create classmethod.

from __future__ import annotations

import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from enum import IntEnum
from typing import Sequence


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class PrivacyLevel(IntEnum):
    """Declared privacy posture. Mirrors ``CircleAI.Personality.PrivacyLevel``.
    Ordinals are the C# declaration order."""

    #: Minimum retention, no proactive surfacing, no third-party calls without prompt.
    STRICT = 0
    #: Default. Reasonable retention, helpful proactive prompts.
    BALANCED = 1
    #: Maximum retention, willing to share personal context across surfaces.
    OPEN = 2


@dataclass(frozen=True, slots=True)
class FormalityRange:
    """Mirrors ``CircleAI.Personality.FormalityRange`` — ``record(string Floor,
    string Ceiling)``. Allowed values: ``"casual"``, ``"neutral"``, ``"formal"``.
    """

    floor: str
    ceiling: str


@dataclass(frozen=True, slots=True)
class Persona:
    """Mirrors ``CircleAI.Personality.Persona``.

    ``record(Guid Id, string DisplayName, string? Pronouns,
    IReadOnlyList<string> IdentityTags, IReadOnlyList<string> Values,
    IReadOnlyList<string> Taboos, string PreferredLocale, string? VoicePreference,
    FormalityRange Formality, PrivacyLevel Privacy, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)``.
    """

    id: uuid.UUID
    display_name: str
    pronouns: object  # Optional[str]
    identity_tags: Sequence[str]
    values: Sequence[str]
    taboos: Sequence[str]
    preferred_locale: str
    voice_preference: object  # Optional[str]
    formality: FormalityRange
    privacy: PrivacyLevel
    created_at: datetime
    updated_at: datetime

    @staticmethod
    def create(display_name: str, locale: str) -> "Persona":
        """Create a persona with sensible defaults: balanced privacy, no taboos /
        values, formality range "casual".."formal", timestamps stamped to now.
        Mirrors ``Persona.Create``."""
        if display_name is None or display_name.strip() == "":
            raise ValueError("displayName required")
        if locale is None or locale.strip() == "":
            raise ValueError("locale required")
        now = _utc_now()
        return Persona(
            id=uuid.uuid4(),
            display_name=display_name,
            pronouns=None,
            identity_tags=[],
            values=[],
            taboos=[],
            preferred_locale=locale,
            voice_preference=None,
            formality=FormalityRange("casual", "formal"),
            privacy=PrivacyLevel.BALANCED,
            created_at=now,
            updated_at=now,
        )
