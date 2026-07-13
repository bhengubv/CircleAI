# pack_loader.py
#
# Port of CircleAI.Skills SkillPackLoader.cs (C# — the EXACT spec).
#
# (2.0.1) Loads Claude Code-style skill packs (SKILL.md with YAML frontmatter +
# markdown body) into an ISkillStore. The C# LoadAsync walks a directory on
# disk; the deterministic in-memory port takes a "materialised pack" — a mapping
# of relative-path -> SKILL.md content (exactly what an IPackDownloader yields in
# this port) — so no filesystem access is required. The YAML-frontmatter parser,
# slugifier, and field/tag extraction are byte-faithful ports of the C# regex
# logic.

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Callable, Dict, List, Mapping, Optional, Tuple

from .contracts import ISkillStore, SkillDraft

DEFAULT_SKILL_FILE = "SKILL.md"


@dataclass(frozen=True, slots=True)
class SkillPackManifest:
    """Mirrors ``CircleAI.Skills.SkillPackManifest`` — ``record(string Name,
    string Version, string SourceUrl, string License, int SkillCount)``."""

    name: str
    version: str
    source_url: str
    license: str
    skill_count: int


@dataclass(frozen=True, slots=True)
class ParsedSkill:
    """Mirrors ``CircleAI.Skills.ParsedSkill`` — ``record(string Id, string Name,
    string Description, string Instructions, IReadOnlyList<string> Tags,
    string SourceFilePath)``."""

    id: str
    name: str
    description: str
    instructions: str
    tags: List[str]
    source_file_path: str


# C# FrontmatterRegex: ^\s*---\s*\r?\n(?<body>[\s\S]*?)\r?\n---\s*\r?\n
_FRONTMATTER = re.compile(r"^\s*---\s*\r?\n(?P<body>[\s\S]*?)\r?\n---\s*\r?\n")
_FIRST_HEADING = re.compile(r"^#\s+(?P<v>.+)$", re.MULTILINE)
_TAGS_INLINE = re.compile(r"^\s*tags\s*:\s*\[(?P<v>[^\]]*)\]", re.MULTILINE)
# NB: the item's trailing whitespace is [ \t]* (horizontal only), NOT \s* — a
# greedy \s* eats the next line's newline+indent and Python's re (unlike .NET)
# won't backtrack to re-enter the group, dropping every tag after the first.
_TAGS_BLOCK = re.compile(r"^\s*tags\s*:\s*\r?\n(?P<v>(?:\s+-\s+\S+[ \t]*\r?\n?)+)", re.MULTILINE)


def _file_name_without_ext(path: str) -> str:
    # Path.GetFileNameWithoutExtension over a POSIX/Windows-ish relative path.
    base = re.split(r"[\\/]", path)[-1]
    dot = base.rfind(".")
    return base if dot <= 0 else base[:dot]


class SkillPackLoader:
    """(2.0.1) Parses SKILL.md packs and imports them into an ISkillStore.
    Static utility mirroring the C# ``static class SkillPackLoader``."""

    DefaultSkillFile = DEFAULT_SKILL_FILE

    @staticmethod
    def load(
        pack: Mapping[str, str],
        skill_file: str = DEFAULT_SKILL_FILE,
        on_warning: Optional[Callable[[str, Exception], None]] = None,
    ) -> List[ParsedSkill]:
        """Parse every ``skill_file`` entry in the materialised ``pack`` (a
        relative-path -> content mapping). Files that fail to parse are skipped,
        with the failure raised on ``on_warning``."""
        results: List[ParsedSkill] = []
        for path, content in pack.items():
            base = re.split(r"[\\/]", path)[-1]
            if base != skill_file:
                continue
            try:
                results.append(SkillPackLoader.parse(content, path))
            except Exception as ex:  # noqa: BLE001 — mirror C# catch (Exception ex)
                if on_warning is not None:
                    on_warning(path, ex)
        return results

    @staticmethod
    async def import_async(
        store: ISkillStore,
        pack: Mapping[str, str],
        pack_name: str,
        pack_version: str = "unknown",
        source_url: str = "",
        license: str = "unknown",
        skill_file: str = DEFAULT_SKILL_FILE,
        on_warning: Optional[Callable[[str, Exception], None]] = None,
        ct: Optional[object] = None,
    ) -> SkillPackManifest:
        """Import every parsed skill into ``store`` via UpsertAsync. Each skill
        gets an extra ``pack:<name>`` tag (deduped, case-insensitively)."""
        if store is None:
            raise ValueError("store must not be None")
        if pack_name is None or pack_name.strip() == "":
            raise ValueError("packName must not be null or whitespace")

        count = 0
        for parsed in SkillPackLoader.load(pack, skill_file, on_warning):
            pack_tag = f"pack:{pack_name.lower()}"
            merged: List[str] = []
            seen = set()
            for t in list(parsed.tags) + [pack_tag]:
                if t.casefold() not in seen:
                    seen.add(t.casefold())
                    merged.append(t)
            draft = SkillDraft(
                name=parsed.name,
                description=parsed.description,
                instructions=parsed.instructions,
                tags=merged,
            )
            await store.upsert_async(parsed.id, draft, ct)
            count += 1
        return SkillPackManifest(pack_name, pack_version, source_url, license, count)

    # ─────────────────────────────────────────────────────────────────────
    # YAML-frontmatter parser (the small subset Claude Code skills use).
    # ─────────────────────────────────────────────────────────────────────

    @staticmethod
    def parse(content: str, source_file_path: str) -> ParsedSkill:
        """Parse a single SKILL.md file's text."""
        if content is None or content == "":
            raise ValueError("content must not be null or empty")
        fm_match = _FRONTMATTER.match(content)
        if fm_match is not None:
            fm_body = fm_match.group("body")
            md_body = content[fm_match.end():].lstrip("\r\n")
        else:
            fm_body = ""
            md_body = content

        name = (
            SkillPackLoader._extract_field(fm_body, "name")
            or SkillPackLoader._extract_first_heading(md_body)
            or _file_name_without_ext(source_file_path)
        )
        description = SkillPackLoader._extract_field(fm_body, "description") or SkillPackLoader._truncate(md_body, 280)
        tags = SkillPackLoader._extract_tags(fm_body)
        id = SkillPackLoader._slugify(name)
        return ParsedSkill(
            id=id,
            name=name,
            description=description,
            instructions=md_body.strip(),
            tags=tags,
            source_file_path=source_file_path,
        )

    @staticmethod
    def _extract_field(fm_body: str, field: str) -> Optional[str]:
        if fm_body is None or fm_body == "":
            return None
        pattern = re.compile(rf"^\s*{re.escape(field)}\s*:\s*(?P<v>.*)$", re.MULTILINE)
        m = pattern.search(fm_body)
        if m is None:
            return None
        value = m.group("v").strip()
        if len(value) >= 2 and (
            (value[0] == '"' and value[-1] == '"') or (value[0] == "'" and value[-1] == "'")
        ):
            value = value[1:-1]
        return None if value == "" else value

    @staticmethod
    def _extract_tags(fm_body: str) -> List[str]:
        if fm_body is None or fm_body == "":
            return []
        inline = _TAGS_INLINE.search(fm_body)
        if inline is not None:
            return [
                s.strip("'\"")
                for s in (part.strip() for part in inline.group("v").split(","))
                if s != "" and s.strip("'\"") != ""
            ]
        block = _TAGS_BLOCK.search(fm_body)
        if block is not None:
            out: List[str] = []
            for line in block.group("v").split("\n"):
                s = line.strip()
                if s == "":
                    continue
                s = s.lstrip("-").strip().strip("'\"")
                if s != "":
                    out.append(s)
            return out
        return []

    @staticmethod
    def _extract_first_heading(md_body: str) -> Optional[str]:
        m = _FIRST_HEADING.search(md_body)
        return m.group("v").strip() if m is not None else None

    @staticmethod
    def _truncate(s: str, max_len: int) -> str:
        s = s.replace("\r", " ").replace("\n", " ").strip()
        if len(s) <= max_len:
            return s
        return s[: max_len - 1] + "…"

    @staticmethod
    def _slugify(name: str) -> str:
        chars: List[str] = []
        prev_dash = False
        for ch in name:
            if ch.isalnum():
                chars.append(ch.lower())
                prev_dash = False
            elif not prev_dash and len(chars) > 0:
                chars.append("-")
                prev_dash = True
        slug = "".join(chars).rstrip("-")
        return "unnamed" if slug == "" else slug
