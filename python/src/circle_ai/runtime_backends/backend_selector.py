# backend_selector.py
#
# Port of CircleAI.Runtime.Backends IBackendSelector.cs + BackendSelector.cs
# (C# — the EXACT spec).
#
# The BackendSelection record, the IBackendSelector contract, and the default
# deterministic table-style selector. No I/O; safe on hot paths. The selector
# NEVER refuses — it always returns a runnable (backend, tier) combination,
# downgrading the tier when compute is short. Every branch documents the host
# shape it claims via the rationale string. Integer VRAM/RAM GiB figures in the
# rationale use floor division to match the C# `bytes / GiB`.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass

from ..runtime.capabilities import (
    ArchitectureKind,
    GpuVendor,
    HostProfile,
    NpuVendor,
    OperatingSystemKind,
)
from .enums import BackendKind, CapabilityTier

_GIB = 1024 * 1024 * 1024


@dataclass(frozen=True, slots=True)
class BackendSelection:
    """Mirrors ``CircleAI.Runtime.Backends.BackendSelection`` — ``record(
    BackendKind Backend, CapabilityTier ActualTier, string Rationale)``."""

    backend: BackendKind
    actual_tier: CapabilityTier
    rationale: str


class IBackendSelector(ABC):
    """Picks the MNN backend + model tier for a given host. Implementations must
    NEVER throw and NEVER return None — every host can run CPU at Tier 0."""

    @abstractmethod
    def select(self, profile: HostProfile, requested_tier: CapabilityTier) -> BackendSelection:
        ...


class BackendSelector(IBackendSelector):
    """Default :class:`IBackendSelector` — deterministic, explicit routing so
    operators can predict selection without running the code."""

    def select(self, profile: HostProfile, requested_tier: CapabilityTier) -> BackendSelection:
        if profile is None:
            raise ValueError("profile must not be None")

        gpu = profile.gpu
        npu = profile.npu

        # ── 1. Apple Silicon — Metal + ANE coexist via unified memory ──────────
        if (
            profile.os == OperatingSystemKind.MacOS
            and profile.arch == ArchitectureKind.Arm64
            and gpu is not None
            and gpu.vendor == GpuVendor.Apple
        ):
            tier = self._clamp_tier(requested_tier, self._tier_for_unified_memory(profile.total_physical_memory_bytes))
            return BackendSelection(
                BackendKind.Metal,
                tier,
                f"Apple Silicon ({profile.cpu_model}); Metal over unified-memory GPU; "
                f"tier capped to {tier.name} by {profile.total_physical_memory_bytes // _GIB} GiB unified RAM.",
            )

        # ── 2. NVIDIA + CUDA — best on Linux + Windows ─────────────────────────
        if gpu is not None and gpu.vendor == GpuVendor.Nvidia and gpu.vram_bytes >= 4 * _GIB:
            tier = self._clamp_tier(requested_tier, self._tier_for_vram(gpu.vram_bytes))
            return BackendSelection(
                BackendKind.Cuda,
                tier,
                f"NVIDIA {gpu.model} with {gpu.vram_bytes // _GIB} GiB VRAM; CUDA backend; "
                f"tier capped to {tier.name} by VRAM.",
            )

        # ── 3. Huawei Ascend NPU — Chinese data-centre + Kirin laptops ─────────
        if npu is not None and npu.vendor == NpuVendor.HuaweiAscend:
            tier = self._clamp_tier(requested_tier, CapabilityTier.Tier3_Large)
            return BackendSelection(
                BackendKind.Ascend,
                tier,
                f"Huawei Ascend NPU detected ({npu.model}); Ascend (CANN) backend; tier capped to {tier.name}.",
            )

        # ── 4. Cambricon MLU — Chinese accelerator ─────────────────────────────
        if npu is not None and npu.vendor == NpuVendor.CambriconMlu:
            tier = self._clamp_tier(requested_tier, CapabilityTier.Tier3_Large)
            return BackendSelection(
                BackendKind.Cambricon,
                tier,
                f"Cambricon MLU detected; Cambricon backend; tier capped to {tier.name}.",
            )

        # ── 5. AMD / Intel discrete GPU — Vulkan ───────────────────────────────
        if (
            gpu is not None
            and (gpu.vendor == GpuVendor.Amd or gpu.vendor == GpuVendor.Intel)
            and gpu.vram_bytes >= 4 * _GIB
        ):
            tier = self._clamp_tier(requested_tier, self._tier_for_vram(gpu.vram_bytes))
            return BackendSelection(
                BackendKind.Vulkan,
                tier,
                f"{gpu.vendor.name} {gpu.model} with {gpu.vram_bytes // _GIB} GiB VRAM; Vulkan backend; "
                f"tier capped to {tier.name} by VRAM.",
            )

        # ── 6. Qualcomm Hexagon NPU on Android / Snapdragon X — OpenCL ────────
        if (npu is not None and npu.vendor == NpuVendor.QualcommHexagon) or (
            gpu is not None and gpu.vendor == GpuVendor.Qualcomm
        ):
            tier = self._clamp_tier(requested_tier, CapabilityTier.Tier1_Small)
            return BackendSelection(
                BackendKind.OpenCL,
                tier,
                f"Qualcomm Snapdragon platform; OpenCL backend (Adreno/Hexagon shared compute); "
                f"tier capped to {tier.name}.",
            )

        # ── 7. ARM Mali via Vulkan (MediaTek, Exynos, Tensor) ──────────────────
        if gpu is not None and gpu.vendor in (GpuVendor.Arm, GpuVendor.Huawei):
            tier = self._clamp_tier(requested_tier, CapabilityTier.Tier1_Small)
            return BackendSelection(
                BackendKind.Vulkan,
                tier,
                f"ARM/Mali class GPU ({gpu.model}); Vulkan backend; tier capped to {tier.name}.",
            )

        # ── 8. CPU fallback — always selectable ────────────────────────────────
        cpu_tier = self._clamp_tier(requested_tier, self._tier_for_cpu_ram(profile.total_physical_memory_bytes))
        return BackendSelection(
            BackendKind.Cpu,
            cpu_tier,
            f"No usable accelerator detected; CPU SIMD backend on {profile.cpu_model} "
            f"({profile.logical_core_count} logical cores, {profile.total_physical_memory_bytes // _GIB} GiB RAM); "
            f"tier capped to {cpu_tier.name} by available RAM.",
        )

    # ── Helpers ───────────────────────────────────────────────────────────────

    @staticmethod
    def _clamp_tier(requested: CapabilityTier, ceiling: CapabilityTier) -> CapabilityTier:
        return requested if requested <= ceiling else ceiling

    @staticmethod
    def _tier_for_vram(vram_bytes: int) -> CapabilityTier:
        if vram_bytes >= 24 * _GIB:
            return CapabilityTier.Tier4_Frontier
        if vram_bytes >= 12 * _GIB:
            return CapabilityTier.Tier3_Large
        if vram_bytes >= 8 * _GIB:
            return CapabilityTier.Tier2_Medium
        if vram_bytes >= 4 * _GIB:
            return CapabilityTier.Tier1_Small
        return CapabilityTier.Tier0_Tiny

    @staticmethod
    def _tier_for_unified_memory(ram_bytes: int) -> CapabilityTier:
        # Apple Silicon shares one pool — be more conservative.
        if ram_bytes >= 64 * _GIB:
            return CapabilityTier.Tier4_Frontier
        if ram_bytes >= 32 * _GIB:
            return CapabilityTier.Tier3_Large
        if ram_bytes >= 16 * _GIB:
            return CapabilityTier.Tier2_Medium
        if ram_bytes >= 8 * _GIB:
            return CapabilityTier.Tier1_Small
        return CapabilityTier.Tier0_Tiny

    @staticmethod
    def _tier_for_cpu_ram(ram_bytes: int) -> CapabilityTier:
        if ram_bytes >= 64 * _GIB:
            return CapabilityTier.Tier3_Large
        if ram_bytes >= 32 * _GIB:
            return CapabilityTier.Tier2_Medium
        if ram_bytes >= 16 * _GIB:
            return CapabilityTier.Tier1_Small
        if ram_bytes >= 8 * _GIB:
            return CapabilityTier.Tier1_Small
        return CapabilityTier.Tier0_Tiny
