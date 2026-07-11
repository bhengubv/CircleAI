# types.py
#
# Port of CircleAI.Languages.Translation TranslationTypes.cs (C# — the EXACT
# spec).
#
# Requests/results/turns for the on-device translation engine. C# enum ->
# IntEnum (stable ordinals). C# records -> frozen slotted dataclasses.
# DateTimeOffset -> datetime.

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import Optional


class TranslationMode(IntEnum):
    """Mirrors ``CircleAI.Languages.Translation.TranslationMode``."""

    STANDARD = 0
    CONVERSATIONAL = 1
    DOCUMENT = 2
    TECHNICAL = 3
    LEGAL = 4
    MEDICAL = 5


@dataclass(frozen=True, slots=True)
class TranslationRequest:
    """A request to translate a piece of text between two languages.

    Mirrors ``CircleAI.Languages.Translation.TranslationRequest`` — ``record(
    string Text, string SourceBcpTag, string TargetBcpTag,
    TranslationMode Mode = Standard, string? ContextHint = null)``.
    """

    text: str
    source_bcp_tag: str
    target_bcp_tag: str
    mode: TranslationMode = TranslationMode.STANDARD
    context_hint: Optional[str] = None


@dataclass(frozen=True, slots=True)
class TranslationResult:
    """Result of a completed translation.

    Mirrors ``CircleAI.Languages.Translation.TranslationResult`` — ``record(
    string OriginalText, string TranslatedText, string SourceBcpTag,
    string TargetBcpTag, float Confidence, DateTimeOffset TranslatedAt)``.
    """

    original_text: str
    translated_text: str
    source_bcp_tag: str
    target_bcp_tag: str
    confidence: float
    translated_at: datetime


@dataclass(frozen=True, slots=True)
class ConversationTurn:
    """One turn in a live bidirectional conversation.

    Mirrors ``CircleAI.Languages.Translation.ConversationTurn`` — ``record(
    string SpeakerBcpTag, string OriginalText, string? TranslatedText,
    DateTimeOffset Timestamp)``.
    """

    speaker_bcp_tag: str
    original_text: str
    translated_text: Optional[str]
    timestamp: datetime
