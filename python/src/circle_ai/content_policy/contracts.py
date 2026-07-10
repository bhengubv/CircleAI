# contracts.py
#
# Port of CircleAI.ContentPolicy Contracts.cs (C# — the EXACT spec).
#
# (2.6.0) Safety-guardrails contracts (Sponsio pattern-adoption). The C#
# namespace is `CircleAI.ContentPolicy`, deliberately distinct from the
# personal-safety domain pack `CircleAI.Safety` (ported to circle_ai.safety).
#
# C# ValueTask<T> maps to async def -> T. C# records map to frozen slotted
# dataclasses. The C# enum maps to an IntEnum with stable ordinals.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import List, Optional, Sequence


class SafetyVerdict(IntEnum):
    """Outcome of a content-safety classification.

    Mirrors ``CircleAI.ContentPolicy.SafetyVerdict``. Ordinals are the C#
    declaration order and are stable across languages.
    """

    ALLOW = 0
    FLAG = 1
    REFUSE = 2


@dataclass(frozen=True, slots=True)
class SafetyFinding:
    """A single safety classification result.

    Mirrors ``CircleAI.ContentPolicy.SafetyFinding`` —
    ``record(SafetyVerdict Verdict, string Category, string Reason, float Confidence)``.
    """

    verdict: SafetyVerdict
    category: str
    reason: str
    confidence: float


class IContentFilter(ABC):
    """(2.6.0) Per-token / per-message content filter."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def classify_async(self, text: str, ct: Optional[object] = None) -> SafetyFinding:
        ...


class IRefusalPolicy(ABC):
    """(2.6.0) Refusal policy — decides whether a finding becomes a refusal."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def should_refuse_async(
        self,
        findings: Sequence[SafetyFinding],
        ct: Optional[object] = None,
    ) -> bool:
        ...


class IPromptInjectionDetector(ABC):
    """(2.6.0) Prompt-injection detector — catches second-order attacks
    (RAG / web / tool output).
    """

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def inspect_async(
        self,
        untrusted_content: str,
        source_label: str,
        ct: Optional[object] = None,
    ) -> SafetyFinding:
        ...


@dataclass(frozen=True, slots=True)
class SafetyAuditEntry:
    """Mirrors ``CircleAI.ContentPolicy.SafetyAuditEntry`` —
    ``record(DateTimeOffset AtUtc, string UserId, string Action, SafetyVerdict Verdict, string Reason)``.
    """

    at_utc: datetime
    user_id: str
    action: str
    verdict: SafetyVerdict
    reason: str


class ISafetyAuditLog(ABC):
    """(2.6.0) Append-only safety audit log."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def log_async(self, entry: SafetyAuditEntry, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def read_async(
        self,
        user_id: Optional[str],
        limit: int = 100,
        ct: Optional[object] = None,
    ) -> List[SafetyAuditEntry]:
        ...
