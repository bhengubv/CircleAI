# file_skill_store.py
#
# Port of CircleAI.Skills FileSkillStore.cs (C# — the EXACT spec).
#
# ISkillStore backed by SKILL.md files in a directory. Each file uses YAML
# front-matter for metadata and a Markdown body for the skill instructions —
# the same format used by Hermes OS1. The ``id`` front-matter field is optional;
# when absent the file name (without extension) is used as the skill ID.
#
# Expected file format:
#
#   ---
#   id: calendar-summariser
#   name: Calendar Summariser
#   description: Summarises upcoming calendar events into a concise digest
#   tags: [productivity, calendar, summaries]
#   ---
#
#   ## Instructions
#   When the user asks about their schedule, call the calendar tool...

from __future__ import annotations

import os
from datetime import datetime, timezone
from typing import Dict, List, Optional

from .contracts import ISkillStore, SkillDetail, SkillDraft, SkillSource, SkillSummary
from .in_memory_skill_store import InMemorySkillStore


def _require_not_whitespace(value: str, name: str) -> None:
    if value is None or value.strip() == "":
        raise ValueError(f"{name} must not be null or whitespace")


class FileSkillStore(ISkillStore):
    """:class:`ISkillStore` backed by SKILL.md files in a directory."""

    def __init__(self, directory_path: str) -> None:
        _require_not_whitespace(directory_path, "directory_path")
        self._directory_path = directory_path
        os.makedirs(self._directory_path, exist_ok=True)

    async def list_async(self, cancellation_token: Optional[object] = None) -> List[SkillSummary]:
        results: List[SkillSummary] = []
        for file in self._get_skill_files():
            detail = self._read_skill_file(file)
            if detail is not None:
                results.append(self._to_summary(detail))
        results.sort(key=lambda s: s.name.casefold())
        return results

    async def get_async(self, id: str, cancellation_token: Optional[object] = None) -> Optional[SkillDetail]:
        _require_not_whitespace(id, "id")
        for file in self._get_skill_files():
            detail = self._read_skill_file(file)
            if detail is not None and detail.id.casefold() == id.casefold():
                return detail
        return None

    async def search_async(self, query: str, cancellation_token: Optional[object] = None) -> List[SkillSummary]:
        if query is None or query.strip() == "":
            return []
        q = query.strip()
        results: List[SkillSummary] = []
        for file in self._get_skill_files():
            detail = self._read_skill_file(file)
            if detail is not None and self._matches_query(detail, q):
                results.append(self._to_summary(detail))
        results.sort(key=lambda s: s.name.casefold())
        return results

    async def upsert_async(
        self, id: Optional[str], draft: SkillDraft, cancellation_token: Optional[object] = None
    ) -> SkillDetail:
        if draft is None:
            raise ValueError("draft must not be None")

        effective_id = (
            InMemorySkillStore.generate_slug(draft.name) if (id is None or id.strip() == "") else id.strip()
        )

        file_path = os.path.join(self._directory_path, f"{effective_id}.md")
        if draft.tags is not None and len(draft.tags) > 0:
            tags = "[" + ", ".join(draft.tags) + "]"
        else:
            tags = "[]"

        lines = [
            "---",
            f"id: {effective_id}",
            f"name: {draft.name}",
            f"description: {draft.description}",
            f"tags: {tags}",
            "---",
            "",
        ]
        # C# AppendLine emits a trailing newline after each header line and the
        # blank separator, then Append (no newline) writes the body.
        content = "\n".join(lines) + "\n" + draft.instructions

        with open(file_path, "w", encoding="utf-8") as fh:
            fh.write(content)

        return SkillDetail(
            id=effective_id,
            name=draft.name,
            description=draft.description,
            instructions=draft.instructions,
            tags=list(draft.tags) if draft.tags is not None else [],
            source=SkillSource.File,
            last_modified=datetime.now(timezone.utc),
        )

    async def delete_async(self, id: str, cancellation_token: Optional[object] = None) -> None:
        _require_not_whitespace(id, "id")
        file_path = os.path.join(self._directory_path, f"{id}.md")
        if os.path.exists(file_path):
            os.remove(file_path)

    # ── Parsing ────────────────────────────────────────────────────────────────

    def _get_skill_files(self) -> List[str]:
        try:
            names = os.listdir(self._directory_path)
        except OSError:
            return []
        return [
            os.path.join(self._directory_path, n)
            for n in names
            if n.lower().endswith(".md") and os.path.isfile(os.path.join(self._directory_path, n))
        ]

    @staticmethod
    def _read_skill_file(file_path: str) -> Optional[SkillDetail]:
        try:
            with open(file_path, "r", encoding="utf-8") as fh:
                content = fh.read()
        except OSError:
            return None
        file_name_no_ext = os.path.splitext(os.path.basename(file_path))[0]
        return FileSkillStore.parse_skill_file(content, file_name_no_ext, file_path)

    @staticmethod
    def parse_skill_file(content: str, file_name_without_ext: str, file_path: str) -> Optional[SkillDetail]:
        if content is None or content.strip() == "":
            return None

        # Locate the YAML front-matter block between the first two "---" lines.
        lines = content.replace("\r\n", "\n").split("\n")
        if len(lines) < 2 or lines[0].strip() != "---":
            return None

        front_matter_end = -1
        for i in range(1, len(lines)):
            if lines[i].strip() == "---":
                front_matter_end = i
                break
        if front_matter_end < 0:
            return None

        # Parse front-matter key: value pairs (case-insensitive keys).
        meta: Dict[str, str] = {}
        for i in range(1, front_matter_end):
            line = lines[i]
            colon = line.find(":")
            if colon < 0:
                continue
            key = line[:colon].strip().casefold()
            value = line[colon + 1 :].strip()
            meta[key] = value

        id_val = meta.get("id")
        skill_id = id_val if (id_val is not None and id_val.strip() != "") else file_name_without_ext
        name = meta.get("name", skill_id)
        description = meta.get("description", "")
        tags = FileSkillStore._parse_tags_list(meta.get("tags", ""))

        # Everything after the closing "---" is the instructions body.
        instructions = "\n".join(lines[front_matter_end + 1 :]).strip()

        if os.path.exists(file_path):
            last_modified = datetime.fromtimestamp(os.path.getmtime(file_path), tz=timezone.utc)
        else:
            last_modified = datetime.now(timezone.utc)

        return SkillDetail(skill_id, name, description, instructions, tags, SkillSource.File, last_modified)

    @staticmethod
    def _parse_tags_list(raw: str) -> List[str]:
        """Parse a YAML inline list like ``[a, b, c]`` or a bare scalar."""
        if raw is None or raw.strip() == "":
            return []
        raw = raw.strip()
        if raw.startswith("[") and raw.endswith("]"):
            raw = raw[1:-1]
        return [t.strip() for t in raw.split(",") if t.strip() != ""]

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
