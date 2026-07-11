"""circle_ai.knowledge — port of the CircleAI.Knowledge assembly.

Markdown-on-disk knowledge notes (Obsidian/CircleUp-style, Git-diffable):

  * KnowledgeNote — YAML-frontmatter + markdown body, with ToFileText / ParseFile.
  * IKnowledgeStore — get / save / delete + streaming search-by-tag / enumerate.
  * FileSystemKnowledgeStore — one .md file per note (atomic write-then-rename).
  * InMemoryKnowledgeStore — deterministic in-memory store (no disk).
  * MarkdownEpisodicMemoryStore — IEpisodicMemoryStore backed by an
    IKnowledgeStore (each episode -> one note).

The C# ``YamlFrontmatter`` is ``internal``; it is exposed here as the
``yaml_frontmatter`` module for the note's use. C# is the exact spec.
"""
from __future__ import annotations

from .knowledge_note import KnowledgeNote
from .knowledge_store import (
    FileSystemKnowledgeStore,
    IKnowledgeStore,
    InMemoryKnowledgeStore,
)
from .markdown_episodic_memory_store import MarkdownEpisodicMemoryStore

__all__ = [
    "KnowledgeNote",
    "IKnowledgeStore",
    "FileSystemKnowledgeStore",
    "InMemoryKnowledgeStore",
    "MarkdownEpisodicMemoryStore",
]
