# core/model_downloader.py
#
# Port of:
#   • CircleAI.Core.IModelDownloader
#   • CircleAI.Core.ModelDownloader (+ DownloadProgressReport + ProgressChanged)
#
# The C# ModelDownloader loads an embedded registry.json resource. Python has
# no embedded resources, so the registry is injectable: pass a dict of entries
# (or a path to a registry.json). The parse rules match C# exactly — walk the
# top-level object, skip any value that is not an object (so free-text metadata
# like "Notes" coexists with model entries). Source matching, candidate-URL
# building, bundle detection and fallthrough are ported faithfully.

from __future__ import annotations

import json
import os
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Callable, Dict, List, Mapping, Optional, Sequence
from urllib.parse import urlparse

from .model_source import DownloadProgress, IModelSource

__all__ = [
    "IModelDownloader",
    "ModelDownloader",
    "DownloadProgressReport",
    "ModelEntry",
    "BundleFileEntry",
]


# ─────────────────────────────────────────────────────────────────────────────
# Registry row shape — mirrors ModelDownloader.ModelEntry / BundleFileEntry.
# ─────────────────────────────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class BundleFileEntry:
    """One file inside a bundle entry."""

    name: str
    sha256: str
    size_bytes: int = 0


@dataclass(frozen=True, slots=True)
class ModelEntry:
    """A registry row. Supports the legacy single-file shape AND the bundle
    shape (``repo`` + ``bundle_files``); ``is_bundle`` selects which."""

    file_name: str = ""
    primary_url: Optional[str] = None
    fallback_url: Optional[str] = None
    checksum: Optional[str] = None
    size_bytes: int = 0
    version: Optional[str] = None
    architecture: Optional[str] = None
    quantization_type: Optional[str] = None
    repo: Optional[str] = None
    total_bytes: int = 0
    bundle_files: Optional[List[BundleFileEntry]] = None

    @property
    def is_bundle(self) -> bool:
        return self.bundle_files is not None and len(self.bundle_files) > 0

    @staticmethod
    def from_json(obj: Mapping[str, object]) -> "ModelEntry":
        """Build from a case-insensitive JSON object (C# PropertyNameCaseInsensitive)."""
        low = {str(k).lower(): v for k, v in obj.items()}

        def _get(key: str):
            return low.get(key.lower())

        bundle_raw = _get("BundleFiles")
        bundle_files: Optional[List[BundleFileEntry]] = None
        if isinstance(bundle_raw, list):
            bundle_files = []
            for bf in bundle_raw:
                if isinstance(bf, Mapping):
                    bl = {str(k).lower(): v for k, v in bf.items()}
                    bundle_files.append(
                        BundleFileEntry(
                            name=str(bl.get("name", "")),
                            sha256=str(bl.get("sha256", "")),
                            size_bytes=int(bl.get("sizebytes", 0) or 0),
                        )
                    )

        return ModelEntry(
            file_name=str(_get("FileName") or ""),
            primary_url=_opt_str(_get("PrimaryUrl")),
            fallback_url=_opt_str(_get("FallbackUrl")),
            checksum=_opt_str(_get("Checksum")),
            size_bytes=int(_get("SizeBytes") or 0),
            version=_opt_str(_get("Version")),
            architecture=_opt_str(_get("Architecture")),
            quantization_type=_opt_str(_get("QuantizationType")),
            repo=_opt_str(_get("Repo")),
            total_bytes=int(_get("TotalBytes") or 0),
            bundle_files=bundle_files,
        )


def _opt_str(v: object) -> Optional[str]:
    return None if v is None else str(v)


# ─────────────────────────────────────────────────────────────────────────────
# DownloadProgressReport — class-shaped progress (ModelDownloader inner type).
# ─────────────────────────────────────────────────────────────────────────────


@dataclass(slots=True)
class DownloadProgressReport:
    """Progress report shape emitted during downloads. Mirrors
    :class:`DownloadProgress` as a mutable class for consumer compatibility."""

    file_name: str = ""
    bytes_received: int = 0
    total_bytes: int = 0
    bytes_per_second: float = 0.0
    estimated_time_remaining: float = 0.0


# ─────────────────────────────────────────────────────────────────────────────
# IModelDownloader — the downloader contract.
# ─────────────────────────────────────────────────────────────────────────────


class IModelDownloader(ABC):
    """Downloads a model file (or set of files) to local storage.

    Implementations walk a chain of :class:`IModelSource` instances so that,
    e.g., ModelScope API can be tried first and ModelScope CDN second.
    """

    @abstractmethod
    async def download_model_async(
        self, model_id: str, local_path: str, ct: object = None
    ) -> None:
        """Download a model identified by *model_id* to *local_path*.
        Implementations resolve the URL set internally."""
        raise NotImplementedError

    @abstractmethod
    async def download_from_candidates_async(
        self,
        candidate_urls: Sequence[str],
        local_file_path: str,
        progress: Optional[Callable[[DownloadProgress], None]] = None,
        ct: object = None,
    ) -> str:
        """Download a single model file by trying each candidate URL in order.
        Returns the name of the source that succeeded."""
        raise NotImplementedError


# ─────────────────────────────────────────────────────────────────────────────
# ModelDownloader — source-agnostic downloader with fallthrough.
# ─────────────────────────────────────────────────────────────────────────────


class ModelDownloader(IModelDownloader):
    """Source-agnostic model downloader. Walks a list of :class:`IModelSource`
    instances in order, falling through on failure so that one supplier going
    dark does not break model bootstrap."""

    def __init__(
        self,
        sources: Sequence[IModelSource],
        owns_sources: bool = False,
        registry: Optional[Mapping[str, object]] = None,
        registry_path: Optional[str] = None,
    ) -> None:
        if sources is None:
            raise ValueError("sources")
        if len(sources) == 0:
            raise ValueError("At least one model source is required")
        self._sources: List[IModelSource] = list(sources)
        self._owns_sources = owns_sources
        self._disposed = False
        self._registry_arg = registry
        self._registry_path = registry_path
        self._registry_cache: Optional[Dict[str, ModelEntry]] = None
        # Event subscribers — invoked with a DownloadProgressReport.
        self._progress_handlers: List[Callable[[DownloadProgressReport], None]] = []

    # ── ProgressChanged event ─────────────────────────────────────────────────

    def add_progress_handler(
        self, handler: Callable[[DownloadProgressReport], None]
    ) -> None:
        """Subscribe to progress reports (mirrors C# ``ProgressChanged +=``)."""
        self._progress_handlers.append(handler)

    def remove_progress_handler(
        self, handler: Callable[[DownloadProgressReport], None]
    ) -> None:
        """Unsubscribe (mirrors C# ``ProgressChanged -=``)."""
        try:
            self._progress_handlers.remove(handler)
        except ValueError:
            pass

    def _raise_progress(self, report: DownloadProgressReport) -> None:
        for h in list(self._progress_handlers):
            h(report)

    # ── registry ──────────────────────────────────────────────────────────────

    @property
    def _registry(self) -> Dict[str, ModelEntry]:
        if self._registry_cache is None:
            self._registry_cache = self._load_registry()
        return self._registry_cache

    def _load_registry(self) -> Dict[str, ModelEntry]:
        if self._registry_arg is not None:
            return self._parse_registry_obj(self._registry_arg)
        if self._registry_path is not None and os.path.isfile(self._registry_path):
            with open(self._registry_path, "r", encoding="utf-8") as fh:
                return self._parse_registry_obj(json.load(fh))
        return {}

    @staticmethod
    def _parse_registry_obj(root: object) -> Dict[str, ModelEntry]:
        registry: Dict[str, ModelEntry] = {}
        if not isinstance(root, Mapping):
            return registry
        for key, value in root.items():
            # Skip metadata fields (Notes, $schema, etc.) — only object values
            # are entries. Mirrors the C# ValueKind != Object skip.
            if not isinstance(value, Mapping):
                continue
            registry[str(key)] = ModelEntry.from_json(value)
        return registry

    # ── IModelDownloader ───────────────────────────────────────────────────────

    async def download_model_async(
        self, model_id: str, local_path: str, ct: object = None
    ) -> None:
        if self._disposed:
            raise RuntimeError("ModelDownloader is disposed")
        if model_id is None or model_id.strip() == "":
            raise ValueError("model_id")
        if local_path is None or local_path.strip() == "":
            raise ValueError("local_path")

        entry = self._registry.get(model_id)
        if entry is None:
            known = ", ".join(self._registry.keys())
            raise KeyError(
                f"Model '{model_id}' is not in the embedded registry. Known models: {known}"
            )

        os.makedirs(local_path, exist_ok=True)

        if entry.is_bundle:
            raise RuntimeError(
                f"Model '{model_id}' is a multi-file MNN bundle (registry entry has "
                "BundleFiles[]). Use CircleAI.Inference.ModelDownloadService.EnsureBundleAsync "
                "from MnnInferenceBridgeFactory instead — this legacy single-file "
                "downloader cannot fetch a multi-file bundle."
            )

        target_file = os.path.join(local_path, entry.file_name)
        candidates = self._build_candidate_list(entry)
        if len(candidates) == 0:
            raise RuntimeError(
                f"Model '{model_id}' has no PrimaryUrl or FallbackUrl configured."
            )

        def bridge(p: DownloadProgress) -> None:
            self._raise_progress(
                DownloadProgressReport(
                    file_name=p.file_name,
                    bytes_received=p.bytes_received,
                    total_bytes=p.total_bytes,
                    bytes_per_second=p.bytes_per_second,
                    estimated_time_remaining=p.estimated_time_remaining,
                )
            )

        try:
            await self.download_from_candidates_async(candidates, target_file, bridge, ct)
        except Exception:
            self._cleanup_partial_file(target_file)
            raise

    async def download_from_candidates_async(
        self,
        candidate_urls: Sequence[str],
        local_file_path: str,
        progress: Optional[Callable[[DownloadProgress], None]] = None,
        ct: object = None,
    ) -> str:
        if self._disposed:
            raise RuntimeError("ModelDownloader is disposed")
        if candidate_urls is None:
            raise ValueError("candidate_urls")
        if len(candidate_urls) == 0:
            raise ValueError("At least one candidate URL is required")
        if local_file_path is None or local_file_path.strip() == "":
            raise ValueError("local_file_path")

        d = os.path.dirname(local_file_path)
        if d:
            os.makedirs(d, exist_ok=True)

        failures: List[str] = []
        for url in candidate_urls:
            if url is None or url.strip() == "":
                continue
            source = self._match_source(url)
            if source is None:
                failures.append(f"(no registered source for '{url}')")
                continue
            try:
                await source.download_async(url, local_file_path, progress, ct)
                return source.name
            except Exception as ex:  # noqa: BLE001 — mirror C# catch-all fallthrough
                failures.append(f"{source.name}: {ex}")
                self._cleanup_partial_file(local_file_path)

        raise RuntimeError("All model sources failed:\n  " + "\n  ".join(failures))

    # ── internals ──────────────────────────────────────────────────────────────

    def _match_source(self, url: str) -> Optional[IModelSource]:
        parsed = urlparse(url)
        if not parsed.scheme or not parsed.netloc:
            # C# Uri.TryCreate(Absolute) fails for non-absolute URLs.
            # Local fixtures use file:// (absolute) so those still match below.
            if parsed.scheme != "file":
                return None
        host = (parsed.hostname or "").lower()

        for s in self._sources:
            if s.name.lower() in host:
                return s

        if "modelscope" in host:
            for s in self._sources:
                if s.name.lower() == "modelscope":
                    return s

        # For file:// fixture URLs, fall back to the first source so the
        # network-free download path is exercisable.
        if parsed.scheme == "file" and self._sources:
            return self._sources[0]

        return None

    @staticmethod
    def _build_candidate_list(entry: ModelEntry) -> List[str]:
        out: List[str] = []
        if entry.primary_url and entry.primary_url.strip():
            out.append(entry.primary_url)
        if entry.fallback_url and entry.fallback_url.strip():
            out.append(entry.fallback_url)
        return out

    @staticmethod
    def _cleanup_partial_file(path: str) -> None:
        try:
            if os.path.exists(path):
                os.remove(path)
        except OSError:
            pass  # best effort

    def dispose(self) -> None:
        if self._disposed:
            return
        if self._owns_sources:
            for s in self._sources:
                disp = getattr(s, "dispose", None)
                if callable(disp):
                    disp()
        self._disposed = True
