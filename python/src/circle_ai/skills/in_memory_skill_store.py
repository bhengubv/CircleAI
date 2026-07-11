# in_memory_skill_store.py
#
# Port of CircleAI.Skills InMemorySkillStore.cs (C# — the EXACT spec).
#
# Thread-safe in-memory ISkillStore. List/Search order by Name
# case-insensitively; Upsert auto-generates a slug ID from the draft Name when
# none is supplied and stamps Source=InMemory + LastModified=UtcNow.
# GenerateSlug ports the C# regex chain exactly; empty results fall back to a
# 32-char lowercase-hex GUID (Guid.NewGuid().ToString("N")) == uuid4().hex.

from __future__ import annotations

import re
import threading
import uuid
from datetime import datetime, timezone
from typing import Dict, List, Optional

from .contracts import ISkillStore, SkillDetail, SkillDraft, SkillSource, SkillSummary

_WS = re.compile(r"\s+")
_NON_SLUG = re.compile(r"[^a-z0-9\-]")
_MULTI_DASH = re.compile(r"-{2,}")


def _require_not_whitespace(value: str, name: str) -> None:
    if value is None or value.strip() == "":
        raise ValueError(f"{name} must not be null or whitespace")


class InMemorySkillStore(ISkillStore):
    def __init__(self) -> None:
        self._skills: Dict[str, SkillDetail] = {}
        self._lock = threading.Lock()

    async def list_async(self, cancellation_token: Optional[object] = None) -> List[SkillSummary]:
        with self._lock:
            details = list(self._skills.values())
        results = [self._to_summary(d) for d in details]
        results.sort(key=lambda s: s.name.casefold())
        return results

    async def get_async(self, id: str, cancellation_token: Optional[object] = None) -> Optional[SkillDetail]:
        _require_not_whitespace(id, "id")
        with self._lock:
            return self._skills.get(id)

    async def search_async(self, query: str, cancellation_token: Optional[object] = None) -> List[SkillSummary]:
        if query is None or query.strip() == "":
            return []
        q = query.strip()
        with self._lock:
            details = list(self._skills.values())
        results = [self._to_summary(s) for s in details if self._matches_query(s, q)]
        results.sort(key=lambda s: s.name.casefold())
        return results

    async def upsert_async(
        self, id: Optional[str], draft: SkillDraft, cancellation_token: Optional[object] = None
    ) -> SkillDetail:
        if draft is None:
            raise ValueError("draft must not be None")
        effective_id = self.generate_slug(draft.name) if (id is None or id.strip() == "") else id.strip()
        detail = SkillDetail(
            id=effective_id,
            name=draft.name,
            description=draft.description,
            instructions=draft.instructions,
            tags=list(draft.tags) if draft.tags is not None else [],
            source=SkillSource.InMemory,
            last_modified=datetime.now(timezone.utc),
        )
        with self._lock:
            self._skills[effective_id] = detail
        return detail

    async def delete_async(self, id: str, cancellation_token: Optional[object] = None) -> None:
        _require_not_whitespace(id, "id")
        with self._lock:
            self._skills.pop(id, None)

    # ── Helpers ───────────────────────────────────────────────────────────────

    @staticmethod
    def _to_summary(d: SkillDetail) -> SkillSummary:
        return SkillSummary(d.id, d.name, d.description, d.tags, d.source)

    @staticmethod
    def _matches_query(s: SkillDetail, query: str) -> bool:
        q = query.casefold()
        return (
            q in s.name.casefold()
            or q in s.description.casefold()
            or any(q in t.casefold() for t in s.tags)
        )

    @staticmethod
    def generate_slug(name: str) -> str:
        """Convert a display name to a URL-safe lowercase slug.
        ``"My Skill"`` -> ``"my-skill"``. Empty results fall back to a
        32-char lowercase-hex GUID."""
        if name is None or name.strip() == "":
            return uuid.uuid4().hex
        slug = name.strip().lower()
        slug = _WS.sub("-", slug)
        slug = _NON_SLUG.sub("", slug)
        slug = _MULTI_DASH.sub("-", slug).strip("-")
        return uuid.uuid4().hex if slug == "" else slug
