"""Bridge factory.

Ports ``CircleAI.Inference.Server.Endpoints.IBridgeFactory`` +
``UnconfiguredBridgeFactory``, and provides a deterministic in-memory factory
standing in for ``MnnInferenceBridgeFactory``.

The C# MNN factory composes: registry lookup -> native runtime fetch ->
native library prep -> model download -> ``QwenTextGenerator`` ->
``LocalProcessInferenceBridge``. None of the native/download steps are portable,
so :class:`DeterministicBridgeFactory` reproduces the *shape* of the pipeline:
build a :class:`DeterministicChatGenerator`, wrap it in a
:class:`LocalProcessInferenceBridge` with a tier-sized :class:`ModelDescriptor`,
and update the injected :class:`INativeRuntimeStatus` (as the real factory does
after native prep). ``ApproxMemoryFromTier`` is ported exactly.
"""
from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Optional

from ..hosting.inference_bridge import (
    DeviceCapabilities,
    IInferenceBridge,
    LocalProcessInferenceBridge,
    ModelDescriptor,
    ModelFormat,
)
from ..inference.generator import DeterministicChatGenerator
from .lifecycle import BackendKind, CapabilityTier
from .native_status import INativeRuntimeStatus, NativeRuntimePaths

__all__ = [
    "IBridgeFactory",
    "UnconfiguredBridgeFactory",
    "DeterministicBridgeFactory",
]

_GIB = 1024 * 1024 * 1024


class IBridgeFactory(ABC):
    """DI factory delegate — materialises an :class:`IInferenceBridge` for a
    given (model_id, backend, tier). Mirrors ``IBridgeFactory``.
    """

    @abstractmethod
    async def create_async(
        self,
        model_id: str,
        backend: BackendKind,
        tier: CapabilityTier,
        ct: object = None,
    ) -> IInferenceBridge: ...


class UnconfiguredBridgeFactory(IBridgeFactory):
    """Default implementation — refuses every load with a clear error. Mirrors
    ``UnconfiguredBridgeFactory``.
    """

    async def create_async(
        self,
        model_id: str,
        backend: BackendKind,
        tier: CapabilityTier,
        ct: object = None,
    ) -> IInferenceBridge:
        raise RuntimeError(
            "No IBridgeFactory is configured. Register one before "
            "calling /v1/admin/models/load."
        )


def _approx_memory_from_tier(tier: CapabilityTier) -> int:
    """Port of ``MnnInferenceBridgeFactory.ApproxMemoryFromTier``."""
    if tier == CapabilityTier.TIER0_TINY:
        return 1 * _GIB
    if tier == CapabilityTier.TIER1_SMALL:
        return 2 * _GIB
    if tier == CapabilityTier.TIER2_MEDIUM:
        return 6 * _GIB
    if tier == CapabilityTier.TIER3_LARGE:
        return 12 * _GIB
    if tier == CapabilityTier.TIER4_FRONTIER:
        return 24 * _GIB
    return 1 * _GIB


class DeterministicBridgeFactory(IBridgeFactory):
    """In-memory :class:`IBridgeFactory` standing in for ``MnnInferenceBridgeFactory``.

    Builds a :class:`LocalProcessInferenceBridge` wrapping a
    :class:`DeterministicChatGenerator`. Optionally updates an injected
    :class:`INativeRuntimeStatus` after "prep" (as the real factory does), and
    accepts injected :class:`DeviceCapabilities` for the bridge to report.
    ``supports_vision`` flags the generator's vision path.
    """

    __slots__ = ("_native_status", "_capabilities", "_supports_vision", "_context_window")

    def __init__(
        self,
        native_status: Optional[INativeRuntimeStatus] = None,
        capabilities: Optional[DeviceCapabilities] = None,
        *,
        supports_vision: bool = False,
        context_window_tokens: int = 4096,
    ) -> None:
        self._native_status = native_status
        self._capabilities = capabilities
        self._supports_vision = supports_vision
        self._context_window = context_window_tokens

    async def create_async(
        self,
        model_id: str,
        backend: BackendKind,
        tier: CapabilityTier,
        ct: object = None,
    ) -> IInferenceBridge:
        if not model_id or not model_id.strip():
            raise ValueError("model_id is required")

        # Emulate the native-runtime prep step's status publication.
        if self._native_status is not None:
            self._native_status.update(
                NativeRuntimePaths(
                    bridge_path=f"<in-memory>/{model_id}/bridge",
                    mnn_core_path=f"<in-memory>/{model_id}/mnn",
                    extracted_root=f"<in-memory>/{model_id}",
                    self_check_passed=True,
                )
            )

        generator = DeterministicChatGenerator(
            model_id, supports_vision=self._supports_vision
        )
        descriptor = ModelDescriptor(
            model_id=model_id,
            version="1.0",
            format=ModelFormat.GGUF,
            context_window_tokens=self._context_window,
            vocab_size=151_936,  # Qwen 3 family default
            parameter_count=0,
            quantisation_label=None,
            approximate_memory_bytes=_approx_memory_from_tier(tier),
        )
        return LocalProcessInferenceBridge(generator, descriptor, self._capabilities)
