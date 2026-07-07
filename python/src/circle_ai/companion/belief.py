# companion/belief.py
#
# Memory integrity: attribution + belief revision. Ported from
# CircleAI.Companion (PersonalBelief, HeuristicBeliefExtractor, SelfBeliefStore)
# — the C# reference — and mirrors the TypeScript pilot (companion/belief.ts) and
# Go port (companion_belief.go).
#
# Every belief carries WHOSE fact it is — the user's own (Self), someone else's
# (Other), or a general fact (World). The highest-harm rule in the whole system:
# a fact about a third party ("my mother is diabetic") must never be recorded as
# a fact about the user. Only Self beliefs become user facts; a newer self-belief
# on the same predicate supersedes the older one; a correction retracts a belief.

from __future__ import annotations

import re
import threading
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import Enum
from typing import Optional, Protocol, runtime_checkable


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class Attribution(Enum):
    """Whose fact a belief is about."""

    Self = "Self"
    Other = "Other"
    World = "World"


@dataclass(frozen=True)
class PersonalBelief:
    """A single attributed belief, with provenance and confidence."""

    attribution: Attribution
    subject: str
    predicate: str
    object: str
    confidence: float
    source: Optional[str]
    recorded_at_utc: datetime = field(default_factory=_utc_now)


@runtime_checkable
class IBeliefExtractor(Protocol):
    """Turns a sentence into attributed beliefs."""

    async def extract_async(
        self, text: str, source: Optional[str], *, ct: Optional[object] = None
    ) -> list[PersonalBelief]:
        """Extract attributed beliefs from a sentence."""
        ...


_RELATIONS: set[str] = {
    "mother", "father", "mom", "mum", "dad", "sister", "brother", "wife", "husband", "son", "daughter",
    "aunt", "uncle", "grandmother", "grandfather", "granny", "grandpa", "gran", "nan", "friend",
    "colleague", "boss", "neighbour", "neighbor", "cousin", "partner", "girlfriend", "boyfriend",
}
_POSSESSIVE: set[str] = {"my", "her", "his", "their", "our"}
_STOP: set[str] = {
    "the", "a", "an", "is", "are", "was", "were", "be", "been", "am", "to", "of", "in", "on", "at", "and", "or",
    "but", "with", "has", "have", "had", "that", "this", "it", "as", "for", "really", "very", "just", "now",
}

# The belief-extractor split set has NO apostrophe (unlike the KG extractor), so
# "i'm" survives as a single token and matches the Self branch below. Matches the
# C# split: ' ', '\t', '\n', '\r', '.', ',', '?', '!', ';', ':', '"', '(', ')'.
_TOKEN_SPLIT = re.compile(r"[ \t\n\r.,?!;:\"()]+")


class HeuristicBeliefExtractor:
    """Model-free belief extractor with attribution discipline.

    Coarse by design — the model-based extractor is far more precise — but it
    never collapses "my mother" into "me". Attribution is decided by the
    sentence's leading subject.
    """

    async def extract_async(
        self, text: str, source: Optional[str], *, ct: Optional[object] = None
    ) -> list[PersonalBelief]:
        if not text or len(text.strip()) == 0:
            return []

        tokens = [t for t in _TOKEN_SPLIT.split(text.lower()) if len(t) > 0]
        if len(tokens) == 0:
            return []

        attribution: Attribution
        subject: str
        skip: set[int] = set()  # subject tokens, excluded from the object

        if len(tokens) >= 2 and tokens[0] in _POSSESSIVE and tokens[1] in _RELATIONS:
            # "my mother ..." -> someone else
            attribution = Attribution.Other
            subject = tokens[1]
            skip.add(0)
            skip.add(1)
        elif tokens[0] in _RELATIONS:
            attribution = Attribution.Other
            subject = tokens[0]
            skip.add(0)
        elif tokens[0] in ("i", "i'm", "im", "me") or tokens[0] == "my":
            # "I ..." or "my <non-relation> ..." -> the user
            attribution = Attribution.Self
            subject = "user"
            skip.add(0)
        else:
            attribution = Attribution.World
            subject = tokens[0]

        obj = " ".join(
            t
            for i, t in enumerate(tokens)
            if i not in skip and len(t) >= 3 and t not in _STOP and t not in _RELATIONS
        )
        if len(obj.strip()) == 0:
            return []

        return [
            PersonalBelief(
                attribution=attribution,
                subject=subject,
                predicate="isAbout",
                object=obj,
                confidence=0.6,
                source=source,
                recorded_at_utc=_utc_now(),
            )
        ]


class SelfBeliefStore:
    """The user's own facts, with attribution filtering, revision, and correction.

    Thread-safe: the encoder writes from its background drain while the session
    reads facts for the prompt. A ``threading.Lock`` guards the lists — the C#
    reference uses ``lock``; asyncio alone would suffice for the pytest suite,
    but a real thread-backed encoder makes the lock load-bearing, matching C#.
    """

    def __init__(self) -> None:
        self._gate = threading.Lock()
        self._self: list[PersonalBelief] = []
        self._audit: list[PersonalBelief] = []  # other/world — never a user fact

    def record(self, belief: PersonalBelief) -> None:
        """Record a belief. Only Self beliefs become user facts; the rest are audited."""
        if belief is None:
            raise ValueError("belief required")
        with self._gate:
            if belief.attribution is not Attribution.Self:
                self._audit.append(belief)
                return
            # Supersede an existing self-belief on the same (subject, predicate):
            # a functional fact holds one current value. The prior value drops out.
            self._self = [
                b
                for b in self._self
                if not (
                    _eq_ci(b.subject, belief.subject)
                    and _eq_ci(b.predicate, belief.predicate)
                )
            ]
            self._self.append(belief)

    def self_facts(self) -> list[PersonalBelief]:
        """The user's own current facts."""
        with self._gate:
            return list(self._self)

    def non_self(self) -> list[PersonalBelief]:
        """Beliefs remembered but never treated as user facts (audit trail)."""
        with self._gate:
            return list(self._audit)

    def retract(self, object_contains: str) -> int:
        """Correction ("no, that's my mother"): drop any user fact mentioning the text."""
        if not object_contains or len(object_contains.strip()) == 0:
            return 0
        needle = object_contains.lower()
        with self._gate:
            before = len(self._self)
            self._self = [b for b in self._self if needle not in b.object.lower()]
            return before - len(self._self)

    def provenance(self) -> list[str]:
        """Introspection ("why do you think that?"): the source turns behind the user's facts."""
        with self._gate:
            seen: set[str] = set()
            out: list[str] = []
            for b in self._self:
                if b.source is not None and b.source not in seen:
                    seen.add(b.source)
                    out.append(b.source)
            return out


def _eq_ci(a: str, b: str) -> bool:
    return a.lower() == b.lower()
