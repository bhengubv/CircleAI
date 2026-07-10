"""Model lifecycle: admission gate + loaded-model ledger.

Ports the model-lifecycle layer of ``CircleAI.Inference.Server.Lifecycle``:
  * enums BackendKind / CapabilityTier (from CircleAI.Runtime.Backends, the
    ordinals the admin endpoint parses),
  * records ModelLoadDescriptor / ModelLoadState / LoadOutcome / LoadResult /
    UnloadOutcome,
  * IModelLifecycleManager + ModelLifecycleManager.

The C# manager probes host capacity via ``ICapabilityProbe`` (a ``HostProfile``
with GPU VRAM + total RAM). Python injects that behind :class:`IHostProfileProbe`
producing a :class:`HostProfile`; the admission gate (already-loaded fast path,
GPU-class VRAM check, always-on RAM check, reserve-before-factory with rollback)
is ported faithfully.
"""
from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import IntEnum
from typing import Awaitable, Callable, List, Optional

from ..hosting.inference_bridge import IInferenceBridge
from .registry import IInferenceServerModelRegistry

__all__ = [
    "BackendKind",
    "CapabilityTier",
    "GpuInfo",
    "HostProfile",
    "IHostProfileProbe",
    "StaticHostProfileProbe",
    "ModelLoadDescriptor",
    "ModelLoadState",
    "LoadOutcome",
    "LoadResult",
    "UnloadOutcome",
    "IModelLifecycleManager",
    "ModelLifecycleManager",
]


class BackendKind(IntEnum):
    """MNN execution backend. Mirrors ``CircleAI.Runtime.Backends.BackendKind``."""

    CPU = 0
    CUDA = 1
    VULKAN = 2
    OPENCL = 3
    METAL = 4
    ASCEND = 5
    CAMBRICON = 6
    CORE_ML = 7

    @staticmethod
    def parse(name: str) -> "Optional[BackendKind]":
        """Case-insensitive parse matching the admin endpoint's Enum.TryParse."""
        if name is None:
            return None
        key = name.strip().lower().replace("_", "")
        for m in BackendKind:
            if m.name.lower().replace("_", "") == key:
                return m
        return None


class CapabilityTier(IntEnum):
    """Device capability tier. Mirrors ``CircleAI.Runtime.Backends.CapabilityTier``."""

    TIER0_TINY = 0
    TIER1_SMALL = 1
    TIER2_MEDIUM = 2
    TIER3_LARGE = 3
    TIER4_FRONTIER = 4

    @staticmethod
    def parse(name: str) -> "Optional[CapabilityTier]":
        """Case-insensitive parse matching the admin endpoint's Enum.TryParse
        (accepts ``Tier1_Small`` and ``TIER1_SMALL``).
        """
        if name is None:
            return None
        key = name.strip().lower().replace("_", "")
        for m in CapabilityTier:
            if m.name.lower().replace("_", "") == key:
                return m
        return None


@dataclass(frozen=True, slots=True)
class GpuInfo:
    """Minimal GPU descriptor for admission accounting."""

    model: str
    vram_bytes: int


@dataclass(frozen=True, slots=True)
class HostProfile:
    """Host capacity snapshot. Stands in for ``CircleAI.Runtime.Capabilities.HostProfile``
    — enough surface for the lifecycle admission gate.
    """

    total_physical_memory_bytes: int
    logical_core_count: int = 1
    gpu: Optional[GpuInfo] = None
    os: str = "Unknown"
    os_version: str = ""


class IHostProfileProbe(ABC):
    """Injected host-capacity probe. Mirrors ``ICapabilityProbe.ProbeAsync`` for
    the subset the lifecycle manager needs.
    """

    @abstractmethod
    async def probe_async(self, ct: object = None) -> HostProfile: ...


class StaticHostProfileProbe(IHostProfileProbe):
    """Returns a fixed :class:`HostProfile`. Deterministic default probe."""

    __slots__ = ("_profile",)

    def __init__(self, profile: HostProfile) -> None:
        if profile is None:
            raise ValueError("profile is required")
        self._profile = profile

    async def probe_async(self, ct: object = None) -> HostProfile:
        return self._profile


# ── Lifecycle DTOs ────────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class ModelLoadDescriptor:
    """What the caller wants to load. Mirrors ``ModelLoadDescriptor``.

    ``bridge_factory`` is an async callable ``(ct) -> IInferenceBridge`` invoked
    only after the admission gate passes.
    """

    model_id: str
    backend: BackendKind
    requested_tier: CapabilityTier
    vram_required_bytes: int
    ram_required_bytes: int
    bridge_factory: Callable[[object], Awaitable[IInferenceBridge]]


@dataclass(frozen=True, slots=True)
class ModelLoadState:
    """Runtime view of one loaded model. Mirrors ``ModelLoadState``."""

    model_id: str
    backend: BackendKind
    tier: CapabilityTier
    vram_bytes: int
    ram_bytes: int
    loaded_at: datetime

    def to_dict(self) -> dict:
        return {
            "model_id": self.model_id,
            "backend": self.backend.name,
            "tier": self.tier.name,
            "vram_bytes": self.vram_bytes,
            "ram_bytes": self.ram_bytes,
            "loaded_at": self.loaded_at.isoformat(),
        }


class LoadOutcome(IntEnum):
    """Outcome enum for a load attempt. Mirrors ``LoadOutcome``."""

    LOADED = 0
    ALREADY_LOADED = 1
    INSUFFICIENT_VRAM = 2
    INSUFFICIENT_RAM = 3
    FACTORY_FAILED = 4


@dataclass(frozen=True, slots=True)
class LoadResult:
    """Result of a load attempt. Mirrors ``LoadResult``."""

    outcome: LoadOutcome
    state: Optional[ModelLoadState]
    rationale: str


class UnloadOutcome(IntEnum):
    """Outcome enum for an unload attempt. Mirrors ``UnloadOutcome``."""

    UNLOADED = 0
    NOT_LOADED = 1


# ── IModelLifecycleManager ────────────────────────────────────────────────


class IModelLifecycleManager(ABC):
    """Admits or rejects model loads and keeps the loaded-model ledger. Mirrors
    ``IModelLifecycleManager``.
    """

    @abstractmethod
    async def load_async(
        self, descriptor: ModelLoadDescriptor, ct: object = None
    ) -> LoadResult: ...

    @abstractmethod
    async def unload_async(self, model_id: str, ct: object = None) -> UnloadOutcome: ...

    @abstractmethod
    def list(self) -> List[ModelLoadState]: ...

    @property
    @abstractmethod
    def total_allocated_vram_bytes(self) -> int: ...

    @property
    @abstractmethod
    def total_allocated_ram_bytes(self) -> int: ...


_GPU_BACKENDS = {
    BackendKind.CUDA,
    BackendKind.VULKAN,
    BackendKind.METAL,
    BackendKind.OPENCL,
}


def _mib(n: int) -> int:
    return n // (1024 * 1024)


class ModelLifecycleManager(IModelLifecycleManager):
    """Default :class:`IModelLifecycleManager`. Port of ``ModelLifecycleManager``.

    Admission gate:
      1. already loaded under this id -> ALREADY_LOADED (no-op success),
      2. GPU-class backend -> VRAM headroom check,
      3. always -> RAM headroom check,
      4. reserve, run factory (FACTORY_FAILED on throw, reservation rolled back),
      5. register in the model registry.

    The first probe result is cached and reused for all admissions.
    """

    __slots__ = ("_registry", "_probe", "_lock", "_loaded", "_cached_profile")

    def __init__(
        self, registry: IInferenceServerModelRegistry, probe: IHostProfileProbe
    ) -> None:
        if registry is None:
            raise ValueError("registry is required")
        if probe is None:
            raise ValueError("probe is required")
        self._registry = registry
        self._probe = probe
        self._lock = threading.Lock()
        self._loaded: dict[str, ModelLoadState] = {}
        self._cached_profile: Optional[HostProfile] = None

    @property
    def total_allocated_vram_bytes(self) -> int:
        with self._lock:
            return sum(s.vram_bytes for s in self._loaded.values())

    @property
    def total_allocated_ram_bytes(self) -> int:
        with self._lock:
            return sum(s.ram_bytes for s in self._loaded.values())

    async def load_async(
        self, descriptor: ModelLoadDescriptor, ct: object = None
    ) -> LoadResult:
        if descriptor is None:
            raise ValueError("descriptor is required")
        if not descriptor.model_id or not descriptor.model_id.strip():
            raise ValueError("descriptor.model_id is required")
        if descriptor.bridge_factory is None:
            raise ValueError("descriptor.bridge_factory is required")

        # Idempotent fast path.
        with self._lock:
            existing = self._loaded.get(descriptor.model_id)
        if existing is not None:
            return LoadResult(
                LoadOutcome.ALREADY_LOADED,
                existing,
                f"Model '{descriptor.model_id}' is already loaded "
                f"({existing.backend.name}, {existing.tier.name}).",
            )

        profile = await self._get_or_probe_async(ct)

        # VRAM admission — GPU-class backends only.
        if descriptor.backend in _GPU_BACKENDS:
            vram_ceiling = profile.gpu.vram_bytes if profile.gpu is not None else 0
            vram_free = vram_ceiling - self.total_allocated_vram_bytes
            if vram_free < descriptor.vram_required_bytes:
                return LoadResult(
                    LoadOutcome.INSUFFICIENT_VRAM,
                    None,
                    f"Need {_mib(descriptor.vram_required_bytes)} MiB VRAM, "
                    f"have {_mib(max(0, vram_free))} MiB free "
                    f"({_mib(self.total_allocated_vram_bytes)} MiB of "
                    f"{_mib(vram_ceiling)} MiB in use).",
                )

        # RAM admission — always.
        ram_free = profile.total_physical_memory_bytes - self.total_allocated_ram_bytes
        if ram_free < descriptor.ram_required_bytes:
            return LoadResult(
                LoadOutcome.INSUFFICIENT_RAM,
                None,
                f"Need {_mib(descriptor.ram_required_bytes)} MiB RAM, "
                f"have {_mib(max(0, ram_free))} MiB free "
                f"({_mib(self.total_allocated_ram_bytes)} MiB of "
                f"{_mib(profile.total_physical_memory_bytes)} MiB in use).",
            )

        # Reserve before invoking the factory so concurrent loads see it.
        reserve_state = ModelLoadState(
            descriptor.model_id,
            descriptor.backend,
            descriptor.requested_tier,
            descriptor.vram_required_bytes,
            descriptor.ram_required_bytes,
            datetime.now(timezone.utc),
        )
        with self._lock:
            race_winner = self._loaded.get(descriptor.model_id)
            if race_winner is not None:
                return LoadResult(
                    LoadOutcome.ALREADY_LOADED,
                    race_winner,
                    f"Model '{descriptor.model_id}' was loaded by a concurrent request.",
                )
            self._loaded[descriptor.model_id] = reserve_state

        try:
            bridge = await descriptor.bridge_factory(ct)
            if bridge is None:
                raise RuntimeError(
                    f"BridgeFactory for '{descriptor.model_id}' returned null."
                )
            self._registry.register(descriptor.model_id, bridge)
            return LoadResult(
                LoadOutcome.LOADED,
                reserve_state,
                f"Loaded '{descriptor.model_id}' on {descriptor.backend.name} "
                f"at {descriptor.requested_tier.name}.",
            )
        except Exception as ex:  # noqa: BLE001 - mirror C# catch-all rollback
            with self._lock:
                self._loaded.pop(descriptor.model_id, None)
            return LoadResult(
                LoadOutcome.FACTORY_FAILED,
                None,
                f"Bridge factory for '{descriptor.model_id}' failed: {ex}",
            )

    async def unload_async(self, model_id: str, ct: object = None) -> UnloadOutcome:
        if not model_id or not model_id.strip():
            raise ValueError("model_id is required")
        with self._lock:
            removed = self._loaded.pop(model_id, None)
        if removed is None:
            return UnloadOutcome.NOT_LOADED

        bridge = self._registry.resolve(model_id)
        # Dispose the bridge if it exposes an async/sync dispose.
        if bridge is not None:
            dispose_async = getattr(bridge, "dispose_async", None)
            if callable(dispose_async):
                await dispose_async()
            else:
                dispose = getattr(bridge, "dispose", None)
                if callable(dispose):
                    dispose()

        self._registry.deregister(model_id)
        return UnloadOutcome.UNLOADED

    def list(self) -> List[ModelLoadState]:
        with self._lock:
            return list(self._loaded.values())

    async def _get_or_probe_async(self, ct: object) -> HostProfile:
        if self._cached_profile is not None:
            return self._cached_profile
        p = await self._probe.probe_async(ct)
        with self._lock:
            if self._cached_profile is None:
                self._cached_profile = p
        return self._cached_profile
