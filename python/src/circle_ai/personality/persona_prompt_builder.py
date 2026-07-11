# persona_prompt_builder.py
#
# Port of CircleAI.Personality PersonaPromptBuilder.cs (C# — the EXACT spec).
#
# Renders a Persona into a compact natural-language system-prompt hint. Returns
# an empty string when the persona is in its default/unedited state so the prompt
# is not bloated with no-op instructions.
#
# Prompt-injection defence (faithful port): every user-controlled string is
# emitted as a JSON string literal so any embedded quote / newline / directive is
# inert text inside a quoted string. The C# uses
# JsonSerializer.Serialize(value, {Encoder = UnsafeRelaxedJsonEscaping}); Python's
# json.dumps(value, ensure_ascii=False) matches that relaxed escaping (quotes,
# backslash and control chars escaped; <, >, &, + left as-is).

from __future__ import annotations

import json
from typing import Sequence

from .persona import Persona, PrivacyLevel


def _quote(value: str) -> str:
    """JSON-encode ``value`` into a quoted literal (relaxed escaping)."""
    return json.dumps(value, ensure_ascii=False)


def _quote_list(items: Sequence[str]) -> str:
    if len(items) == 0:
        return ""
    return ", ".join(_quote(i) for i in items)


def _is_effectively_default(p: Persona) -> bool:
    """True when the persona contains no information beyond the
    :meth:`Persona.create` defaults."""
    pronouns = p.pronouns
    voice = p.voice_preference
    return (
        (pronouns is None or str(pronouns).strip() == "")
        and len(p.identity_tags) == 0
        and len(p.values) == 0
        and len(p.taboos) == 0
        and (voice is None or str(voice).strip() == "")
        and p.privacy == PrivacyLevel.BALANCED
        and p.formality.floor == "casual"
        and p.formality.ceiling == "formal"
    )


def build_system_hint(persona: Persona) -> str:
    """Render ``persona`` into a compact system-prompt hint, or an empty string
    when the persona is effectively default. Mirrors
    ``PersonaPromptBuilder.BuildSystemHint``."""
    if persona is None:
        raise ValueError("persona")

    if _is_effectively_default(persona):
        return ""

    parts = ["[Persona]"]
    parts.append("\nYou are speaking with ")
    parts.append(_quote(persona.display_name))
    parts.append(".")

    pronouns = persona.pronouns
    if pronouns is not None and str(pronouns).strip() != "":
        parts.append(" They identify as ")
        parts.append(_quote(str(pronouns)))
        parts.append(".")

    parts.append("\nThey prefer responses in ")
    parts.append(_quote(persona.preferred_locale))
    parts.append(", tone between ")
    parts.append(_quote(persona.formality.floor))
    parts.append(" and ")
    parts.append(_quote(persona.formality.ceiling))
    parts.append(".")

    if len(persona.identity_tags) > 0:
        parts.append("\nIdentity tags: ")
        parts.append(_quote_list(persona.identity_tags))
        parts.append(".")

    if len(persona.values) > 0:
        parts.append("\nTheir declared values: ")
        parts.append(_quote_list(persona.values))
        parts.append(".")

    if len(persona.taboos) > 0:
        parts.append("\nAvoid: ")
        parts.append(_quote_list(persona.taboos))
        parts.append(".")

    voice = persona.voice_preference
    if voice is not None and str(voice).strip() != "":
        parts.append("\nPreferred voice tag: ")
        parts.append(_quote(str(voice)))
        parts.append(".")

    if persona.privacy == PrivacyLevel.STRICT:
        parts.append(
            "\nPrivacy: strict — minimize stored signals, do not surface personal "
            "context proactively, and never share personal context across surfaces "
            "without explicit prompt."
        )
    elif persona.privacy == PrivacyLevel.OPEN:
        parts.append(
            "\nPrivacy: open — the user has authorised broader retention and "
            "proactive surfacing."
        )

    return "".join(parts)


class PersonaPromptBuilder:
    """Static-style holder mirroring the C# ``PersonaPromptBuilder`` class."""

    build_system_hint = staticmethod(build_system_hint)
