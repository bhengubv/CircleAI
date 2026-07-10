# speech/cloud/keyword_voice_intent_router.py
#
# Port of CircleAI.Speech.Cloud/KeywordVoiceIntentRouter.cs (C# — the EXACT spec).
#
# (3.2.0) Generic regex-based voice intent router. Lifted from CircleUp's
# KeywordVoiceCommandRouter — vault-specific patterns stripped, replaced with a
# host-supplied list of intent definitions. The router matches in order; first
# hit wins; falls through to a caller-defined fallback intent (typically
# "ask-ai") when nothing matches.

from __future__ import annotations

import re
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Dict, Iterable, List, Mapping, Tuple


@dataclass(frozen=True, slots=True)
class VoiceIntent:
    """(3.2.0) One named intent the router recognises.

    Mirrors ``CircleAI.Speech.Cloud.VoiceIntent``. ``pattern`` is matched against
    the trimmed transcript; on a hit, every named group is exposed in
    :attr:`VoiceIntentMatch.captures`.
    """

    name: str
    pattern: re.Pattern


@dataclass(frozen=True, slots=True)
class VoiceIntentMatch:
    """One match outcome. Mirrors ``CircleAI.Speech.Cloud.VoiceIntentMatch``."""

    intent_name: str
    transcript: str
    captures: Mapping[str, str]


class IVoiceIntentRouter(ABC):
    """(3.2.0) Maps a transcript to one of a host-supplied set of intents.
    Rule-based, sub-millisecond per attempt, hermetic.

    Mirrors ``CircleAI.Speech.Cloud.IVoiceIntentRouter``."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification — "keyword", "null"."""
        ...

    @abstractmethod
    async def route_async(self, transcript: str, ct: object = None) -> VoiceIntentMatch:
        """Match the transcript against the configured intents. Returns a match for
        the first hitting intent, or for the fallback intent when nothing matches
        (whose :attr:`VoiceIntentMatch.captures` is empty)."""
        ...


class KeywordVoiceIntentRouter(IVoiceIntentRouter):
    """(3.2.0) Default :class:`IVoiceIntentRouter`. Takes an ordered list of
    intents plus a fallback name (typically "ask-ai") and tries each pattern in
    order.

    Mirrors ``CircleAI.Speech.Cloud.KeywordVoiceIntentRouter``."""

    __slots__ = ("_intents", "_fallback_intent_name")

    def __init__(self, intents: Iterable[VoiceIntent], fallback_intent_name: str = "ask-ai") -> None:
        if intents is None:
            raise ValueError("intents")
        if fallback_intent_name is None or not fallback_intent_name.strip():
            raise ValueError("fallback_intent_name")
        self._intents: List[VoiceIntent] = list(intents)
        self._fallback_intent_name = fallback_intent_name

    @property
    def backend_id(self) -> str:
        return "keyword"

    async def route_async(self, transcript: str, ct: object = None) -> VoiceIntentMatch:
        text = (transcript or "").strip()
        if len(text) == 0:
            return VoiceIntentMatch(
                intent_name=self._fallback_intent_name,
                transcript="",
                captures={},
            )

        for intent in self._intents:
            match = intent.pattern.search(text)
            if match is None:
                continue

            captures: Dict[str, str] = {}
            # Surface only named groups (skip the implicit whole-match / numeric
            # groups), matching C# GetGroupNames() + int.TryParse skip.
            for name in intent.pattern.groupindex:
                value = match.group(name)
                if value is not None and value != "":
                    captures[name] = value.strip()

            return VoiceIntentMatch(
                intent_name=intent.name,
                transcript=text,
                captures=captures,
            )

        return VoiceIntentMatch(
            intent_name=self._fallback_intent_name,
            transcript=text,
            captures={},
        )


class NullVoiceIntentRouter(IVoiceIntentRouter):
    """(3.2.0) Empty router — always returns the fallback intent.

    Mirrors ``CircleAI.Speech.Cloud.NullVoiceIntentRouter``."""

    _instance: "NullVoiceIntentRouter | None" = None

    @classmethod
    def instance(cls) -> "NullVoiceIntentRouter":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def backend_id(self) -> str:
        return "null"

    async def route_async(self, transcript: str, ct: object = None) -> VoiceIntentMatch:
        return VoiceIntentMatch(
            intent_name="ask-ai",
            transcript=transcript or "",
            captures={},
        )


__all__ = [
    "VoiceIntent",
    "VoiceIntentMatch",
    "IVoiceIntentRouter",
    "KeywordVoiceIntentRouter",
    "NullVoiceIntentRouter",
]
