"""Device probe + tier classification — port of CircleAI.Core.DeviceProbe.

Cross-platform: uses stdlib only by default. When `psutil` is installed,
RAM-available + connectivity become more accurate. Without psutil, those
fields fall back to None.

Mirrors:
    * DeviceProbe          (snapshot of RAM/storage/CPU/GPU/thermal/conn)
    * DeviceTier           (Wearable / Phone / Tablet / Desktop / Workstation)
    * DeviceTierDefaults   (ContextWindow / MaxConcurrency / AgenticMaxIter)
    * IDeviceContext       (host-supplied sensorium + IDeviceContext.thermal)
    * DefaultDeviceContext (stdlib-only probe; psutil-aware when available)
    * NullDeviceContext    (no-op for tests)
"""
from __future__ import annotations

import datetime as _dt
import locale as _locale
import os
import shutil
import socket
from dataclasses import dataclass, field
from enum import IntEnum
from typing import Optional, Protocol, runtime_checkable

try:  # optional — improves accuracy when present
    import psutil as _psutil  # type: ignore
except ImportError:  # pragma: no cover - exercised via test env
    _psutil = None  # type: ignore


# ── Enums ──────────────────────────────────────────────────────────────────


class GpuKind(IntEnum):
    """What kind of GPU acceleration the device exposes."""

    NONE = 0
    INTEGRATED = 1
    DISCRETE = 2
    NPU = 3
    METAL = 4
    VULKAN = 5
    OPEN_CL = 6


class ThermalClass(IntEnum):
    """Sustained-load thermal capacity."""

    ACTIVE = 0       # fan-cooled desktop / workstation
    PASSIVE = 1      # tablet / fanless laptop
    CONSTRAINED = 2  # phone
    SEALED = 3       # wearable


class Connectivity(IntEnum):
    """Reachability of the model registry / catalog."""

    UNKNOWN = 0
    OFFLINE = 1
    MESH_ONLY = 2
    METERED = 3
    UNLIMITED = 4


class DeviceTier(IntEnum):
    """Classification used to derive sensible defaults."""

    WEARABLE = 0
    PHONE = 1
    TABLET = 2
    DESKTOP = 3
    WORKSTATION = 4


# ── DeviceProbe ────────────────────────────────────────────────────────────


@dataclass(frozen=True)
class DeviceProbe:
    """A point-in-time snapshot of what the device can physically do."""

    ram_available_bytes: int
    storage_free_bytes: int
    cpu_cores: int
    gpu_kind: GpuKind = GpuKind.NONE
    thermal_class: ThermalClass = ThermalClass.ACTIVE
    connectivity: Connectivity = Connectivity.UNKNOWN

    @staticmethod
    def snapshot(
        model_cache_directory: Optional[str] = None,
        gpu_override: Optional[GpuKind] = None,
        thermal_override: Optional[ThermalClass] = None,
    ) -> "DeviceProbe":
        """Capture the current device state."""
        ram = _probe_ram_available()
        storage = _probe_storage_free(model_cache_directory or os.getcwd())
        cpu = os.cpu_count() or 1
        conn = _probe_connectivity()
        return DeviceProbe(
            ram_available_bytes=ram,
            storage_free_bytes=storage,
            cpu_cores=cpu,
            gpu_kind=gpu_override or GpuKind.NONE,
            thermal_class=thermal_override or ThermalClass.ACTIVE,
            connectivity=conn,
        )

    def classify(self) -> DeviceTier:
        """Classify into one of the five tiers."""
        gb = self.ram_available_bytes / (1024 ** 3)
        # Same thresholds as the C# port.
        if self.thermal_class == ThermalClass.SEALED:
            return DeviceTier.WEARABLE
        if gb < 2 or self.thermal_class == ThermalClass.CONSTRAINED:
            return DeviceTier.PHONE
        if gb < 8 or self.thermal_class == ThermalClass.PASSIVE:
            return DeviceTier.TABLET
        if gb < 32:
            return DeviceTier.DESKTOP
        return DeviceTier.WORKSTATION


def _probe_ram_available() -> int:
    if _psutil is not None:
        try:
            return int(_psutil.virtual_memory().available)
        except Exception:
            pass
    # Stdlib best-effort: POSIX sysconf.
    try:
        page = os.sysconf("SC_PAGESIZE")
        avail = os.sysconf("SC_AVPHYS_PAGES")
        if page > 0 and avail > 0:
            return page * avail
    except (AttributeError, ValueError, OSError):
        pass
    # Windows fallback via GlobalMemoryStatusEx (ctypes — no extra deps).
    if os.name == "nt":
        try:
            import ctypes

            class _MEMORYSTATUSEX(ctypes.Structure):
                _fields_ = [
                    ("dwLength", ctypes.c_uint32),
                    ("dwMemoryLoad", ctypes.c_uint32),
                    ("ullTotalPhys", ctypes.c_uint64),
                    ("ullAvailPhys", ctypes.c_uint64),
                    ("ullTotalPageFile", ctypes.c_uint64),
                    ("ullAvailPageFile", ctypes.c_uint64),
                    ("ullTotalVirtual", ctypes.c_uint64),
                    ("ullAvailVirtual", ctypes.c_uint64),
                    ("sullAvailExtendedVirtual", ctypes.c_uint64),
                ]

            stat = _MEMORYSTATUSEX()
            stat.dwLength = ctypes.sizeof(_MEMORYSTATUSEX)
            if ctypes.windll.kernel32.GlobalMemoryStatusEx(  # type: ignore[attr-defined]
                ctypes.byref(stat)
            ):
                return int(stat.ullAvailPhys)
        except Exception:
            pass
    return 0


def _probe_storage_free(path: str) -> int:
    try:
        return shutil.disk_usage(path).free
    except (FileNotFoundError, PermissionError, OSError):
        return 0


def _probe_connectivity() -> Connectivity:
    """Best-effort: try a TCP socket open to a public IP. UDP DNS probe would
    be lighter but requires a known DNS server. Cheap fallback to UNKNOWN.
    """
    try:
        with socket.create_connection(("1.1.1.1", 53), timeout=0.5):
            return Connectivity.UNLIMITED
    except (socket.timeout, OSError):
        return Connectivity.OFFLINE


# ── DeviceTierDefaults ────────────────────────────────────────────────────


class DeviceTierDefaults:
    """Sensible defaults sized by DeviceTier. Matches the C# port byte-for-byte."""

    @staticmethod
    def context_window(tier: DeviceTier) -> int:
        return {
            DeviceTier.WEARABLE: 2048,
            DeviceTier.PHONE: 4096,
            DeviceTier.TABLET: 8192,
            DeviceTier.DESKTOP: 32_768,
            DeviceTier.WORKSTATION: 131_072,
        }[tier]

    @staticmethod
    def max_concurrency(tier: DeviceTier, cpu_cores: int) -> int:
        if tier == DeviceTier.WEARABLE:
            return 1
        if tier == DeviceTier.PHONE:
            return 2
        if tier == DeviceTier.TABLET:
            return 4
        if tier == DeviceTier.DESKTOP:
            return 8
        return min(16, max(1, cpu_cores - 2))

    @staticmethod
    def agentic_max_iterations(tier: DeviceTier) -> int:
        return {
            DeviceTier.WEARABLE: 2,
            DeviceTier.PHONE: 3,
            DeviceTier.TABLET: 5,
            DeviceTier.DESKTOP: 10,
            DeviceTier.WORKSTATION: 10,
        }[tier]


# ── IDeviceContext + DefaultDeviceContext + NullDeviceContext ──────────────


@runtime_checkable
class IDeviceContext(Protocol):
    """Sensorium contract — anything platform-specific the SDK queries.

    Mirrors CircleAI.Core.IDeviceContext. Returning None is always valid;
    the SDK degrades gracefully.
    """

    active_app_id: Optional[str]
    locale: Optional[str]
    time_zone_id: Optional[str]
    local_time: Optional[_dt.datetime]

    latitude: Optional[float]
    longitude: Optional[float]
    location_hint: Optional[str]

    battery_level: Optional[float]
    is_charging: Optional[bool]

    network_type: Optional[str]
    cpu_usage_percent: Optional[float]
    available_memory_bytes: Optional[int]
    thermal_state: Optional[str]
    storage_free_bytes: Optional[int]
    last_active_utc: Optional[_dt.datetime]


@dataclass
class NullDeviceContext:
    """No-op IDeviceContext. Use in tests."""

    active_app_id: Optional[str] = None
    locale: Optional[str] = None
    time_zone_id: Optional[str] = None
    local_time: Optional[_dt.datetime] = None
    latitude: Optional[float] = None
    longitude: Optional[float] = None
    location_hint: Optional[str] = None
    battery_level: Optional[float] = None
    is_charging: Optional[bool] = None
    network_type: Optional[str] = None
    cpu_usage_percent: Optional[float] = None
    available_memory_bytes: Optional[int] = None
    thermal_state: Optional[str] = None
    storage_free_bytes: Optional[int] = None
    last_active_utc: Optional[_dt.datetime] = None


class DefaultDeviceContext:
    """Stdlib-only IDeviceContext that probes RAM/storage/locale/timezone.

    psutil-aware when present (improves RAM accuracy). Platform-specific
    sensors (GPS, battery, active app) stay None — platforms with those
    sensors should ship their own IDeviceContext.
    """

    def __init__(
        self,
        model_cache_dir: Optional[str] = None,
        thermal_hint: ThermalClass = ThermalClass.ACTIVE,
    ):
        self._model_cache_dir = model_cache_dir or os.getcwd()
        self._thermal_hint = thermal_hint

    # ── Sensorium ────────────────────────────────────────────────────

    @property
    def active_app_id(self) -> Optional[str]:
        return None

    @property
    def locale(self) -> Optional[str]:
        try:
            loc, _ = _locale.getlocale()
            return loc
        except Exception:
            return None

    @property
    def time_zone_id(self) -> Optional[str]:
        try:
            return _dt.datetime.now().astimezone().tzname()
        except Exception:
            return None

    @property
    def local_time(self) -> Optional[_dt.datetime]:
        return _dt.datetime.now().astimezone()

    @property
    def latitude(self) -> Optional[float]:
        return None

    @property
    def longitude(self) -> Optional[float]:
        return None

    @property
    def location_hint(self) -> Optional[str]:
        return None

    @property
    def battery_level(self) -> Optional[float]:
        if _psutil is None:
            return None
        try:
            bat = _psutil.sensors_battery()
            return None if bat is None else float(bat.percent) / 100.0
        except Exception:
            return None

    @property
    def is_charging(self) -> Optional[bool]:
        if _psutil is None:
            return None
        try:
            bat = _psutil.sensors_battery()
            return None if bat is None else bool(bat.power_plugged)
        except Exception:
            return None

    @property
    def network_type(self) -> Optional[str]:
        c = _probe_connectivity()
        return "online" if c == Connectivity.UNLIMITED else "none"

    @property
    def cpu_usage_percent(self) -> Optional[float]:
        if _psutil is None:
            return None
        try:
            return float(_psutil.cpu_percent(interval=None))
        except Exception:
            return None

    @property
    def available_memory_bytes(self) -> Optional[int]:
        v = _probe_ram_available()
        return v if v > 0 else None

    @property
    def thermal_state(self) -> Optional[str]:
        # Most hosts have no thermal sensor; report a sticky hint.
        return "normal"

    @property
    def storage_free_bytes(self) -> Optional[int]:
        v = _probe_storage_free(self._model_cache_dir)
        return v if v > 0 else None

    @property
    def last_active_utc(self) -> Optional[_dt.datetime]:
        return None

    # ── Helper: build a DeviceProbe with the same conventions ──────────

    def build_probe(self, gpu_override: Optional[GpuKind] = None) -> DeviceProbe:
        return DeviceProbe.snapshot(
            model_cache_directory=self._model_cache_dir,
            gpu_override=gpu_override,
            thermal_override=self._thermal_hint,
        )


# Singleton-equivalent for callers who just want the defaults.
DEFAULT_DEVICE_CONTEXT = DefaultDeviceContext()
