# paca_docs.py
#
# Port of CircleAI.Workflows PacaDocs.cs (C# — the EXACT spec).
#
# (3.3.0) Project-level living documents with folders, version snapshots,
# activity feed, task/epic linkage, and @mentions of humans + agents (paca
# port). Edit() writes an immutable snapshot of the PREVIOUS content, appends an
# "edited" / "ai-edited" activity entry, and returns the handles mentioned in the
# new content. Records → frozen dataclasses; record-with → dataclasses.replace.

from __future__ import annotations

import re
import threading
import uuid
from dataclasses import dataclass, replace
from datetime import datetime, timezone
from typing import Callable, Dict, List, Optional, Tuple


@dataclass(frozen=True, slots=True)
class DocNode:
    """(3.3.0) A doc node (folder OR document)."""

    id: str
    project_id: str
    parent_id: Optional[str]
    is_folder: bool
    title: str
    content_json: str
    created_at_utc: datetime
    deleted_at_utc: Optional[datetime]


@dataclass(frozen=True, slots=True)
class DocVersion:
    """(3.3.0) One immutable snapshot of a doc."""

    version_id: str
    doc_id: str
    content_json: str
    saved_at_utc: datetime
    author_member_id: str


@dataclass(frozen=True, slots=True)
class DocActivity:
    """(3.3.0) One document-activity event.

    ``action`` is "created" / "edited" / "ai-edited" / "linked" / "commented"."""

    activity_id: str
    doc_id: str
    author_member_id: str
    action: str
    detail: Optional[str]
    at: datetime


@dataclass(frozen=True, slots=True)
class DocLink:
    """(3.3.0) Link between a doc section and a task / epic."""

    link_id: str
    doc_id: str
    section_anchor: str
    project_id: str
    task_number: int


_MENTION_PATTERN = re.compile(r"@([a-zA-Z0-9_\-]+)")


class PacaDocService:
    """(3.3.0) In-memory doc service."""

    def __init__(self, clock: Optional[Callable[[], datetime]] = None) -> None:
        self._clock = clock if clock is not None else (lambda: datetime.now(timezone.utc))
        self._nodes: Dict[str, DocNode] = {}
        self._versions: Dict[str, List[DocVersion]] = {}
        self._activity: Dict[str, List[DocActivity]] = {}
        self._links: Dict[str, List[DocLink]] = {}
        self._lock = threading.Lock()

    def create_folder(self, id: str, project_id: str, parent_id: Optional[str], title: str) -> DocNode:
        return self._create(id, project_id, parent_id, True, title, "{}", "system")

    def create_document(
        self,
        id: str,
        project_id: str,
        parent_id: Optional[str],
        title: str,
        content_json: str,
        author_member_id: str,
    ) -> DocNode:
        return self._create(id, project_id, parent_id, False, title, content_json, author_member_id)

    def _create(
        self,
        id: str,
        project_id: str,
        parent_id: Optional[str],
        is_folder: bool,
        title: str,
        content_json: str,
        author_member_id: str,
    ) -> DocNode:
        if id is None or id.strip() == "":
            raise ValueError("id required")
        if project_id is None or project_id.strip() == "":
            raise ValueError("projectId required")
        node = DocNode(
            id,
            project_id,
            parent_id,
            is_folder,
            title if title is not None else "",
            content_json if content_json is not None else "{}",
            self._clock(),
            None,
        )
        with self._lock:
            if id in self._nodes:
                raise RuntimeError(f"Doc '{id}' already exists.")
            self._nodes[id] = node
            if not is_folder:
                self._versions[id] = []
                self._activity[id] = [
                    DocActivity(uuid.uuid4().hex, id, author_member_id, "created", None, self._clock())
                ]
        return node

    def get(self, id: str) -> Optional[DocNode]:
        with self._lock:
            n = self._nodes.get(id)
            return n if (n is not None and n.deleted_at_utc is None) else None

    def list_children(self, project_id: str, parent_id: Optional[str]) -> List[DocNode]:
        with self._lock:
            children = [
                n
                for n in self._nodes.values()
                if n.project_id == project_id and n.parent_id == parent_id and n.deleted_at_utc is None
            ]
        return sorted(children, key=lambda n: n.title)

    def edit(
        self, id: str, new_content_json: str, author_member_id: str, is_ai_edit: bool = False
    ) -> List[str]:
        """(3.3.0) Edit a document: writes a new version + activity entry,
        returns mentioned handles."""
        with self._lock:
            node = self._nodes.get(id)
            if node is None or node.is_folder or node.deleted_at_utc is not None:
                raise RuntimeError(f"Doc '{id}' is not editable.")

            updated = replace(node, content_json=new_content_json if new_content_json is not None else "{}")
            self._nodes[id] = updated

            # Snapshot captures the PREVIOUS content (C# uses node.ContentJson).
            version = DocVersion(uuid.uuid4().hex, id, node.content_json, self._clock(), author_member_id)
            self._versions[id].append(version)

            self._activity[id].append(
                DocActivity(
                    uuid.uuid4().hex,
                    id,
                    author_member_id,
                    "ai-edited" if is_ai_edit else "edited",
                    None,
                    self._clock(),
                )
            )

        return self._extract_mentions(new_content_json if new_content_json is not None else "")

    def versions(self, doc_id: str) -> List[DocVersion]:
        with self._lock:
            lst = self._versions.get(doc_id)
            return list(lst) if lst is not None else []

    def diff_lines(self, before: str, after: str) -> Tuple[List[str], List[str]]:
        """(3.3.0) Cheap diff between two versions — returns added + removed text
        lines."""
        b = set((before if before is not None else "").split("\n"))
        a = set((after if after is not None else "").split("\n"))
        return list(a - b), list(b - a)

    def activity(self, doc_id: str) -> List[DocActivity]:
        with self._lock:
            lst = self._activity.get(doc_id)
            return list(lst) if lst is not None else []

    def link(self, doc_id: str, section_anchor: str, project_id: str, task_number: int) -> DocLink:
        link = DocLink(uuid.uuid4().hex, doc_id, section_anchor, project_id, task_number)
        with self._lock:
            bucket = self._links.get(doc_id)
            if bucket is None:
                bucket = []
                self._links[doc_id] = bucket
            bucket.append(link)
            # Mirrors C#: unconditionally append a "linked" activity entry.
            self._activity[doc_id].append(
                DocActivity(
                    uuid.uuid4().hex,
                    doc_id,
                    "system",
                    "linked",
                    f"{project_id}-{task_number}@{section_anchor}",
                    self._clock(),
                )
            )
        return link

    def links(self, doc_id: str) -> List[DocLink]:
        with self._lock:
            lst = self._links.get(doc_id)
            return list(lst) if lst is not None else []

    @staticmethod
    def _extract_mentions(content: str) -> List[str]:
        # Case-insensitive de-dupe (HashSet<string> with OrdinalIgnoreCase),
        # keeping the first-seen casing of each handle.
        result: List[str] = []
        seen: set = set()
        for m in _MENTION_PATTERN.finditer(content):
            handle = m.group(1)
            key = handle.casefold()
            if key not in seen:
                seen.add(key)
                result.append(handle)
        return result
