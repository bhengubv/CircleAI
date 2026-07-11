"""circle_ai.skills — port of the CircleAI.Skills assembly.

B! skill domain: the SkillSource enum, the summary / detail / draft records, the
ISkillStore contract + thread-safe InMemorySkillStore (with the GenerateSlug
slugifier), the SkillPackSource declaration + KnownSkillPacks catalogue, the
SKILL.md pack loader/parser (SkillPackLoader + SkillPackManifest + ParsedSkill),
and the auto-importer (IPackDownloader seam + InMemoryPackDownloader +
SkillPackSourcesOptions + SkillPackAutoImporter). C# is the exact spec — the
HTTP tarball downloader is the injected network seam.

Public surface:

  * SkillSource                                           — enum.
  * SkillSummary / SkillDetail / SkillDraft              — records.
  * ISkillStore / InMemorySkillStore.
  * SkillPackSource / KnownSkillPacks.
  * SkillPackManifest / ParsedSkill / SkillPackLoader.
  * IPackDownloader / InMemoryPackDownloader / SkillPackSourcesOptions /
    SkillPackAutoImporter.
"""
from __future__ import annotations

from .auto_importer import (
    IPackDownloader,
    InMemoryPackDownloader,
    MaterialisedPack,
    SkillPackAutoImporter,
    SkillPackSourcesOptions,
)
from .contracts import (
    ISkillStore,
    SkillDetail,
    SkillDraft,
    SkillSource,
    SkillSummary,
)
from .in_memory_skill_store import InMemorySkillStore
from .pack_loader import ParsedSkill, SkillPackLoader, SkillPackManifest
from .pack_source import KnownSkillPacks, SkillPackSource

__all__ = [
    "SkillSource",
    "SkillSummary",
    "SkillDetail",
    "SkillDraft",
    "ISkillStore",
    "InMemorySkillStore",
    "SkillPackSource",
    "KnownSkillPacks",
    "SkillPackManifest",
    "ParsedSkill",
    "SkillPackLoader",
    "IPackDownloader",
    "InMemoryPackDownloader",
    "MaterialisedPack",
    "SkillPackSourcesOptions",
    "SkillPackAutoImporter",
]
