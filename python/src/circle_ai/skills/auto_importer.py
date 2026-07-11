# auto_importer.py
#
# Port of CircleAI.Skills SkillPackAutoImporter.cs (C# — the EXACT spec).
#
# (2.0.2) Materialises each enabled SkillPackSource via an injected
# IPackDownloader and feeds its SKILL.md files through SkillPackLoader.import.
# The C# HttpPackDownloader fetches a GitHub tarball and extracts it to disk —
# real network + disk I/O — so the deterministic port keeps only the injection
# seam (IPackDownloader) and ships an InMemoryPackDownloader for tests/hosts.
# A "materialised pack" is a mapping of relative-path -> SKILL.md content; the
# importer applies the source's SkillSubdir as a path prefix filter.

from __future__ import annotations

from abc import ABC, abstractmethod
from datetime import timedelta
from typing import Callable, Dict, List, Mapping, Optional

from .contracts import ISkillStore
from .pack_loader import SkillPackLoader, SkillPackManifest
from .pack_source import KnownSkillPacks, SkillPackSource

# A materialised pack: relative path -> SKILL.md content.
MaterialisedPack = Mapping[str, str]


class IPackDownloader(ABC):
    """Strategy for materialising a remote pack. Default in this port:
    :class:`InMemoryPackDownloader`. The C# default is an HTTP tarball
    downloader; that network path is host-injected here."""

    @abstractmethod
    async def ensure_async(
        self,
        source: SkillPackSource,
        cache_root: str,
        cache_ttl: timedelta,
        ct: Optional[object],
    ) -> MaterialisedPack:
        """Ensure ``source`` is materialised. Returns the pack contents (a
        relative-path -> content mapping)."""
        ...


class InMemoryPackDownloader(IPackDownloader):
    """Deterministic downloader — serves pre-staged pack contents from an
    in-memory registry keyed by source name. Hosts pre-register a pack's
    SKILL.md files; tests inject fakes."""

    def __init__(self) -> None:
        self._packs: Dict[str, Dict[str, str]] = {}

    def register(self, source_name: str, contents: Mapping[str, str]) -> None:
        if source_name is None or source_name.strip() == "":
            raise ValueError("sourceName required")
        self._packs[source_name] = dict(contents)

    async def ensure_async(
        self,
        source: SkillPackSource,
        cache_root: str,
        cache_ttl: timedelta,
        ct: Optional[object],
    ) -> MaterialisedPack:
        if source is None:
            raise ValueError("source must not be None")
        if cache_root is None or cache_root.strip() == "":
            raise ValueError("cacheRoot must not be null or whitespace")
        return self._packs.get(source.name, {})


class SkillPackSourcesOptions:
    """(2.0.2) Settings for :class:`SkillPackAutoImporter`. Mirrors the C#
    ``SkillPackSourcesOptions`` mutable properties."""

    def __init__(self) -> None:
        self.sources: List[SkillPackSource] = list(KnownSkillPacks.All)
        self.cache_directory: str = "circleai-skill-packs"
        self.import_default_enabled_packs: bool = True
        self.explicitly_enabled: List[str] = []
        self.cache_ttl: timedelta = timedelta(days=7)


class SkillPackAutoImporter:
    """(2.0.2) Orchestrates materialise + import for every enabled pack."""

    def __init__(
        self,
        store: ISkillStore,
        options: SkillPackSourcesOptions,
        downloader: Optional[IPackDownloader] = None,
    ) -> None:
        if store is None:
            raise ValueError("store must not be None")
        if options is None:
            raise ValueError("options must not be None")
        self._store = store
        self._options = options
        self._downloader = downloader if downloader is not None else InMemoryPackDownloader()

    async def import_enabled_async(
        self,
        on_error: Optional[Callable[[str, Exception], None]] = None,
        ct: Optional[object] = None,
    ) -> List[SkillPackManifest]:
        """Resolve which packs to import, materialise + import each. Continues on
        per-pack failure; returns one manifest per successfully-imported pack."""
        results: List[SkillPackManifest] = []
        for source in self._enumerate_enabled():
            try:
                pack = await self._downloader.ensure_async(
                    source, self._options.cache_directory, self._options.cache_ttl, ct
                )
                skill_pack = self._apply_subdir(pack, source.skill_subdir)
                if not skill_pack:
                    if on_error is not None:
                        on_error(
                            source.name,
                            FileNotFoundError(
                                f"Skill subdir '{source.skill_subdir}' not found in pack '{source.name}'."
                            ),
                        )
                    continue
                manifest = await SkillPackLoader.import_async(
                    self._store,
                    skill_pack,
                    pack_name=source.name,
                    pack_version=source.git_ref,
                    source_url=source.repo_url,
                    license=source.license,
                    on_warning=(lambda path, ex, s=source: on_error(f"{s.name}: {path}", ex)) if on_error else None,
                    ct=ct,
                )
                results.append(manifest)
            except Exception as ex:  # noqa: BLE001 — mirror C# catch (Exception ex)
                if on_error is not None:
                    on_error(source.name, ex)
        return results

    @staticmethod
    def _apply_subdir(pack: MaterialisedPack, subdir: str) -> Dict[str, str]:
        """Emulate ``Path.Combine(packDir, SkillSubdir)`` + directory existence:
        when a subdir is set, keep only entries under it (stripping the prefix).
        An empty subdir keeps the whole pack."""
        if subdir is None or subdir == "":
            return dict(pack)
        prefix = subdir.replace("\\", "/").rstrip("/") + "/"
        out: Dict[str, str] = {}
        for path, content in pack.items():
            norm = path.replace("\\", "/")
            if norm.startswith(prefix):
                out[norm[len(prefix):]] = content
        return out

    def _enumerate_enabled(self) -> List[SkillPackSource]:
        by_name = {s.name.casefold(): s for s in self._options.sources}
        seen: set = set()
        out: List[SkillPackSource] = []
        if self._options.import_default_enabled_packs:
            for s in self._options.sources:
                if s.is_default_enabled and s.name.casefold() not in seen:
                    seen.add(s.name.casefold())
                    out.append(s)
        for name in self._options.explicitly_enabled:
            src = by_name.get(name.casefold())
            if src is not None and src.name.casefold() not in seen:
                seen.add(src.name.casefold())
                out.append(src)
        return out
