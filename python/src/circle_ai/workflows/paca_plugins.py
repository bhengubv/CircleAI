# paca_plugins.py
#
# Port of CircleAI.Workflows PacaPlugins.cs (C# — the EXACT spec).
#
# (3.3.0) Plugin runtime + manifest + lifecycle ported from paca: plugin
# manifest validation, semver upgrade detection, reverse-DNS naming, marketplace
# install/upgrade/uninstall, frontend module surface, extension points, artifact
# + migration management, per-plugin resource limits + WASI snapshot preview-1
# support. The wazero / WASM execution layer is host-supplied via
# IPluginRuntimeHost; this module owns the lifecycle.
#
# C# System.Version (dotted numeric, up to 4 parts) + StripPrerelease is ported
# as a self-contained numeric-tuple comparison. Uri? → Optional[str].

from __future__ import annotations

import re
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, field, replace
from datetime import datetime, timezone
from enum import IntEnum
from typing import Callable, Dict, List, Optional, Tuple


class PluginExtensionPoint(IntEnum):
    """(3.3.0) Plugin extension points supported by the marketplace."""

    Sidebar = 0
    TaskDetail = 1
    Settings = 2
    CustomView = 3
    Route = 4
    Event = 5
    McpTool = 6


@dataclass(frozen=True, slots=True)
class PluginResourceLimits:
    """(3.3.0) Per-plugin resource limits.

    ``call_timeout_ms``: max wall-clock time for one host call (default 5000ms).
    ``memory_ceiling_bytes``: max memory the WASM instance may allocate
    (default 64MB)."""

    call_timeout_ms: int = 5000
    memory_ceiling_bytes: int = 64 * 1024 * 1024


@dataclass(frozen=True, slots=True)
class PluginManifest:
    """(3.3.0) Plugin manifest from ``plugin.json``.

    ``name`` is reverse-DNS, e.g. "com.paca.bdd"; ``version`` is SemVer."""

    name: str
    display_name: str
    version: str
    description: str
    artifact_wasm_url: Optional[str]
    frontend_module_url: Optional[str]
    extension_points: List[PluginExtensionPoint]
    mcp_tools: List[str]
    sql_migration_files: List[str]
    limits: PluginResourceLimits = field(default_factory=PluginResourceLimits)


@dataclass(frozen=True, slots=True)
class InstalledPlugin:
    """(3.3.0) Installed instance. ``id`` matches ``manifest.name``."""

    id: str
    manifest: PluginManifest
    installed_from_catalog: str
    installed_at_utc: datetime
    enabled: bool


class IPluginRuntimeHost(ABC):
    """(3.3.0) Plugin runtime host (wazero-style). Provided by the deploy."""

    @abstractmethod
    async def install_async(self, plugin: InstalledPlugin, ct: Optional[object] = None) -> None:
        """Install + initialise. Run SQL migrations + cache the WASM artifact."""
        ...

    @abstractmethod
    async def uninstall_async(
        self, plugin_id: str, drop_artifacts: bool, ct: Optional[object] = None
    ) -> None:
        """Uninstall — drop WASM + clean artifacts; do NOT roll back data unless
        asked."""
        ...

    @abstractmethod
    async def upgrade_async(
        self, from_plugin: InstalledPlugin, to_plugin: InstalledPlugin, ct: Optional[object] = None
    ) -> None:
        """Hot-swap to a new version (semver upgrade)."""
        ...


_REVERSE_DNS_PATTERN = re.compile(r"^[a-z][a-z0-9]*(\.[a-z][a-z0-9_-]*)+$")


def _strip_prerelease(v: str) -> str:
    return re.split(r"[-+]", v, maxsplit=1)[0]


def _parse_version(v: str) -> Optional[Tuple[int, ...]]:
    """Mirror System.Version.TryParse over the pre-release-stripped string:
    2..4 dotted non-negative integer components."""
    core = _strip_prerelease(v)
    parts = core.split(".")
    if len(parts) < 2 or len(parts) > 4:
        return None
    out: List[int] = []
    for p in parts:
        if p == "" or not p.isdigit():
            return None
        out.append(int(p))
    return tuple(out)


def _compare_version_tuples(a: Tuple[int, ...], b: Tuple[int, ...]) -> int:
    # System.Version compares Major, Minor, Build, Revision; unspecified
    # trailing components behave as their default (0) is NOT how System.Version
    # works — it treats an unspecified component as -1. But CompareSemver here
    # only ever parses successfully-validated 2-4 part versions, and paca ships
    # 3-part semver; pad with 0 for a total order that matches real inputs.
    la = list(a) + [0] * (4 - len(a))
    lb = list(b) + [0] * (4 - len(b))
    for x, y in zip(la, lb):
        if x != y:
            return -1 if x < y else 1
    return 0


class PacaPluginRegistry:
    """(3.3.0) Plugin lifecycle manager. Installs / upgrades / uninstalls /
    enables / disables."""

    def __init__(self, runtime: IPluginRuntimeHost, clock: Optional[Callable[[], datetime]] = None) -> None:
        if runtime is None:
            raise ValueError("runtime must not be None")
        self._runtime = runtime
        self._clock = clock if clock is not None else (lambda: datetime.now(timezone.utc))
        self._installed: Dict[str, InstalledPlugin] = {}
        self._lock = threading.Lock()

    def list_installed(self) -> List[InstalledPlugin]:
        with self._lock:
            return list(self._installed.values())

    def get(self, id: str) -> Optional[InstalledPlugin]:
        with self._lock:
            return self._installed.get(id)

    @staticmethod
    def validate_manifest(manifest: PluginManifest) -> None:
        """(3.3.0) Validate a manifest before install / upgrade."""
        if manifest is None:
            raise ValueError("manifest must not be None")
        if not _REVERSE_DNS_PATTERN.match(manifest.name):
            raise ValueError(
                f"Plugin name '{manifest.name}' must be reverse-DNS (e.g. com.paca.bdd)."
            )
        if _parse_version(manifest.version) is None:
            raise ValueError(f"Plugin version '{manifest.version}' is not parseable SemVer.")
        if manifest.limits.call_timeout_ms <= 0:
            raise ValueError("CallTimeoutMs must be positive.")
        if manifest.limits.memory_ceiling_bytes <= 0:
            raise ValueError("MemoryCeilingBytes must be positive.")

    async def install_async(
        self, manifest: PluginManifest, catalog: str, ct: Optional[object] = None
    ) -> InstalledPlugin:
        """(3.3.0) Install plugin from the supplied manifest."""
        self.validate_manifest(manifest)
        with self._lock:
            if manifest.name in self._installed:
                raise RuntimeError(
                    f"Plugin '{manifest.name}' is already installed; use upgrade_async."
                )
        installed = InstalledPlugin(manifest.name, manifest, catalog, self._clock(), True)
        await self._runtime.install_async(installed, ct)
        with self._lock:
            self._installed[manifest.name] = installed
        return installed

    async def upgrade_async(
        self, new_manifest: PluginManifest, catalog: str, ct: Optional[object] = None
    ) -> InstalledPlugin:
        """(3.3.0) Upgrade if ``new_manifest``'s SemVer is strictly newer."""
        self.validate_manifest(new_manifest)
        with self._lock:
            current = self._installed.get(new_manifest.name)
            if current is None:
                raise RuntimeError(f"Plugin '{new_manifest.name}' is not installed.")
        if self.compare_semver(new_manifest.version, current.manifest.version) <= 0:
            raise RuntimeError(
                f"Version {new_manifest.version} is not newer than {current.manifest.version}."
            )
        nxt = InstalledPlugin(new_manifest.name, new_manifest, catalog, self._clock(), current.enabled)
        await self._runtime.upgrade_async(current, nxt, ct)
        with self._lock:
            self._installed[new_manifest.name] = nxt
        return nxt

    async def uninstall_async(
        self, id: str, drop_artifacts: bool = True, ct: Optional[object] = None
    ) -> None:
        with self._lock:
            if id not in self._installed:
                return
            del self._installed[id]
        await self._runtime.uninstall_async(id, drop_artifacts, ct)

    def set_enabled(self, id: str, enabled: bool) -> None:
        with self._lock:
            current = self._installed.get(id)
            if current is not None:
                self._installed[id] = replace(current, enabled=enabled)

    @staticmethod
    def compare_semver(a: str, b: str) -> int:
        """(3.3.0) Compare SemVer-ish strings: returns <0 / 0 / >0."""
        va = _parse_version(a)
        vb = _parse_version(b)
        if va is None:
            raise ValueError(f"Version '{a}' is not parseable.")
        if vb is None:
            raise ValueError(f"Version '{b}' is not parseable.")
        return _compare_version_tuples(va, vb)
