"""test_runtime_backends.py — CircleAI.Runtime.Backends port.

Covers the BackendKind / CapabilityTier enum ordinals and the deterministic
BackendSelector routing table: Apple Metal, NVIDIA CUDA, Huawei Ascend,
Cambricon, AMD/Intel Vulkan, Qualcomm OpenCL, ARM/Mali Vulkan, and the CPU
fallback — with tier downgrade by VRAM / unified-memory / RAM. C# is the exact
spec.
"""
from __future__ import annotations

from datetime import datetime, timezone

import pytest

from circle_ai.runtime import (
    ArchitectureKind,
    GpuInfo,
    GpuVendor,
    HostProfile,
    NpuInfo,
    NpuVendor,
    OperatingSystemKind,
)
from circle_ai.runtime_backends import (
    BackendKind,
    BackendSelection,
    BackendSelector,
    CapabilityTier,
    IBackendSelector,
)

_GIB = 1024 ** 3
_NOW = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _profile(
    os=OperatingSystemKind.Linux,
    arch=ArchitectureKind.X64,
    cpu="Generic CPU",
    cores=8,
    ram=16 * _GIB,
    gpu=None,
    npu=None,
) -> HostProfile:
    return HostProfile(os, "1.0", arch, cpu, cores, cores, ram, gpu, npu, _NOW)


def test_enum_ordinals():
    assert BackendKind.Cpu == 0 and BackendKind.CoreML == 7
    assert CapabilityTier.Tier0_Tiny == 0 and CapabilityTier.Tier4_Frontier == 4


def test_apple_silicon_selects_metal():
    p = _profile(
        os=OperatingSystemKind.MacOS,
        arch=ArchitectureKind.Arm64,
        cpu="Apple M2 Pro",
        ram=32 * _GIB,
        gpu=GpuInfo(GpuVendor.Apple, "Apple M2 Pro GPU", 0, None),
    )
    sel = BackendSelector()
    assert isinstance(sel, IBackendSelector)
    r = sel.select(p, CapabilityTier.Tier4_Frontier)
    assert isinstance(r, BackendSelection)
    assert r.backend == BackendKind.Metal
    assert r.actual_tier == CapabilityTier.Tier3_Large  # 32 GiB unified -> Tier3
    assert "Apple Silicon" in r.rationale


def test_nvidia_selects_cuda_with_vram_tier():
    p = _profile(gpu=GpuInfo(GpuVendor.Nvidia, "RTX 4090", 24 * _GIB, "555.0"))
    r = BackendSelector().select(p, CapabilityTier.Tier4_Frontier)
    assert r.backend == BackendKind.Cuda
    assert r.actual_tier == CapabilityTier.Tier4_Frontier  # 24 GiB VRAM -> Tier4


def test_nvidia_downgrades_when_requested_higher():
    p = _profile(gpu=GpuInfo(GpuVendor.Nvidia, "RTX 3060", 8 * _GIB, None))
    r = BackendSelector().select(p, CapabilityTier.Tier4_Frontier)
    assert r.backend == BackendKind.Cuda
    assert r.actual_tier == CapabilityTier.Tier2_Medium  # 8 GiB -> Tier2 ceiling


def test_huawei_ascend_npu():
    p = _profile(npu=NpuInfo(NpuVendor.HuaweiAscend, "Ascend 910"))
    r = BackendSelector().select(p, CapabilityTier.Tier4_Frontier)
    assert r.backend == BackendKind.Ascend
    assert r.actual_tier == CapabilityTier.Tier3_Large  # capped at Tier3


def test_cambricon_npu():
    p = _profile(npu=NpuInfo(NpuVendor.CambriconMlu, "MLU370"))
    r = BackendSelector().select(p, CapabilityTier.Tier4_Frontier)
    assert r.backend == BackendKind.Cambricon
    assert r.actual_tier == CapabilityTier.Tier3_Large


def test_amd_intel_vulkan():
    p = _profile(gpu=GpuInfo(GpuVendor.Amd, "RX 7900", 12 * _GIB, None))
    r = BackendSelector().select(p, CapabilityTier.Tier4_Frontier)
    assert r.backend == BackendKind.Vulkan
    assert r.actual_tier == CapabilityTier.Tier3_Large  # 12 GiB -> Tier3


def test_qualcomm_opencl():
    p = _profile(npu=NpuInfo(NpuVendor.QualcommHexagon, "Hexagon"))
    r = BackendSelector().select(p, CapabilityTier.Tier4_Frontier)
    assert r.backend == BackendKind.OpenCL
    assert r.actual_tier == CapabilityTier.Tier1_Small


def test_arm_mali_vulkan():
    p = _profile(gpu=GpuInfo(GpuVendor.Arm, "Mali-G710", 0, None))
    r = BackendSelector().select(p, CapabilityTier.Tier4_Frontier)
    assert r.backend == BackendKind.Vulkan
    assert r.actual_tier == CapabilityTier.Tier1_Small


def test_cpu_fallback_and_ram_tier():
    p = _profile(cpu="Xeon", cores=32, ram=64 * _GIB)  # no gpu/npu
    r = BackendSelector().select(p, CapabilityTier.Tier4_Frontier)
    assert r.backend == BackendKind.Cpu
    assert r.actual_tier == CapabilityTier.Tier3_Large  # 64 GiB CPU -> Tier3 ceiling
    assert "CPU SIMD backend" in r.rationale


def test_low_ram_cpu_falls_to_tier0():
    p = _profile(ram=4 * _GIB)
    r = BackendSelector().select(p, CapabilityTier.Tier4_Frontier)
    assert r.backend == BackendKind.Cpu
    assert r.actual_tier == CapabilityTier.Tier0_Tiny


def test_selector_never_upgrades_beyond_request():
    p = _profile(gpu=GpuInfo(GpuVendor.Nvidia, "RTX 4090", 24 * _GIB, None))
    r = BackendSelector().select(p, CapabilityTier.Tier1_Small)
    assert r.actual_tier == CapabilityTier.Tier1_Small  # request is the ceiling


def test_select_none_profile_raises():
    with pytest.raises(ValueError):
        BackendSelector().select(None, CapabilityTier.Tier0_Tiny)  # type: ignore[arg-type]
