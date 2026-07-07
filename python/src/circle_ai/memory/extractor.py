# memory/extractor.py
#
# Knowledge-graph extraction: turn -> (subject, predicate, object) triples.
# Ported from CircleAI.Companion (IKnowledgeGraphExtractor,
# HeuristicKnowledgeGraphExtractor) — the C# reference — and mirrors the
# TypeScript pilot (memory/extractor.ts) and Go port (memory_extractor.go).
#
# The heuristic extractor is model-free: it links the content words a turn
# mentions to the memory they came from, two-way, so a later question can reach
# an older memory across turns. It is the offline counterpart to the LLM-based
# extractor (same interface, no network) — the graph still fills, just coarsely.

from __future__ import annotations

import re
from typing import Optional, Protocol, runtime_checkable

from .graph import KnowledgeTriple


@runtime_checkable
class IKnowledgeGraphExtractor(Protocol):
    """Turns a conversation turn into knowledge-graph triples."""

    async def extract_from_turn_async(
        self,
        user_text: str,
        assistant_text: str,
        source_episode_id: Optional[str],
        *,
        ct: Optional[object] = None,
    ) -> list[KnowledgeTriple]:
        """Extract triples linking the turn's content words to their memory."""
        ...


_DEFAULT_CONFIDENCE = 0.6

# Common function words carry no association — drop them so links form on
# meaningful words (names, places, symptoms, things), not "the" and "my".
_STOP: set[str] = {
    "the", "a", "an", "and", "or", "but", "if", "is", "are", "was", "were", "be", "been", "being",
    "to", "of", "in", "on", "at", "for", "with", "from", "by", "as", "into", "about", "over", "under",
    "my", "your", "our", "their", "his", "her", "its", "this", "that", "these", "those",
    "i", "you", "he", "she", "it", "we", "they", "me", "him", "them", "us",
    "do", "does", "did", "done", "have", "has", "had", "will", "would", "can", "could", "should",
    "shall", "may", "might", "must", "not", "no", "yes", "so", "than", "then", "there", "here",
    "how", "why", "what", "when", "where", "who", "which", "whom",
    "am", "get", "got", "really", "just", "very", "much", "many", "some", "any", "all",
}

# Split on whitespace + punctuation. The C# split set includes apostrophe, hyphen
# and slash (['"()/-] plus the whitespace/.,?!;: group); mirror it exactly.
_WORD_SPLIT = re.compile(r"[ \t\n\r.,?!;:'\"()/\-]+")


class HeuristicKnowledgeGraphExtractor:
    """Model-free extractor: links a turn's content words to their memory, two-way."""

    async def extract_from_turn_async(
        self,
        user_text: str,
        assistant_text: str,
        source_episode_id: Optional[str],
        *,
        ct: Optional[object] = None,
    ) -> list[KnowledgeTriple]:
        # The memory node is identified by the source id when given, else the
        # user's words — so recall can hand back the memory it came from.
        memory = (
            source_episode_id
            if source_episode_id and len(source_episode_id.strip()) > 0
            else user_text
        )
        if not memory or len(memory.strip()) == 0:
            return []

        words = _content_words((user_text or "") + " " + (assistant_text or ""))
        now = _utc_now()
        triples: list[KnowledgeTriple] = []
        for w in words:
            # Two-way so a walk can go word -> memory -> word -> memory across turns.
            triples.append(
                KnowledgeTriple(memory, "mentions", w, source_episode_id, _DEFAULT_CONFIDENCE, now)
            )
            triples.append(
                KnowledgeTriple(w, "seenin", memory, source_episode_id, _DEFAULT_CONFIDENCE, now)
            )
        return triples


def _content_words(text: str) -> list[str]:
    """Lowercase, split on separators, drop short/stop words, dedupe preserving order."""
    seen: set[str] = set()
    result: list[str] = []
    for raw in _WORD_SPLIT.split(text.lower()):
        if len(raw) < 3 or raw in _STOP:
            continue
        if raw not in seen:
            seen.add(raw)
            result.append(raw)
    return result


def _utc_now():
    from datetime import datetime, timezone

    return datetime.now(timezone.utc)
