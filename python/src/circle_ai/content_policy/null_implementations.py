# null_implementations.py
#
# Port of CircleAI.ContentPolicy NullImplementations.cs (C# — the EXACT spec).
#
# (2.6.0) Fail-closed defaults — when there is no real backend wired we treat
# content as refused (safest default). The C# `static readonly Instance`
# singletons map to module-level singletons exposed as class attributes.

from __future__ import annotations

from typing import List, Optional

from .contracts import (
    IContentFilter,
    IPromptInjectionDetector,
    IRefusalPolicy,
    ISafetyAuditLog,
    SafetyAuditEntry,
    SafetyFinding,
    SafetyVerdict,
)


class NullContentFilter(IContentFilter):
    """Fail-closed content filter — always refuses."""

    Instance: "NullContentFilter"

    @property
    def backend_id(self) -> str:
        return "null"

    async def classify_async(self, text: str, ct: Optional[object] = None) -> SafetyFinding:
        return SafetyFinding(
            verdict=SafetyVerdict.REFUSE,
            category="no-filter-configured",
            reason="Fail-closed default — wire a real IContentFilter to relax.",
            confidence=1.0,
        )


class NullRefusalPolicy(IRefusalPolicy):
    """Fail-closed refusal policy — always refuses."""

    Instance: "NullRefusalPolicy"

    @property
    def backend_id(self) -> str:
        return "null"

    async def should_refuse_async(self, findings, ct: Optional[object] = None) -> bool:
        return True


class NullPromptInjectionDetector(IPromptInjectionDetector):
    """Fail-closed prompt-injection detector — always refuses."""

    Instance: "NullPromptInjectionDetector"

    @property
    def backend_id(self) -> str:
        return "null"

    async def inspect_async(
        self, content: str, source: str, ct: Optional[object] = None
    ) -> SafetyFinding:
        return SafetyFinding(
            verdict=SafetyVerdict.REFUSE,
            category="no-detector-configured",
            reason="Fail-closed default.",
            confidence=1.0,
        )


class NullSafetyAuditLog(ISafetyAuditLog):
    """No-op audit log — logs are dropped, reads return empty."""

    Instance: "NullSafetyAuditLog"

    @property
    def backend_id(self) -> str:
        return "null"

    async def log_async(self, entry: SafetyAuditEntry, ct: Optional[object] = None) -> None:
        return None

    async def read_async(
        self, user_id: Optional[str], limit: int = 100, ct: Optional[object] = None
    ) -> List[SafetyAuditEntry]:
        return []


# `static readonly Instance` singletons (see C# NullImplementations.cs).
NullContentFilter.Instance = NullContentFilter()
NullRefusalPolicy.Instance = NullRefusalPolicy()
NullPromptInjectionDetector.Instance = NullPromptInjectionDetector()
NullSafetyAuditLog.Instance = NullSafetyAuditLog()
