# keyword_content_filter.py
#
# Port of CircleAI.ContentPolicy KeywordContentFilter.cs (C# — the EXACT spec).
#
# (3.3.0) Real keyword/regex content filter + threshold refusal policy +
# prompt-injection detector. These are not LLM-grade safety models — they're
# production-grade fast checks. Hosts that need a real safety LLM wrap one behind
# the same contract.
#
# .NET RegexOptions.IgnoreCase -> Python re.IGNORECASE. RegexOptions.Compiled is
# a JIT hint with no Python equivalent (re caches compiled patterns anyway).

from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import List, Optional, Sequence

from .contracts import (
    IContentFilter,
    IPromptInjectionDetector,
    IRefusalPolicy,
    SafetyFinding,
    SafetyVerdict,
)


@dataclass(frozen=True, slots=True)
class KeywordRule:
    """(3.3.0) Rule for the keyword content filter.

    Mirrors ``CircleAI.ContentPolicy.KeywordRule`` —
    ``record(string Category, string Pattern, SafetyVerdict OnMatch, float Confidence = 0.9f)``
    with a lazily-materialised compiled ``Regex`` property.
    """

    category: str
    pattern: str
    on_match: SafetyVerdict
    confidence: float = 0.9
    # Compiled regex — the C# record materialises this in the initializer with
    # IgnoreCase|Compiled. field(compare=False) keeps record equality by the
    # data members only (a compiled Pattern object is not value-comparable).
    regex: re.Pattern = field(init=False, compare=False, repr=False)

    def __post_init__(self) -> None:
        object.__setattr__(self, "regex", re.compile(self.pattern, re.IGNORECASE))


class CommonKeywordRules:
    """(3.3.0) Default rule set for everyday harm classes.

    Mirrors ``CircleAI.ContentPolicy.CommonKeywordRules``.
    """

    #: Immutable default rule set — same order and patterns as the C# spec.
    Default: Sequence[KeywordRule] = (
        KeywordRule("self-harm", r"\b(kill myself|suicide|self\s*-?\s*harm)\b", SafetyVerdict.REFUSE, 0.95),
        KeywordRule("explicit-sexual", r"\b(porn|sexual content|nsfw)\b", SafetyVerdict.FLAG, 0.7),
        KeywordRule("violence", r"\b(how to make a bomb|chemical weapon|murder)\b", SafetyVerdict.REFUSE, 0.9),
        KeywordRule("hate", r"\b(racial slur|hate speech)\b", SafetyVerdict.REFUSE, 0.9),
        KeywordRule("pii-card", r"\b(?:\d[ -]*?){13,19}\b", SafetyVerdict.FLAG, 0.8),
    )


class KeywordContentFilter(IContentFilter):
    """(3.3.0) Fast keyword/regex content filter."""

    def __init__(self, rules: Optional[Sequence[KeywordRule]] = None) -> None:
        self._rules: Sequence[KeywordRule] = rules if rules is not None else CommonKeywordRules.Default

    @property
    def backend_id(self) -> str:
        return "keyword"

    async def classify_async(self, text: str, ct: Optional[object] = None) -> SafetyFinding:
        if text is None:
            raise ValueError("text must not be None")
        for r in self._rules:
            if r.regex.search(text) is not None:
                return SafetyFinding(r.on_match, r.category, f"Matched rule '{r.category}'", r.confidence)
        return SafetyFinding(SafetyVerdict.ALLOW, "ok", "No rule matched", 1.0)


class ThresholdRefusalPolicy(IRefusalPolicy):
    """(3.3.0) Threshold refusal policy — refuse when any finding's Refuse verdict
    is above the threshold, or when the count of Flag findings exceeds the
    configured ceiling.
    """

    def __init__(self, refuse_threshold: float = 0.5, flag_ceiling: int = 3) -> None:
        self._refuse_threshold = refuse_threshold
        self._flag_ceiling = flag_ceiling

    @property
    def backend_id(self) -> str:
        return "threshold"

    async def should_refuse_async(
        self, findings: Sequence[SafetyFinding], ct: Optional[object] = None
    ) -> bool:
        if findings is None:
            raise ValueError("findings must not be None")
        if any(
            f.verdict == SafetyVerdict.REFUSE and f.confidence >= self._refuse_threshold
            for f in findings
        ):
            return True
        flag_count = sum(1 for f in findings if f.verdict == SafetyVerdict.FLAG)
        return flag_count > self._flag_ceiling


class KeywordPromptInjectionDetector(IPromptInjectionDetector):
    """(3.3.0) Detect common prompt-injection patterns in untrusted text from
    RAG / tool output / web.
    """

    #: Same patterns and order as the C# spec.
    _PATTERNS: Sequence[re.Pattern] = (
        re.compile(r"ignore (all|the|any) (previous|prior) instructions", re.IGNORECASE),
        re.compile(r"forget (everything|all) (above|prior)", re.IGNORECASE),
        re.compile(r"you (are now|will be|are no longer)", re.IGNORECASE),
        re.compile(r"system prompt[:\s]", re.IGNORECASE),
        re.compile(r"reveal (your|the) (instructions|system prompt|hidden context)", re.IGNORECASE),
        re.compile(r"<\|im_(start|end)\|>", re.IGNORECASE),
        re.compile(r"(BEGIN|END)\s+(SYSTEM|DEVELOPER|ASSISTANT)\s+MESSAGE", re.IGNORECASE),
    )

    @property
    def backend_id(self) -> str:
        return "keyword"

    async def inspect_async(
        self, untrusted_content: str, source_label: str, ct: Optional[object] = None
    ) -> SafetyFinding:
        if untrusted_content is None:
            raise ValueError("untrusted_content must not be None")
        for p in self._PATTERNS:
            match = p.search(untrusted_content)
            if match is not None:
                return SafetyFinding(
                    SafetyVerdict.REFUSE,
                    "prompt-injection",
                    f'Pattern matched in {source_label}: "{_truncate(match.group(0), 60)}"',
                    0.9,
                )
        return SafetyFinding(SafetyVerdict.ALLOW, "ok", "No injection patterns", 1.0)


def _truncate(s: str, max_len: int) -> str:
    return s if len(s) <= max_len else s[:max_len] + "…"
