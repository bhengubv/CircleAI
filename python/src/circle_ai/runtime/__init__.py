"""circle_ai.runtime — port of the CircleAI.Runtime assembly.

Runtime capability + native-runtime layer: the OperatingSystemKind /
ArchitectureKind / GpuVendor / NpuVendor enums, the GpuInfo / NpuInfo /
HostProfile records, the ICapabilityProbe contract, the NativeRuntimeBundle /
NativeRuntimeInstall records, the embedded-JSON NativeRuntimeRegistry, the
INativeRuntimeFetcher contract, and a deterministic InMemoryNativeRuntimeFetcher.
C# is the exact spec. (The OS-specific probes — WMI / /proc / sysctl / Build.* —
and the HTTP+disk NativeRuntimeFetcher are the injected host seams.)

Public surface:

  * OperatingSystemKind / ArchitectureKind / GpuVendor / NpuVendor — enums.
  * GpuInfo / NpuInfo / HostProfile                       — records.
  * ICapabilityProbe                                      — contract.
  * NativeRuntimeBundle / NativeRuntimeInstall            — records.
  * NativeRuntimeRegistry / INativeRuntimeFetcher / InMemoryNativeRuntimeFetcher.
"""
from __future__ import annotations

from .capabilities import (
    ArchitectureKind,
    GpuInfo,
    GpuVendor,
    HostProfile,
    ICapabilityProbe,
    NpuInfo,
    NpuVendor,
    OperatingSystemKind,
)
from .native_runtimes import (
    INativeRuntimeFetcher,
    InMemoryNativeRuntimeFetcher,
    NativeRuntimeBundle,
    NativeRuntimeInstall,
    NativeRuntimeRegistry,
    RuntimeMaterialiser,
)

__all__ = [
    "OperatingSystemKind",
    "ArchitectureKind",
    "GpuVendor",
    "NpuVendor",
    "GpuInfo",
    "NpuInfo",
    "HostProfile",
    "ICapabilityProbe",
    "NativeRuntimeBundle",
    "NativeRuntimeInstall",
    "NativeRuntimeRegistry",
    "INativeRuntimeFetcher",
    "InMemoryNativeRuntimeFetcher",
    "RuntimeMaterialiser",
]
