# knowledge_note.py
#
# Port of CircleAI.Knowledge KnowledgeNote.cs (C# — the EXACT spec).
#
# A single markdown-on-disk knowledge entry: YAML frontmatter + markdown body.
# Serialised as ``---\nkey: value\n---\n(body)``. Git-diffable and user-editable.
#
# C# record -> frozen slotted dataclass. Guid -> uuid.UUID. DateTimeOffset ->
# datetime. ``Id.ToString("D")`` -> str(uuid) (dashed). ``CreatedAt.ToString("O")``
# -> isoformat (round-trippable). Well-known frontmatter keys (id/title/created_at/
# updated_at/tags) win over user-supplied keys and are stripped from the
# user-visible view on parse.

from __future__ import annotations

import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Dict, List, Mapping, Sequence

from . import yaml_frontmatter

_TITLE_KEY = "title"
_CREATED_KEY = "created_at"
_UPDATED_KEY = "updated_at"
_ID_KEY = "id"
_TAGS_KEY = "tags"


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass(frozen=True, slots=True)
class KnowledgeNote:
    """Mirrors ``CircleAI.Knowledge.KnowledgeNote`` — ``record(Guid Id,
    string Title, string BodyMarkdown, IReadOnlyDictionary<string,string>
    Frontmatter, IReadOnlyList<string> Tags, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)``.
    """

    id: uuid.UUID
    title: str
    body_markdown: str
    frontmatter: Mapping[str, str]
    tags: Sequence[str]
    created_at: datetime
    updated_at: datetime

    def to_file_text(self) -> str:
        """Serialise this note to its on-disk text form. Mirrors ``ToFileText``."""
        merged: Dict[str, str] = {}
        for k, v in self.frontmatter.items():
            merged[k] = v
        merged[_ID_KEY] = str(self.id)
        merged[_TITLE_KEY] = self.title
        merged[_CREATED_KEY] = self.created_at.isoformat()
        merged[_UPDATED_KEY] = self.updated_at.isoformat()
        merged[_TAGS_KEY] = ",".join(self.tags)
        return yaml_frontmatter.write(merged, self.body_markdown)

    @staticmethod
    def parse_file(text: str) -> "KnowledgeNote":
        """Parse the on-disk text form back into a note. Mirrors ``ParseFile``."""
        if text is None:
            raise ValueError("text")
        frontmatter, body = yaml_frontmatter.read(text)

        id_raw = frontmatter.get(_ID_KEY)
        parsed_id = _try_parse_guid(id_raw)
        if id_raw is None or parsed_id is None:
            raise ValueError("Knowledge note frontmatter missing or invalid 'id'.")

        title = frontmatter.get(_TITLE_KEY, "")

        created = _parse_timestamp(frontmatter, _CREATED_KEY)
        updated = _parse_timestamp(frontmatter, _UPDATED_KEY)

        raw_tags = frontmatter.get(_TAGS_KEY)
        if raw_tags is not None and raw_tags.strip() != "":
            tags = [t.strip() for t in raw_tags.split(",") if t.strip() != ""]
        else:
            tags = []

        user_frontmatter: Dict[str, str] = {}
        for k, v in frontmatter.items():
            if k in (_ID_KEY, _TITLE_KEY, _CREATED_KEY, _UPDATED_KEY, _TAGS_KEY):
                continue
            user_frontmatter[k] = v

        return KnowledgeNote(
            id=parsed_id,
            title=title,
            body_markdown=body,
            frontmatter=user_frontmatter,
            tags=tags,
            created_at=created,
            updated_at=updated,
        )


def _try_parse_guid(raw):
    if raw is None:
        return None
    try:
        return uuid.UUID(raw)
    except (ValueError, AttributeError):
        return None


def _parse_timestamp(map_: Mapping[str, str], key: str) -> datetime:
    raw = map_.get(key)
    if raw is None or raw.strip() == "":
        return _utc_now()
    try:
        return datetime.fromisoformat(raw)
    except ValueError:
        return _utc_now()
