"""ModelScope catalog client — port of CircleAI.Core.Models.ModelScopeCatalogClient.

Async HTTP via `asyncio.to_thread(urllib.request.urlopen)` so this stays
stdlib-only — no aiohttp/httpx hard dep. Caches the catalog to disk as
JSON, gates refresh on cadence + (optionally) host-supplied connectivity.
"""
from __future__ import annotations

import asyncio
import dataclasses
import datetime as _dt
import json
import os
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from enum import IntEnum
from pathlib import Path
from typing import Optional

from ..models.models import BundleFile
from .catalog_signature_verifier import (
    CatalogSignatureResult,
    ICatalogSignatureVerifier,
    NullCatalogSignatureVerifier,
)


# ── Options / cadence ──────────────────────────────────────────────────────


class CatalogRefreshCadence(IntEnum):
    """How often to refresh from the live API."""

    ON_STARTUP = 0
    DAILY = 1
    MANUAL = 2
    NEVER = 3


def _default_cache_dir() -> str:
    base = os.environ.get("APPDATA") or os.path.expanduser("~/.local/share")
    return os.path.join(base, "CircleAI", "catalog")


@dataclass
class ModelScopeCatalogOptions:
    base_uri: str = "https://www.modelscope.cn"
    cache_directory: str = dataclasses.field(default_factory=_default_cache_dir)
    cadence: CatalogRefreshCadence = CatalogRefreshCadence.ON_STARTUP
    filter: str = "MNN"
    page_size: int = 100
    user_agent: str = "Mozilla/5.0 (Circle AI SDK) CircleAI-Python/1.5"


# ── Registry record (lightweight — mirrors ModelEntry shape) ───────────────


@dataclass(frozen=True)
class ModelEntry:
    name: str
    version: str
    quantization: str = ""
    url: Optional[str] = None
    checksum: Optional[str] = None
    repo: Optional[str] = None
    total_bytes: int = 0
    bundle_files: Optional[list[BundleFile]] = None
    min_ram_gb: float = 0.0
    min_storage_gb: float = 0.0
    capabilities: Optional[list[str]] = None
    quality_rank: int = 0

    @property
    def is_bundle(self) -> bool:
        return bool(self.bundle_files)


@dataclass(frozen=True)
class ModelRegistry:
    registry_url: str
    last_updated: _dt.datetime
    models: list[ModelEntry]


# ── ModelScopeCatalogClient ────────────────────────────────────────────────


class ModelScopeCatalogClient:
    """Discovers MNN-compatible models on ModelScope, caches to disk, refreshes per cadence."""

    def __init__(
        self,
        options: Optional[ModelScopeCatalogOptions] = None,
        verifier: Optional[ICatalogSignatureVerifier] = None,
        network_type_provider=None,  # callable returning "online" / "none" / None
    ) -> None:
        self._options = options or ModelScopeCatalogOptions()
        self._verifier = verifier or NullCatalogSignatureVerifier.instance()
        self._network_type_provider = network_type_provider
        self._refreshed_this_process = False
        Path(self._options.cache_directory).mkdir(parents=True, exist_ok=True)

    # ── Paths ───────────────────────────────────────────────────────────

    @property
    def cache_file_path(self) -> str:
        return os.path.join(self._options.cache_directory, "catalog.json")

    @property
    def signature_file_path(self) -> str:
        return os.path.join(self._options.cache_directory, "catalog.sig")

    # ── Public API ──────────────────────────────────────────────────────

    async def is_refresh_due_async(self) -> bool:
        if self._options.cadence == CatalogRefreshCadence.NEVER:
            return False
        if self._options.cadence == CatalogRefreshCadence.MANUAL:
            return False

        # Connectivity gate
        if self._network_type_provider is not None:
            try:
                net = self._network_type_provider()
                if net and str(net).lower() == "none":
                    return False
            except Exception:
                pass

        if not os.path.exists(self.cache_file_path):
            return True

        if self._options.cadence == CatalogRefreshCadence.ON_STARTUP:
            return not self._refreshed_this_process

        # DAILY
        try:
            mtime = _dt.datetime.fromtimestamp(
                os.path.getmtime(self.cache_file_path), tz=_dt.timezone.utc
            )
            now_utc = _dt.datetime.now(_dt.timezone.utc)
            return mtime.date() < now_utc.date()
        except OSError:
            return False

    def load_from_disk(self) -> Optional[ModelRegistry]:
        if not os.path.exists(self.cache_file_path):
            return None
        try:
            with open(self.cache_file_path, encoding="utf-8") as f:
                raw = json.load(f)
            return _registry_from_dict(raw)
        except (OSError, json.JSONDecodeError, KeyError):
            return None

    async def get_cached_catalog_async(
        self, accept_stale_on_error: bool = True
    ) -> Optional[ModelRegistry]:
        if await self.is_refresh_due_async():
            try:
                return await self.refresh_async()
            except Exception:
                if not accept_stale_on_error:
                    raise
        return self.load_from_disk()

    async def refresh_async(self) -> ModelRegistry:
        registry = await self._fetch_live_async()
        json_bytes = json.dumps(
            _registry_to_dict(registry), ensure_ascii=False
        ).encode("utf-8")

        existing_sig = None
        if os.path.exists(self.signature_file_path):
            try:
                with open(self.signature_file_path, encoding="utf-8") as f:
                    existing_sig = f.read().strip() or None
            except OSError:
                existing_sig = None

        sig_result = self._verifier.verify(json_bytes, existing_sig)
        if sig_result == CatalogSignatureResult.INVALID:
            raise RuntimeError(
                "Catalog signature did not verify against the configured public key. "
                "Keeping previous cache; not applying fetched payload."
            )

        with open(self.cache_file_path, "wb") as f:
            f.write(json_bytes)
        self._refreshed_this_process = True
        return registry

    # ── Internals ───────────────────────────────────────────────────────

    async def _fetch_live_async(self) -> ModelRegistry:
        # List models that match the filter.
        url = (
            f"{self._options.base_uri}/api/v1/models"
            f"?Name={urllib.parse.quote(self._options.filter)}"
            f"&PageSize={self._options.page_size}"
        )
        listing = await asyncio.to_thread(self._http_get_json, url)

        entries: list[ModelEntry] = []
        # ModelScope shape: { "Data": { "Model": [ {Name, Path, ...} ] } }
        # Be tolerant — we only need Name + Path.
        models = (listing.get("Data") or {}).get("Model") or []
        for m in models:
            name = m.get("Name") or m.get("ChineseName") or ""
            path = m.get("Path") or m.get("ChineseName") or ""
            if not name or not path:
                continue

            files_url = (
                f"{self._options.base_uri}/api/v1/models/{path}/repo/files"
                "?Revision=master"
            )
            try:
                files_resp = await asyncio.to_thread(
                    self._http_get_json, files_url
                )
            except urllib.error.URLError:
                continue

            file_list = (files_resp.get("Data") or {}).get("Files") or []
            bundle = [
                BundleFile(
                    name=f.get("Path") or f.get("Name") or "",
                    sha256=str(f.get("Sha256") or ""),
                    size_bytes=int(f.get("Size") or 0),
                )
                for f in file_list
                if (f.get("Path") or f.get("Name"))
            ]
            total = sum(b.size_bytes for b in bundle)
            entries.append(
                ModelEntry(
                    name=name,
                    version=str(m.get("Revision") or "master"),
                    quantization=str(m.get("Quantization") or ""),
                    repo=path,
                    total_bytes=total,
                    bundle_files=bundle,
                )
            )

        return ModelRegistry(
            registry_url=self._options.base_uri,
            last_updated=_dt.datetime.now(_dt.timezone.utc),
            models=entries,
        )

    def _http_get_json(self, url: str) -> dict:
        req = urllib.request.Request(
            url, headers={"User-Agent": self._options.user_agent}
        )
        with urllib.request.urlopen(req, timeout=10) as resp:
            return json.loads(resp.read().decode("utf-8"))


# ── (De)serialisation helpers ──────────────────────────────────────────────


def _registry_to_dict(reg: ModelRegistry) -> dict:
    return {
        "RegistryUrl": reg.registry_url,
        "LastUpdated": reg.last_updated.isoformat(),
        "Models": [_entry_to_dict(e) for e in reg.models],
    }


def _registry_from_dict(d: dict) -> ModelRegistry:
    return ModelRegistry(
        registry_url=d.get("RegistryUrl") or "",
        last_updated=_dt.datetime.fromisoformat(
            d.get("LastUpdated") or _dt.datetime.now(_dt.timezone.utc).isoformat()
        ),
        models=[_entry_from_dict(e) for e in (d.get("Models") or [])],
    )


def _entry_to_dict(e: ModelEntry) -> dict:
    return {
        "Name": e.name,
        "Version": e.version,
        "Quantization": e.quantization,
        "Url": e.url,
        "Checksum": e.checksum,
        "Repo": e.repo,
        "TotalBytes": e.total_bytes,
        "BundleFiles": (
            None
            if e.bundle_files is None
            else [
                {"Name": f.name, "Sha256": f.sha256, "SizeBytes": f.size_bytes}
                for f in e.bundle_files
            ]
        ),
        "MinRamGb": e.min_ram_gb,
        "MinStorageGb": e.min_storage_gb,
        "Capabilities": e.capabilities,
        "QualityRank": e.quality_rank,
    }


def _entry_from_dict(d: dict) -> ModelEntry:
    bundle = None
    if d.get("BundleFiles"):
        bundle = [
            BundleFile(
                name=str(f.get("Name") or ""),
                sha256=str(f.get("Sha256") or ""),
                size_bytes=int(f.get("SizeBytes") or 0),
            )
            for f in d["BundleFiles"]
        ]
    return ModelEntry(
        name=str(d.get("Name") or ""),
        version=str(d.get("Version") or ""),
        quantization=str(d.get("Quantization") or ""),
        url=d.get("Url"),
        checksum=d.get("Checksum"),
        repo=d.get("Repo"),
        total_bytes=int(d.get("TotalBytes") or 0),
        bundle_files=bundle,
        min_ram_gb=float(d.get("MinRamGb") or 0.0),
        min_storage_gb=float(d.get("MinStorageGb") or 0.0),
        capabilities=d.get("Capabilities"),
        quality_rank=int(d.get("QualityRank") or 0),
    )
