# capabilities.py
#
# Port of CircleAI.Runtime.Capabilities HostProfile.cs + ICapabilityProbe.cs
# (C# — the EXACT spec).
#
# The OS / arch / GPU / NPU enums, the GpuInfo / NpuInfo / HostProfile records,
# and the ICapabilityProbe contract. C# enums map to IntEnum with the exact
# ordinals declared in the source (Loong64 = 5, GPU/NPU Other = 99, etc.).
# The platform-specific probes (WMI / /proc / sysctl / Build.*) perform host
# OS reads and are host-injected — this port ships the contract + record only.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import Optional

_TWO_GIB = 2 * 1024 * 1024 * 1024


class OperatingSystemKind(IntEnum):
    """Mirrors ``CircleAI.Runtime.Capabilities.OperatingSystemKind``."""

    Unknown = 0
    Windows = 1
    Linux = 2
    MacOS = 3
    Android = 4
    IOS = 5
    HarmonyOS = 6


class ArchitectureKind(IntEnum):
    """Mirrors ``CircleAI.Runtime.Capabilities.ArchitectureKind``."""

    Unknown = 0
    X86 = 1
    X64 = 2
    Arm = 3
    Arm64 = 4
    Loong64 = 5


class GpuVendor(IntEnum):
    """Mirrors ``CircleAI.Runtime.Capabilities.GpuVendor``."""

    NoneVendor = 0
    Nvidia = 1
    Amd = 2
    Intel = 3
    Apple = 4
    Qualcomm = 5
    Huawei = 6
    Arm = 7
    Other = 99


class NpuVendor(IntEnum):
    """Mirrors ``CircleAI.Runtime.Capabilities.NpuVendor``."""

    NoneVendor = 0
    AppleNeuralEngine = 1
    QualcommHexagon = 2
    HuaweiAscend = 3
    IntelVpu = 4
    CambriconMlu = 5
    Other = 99


@dataclass(frozen=True, slots=True)
class GpuInfo:
    """Mirrors ``CircleAI.Runtime.Capabilities.GpuInfo`` — ``record(GpuVendor
    Vendor, string Model, long VramBytes, string? DriverVersion)``."""

    vendor: GpuVendor
    model: str
    vram_bytes: int
    driver_version: Optional[str]


@dataclass(frozen=True, slots=True)
class NpuInfo:
    """Mirrors ``CircleAI.Runtime.Capabilities.NpuInfo`` — ``record(NpuVendor
    Vendor, string Model)``."""

    vendor: NpuVendor
    model: str


@dataclass(frozen=True, slots=True)
class HostProfile:
    """Mirrors ``CircleAI.Runtime.Capabilities.HostProfile`` — the full host
    capability snapshot."""

    os: OperatingSystemKind
    os_version: str
    arch: ArchitectureKind
    cpu_model: str
    logical_core_count: int
    physical_core_count: int
    total_physical_memory_bytes: int
    gpu: Optional[GpuInfo]
    npu: Optional[NpuInfo]
    probed_at: datetime

    def has_usable_gpu(self, minimum_vram_bytes: int = _TWO_GIB) -> bool:
        """True when a GPU is present with at least ``minimum_vram_bytes`` of
        dedicated VRAM."""
        return self.gpu is not None and self.gpu.vram_bytes >= minimum_vram_bytes

    @property
    def is_64bit(self) -> bool:
        """True on a 64-bit architecture (X64, Arm64, Loong64)."""
        return self.arch in (ArchitectureKind.X64, ArchitectureKind.Arm64, ArchitectureKind.Loong64)


class ICapabilityProbe(ABC):
    """Discovers the host's hardware capabilities and returns a normalised
    :class:`HostProfile`. Implementations are OS-specific and MUST NOT throw on
    probe failure (unresolved fields come back Unknown/None/0)."""

    @abstractmethod
    async def probe_async(self, ct: Optional[object] = None) -> HostProfile:
        ...
