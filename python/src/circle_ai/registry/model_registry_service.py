"""Model registry service with upgrade detection — port of
CircleAI.Core.Models.ModelRegistryService.

Holds the active set of ModelEntry (from a ModelScopeCatalogClient cache
when one is supplied, else an empty list) and provides:

    * all_models                 — list[ModelEntry]
    * get_latest_model(name)     — Optional[ModelEntry]
    * check_for_upgrades_async   — walks installed.json files in a storage
                                   directory, returns list[UpgradeInfo]
"""
from __future__ import annotations

import datetime as _dt
import json
import os
from dataclasses import is_dataclass, asdict
from pathlib import Path
from typing import Optional

from ..catalog.modelscope_catalog_client import (
    ModelEntry,
    ModelRegistry,
    ModelScopeCatalogClient,
)
from ..models.models import (
    BundleFile,
    InstalledManifest,
    UpgradeInfo,
    UpgradeReason,
)


class ModelRegistryService:
    """Holds a ModelRegistry and exposes the upgrade detector."""

    def __init__(
        self,
        catalog_client: Optional[ModelScopeCatalogClient] = None,
    ) -> None:
        self._catalog_client = catalog_client
        self._registry: Optional[ModelRegistry] = None
        if catalog_client is not None:
            try:
                self._registry = catalog_client.load_from_disk()
            except Exception:
                self._registry = None

    async def prime_from_catalog_async(self) -> None:
        """Refresh the cached catalog when a client is wired up. Never raises."""
        if self._catalog_client is None:
            return
        try:
            reg = await self._catalog_client.get_cached_catalog_async(
                accept_stale_on_error=True
            )
            if reg is not None:
                self._registry = reg
        except Exception:
            # Honour the C# directive — bad signature / network error keeps
            # using cached catalog, raises observer event in higher layers.
            pass

    @property
    def all_models(self) -> list[ModelEntry]:
        if self._registry is None:
            return []
        return list(self._registry.models)

    def get_latest_model(self, model_name: str) -> Optional[ModelEntry]:
        if not model_name:
            return None
        target = model_name.lower()
        for m in self.all_models:
            if m.name.lower() == target:
                return m
        return None

    async def check_for_upgrades_async(
        self, storage_directory: str
    ) -> list[UpgradeInfo]:
        """Compare every installed model under `storage_directory` against
        the active registry. Returns one UpgradeInfo per detected drift —
        Version mismatch, file SHA mismatch, or both.
        """
        if not storage_directory:
            raise ValueError("storage_directory is required")

        upgrades: list[UpgradeInfo] = []
        now = _dt.datetime.now(_dt.timezone.utc)

        for entry in self.all_models:
            model_dir = os.path.join(storage_directory, entry.name)
            if not os.path.isdir(model_dir):
                continue  # not installed — not an upgrade

            manifest_path = os.path.join(model_dir, "installed.json")
            manifest: Optional[InstalledManifest] = None
            if os.path.exists(manifest_path):
                try:
                    with open(manifest_path, encoding="utf-8") as f:
                        manifest = _manifest_from_dict(json.load(f))
                except (OSError, json.JSONDecodeError, KeyError, ValueError):
                    manifest = None

            # No manifest but directory exists → pre-feature install.
            if manifest is None:
                upgrades.append(
                    UpgradeInfo(
                        model_id=entry.name,
                        installed_version=None,
                        available_version=entry.version,
                        reason=UpgradeReason.UNKNOWN,
                        estimated_download_bytes=entry.total_bytes,
                        detected_at=now,
                    )
                )
                continue

            version_changed = manifest.version != entry.version
            sha_changed, drift_bytes = _compare_bundle_sha(
                manifest.files, entry.bundle_files
            )

            if not version_changed and not sha_changed:
                continue  # up to date

            if version_changed and sha_changed:
                reason = UpgradeReason.BOTH
            elif version_changed:
                reason = UpgradeReason.VERSION_CHANGED
            else:
                reason = UpgradeReason.SHA_CHANGED

            upgrades.append(
                UpgradeInfo(
                    model_id=entry.name,
                    installed_version=manifest.version,
                    available_version=entry.version,
                    reason=reason,
                    estimated_download_bytes=drift_bytes,
                    detected_at=now,
                )
            )

        return upgrades


# ── Helpers ────────────────────────────────────────────────────────────────


def _compare_bundle_sha(
    installed: Optional[list[BundleFile]],
    available: Optional[list[BundleFile]],
) -> tuple[bool, int]:
    """Returns (any drift, sum-of-bytes for files that would re-download)."""
    if not available:
        return False, 0
    installed_by_name = {f.name: f for f in (installed or [])}
    drift = False
    bytes_total = 0
    for av in available:
        inst = installed_by_name.get(av.name)
        if inst is None or inst.sha256.lower() != av.sha256.lower():
            drift = True
            bytes_total += av.size_bytes
    return drift, bytes_total


def _manifest_from_dict(d: dict) -> InstalledManifest:
    files = [
        BundleFile(
            name=str(f.get("name") or f.get("Name") or ""),
            sha256=str(f.get("sha256") or f.get("Sha256") or ""),
            size_bytes=int(f.get("size_bytes") or f.get("SizeBytes") or 0),
        )
        for f in (d.get("files") or d.get("Files") or [])
    ]
    installed_at_raw = (
        d.get("installed_at_utc")
        or d.get("InstalledAtUtc")
        or _dt.datetime.now(_dt.timezone.utc).isoformat()
    )
    if isinstance(installed_at_raw, str):
        installed_at = _dt.datetime.fromisoformat(installed_at_raw)
    else:
        installed_at = installed_at_raw
    return InstalledManifest(
        model_id=str(d.get("model_id") or d.get("ModelId") or ""),
        version=str(d.get("version") or d.get("Version") or ""),
        repo=d.get("repo") or d.get("Repo"),
        total_bytes=int(d.get("total_bytes") or d.get("TotalBytes") or 0),
        files=files,
        installed_at_utc=installed_at,
    )


def write_installed_manifest(
    model_dir: str,
    model_id: str,
    version: str,
    repo: Optional[str],
    bundle_files: list[BundleFile],
) -> None:
    """Stamps installed.json into `model_dir` — best-effort, swallows errors."""
    try:
        manifest = InstalledManifest(
            model_id=model_id,
            version=version or "",
            repo=repo,
            total_bytes=sum(max(0, f.size_bytes) for f in bundle_files),
            files=list(bundle_files),
            installed_at_utc=_dt.datetime.now(_dt.timezone.utc),
        )
        d = {
            "model_id": manifest.model_id,
            "version": manifest.version,
            "repo": manifest.repo,
            "total_bytes": manifest.total_bytes,
            "files": [
                {"name": f.name, "sha256": f.sha256, "size_bytes": f.size_bytes}
                for f in manifest.files
            ],
            "installed_at_utc": manifest.installed_at_utc.isoformat(),
        }
        path = os.path.join(model_dir, "installed.json")
        Path(model_dir).mkdir(parents=True, exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            json.dump(d, f, indent=2, ensure_ascii=False)
    except Exception:
        # Best-effort. Missing manifest just downgrades CheckForUpgrades
        # to UpgradeReason.UNKNOWN.
        pass
