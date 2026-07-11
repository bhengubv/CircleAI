# guardrails.py
#
# Port of CircleAI.Telephony Guardrails.cs (C# — the EXACT spec).
#
# (3.3.0) Pre-TTS phrase blocking. The model's draft response runs through the
# guardrails before TTS — banned phrases are rewritten or the whole turn is
# replaced with a fallback message. Useful for keeping the AI on-script, banning
# PII leaks, or stopping competitor name mentions.
#
# C# Regex(IgnoreCase | Compiled) -> re.compile(..., re.IGNORECASE). The
# Redact branch uses regex.Replace(text, replacement) -> pattern.sub. The
# "modified" flag mirrors the C# reference-equality-then-value check; in Python
# that reduces to a plain value comparison against the original draft.

from __future__ import annotations

import re
from dataclasses import dataclass
from enum import IntEnum
from typing import Iterable, List, Optional, Tuple


class GuardrailAction(IntEnum):
    """(3.3.0) What a guardrail does on match."""

    #: Block the turn entirely — the AI says ``fallback_message`` instead.
    REPLACE = 0
    #: Redact only the matched text (e.g. credit-card numbers -> "[redacted]").
    REDACT = 1
    #: Pass through but flag in the audit log.
    WARN = 2


@dataclass(frozen=True, slots=True)
class GuardrailRule:
    """(3.3.0) One rule the guardrail checks.

    ``name``: display name for logging.
    ``pattern``: regex pattern (case-insensitive).
    ``action``: what to do when the pattern matches.
    ``replace_with``: replacement text for :attr:`GuardrailAction.REDACT`.
    ``fallback_message``: speak this instead when :attr:`GuardrailAction.REPLACE`.
    """

    name: str
    pattern: str
    action: GuardrailAction
    replace_with: Optional[str] = None
    fallback_message: Optional[str] = None


@dataclass(frozen=True, slots=True)
class GuardrailResult:
    """(3.3.0) Outcome of running guardrails on one text draft.

    Mirrors ``record(string FinalText, bool WasModified, bool WasBlocked,
    IReadOnlyList<string> TriggeredRules)``.
    """

    final_text: str
    was_modified: bool
    was_blocked: bool
    triggered_rules: List[str]


class Guardrails:
    """(3.3.0) Pre-TTS guardrail engine."""

    def __init__(
        self,
        rules: Optional[Iterable[GuardrailRule]] = None,
        default_fallback: str = "I'm sorry, I can't help with that right now.",
    ) -> None:
        self._default_fallback = default_fallback
        self._rules: List[Tuple[GuardrailRule, "re.Pattern[str]"]] = [
            (r, re.compile(r.pattern, re.IGNORECASE)) for r in (rules if rules is not None else [])
        ]

    def apply(self, draft: str) -> GuardrailResult:
        """(3.3.0) Run the guardrails against a draft response."""
        if not draft:
            return GuardrailResult(draft if draft is not None else "", False, False, [])

        triggered: List[str] = []
        text = draft
        blocked = False

        for rule, regex in self._rules:
            if not regex.search(text):
                continue
            triggered.append(rule.name)

            if rule.action == GuardrailAction.REPLACE:
                blocked = True
                text = rule.fallback_message if rule.fallback_message is not None else self._default_fallback
                return GuardrailResult(text, True, True, triggered)
            elif rule.action == GuardrailAction.REDACT:
                text = regex.sub(rule.replace_with if rule.replace_with is not None else "[redacted]", text)
            elif rule.action == GuardrailAction.WARN:
                # No mutation; just flag.
                pass

        modified = text != draft
        return GuardrailResult(text, modified, blocked, triggered)


class CommonGuardrails:
    """(3.3.0) Common guardrails out of the box."""

    #: (3.3.0) Redact 13-19 digit credit-card numbers.
    CreditCardRedactor: GuardrailRule = GuardrailRule(
        name="credit-card",
        pattern=r"\b(?:\d[ -]*?){13,19}\b",
        action=GuardrailAction.REDACT,
        replace_with="[redacted card number]",
    )

    #: (3.3.0) Block US SSN-shaped sequences (xxx-xx-xxxx).
    SsnBlocker: GuardrailRule = GuardrailRule(
        name="ssn",
        pattern=r"\b\d{3}-\d{2}-\d{4}\b",
        action=GuardrailAction.REPLACE,
        fallback_message="For security I can't share that information.",
    )

    @staticmethod
    def competitor_mention(*competitors: str) -> GuardrailRule:
        """(3.3.0) Block competitor mentions — supply names per deployment."""
        joined = "|".join(re.escape(c) for c in competitors)
        return GuardrailRule(
            name="competitor",
            pattern=r"\b(?:" + joined + r")\b",
            action=GuardrailAction.REPLACE,
            fallback_message="I can't comment on other providers, but I can help with your account.",
        )
