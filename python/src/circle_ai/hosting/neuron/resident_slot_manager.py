"""Two-slot residency — port of CircleAI.Hosting.Neuron.ResidentSlotManager.

Owns the Neuron's one evictable specialist slot beside the always-warm generalist
floor (held by AIService). RAM headroom is checked before a specialist is built;
the incumbent specialist is evicted first on a swap.
"""
from __future__ import annotations

import asyncio
from dataclasses import dataclass
from enum import IntEnum
from typing import Callable, Optional

from ...inference.inference import IChatGenerator, ModelSelection

__all__ = ["SlotOutcome", "SlotAdmission", "ResidentSlotManager"]


class SlotOutcome(IntEnum):
    ADMITTED = 0
    ALREADY_RESIDENT = 1
    INSUFFICIENT_RAM = 2
    BUILD_FAILED = 3


@dataclass(frozen=True)
class SlotAdmission:
    outcome: SlotOutcome
    generator: Optional[IChatGenerator]
    message: str


async def _dispose(gen: object) -> None:
    da = getattr(gen, "dispose_async", None)
    if da is not None:
        try:
            await da()
        except Exception:  # noqa: BLE001 - dispose failure is non-fatal
            pass
        return
    d = getattr(gen, "dispose", None)
    if d is not None:
        try:
            d()
        except Exception:  # noqa: BLE001
            pass


class ResidentSlotManager:
    """Manages one evictable specialist slot. The generalist floor is never held
    here — only its reserved footprint counts against the RAM gate.
    """

    def __init__(
        self,
        generalist_reserved_bytes: int,
        ram_available: Optional[Callable[[], int]] = None,
    ) -> None:
        self._generalist_reserved_bytes = max(0, generalist_reserved_bytes)
        self._ram_available = ram_available or (lambda: 0)
        self._gate = asyncio.Lock()
        self._specialist: Optional[IChatGenerator] = None
        self._specialist_model_id: Optional[str] = None
        self._disposed = False

    @property
    def resident_specialist_model_id(self) -> Optional[str]:
        return self._specialist_model_id

    @property
    def resident_specialist(self) -> Optional[IChatGenerator]:
        return self._specialist

    async def ensure_specialist_async(
        self,
        selection: ModelSelection,
        build: Callable[[str], IChatGenerator],
    ) -> SlotAdmission:
        """Ensure a specialist for ``selection`` is resident, building it via
        ``build`` when needed. Admission gate: generalist floor + specialist must
        fit under the device RAM ceiling. On denial / build failure the slot is
        left empty and the caller answers from the generalist.
        """
        if selection is None:
            raise ValueError("selection is required")
        if build is None:
            raise ValueError("build is required")
        if self._disposed:
            raise RuntimeError("ResidentSlotManager is disposed")

        async with self._gate:
            if (
                self._specialist is not None
                and self._specialist_model_id is not None
                and self._specialist_model_id.lower() == selection.model_id.lower()
            ):
                return SlotAdmission(
                    SlotOutcome.ALREADY_RESIDENT,
                    self._specialist,
                    f"Specialist '{selection.model_id}' already resident.",
                )

            ceiling = max(0, self._ram_available())
            needed = self._generalist_reserved_bytes + max(0, selection.estimated_bytes)
            if ceiling > 0 and needed > ceiling:
                return SlotAdmission(
                    SlotOutcome.INSUFFICIENT_RAM,
                    None,
                    f"Specialist '{selection.model_id}' needs {needed >> 20} MiB; "
                    f"device ceiling {ceiling >> 20} MiB.",
                )

            # Only one specialist slot — evict the incumbent before building.
            await self._dispose_specialist_locked()

            try:
                built = build(selection.model_id)
                if built is None:
                    raise RuntimeError("Specialist build returned None.")
            except Exception as ex:  # noqa: BLE001 - reported, caller degrades
                return SlotAdmission(
                    SlotOutcome.BUILD_FAILED,
                    None,
                    f"Specialist '{selection.model_id}' build failed: {ex}",
                )

            self._specialist = built
            self._specialist_model_id = selection.model_id
            return SlotAdmission(
                SlotOutcome.ADMITTED,
                built,
                f"Specialist '{selection.model_id}' resident.",
            )

    async def evict_specialist_async(self) -> None:
        """Evict the specialist (generalist floor is never touched)."""
        if self._disposed:
            return
        async with self._gate:
            await self._dispose_specialist_locked()

    async def _dispose_specialist_locked(self) -> None:
        gen = self._specialist
        self._specialist = None
        self._specialist_model_id = None
        if gen is not None:
            await _dispose(gen)

    async def dispose_async(self) -> None:
        if self._disposed:
            return
        await self.evict_specialist_async()
        self._disposed = True
