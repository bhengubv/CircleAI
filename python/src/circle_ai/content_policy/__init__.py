"""circle_ai.content_policy — port of the CircleAI.ContentPolicy assembly.

(2.6.0/3.3.0) Safety-guardrails contracts + production-grade fast filters
(Sponsio pattern-adoption). The C# namespace is ``CircleAI.ContentPolicy``,
deliberately distinct from the personal-safety domain pack ``CircleAI.Safety``
(ported to :mod:`circle_ai.safety`) to avoid collision.

Public surface (C# is the exact spec):

  * SafetyVerdict                    — Allow / Flag / Refuse.
  * SafetyFinding                    — (verdict, category, reason, confidence).
  * IContentFilter                   — per-message content classifier.
  * IRefusalPolicy                   — turns findings into a refuse/allow.
  * IPromptInjectionDetector         — catches second-order (RAG/web/tool) attacks.
  * SafetyAuditEntry / ISafetyAuditLog — append-only audit trail.
  * KeywordRule / CommonKeywordRules — regex rule set for everyday harm classes.
  * KeywordContentFilter             — fast keyword/regex filter.
  * ThresholdRefusalPolicy           — refuse on Refuse>=threshold or too many Flags.
  * KeywordPromptInjectionDetector   — pattern-based injection detector.
  * Null* fail-closed defaults        — refuse when no real backend is wired.
"""
from __future__ import annotations

from .contracts import (
    IContentFilter,
    IPromptInjectionDetector,
    IRefusalPolicy,
    ISafetyAuditLog,
    SafetyAuditEntry,
    SafetyFinding,
    SafetyVerdict,
)
from .keyword_content_filter import (
    CommonKeywordRules,
    KeywordContentFilter,
    KeywordPromptInjectionDetector,
    KeywordRule,
    ThresholdRefusalPolicy,
)
from .null_implementations import (
    NullContentFilter,
    NullPromptInjectionDetector,
    NullRefusalPolicy,
    NullSafetyAuditLog,
)

__all__ = [
    "SafetyVerdict",
    "SafetyFinding",
    "IContentFilter",
    "IRefusalPolicy",
    "IPromptInjectionDetector",
    "SafetyAuditEntry",
    "ISafetyAuditLog",
    "KeywordRule",
    "CommonKeywordRules",
    "KeywordContentFilter",
    "ThresholdRefusalPolicy",
    "KeywordPromptInjectionDetector",
    "NullContentFilter",
    "NullRefusalPolicy",
    "NullPromptInjectionDetector",
    "NullSafetyAuditLog",
]
