"""test_runtime_capabilities.py — CircleAI.Runtime port.

Covers the HostProfile helpers (HasUsableGpu / Is64Bit), the enum ordinals, the
NativeRuntimeRegistry (embedded load + tuple find + newest-version tie-break),
and the deterministic InMemoryNativeRuntimeFetcher (resolve/materialise/cache +
unknown-tuple error). C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timezone

import pytest

from circle_ai.runtime import (
    ArchitectureKind,
    GpuInfo,
    GpuVendor,
    HostProfile,
    INativeRuntimeFetcher,
    InMemoryNativeRuntimeFetcher,
    NativeRuntimeBundle,
    NativeRuntimeInstall,
    NativeRuntimeRegistry,
    NpuVendor,
    OperatingSystemKind,
)
from circle_ai.runtime_backends import BackendKind

_GIB = 1024 ** 3
_NOW = datetime(2026, 1, 1, tzinfo=timezone.utc)


def test_enum_ordinals():
    assert OperatingSystemKind.Unknown == 0 and OperatingSystemKind.HarmonyOS == 6
    assert ArchitectureKind.X64 == 2 and ArchitectureKind.Loong64 == 5
    assert GpuVendor.NoneVendor == 0 and GpuVendor.Other == 99
    assert NpuVendor.HuaweiAscend == 3 and NpuVendor.Other == 99


def test_host_profile_helpers():
    with_gpu = HostProfile(
        OperatingSystemKind.Linux, "1", ArchitectureKind.X64, "cpu", 8, 8, 16 * _GIB,
        GpuInfo(GpuVendor.Nvidia, "RTX", 8 * _GIB, None), None, _NOW,
    )
    assert with_gpu.has_usable_gpu() is True  # 8 GiB >= 2 GiB default
    assert with_gpu.has_usable_gpu(16 * _GIB) is False
    assert with_gpu.is_64bit is True

    arm32 = HostProfile(
        OperatingSystemKind.Android, "1", ArchitectureKind.Arm, "cpu", 4, 4, 2 * _GIB, None, None, _NOW
    )
    assert arm32.has_usable_gpu() is False
    assert arm32.is_64bit is False


def test_registry_loads_embedded_and_finds_tuple():
    reg = NativeRuntimeRegistry.load_embedded()
    assert len(reg.all) == 17  # 2 win + 2 linux + 8 macos + 4 android + 3 ios
    win_cpu = reg.find(OperatingSystemKind.Windows, ArchitectureKind.X64, BackendKind.Cpu)
    assert win_cpu is not None
    assert win_cpu.mnn_version == "3.5.0"
    assert win_cpu.mnn_core_library_name == "MNN.dll"
    assert win_cpu.archive_sha256_hex.startswith("e37dbed6")
    mac_metal = reg.find(OperatingSystemKind.MacOS, ArchitectureKind.Arm64, BackendKind.Metal)
    assert mac_metal is not None and mac_metal.mnn_core_library_name == "MNN"
    assert reg.find(OperatingSystemKind.Linux, ArchitectureKind.Arm64, BackendKind.Cuda) is None


def test_registry_newest_version_wins():
    text = (
        '{"mnn_versions":['
        '{"version":"3.5.0","bundles":[{"os":"Linux","arch":"X64","backend":"Cpu",'
        '"url":"https://x/3.5.0.zip","mnn_lib":"libMNN.so"}]},'
        '{"version":"3.6.0","bundles":[{"os":"Linux","arch":"X64","backend":"Cpu",'
        '"url":"https://x/3.6.0.zip","mnn_lib":"libMNN.so"}]}'
        ']}'
    )
    reg = NativeRuntimeRegistry.load_from_json(text)
    found = reg.find(OperatingSystemKind.Linux, ArchitectureKind.X64, BackendKind.Cpu)
    assert found.mnn_version == "3.6.0"  # highest string wins


def test_registry_tolerates_missing_versions_key():
    reg = NativeRuntimeRegistry.load_from_json('{"other": 1}')
    assert reg.all == []


async def test_fetcher_resolves_materialises_and_caches():
    fetcher = InMemoryNativeRuntimeFetcher("cache")
    assert isinstance(fetcher, INativeRuntimeFetcher)
    assert len(fetcher.list_available_bundles()) == 17

    assert await fetcher.is_runtime_cached_async(
        OperatingSystemKind.Windows, ArchitectureKind.X64, BackendKind.Cpu
    ) is False

    progress: list = []
    install = await fetcher.ensure_runtime_async(
        OperatingSystemKind.Windows, ArchitectureKind.X64, BackendKind.Cpu, progress=progress.append
    )
    assert isinstance(install, NativeRuntimeInstall)
    assert isinstance(install.bundle, NativeRuntimeBundle)
    assert install.mnn_core_path.endswith("MNN.dll")
    assert progress[-1] == 1.0

    assert await fetcher.is_runtime_cached_async(
        OperatingSystemKind.Windows, ArchitectureKind.X64, BackendKind.Cpu
    ) is True
    # Cached fast path returns the same install.
    again = await fetcher.ensure_runtime_async(
        OperatingSystemKind.Windows, ArchitectureKind.X64, BackendKind.Cpu
    )
    assert again is install


async def test_fetcher_custom_materialiser():
    def materialise(bundle: NativeRuntimeBundle):
        return ("/opt/mnn", "/opt/mnn/libMNN.so")

    fetcher = InMemoryNativeRuntimeFetcher("cache", materialiser=materialise)
    install = await fetcher.ensure_runtime_async(
        OperatingSystemKind.Linux, ArchitectureKind.X64, BackendKind.Cpu
    )
    assert install.extracted_root == "/opt/mnn"
    assert install.mnn_core_path == "/opt/mnn/libMNN.so"


async def test_fetcher_unknown_tuple_raises():
    fetcher = InMemoryNativeRuntimeFetcher("cache")
    with pytest.raises(RuntimeError):
        await fetcher.ensure_runtime_async(
            OperatingSystemKind.Linux, ArchitectureKind.Arm64, BackendKind.Cuda
        )


def test_fetcher_empty_cache_root_raises():
    with pytest.raises(ValueError):
        InMemoryNativeRuntimeFetcher("  ")
