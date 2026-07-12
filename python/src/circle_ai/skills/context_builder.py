# context_builder.py
#
# Port of CircleAI.Skills SkillContextBuilder.cs (C# — the EXACT spec).
#
# Selects the most relevant skills for a user query and formats them as a
# system-prompt context block. Drop this into the B! system prompt enrichment
# pipeline to give the model knowledge of available skills before each call.
#
# The C# search-then-fall-back logic: SearchAsync first; if it matched, take the
# top-N matches; otherwise fall back to the top-N of the full list (empty string
# when the store is empty). Each candidate is loaded to full detail so the block
# can include the instructions (indented two spaces per line).

from __future__ import annotations

from typing import List, Optional

from .contracts import ISkillStore, SkillSummary


class SkillContextBuilder:
    """Selects the most relevant skills for a user query and formats them as a
    system-prompt context block."""

    def __init__(self, store: ISkillStore, max_skills: int = 5) -> None:
        if store is None:
            raise ValueError("store must not be None")
        if max_skills < 1:
            raise ValueError("max_skills must be at least 1.")
        self._store = store
        self._max_skills = max_skills

    async def build_context_async(
        self, user_query: str, cancellation_token: Optional[object] = None
    ) -> str:
        """Return a formatted system-prompt block listing the most relevant
        skills for ``user_query``. Returns an empty string when the store is
        empty or no skills match."""
        if user_query is None or user_query.strip() == "":
            return ""

        # Search for matching skills; fall back to the full list if nothing matches.
        matches = await self._store.search_async(user_query, cancellation_token)

        candidates: List[SkillSummary]
        if len(matches) > 0:
            candidates = list(matches[: self._max_skills])
        else:
            all_skills = await self._store.list_async(cancellation_token)
            if len(all_skills) == 0:
                return ""
            candidates = list(all_skills[: self._max_skills])

        # Load full detail so we can include instructions.
        parts: List[str] = ["## Available Skills"]

        for summary in candidates:
            detail = await self._store.get_async(summary.id, cancellation_token)
            if detail is None:
                continue

            parts.append("")
            parts.append(f"**{detail.id}** — {detail.description}")
            if detail.instructions is not None and detail.instructions.strip() != "":
                # Indent instructions for readability inside the system prompt.
                for line in detail.instructions.split("\n"):
                    parts.append(f"  {line}")

        # C# builds with AppendLine then TrimEnd() — join on newline and rstrip.
        return "\n".join(parts).rstrip()
