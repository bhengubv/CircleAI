# registry.py
#
# Port of CircleAI.Plugins PluginRegistry.cs (C# — the EXACT spec).
#
# (3.2.0) Installed-plugin registry + marketplace catalog. Direct lift from
# CircleUp's PluginRegistry — JSON-backed, atomic save (tmp + rename),
# thread-safe, opt-in permissions per plugin. Permissions are declarative —
# users audit before trusting. Plugin-id matching is case-insensitive.
#
# The C# collectible-AssemblyLoadContext hot-reload is a host concern; this port
# owns the JSON persistence + enable/permission CRUD. RegisteredPlugin and
# MarketplaceEntry are mutable POCOs (settable properties) → non-frozen
# dataclasses with to/from-dict JSON round-tripping using the C# PascalCase keys.

from __future__ import annotations

import json
import os
import threading
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Dict, Iterable, List, Optional


@dataclass(slots=True)
class RegisteredPlugin:
    """(3.2.0) One installed plugin entry."""

    id: str = ""
    display_name: str = ""
    version: str = "0.0.0"
    permissions: List[str] = field(default_factory=list)
    enabled: bool = False
    installed_at: Optional[datetime] = None

    def to_dict(self) -> Dict[str, object]:
        return {
            "Id": self.id,
            "DisplayName": self.display_name,
            "Version": self.version,
            "Permissions": list(self.permissions),
            "Enabled": self.enabled,
            "InstalledAt": self.installed_at.isoformat() if self.installed_at is not None else None,
        }

    @staticmethod
    def from_dict(d: Dict[str, object]) -> "RegisteredPlugin":
        installed_raw = d.get("InstalledAt")
        installed_at: Optional[datetime] = None
        if isinstance(installed_raw, str) and installed_raw != "":
            installed_at = _parse_iso(installed_raw)
        perms = d.get("Permissions") or []
        return RegisteredPlugin(
            id=str(d.get("Id", "") or ""),
            display_name=str(d.get("DisplayName", "") or ""),
            version=str(d.get("Version", "0.0.0") or "0.0.0"),
            permissions=[str(p) for p in perms],
            enabled=bool(d.get("Enabled", False)),
            installed_at=installed_at,
        )


@dataclass(slots=True)
class MarketplaceEntry:
    """(3.2.0) One marketplace catalog entry."""

    id: str = ""
    display_name: str = ""
    version: str = "0.0.0"
    description: str = ""
    author: str = ""
    download_url: str = ""
    permissions: List[str] = field(default_factory=list)

    @staticmethod
    def from_dict(d: Dict[str, object]) -> "MarketplaceEntry":
        perms = d.get("Permissions") or []
        return MarketplaceEntry(
            id=str(d.get("Id", "") or ""),
            display_name=str(d.get("DisplayName", "") or ""),
            version=str(d.get("Version", "0.0.0") or "0.0.0"),
            description=str(d.get("Description", "") or ""),
            author=str(d.get("Author", "") or ""),
            download_url=str(d.get("DownloadUrl", "") or ""),
            permissions=[str(p) for p in perms],
        )


def _parse_iso(raw: str) -> Optional[datetime]:
    try:
        # Accept a trailing "Z" (UTC) as well as offset forms.
        return datetime.fromisoformat(raw.replace("Z", "+00:00"))
    except ValueError:
        return None


class PluginRegistry:
    """(3.2.0) Tracks installed plugins. JSON-backed, atomic save, thread-safe."""

    def __init__(self, plugins_root: str, logger: Optional[object] = None) -> None:
        if plugins_root is None:
            raise ValueError("plugins_root must not be None")
        self._plugins_root = plugins_root
        self._logger = logger
        os.makedirs(self._plugins_root, exist_ok=True)
        self._manifest_path = os.path.join(self._plugins_root, "registry.json")
        self._gate = threading.RLock()
        self._installed: List[RegisteredPlugin] = []
        self._load()

    @property
    def installed(self) -> List[RegisteredPlugin]:
        with self._gate:
            return list(self._installed)

    def get(self, id: str) -> Optional[RegisteredPlugin]:
        with self._gate:
            return next((p for p in self._installed if _eq(p.id, id)), None)

    def register(
        self, id: str, display_name: str, version: str, permissions: Iterable[str]
    ) -> RegisteredPlugin:
        entry = RegisteredPlugin(
            id=id,
            display_name=display_name,
            version=version,
            permissions=list(permissions),
            enabled=False,
            installed_at=datetime.now(timezone.utc),
        )
        with self._gate:
            self._installed = [p for p in self._installed if not _eq(p.id, id)]
            self._installed.append(entry)
            self._save()
        return entry

    def set_enabled(self, id: str, enabled: bool) -> bool:
        with self._gate:
            p = next((x for x in self._installed if _eq(x.id, id)), None)
            if p is None:
                return False
            p.enabled = enabled
            self._save()
            return True

    def grant_permission(self, id: str, permission: str) -> bool:
        with self._gate:
            p = next((x for x in self._installed if _eq(x.id, id)), None)
            if p is None:
                return False
            if not any(_eq(perm, permission) for perm in p.permissions):
                p.permissions.append(permission)
                self._save()
            return True

    def revoke_permission(self, id: str, permission: str) -> bool:
        with self._gate:
            p = next((x for x in self._installed if _eq(x.id, id)), None)
            if p is None:
                return False
            before = len(p.permissions)
            p.permissions = [perm for perm in p.permissions if not _eq(perm, permission)]
            removed = before - len(p.permissions)
            if removed > 0:
                self._save()
            return removed > 0

    def uninstall(self, id: str) -> bool:
        with self._gate:
            before = len(self._installed)
            self._installed = [p for p in self._installed if not _eq(p.id, id)]
            removed = len(self._installed) < before
            if removed:
                self._save()
                # Best-effort: delete the plugin folder too.
                directory = os.path.join(self._plugins_root, id)
                if os.path.isdir(directory):
                    try:
                        import shutil

                        shutil.rmtree(directory)
                    except OSError as ex:
                        self._warn(f"Failed to delete plugin folder {directory}", ex)
            return removed

    def _load(self) -> None:
        if not os.path.exists(self._manifest_path):
            return
        try:
            with open(self._manifest_path, "r", encoding="utf-8") as fh:
                data = json.load(fh)
            if isinstance(data, list):
                self._installed.clear()
                self._installed.extend(RegisteredPlugin.from_dict(d) for d in data if isinstance(d, dict))
        except (OSError, ValueError):
            # corrupt — start fresh
            pass

    def _save(self) -> None:
        try:
            payload = [p.to_dict() for p in self._installed]
            text = json.dumps(payload, indent=2)
            tmp = self._manifest_path + ".tmp"
            with open(tmp, "w", encoding="utf-8") as fh:
                fh.write(text)
            if os.path.exists(self._manifest_path):
                os.remove(self._manifest_path)
            os.replace(tmp, self._manifest_path)
        except OSError as ex:
            self._warn("Failed to save plugin registry.", ex)

    def _warn(self, message: str, ex: Optional[BaseException] = None) -> None:
        warn = getattr(self._logger, "warning", None)
        if callable(warn):
            try:
                warn(f"{message}: {ex}" if ex is not None else message)
            except Exception:  # noqa: BLE001 — logging must never throw
                pass


class PluginMarketplace:
    """(3.2.0) Marketplace catalog. Backed by a JSON file the operator publishes
    (typically ``plugins/marketplace.json``). Catalog is metadata only — install
    downloads the plugin into ``plugins/{id}/``."""

    def __init__(self, catalog_path: str) -> None:
        if catalog_path is None:
            raise ValueError("catalog_path must not be None")
        self._catalog_path = catalog_path

    def list(self) -> List[MarketplaceEntry]:
        if not os.path.exists(self._catalog_path):
            return []
        try:
            with open(self._catalog_path, "r", encoding="utf-8") as fh:
                data = json.load(fh)
            if not isinstance(data, list):
                return []
            return [MarketplaceEntry.from_dict(d) for d in data if isinstance(d, dict)]
        except (OSError, ValueError):
            return []


def _eq(a: Optional[str], b: Optional[str]) -> bool:
    """Case-insensitive ordinal equality (StringComparer.OrdinalIgnoreCase)."""
    if a is None or b is None:
        return a is b
    return a.casefold() == b.casefold()
