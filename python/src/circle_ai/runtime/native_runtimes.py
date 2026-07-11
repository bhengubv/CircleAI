# native_runtimes.py
#
# Port of CircleAI.Runtime.NativeRuntimes NativeRuntimeBundle.cs /
# NativeRuntimeRegistry.cs / INativeRuntimeFetcher.cs (C# — the EXACT spec).
#
# The NativeRuntimeBundle / NativeRuntimeInstall records, the embedded-JSON
# registry (deterministic parse + tuple lookup), the INativeRuntimeFetcher
# contract, and a deterministic in-memory fetcher.
#
# The C# NativeRuntimeFetcher does real HTTP download + SHA-256 verify + zip
# extract to a cache directory. That network + disk work is host-injected here:
# InMemoryNativeRuntimeFetcher resolves the bundle from the registry and
# materialises an install through an injected "materialiser" callback (default:
# a deterministic in-memory core-path), tracking the cache in a dict.

from __future__ import annotations

import json
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Callable, Dict, List, Optional, Tuple

from ..runtime_backends.enums import BackendKind
from .capabilities import ArchitectureKind, OperatingSystemKind


@dataclass(frozen=True, slots=True)
class NativeRuntimeBundle:
    """Mirrors ``CircleAI.Runtime.NativeRuntimes.NativeRuntimeBundle`` — a single
    fetchable MNN runtime bundle for one (OS, arch, backend) tuple. C# Uri maps
    to str; Uri? / string? map to Optional[str]."""

    mnn_version: str
    os: OperatingSystemKind
    arch: ArchitectureKind
    backend: BackendKind
    primary_uri: str
    fallback_uri: Optional[str]
    archive_sha256_hex: Optional[str]
    mnn_core_library_name: str


@dataclass(frozen=True, slots=True)
class NativeRuntimeInstall:
    """Mirrors ``CircleAI.Runtime.NativeRuntimes.NativeRuntimeInstall`` — result
    of a successful EnsureRuntime call: which bundle, where it extracted, and
    where MNN lives."""

    bundle: NativeRuntimeBundle
    extracted_root: str
    mnn_core_path: str


def _default_core_lib_name(os: OperatingSystemKind) -> str:
    if os == OperatingSystemKind.Windows:
        return "MNN.dll"
    if os in (OperatingSystemKind.MacOS, OperatingSystemKind.IOS):
        return "MNN"
    return "libMNN.so"


class NativeRuntimeRegistry:
    """Loads the native-runtime registry and exposes lookup by tuple. Mirrors
    ``CircleAI.Runtime.NativeRuntimes.NativeRuntimeRegistry`` — non-object
    entries (notes/headers) are tolerated so the JSON stays human-editable."""

    def __init__(self, bundles: List[NativeRuntimeBundle]) -> None:
        self._bundles = list(bundles)

    @staticmethod
    def load_from_json(text: str) -> "NativeRuntimeRegistry":
        """Load from a JSON string (equivalent to the C# LoadFromStream)."""
        if text is None:
            raise ValueError("json must not be None")
        doc = json.loads(text)
        bundles: List[NativeRuntimeBundle] = []
        if not isinstance(doc, dict) or "mnn_versions" not in doc:
            return NativeRuntimeRegistry(bundles)
        for version_entry in doc.get("mnn_versions", []):
            if not isinstance(version_entry, dict):
                continue
            if "version" not in version_entry or "bundles" not in version_entry:
                continue
            mnn_version = version_entry.get("version") or ""
            for b in version_entry.get("bundles", []):
                if not isinstance(b, dict):
                    continue
                bundle = NativeRuntimeRegistry._try_parse_bundle(mnn_version, b)
                if bundle is not None:
                    bundles.append(bundle)
        return NativeRuntimeRegistry(bundles)

    @staticmethod
    def load_embedded() -> "NativeRuntimeRegistry":
        """Load the embedded registry (the 3.5.0 MNN bundle set)."""
        return NativeRuntimeRegistry.load_from_json(_EMBEDDED_REGISTRY_JSON)

    @staticmethod
    def _try_parse_bundle(mnn_version: str, b: Dict[str, object]) -> Optional[NativeRuntimeBundle]:
        if "os" not in b or "arch" not in b or "backend" not in b or "url" not in b:
            return None
        os_v = _parse_enum(OperatingSystemKind, b.get("os"))
        arch_v = _parse_enum(ArchitectureKind, b.get("arch"))
        backend_v = _parse_enum(BackendKind, b.get("backend"))
        if os_v is None or arch_v is None or backend_v is None:
            return None
        primary = b.get("url")
        if not isinstance(primary, str) or primary == "":
            return None
        fallback = b.get("fallback_url")
        fallback = fallback if isinstance(fallback, str) and fallback != "" else None
        sha = b.get("sha256")
        sha = sha if isinstance(sha, str) else None
        core_lib = b.get("mnn_lib")
        core_lib = core_lib if isinstance(core_lib, str) and core_lib != "" else _default_core_lib_name(os_v)
        return NativeRuntimeBundle(mnn_version, os_v, arch_v, backend_v, primary, fallback, sha, core_lib)

    @property
    def all(self) -> List[NativeRuntimeBundle]:
        return list(self._bundles)

    def find(
        self, os: OperatingSystemKind, arch: ArchitectureKind, backend: BackendKind
    ) -> Optional[NativeRuntimeBundle]:
        """Newest bundle matching the tuple; ties broken by highest version
        string (ordinal descending). Mirrors ``NativeRuntimeRegistry.Find``."""
        matches = [
            b for b in self._bundles if b.os == os and b.arch == arch and b.backend == backend
        ]
        if not matches:
            return None
        matches.sort(key=lambda b: b.mnn_version, reverse=True)
        return matches[0]


def _parse_enum(enum_cls, value: object):
    """Enum.TryParse(ignoreCase: true) — case-insensitive name match."""
    if not isinstance(value, str):
        return None
    for member in enum_cls:
        if member.name.casefold() == value.casefold():
            return member
    return None


class INativeRuntimeFetcher(ABC):
    """Pre-built MNN native runtime fetcher. Mirrors
    ``CircleAI.Runtime.NativeRuntimes.INativeRuntimeFetcher``. Implemented by
    :class:`InMemoryNativeRuntimeFetcher` in this port; hosts inject a real
    downloader for on-disk fetch + SHA verify."""

    @abstractmethod
    async def ensure_runtime_async(
        self,
        os: OperatingSystemKind,
        arch: ArchitectureKind,
        backend: BackendKind,
        progress: Optional[Callable[[float], None]] = None,
        ct: Optional[object] = None,
    ) -> NativeRuntimeInstall:
        ...

    @abstractmethod
    async def is_runtime_cached_async(
        self,
        os: OperatingSystemKind,
        arch: ArchitectureKind,
        backend: BackendKind,
        ct: Optional[object] = None,
    ) -> bool:
        ...

    @abstractmethod
    def list_available_bundles(self) -> List[NativeRuntimeBundle]:
        ...


# Injected materialiser: given a resolved bundle, return (extracted_root,
# mnn_core_path). Default is a deterministic in-memory placement.
RuntimeMaterialiser = Callable[[NativeRuntimeBundle], Tuple[str, str]]


class InMemoryNativeRuntimeFetcher(INativeRuntimeFetcher):
    """Deterministic in-memory INativeRuntimeFetcher. Resolves the bundle from
    the registry, then materialises an install via an injected callback
    (default: an in-memory ``<cache_root>/<version>-<os>-<arch>-<backend>``
    extract dir + a nested MNN core path). The cache is a dict; no disk or
    network is touched."""

    def __init__(
        self,
        cache_root: str,
        registry: Optional[NativeRuntimeRegistry] = None,
        materialiser: Optional[RuntimeMaterialiser] = None,
    ) -> None:
        if cache_root is None or cache_root.strip() == "":
            raise ValueError("Cache root must not be empty.")
        self._cache_root = cache_root
        self._registry = registry if registry is not None else NativeRuntimeRegistry.load_embedded()
        self._materialiser = materialiser if materialiser is not None else self._default_materialiser
        self._cache: Dict[str, NativeRuntimeInstall] = {}
        self._lock = threading.Lock()

    def list_available_bundles(self) -> List[NativeRuntimeBundle]:
        return self._registry.all

    async def is_runtime_cached_async(
        self,
        os: OperatingSystemKind,
        arch: ArchitectureKind,
        backend: BackendKind,
        ct: Optional[object] = None,
    ) -> bool:
        bundle = self._registry.find(os, arch, backend)
        if bundle is None:
            return False
        with self._lock:
            return self._cache_key(bundle) in self._cache

    async def ensure_runtime_async(
        self,
        os: OperatingSystemKind,
        arch: ArchitectureKind,
        backend: BackendKind,
        progress: Optional[Callable[[float], None]] = None,
        ct: Optional[object] = None,
    ) -> NativeRuntimeInstall:
        bundle = self._registry.find(os, arch, backend)
        if bundle is None:
            available = ", ".join(f"({b.os.name},{b.arch.name},{b.backend.name})" for b in self._registry.all)
            raise RuntimeError(
                f"No native runtime bundle registered for ({os.name}, {arch.name}, {backend.name}). "
                f"Available bundles: {available}"
            )
        key = self._cache_key(bundle)
        with self._lock:
            cached = self._cache.get(key)
        if cached is not None:
            if progress is not None:
                progress(1.0)
            return cached

        extracted_root, core_path = self._materialiser(bundle)
        install = NativeRuntimeInstall(bundle, extracted_root, core_path)
        with self._lock:
            self._cache[key] = install
        if progress is not None:
            progress(1.0)
        return install

    def _default_materialiser(self, bundle: NativeRuntimeBundle) -> Tuple[str, str]:
        extract = f"{self._cache_root}/{bundle.mnn_version}-{bundle.os.name}-{bundle.arch.name}-{bundle.backend.name}"
        core = f"{extract}/{bundle.mnn_core_library_name}"
        return extract, core

    @staticmethod
    def _cache_key(bundle: NativeRuntimeBundle) -> str:
        return f"{bundle.os.name}/{bundle.arch.name}/{bundle.backend.name}"


# Embedded MNN 3.5.0 registry — verbatim port of
# NativeRuntimes/embedded_native_registry.json (bundles only; the "_notes"
# header is descriptive and omitted). Kept as a Python string so the port has
# no packaged-resource dependency.
_EMBEDDED_REGISTRY_JSON = json.dumps(
    {
        "mnn_versions": [
            {
                "version": "3.5.0",
                "released_at": "2026-04-07",
                "bundles": [
                    {"os": "Windows", "arch": "X64", "backend": "Cpu",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_windows_x64_cpu_opencl.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_windows_x64_cpu_opencl.zip",
                     "sha256": "e37dbed6a5a6c26122239468d7fc8569d003c7f4a12c8a8024a33660fb13e4b7",
                     "mnn_lib": "MNN.dll"},
                    {"os": "Windows", "arch": "X64", "backend": "OpenCL",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_windows_x64_cpu_opencl.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_windows_x64_cpu_opencl.zip",
                     "sha256": "e37dbed6a5a6c26122239468d7fc8569d003c7f4a12c8a8024a33660fb13e4b7",
                     "mnn_lib": "MNN.dll"},
                    {"os": "Linux", "arch": "X64", "backend": "Cpu",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_linux_x64_cpu_opencl.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_linux_x64_cpu_opencl.zip",
                     "sha256": "a9f4b00bf7c8473b3ef3c47873df84512ebf452d4422a4fde0a70ee81e073a17",
                     "mnn_lib": "libMNN.so"},
                    {"os": "Linux", "arch": "X64", "backend": "OpenCL",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_linux_x64_cpu_opencl.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_linux_x64_cpu_opencl.zip",
                     "sha256": "a9f4b00bf7c8473b3ef3c47873df84512ebf452d4422a4fde0a70ee81e073a17",
                     "mnn_lib": "libMNN.so"},
                    {"os": "MacOS", "arch": "Arm64", "backend": "Cpu",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal.zip",
                     "sha256": "dec927b86f32ef4351c5af527d54ec0afe0bef0b9b1b2bf94e59e3ae55bf42eb",
                     "mnn_lib": "MNN"},
                    {"os": "MacOS", "arch": "Arm64", "backend": "OpenCL",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal.zip",
                     "sha256": "dec927b86f32ef4351c5af527d54ec0afe0bef0b9b1b2bf94e59e3ae55bf42eb",
                     "mnn_lib": "MNN"},
                    {"os": "MacOS", "arch": "Arm64", "backend": "Metal",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal.zip",
                     "sha256": "dec927b86f32ef4351c5af527d54ec0afe0bef0b9b1b2bf94e59e3ae55bf42eb",
                     "mnn_lib": "MNN"},
                    {"os": "MacOS", "arch": "X64", "backend": "Cpu",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal.zip",
                     "sha256": "dec927b86f32ef4351c5af527d54ec0afe0bef0b9b1b2bf94e59e3ae55bf42eb",
                     "mnn_lib": "MNN"},
                    {"os": "MacOS", "arch": "X64", "backend": "OpenCL",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal.zip",
                     "sha256": "dec927b86f32ef4351c5af527d54ec0afe0bef0b9b1b2bf94e59e3ae55bf42eb",
                     "mnn_lib": "MNN"},
                    {"os": "MacOS", "arch": "X64", "backend": "Metal",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal.zip",
                     "sha256": "dec927b86f32ef4351c5af527d54ec0afe0bef0b9b1b2bf94e59e3ae55bf42eb",
                     "mnn_lib": "MNN"},
                    {"os": "Android", "arch": "Arm64", "backend": "Cpu",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_android_armv7_armv8_cpu_opencl_vulkan.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_android_armv7_armv8_cpu_opencl_vulkan.zip",
                     "sha256": "b5513459ee5d70dec98e7a0763ce2d09a9824897c150069e65b2b1a04570c573",
                     "mnn_lib": "libMNN.so"},
                    {"os": "Android", "arch": "Arm64", "backend": "OpenCL",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_android_armv7_armv8_cpu_opencl_vulkan.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_android_armv7_armv8_cpu_opencl_vulkan.zip",
                     "sha256": "b5513459ee5d70dec98e7a0763ce2d09a9824897c150069e65b2b1a04570c573",
                     "mnn_lib": "libMNN.so"},
                    {"os": "Android", "arch": "Arm64", "backend": "Vulkan",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_android_armv7_armv8_cpu_opencl_vulkan.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_android_armv7_armv8_cpu_opencl_vulkan.zip",
                     "sha256": "b5513459ee5d70dec98e7a0763ce2d09a9824897c150069e65b2b1a04570c573",
                     "mnn_lib": "libMNN.so"},
                    {"os": "Android", "arch": "Arm", "backend": "Cpu",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_android_armv7_armv8_cpu_opencl_vulkan.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_android_armv7_armv8_cpu_opencl_vulkan.zip",
                     "sha256": "b5513459ee5d70dec98e7a0763ce2d09a9824897c150069e65b2b1a04570c573",
                     "mnn_lib": "libMNN.so"},
                    {"os": "IOS", "arch": "Arm64", "backend": "Cpu",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_ios_armv82_cpu_metal_coreml.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_ios_armv82_cpu_metal_coreml.zip",
                     "sha256": "fd9b6c5769718286f07ff300897c72ff6511a1d2a25ef79b3b2f8b2b3313281a",
                     "mnn_lib": "MNN"},
                    {"os": "IOS", "arch": "Arm64", "backend": "Metal",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_ios_armv82_cpu_metal_coreml.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_ios_armv82_cpu_metal_coreml.zip",
                     "sha256": "fd9b6c5769718286f07ff300897c72ff6511a1d2a25ef79b3b2f8b2b3313281a",
                     "mnn_lib": "MNN"},
                    {"os": "IOS", "arch": "Arm64", "backend": "CoreML",
                     "url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_ios_armv82_cpu_metal_coreml.zip",
                     "fallback_url": "https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_ios_armv82_cpu_metal_coreml.zip",
                     "sha256": "fd9b6c5769718286f07ff300897c72ff6511a1d2a25ef79b3b2f8b2b3313281a",
                     "mnn_lib": "MNN"},
                ],
            }
        ]
    }
)
