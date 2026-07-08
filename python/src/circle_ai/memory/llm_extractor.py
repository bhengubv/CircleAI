# memory/llm_extractor.py
#
# LLM-backed knowledge-graph extraction: turn -> (subject, predicate, object)
# triples. Ported from CircleAI.Companion (LlmKnowledgeGraphExtractor) — the C#
# reference — and mirrors the TypeScript pilot (memory/llm_extractor.ts).
#
# Uses an on-device IChatGenerator to ask an LLM to extract triples from a
# single conversation turn. The extraction prompt asks for strict-JSON output;
# the parser is defensive against the model emitting extra prose or fences.

from __future__ import annotations

import json
from datetime import datetime, timezone
from typing import Optional

from ..inference.inference import IChatGenerator
from ..models.models import ChatMessage
from .graph import KnowledgeTriple

# Confidence used when the model omits (or malforms) the "c" field.
_DEFAULT_CONFIDENCE = 0.75

_SYSTEM_PROMPT = (
    "You are a knowledge-graph extractor. Read the conversation turn between USER and ASSISTANT. "
    "Identify entities (people, places, things, concepts) and facts. "
    'Output a single JSON array of triples like [{"s":"Subject","p":"predicate","o":"object","c":0.0-1.0}, ...]. '
    "Only output the JSON — no prose, no markdown fences."
)


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


def _is_blank(s: Optional[str]) -> bool:
    return s is None or len(s.strip()) == 0


def _clamp(x: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, x))


class LlmKnowledgeGraphExtractor:
    """Model-backed extractor: asks an LLM for triples and parses its JSON reply.

    Satisfies the :class:`IKnowledgeGraphExtractor` Protocol in
    :mod:`circle_ai.memory.extractor` structurally.
    """

    def __init__(self, ai: IChatGenerator) -> None:
        if ai is None:
            raise ValueError("ai required")
        self._ai = ai

    async def extract_from_turn_async(
        self,
        user_text: str,
        assistant_text: str,
        source_episode_id: Optional[str],
        *,
        ct: Optional[object] = None,
    ) -> list[KnowledgeTriple]:
        if _is_blank(user_text) and _is_blank(assistant_text):
            return []

        user_msg = (
            "USER:\n"
            + (user_text or "")
            + "\nASSISTANT:\n"
            + (assistant_text or "")
            + "\n"
        )

        try:
            reply = await self._ai.generate_async(
                [
                    ChatMessage(role="system", content=_SYSTEM_PROMPT),
                    ChatMessage(role="user", content=user_msg),
                ]
            )
        except Exception:
            # LLM call failed — degrade gracefully, no triples this turn.
            return []

        return parse_triples(reply, source_episode_id)


def parse_triples(
    raw: str, source_episode_id: Optional[str]
) -> list[KnowledgeTriple]:
    """Parse the model's reply into triples.

    Finds the first ``[`` and last ``]``, JSON-parses the slice, and reads
    s/p/o/c from each object. Any structural problem yields an empty list
    rather than raising.
    """
    if _is_blank(raw):
        return []
    first_bracket = raw.find("[")
    last_bracket = raw.rfind("]")
    if first_bracket < 0 or last_bracket <= first_bracket:
        return []
    json_slice = raw[first_bracket : last_bracket + 1]

    try:
        parsed = json.loads(json_slice)
    except (ValueError, TypeError):
        # Malformed JSON — return nothing.
        return []

    if not isinstance(parsed, list):
        return []

    now = _utc_now()
    hits: list[KnowledgeTriple] = []
    for entry in parsed:
        # Skip non-object array entries (numbers, strings, null, arrays).
        if not isinstance(entry, dict):
            continue
        s = entry.get("s")
        p = entry.get("p")
        o = entry.get("o")
        s = s if isinstance(s, str) else None
        p = p if isinstance(p, str) else None
        o = o if isinstance(o, str) else None
        c_raw = entry.get("c")
        # bool is a subclass of int/float — exclude it, matching "number" in JSON.
        if isinstance(c_raw, bool) or not isinstance(c_raw, (int, float)):
            c = _DEFAULT_CONFIDENCE
        else:
            c = _clamp(float(c_raw), 0.0, 1.0)
        if _is_blank(s) or _is_blank(p) or _is_blank(o):
            continue
        hits.append(
            KnowledgeTriple(
                subject=s,  # type: ignore[arg-type]
                predicate=p,  # type: ignore[arg-type]
                object=o,  # type: ignore[arg-type]
                source=source_episode_id,
                confidence=c,
                recorded_at_utc=now,
            )
        )
    return hits
