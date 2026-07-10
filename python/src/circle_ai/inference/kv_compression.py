"""KV-cache compression modes + power-budget policy resolution.

Ports:
  * ``CircleAI.Inference.KvCompressionMode`` — the C ABI integer encoding.
  * ``CircleAI.Inference.KvCompressionApplyResult`` — typed result of a set.
  * ``MnnKvCompression`` — apply/read over an injected native handle
    (native side behind ``IKvCompressionNative`` so it's testable in-process).
  * ``CircleAI.Inference.PowerBudgetPolicy`` — maps a :class:`PowerBudget`
    to concrete generation knobs (``PowerBudgetResolution``).

The C# ``MnnKvCompression`` is ``internal`` and P/Invokes ``mnnbridge``.
Python injects the native seam behind :class:`IKvCompressionNative`; the
default in-memory implementation stores the last-set mode per handle so the
apply/read round-trip is deterministic and unit-testable without a native lib.
"""
from __future__ import annotations

from abc import ABC, abstractmethod
from enum import IntEnum
from typing import Dict, Optional

from .inference import PowerBudget

__all__ = [
    "KvCompressionMode",
    "KvCompressionApplyResult",
    "IKvCompressionNative",
    "InMemoryKvCompressionNative",
    "MnnKvCompression",
    "PowerBudgetResolution",
    "PowerBudgetPolicy",
]


class KvCompressionMode(IntEnum):
    """KV cache compression mode. Mirrors the C ABI integer encoding so the
    managed and native layers agree without translation tables.

    Mirrors ``CircleAI.Inference.KvCompressionMode``.
    """

    OFF = 0
    """Full FP16 KV cache — default behaviour, always supported."""

    TURBO_QUANT_4BIT = 1
    """TurboQuant at 4 bits per channel — ~4x shrink, < 1% accuracy loss."""

    TURBO_QUANT_3BIT = 2
    """TurboQuant at 3 bits per channel — ~5x shrink, marginal accuracy loss."""

    TURBO_QUANT_2BIT = 3
    """TurboQuant at 2 bits per channel — ~8x shrink, noticeable accuracy loss."""


class KvCompressionApplyResult(IntEnum):
    """Outcome of applying a KV compression mode. Mirrors the C ABI status
    codes (``CircleAI.Inference.KvCompressionApplyResult``).
    """

    APPLIED = 0
    """Native path accepted the mode and will use it."""

    INVALID_MODE = 1
    """The mode value was outside the valid 0..3 range."""

    NOT_IMPLEMENTED = 2
    """LEGACY (mnnbridge <= 1.1.0) — scaffolding-only response."""

    HANDLE_INVALID = -1
    """Handle pointer was invalid."""


class IKvCompressionNative(ABC):
    """Injected native seam over ``mnn_llm_set/get_kv_compression_mode``.

    The C# ``MnnKvCompression`` P/Invokes the bridge directly; Python defers
    to this interface so the apply/read logic is exercised without a native
    library. ``set_mode`` returns the raw C ABI status code (0 applied, 1
    invalid mode, 2 not-implemented, < 0 handle invalid); ``get_mode`` returns
    the last-stored mode (0..3), or -1 on invalid handle.
    """

    @abstractmethod
    def set_mode(self, handle: object, mode: int) -> int: ...

    @abstractmethod
    def get_mode(self, handle: object) -> int: ...


class InMemoryKvCompressionNative(IKvCompressionNative):
    """Deterministic in-memory stand-in for the mnnbridge KV-compression ABI.

    Stores the last-set mode per handle identity. A ``None`` handle is treated
    as invalid (returns the handle-invalid status), matching the C# guard that
    throws on a null handle.
    """

    __slots__ = ("_modes",)

    def __init__(self) -> None:
        self._modes: Dict[int, int] = {}

    def set_mode(self, handle: object, mode: int) -> int:
        if handle is None:
            return -1
        if mode < 0 or mode > 3:
            return 1
        self._modes[id(handle)] = mode
        return 0

    def get_mode(self, handle: object) -> int:
        if handle is None:
            return -1
        return self._modes.get(id(handle), int(KvCompressionMode.OFF))


class MnnKvCompression:
    """Typed wrapper over the KV-compression ABI so callers don't deal with
    raw integers. Mirrors ``CircleAI.Inference.MnnKvCompression``.

    In C# this is a static class bound to the ``mnnbridge`` P/Invokes. Here it
    wraps an injected :class:`IKvCompressionNative` (defaults to the in-memory
    stand-in) so the apply/read behaviour is deterministic and testable.
    """

    __slots__ = ("_native",)

    def __init__(self, native: Optional[IKvCompressionNative] = None) -> None:
        self._native = native if native is not None else InMemoryKvCompressionNative()

    def set(self, handle: object, mode: KvCompressionMode) -> KvCompressionApplyResult:
        """Apply the requested mode and return the typed result."""
        if handle is None:
            raise ValueError("handle is required")
        raw = self._native.set_mode(handle, int(mode))
        if raw == 0:
            return KvCompressionApplyResult.APPLIED
        if raw == 1:
            return KvCompressionApplyResult.INVALID_MODE
        if raw == 2:
            return KvCompressionApplyResult.NOT_IMPLEMENTED
        return KvCompressionApplyResult.HANDLE_INVALID

    def get(self, handle: object) -> KvCompressionMode:
        """Read the last-set mode (or ``OFF`` on invalid handle)."""
        if handle is None:
            raise ValueError("handle is required")
        raw = self._native.get_mode(handle)
        if 0 <= raw <= 3:
            return KvCompressionMode(raw)
        return KvCompressionMode.OFF


# ── PowerBudgetPolicy ─────────────────────────────────────────────────────


from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class PowerBudgetResolution:
    """Resolved budget for a single generation call.

    Mirrors ``CircleAI.Inference.PowerBudgetPolicy.Resolution``.

    * ``max_tokens`` — cap on output tokens for this call.
    * ``preferred_kv_mode`` — which :class:`KvCompressionMode` the runtime
      prefers for this budget (a hint; the applied mode is load-time).
    * ``prefer_smaller_model_in_chain`` — when a fallback chain is configured,
      whether to pick a smaller model than the chain head.
    """

    max_tokens: int
    preferred_kv_mode: KvCompressionMode
    prefer_smaller_model_in_chain: bool


class PowerBudgetPolicy:
    """Maps a :class:`PowerBudget` to concrete generation knobs.

    Mirrors ``CircleAI.Inference.PowerBudgetPolicy`` — a static helper so
    generators (and tests) agree on the mapping without hard-coding it.
    """

    @staticmethod
    def resolve(
        budget: PowerBudget,
        requested_max_tokens: int,
        battery_level_percent: Optional[int] = None,
        thermal_throttled: bool = False,
    ) -> PowerBudgetResolution:
        """Map a budget to concrete knobs, capping over-budget values.

        Auto-downgrades ``NORMAL`` to ``LOW`` below 15% battery, and ``HIGH``
        to ``NORMAL`` under thermal throttling — matching the C# switch.
        """
        # Auto-downgrade based on device state.
        if budget == PowerBudget.NORMAL and battery_level_percent is not None and battery_level_percent < 15:
            budget = PowerBudget.LOW
        if budget == PowerBudget.HIGH and thermal_throttled:
            budget = PowerBudget.NORMAL

        if budget == PowerBudget.NONE:
            return PowerBudgetResolution(
                max_tokens=requested_max_tokens,
                preferred_kv_mode=KvCompressionMode.TURBO_QUANT_4BIT,
                prefer_smaller_model_in_chain=False,
            )
        if budget == PowerBudget.LOW:
            return PowerBudgetResolution(
                max_tokens=min(requested_max_tokens, 64),
                preferred_kv_mode=KvCompressionMode.TURBO_QUANT_4BIT,
                prefer_smaller_model_in_chain=True,
            )
        if budget == PowerBudget.NORMAL:
            return PowerBudgetResolution(
                max_tokens=min(requested_max_tokens, 512),
                preferred_kv_mode=KvCompressionMode.TURBO_QUANT_4BIT,
                prefer_smaller_model_in_chain=False,
            )
        if budget == PowerBudget.HIGH:
            return PowerBudgetResolution(
                max_tokens=min(requested_max_tokens, 2048),
                preferred_kv_mode=KvCompressionMode.OFF,
                prefer_smaller_model_in_chain=False,
            )
        return PowerBudgetResolution(
            max_tokens=requested_max_tokens,
            preferred_kv_mode=KvCompressionMode.TURBO_QUANT_4BIT,
            prefer_smaller_model_in_chain=False,
        )
