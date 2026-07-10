"""BackgroundInferenceWorker — port of CircleAI.Hosting.BackgroundInferenceWorker.

Hosted-service adapter that binds an :class:`IAIService` (the butler) to a
long-running host's start/stop lifecycle. In C# this implements
``IHostedService`` for the .NET Generic Host (dotnet run, Windows Service,
systemd unit); Python has no equivalent generic host, so the port exposes the
same ``start_async`` / ``stop_async`` / ``dispose_async`` surface for a host to
call from its own lifecycle (an ``asyncio`` app, an ASGI lifespan handler, a
service wrapper, …). The behaviour is otherwise identical.

When an :class:`IThermalThrottleService` is supplied the worker subscribes to
its state-change notifications and flips :attr:`is_paused` to ``True`` while the
device is thermally throttled (state ``SERIOUS`` or ``CRITICAL``). Callers that
drive inference should check :attr:`is_paused` before submitting work. When no
thermal service is given, thermal monitoring is skipped and :attr:`is_paused`
is always ``False`` — matching the C# ``thermal == null`` path.

Port notes vs the C#:
  * ``IHostedService.StartAsync/StopAsync`` → :meth:`start_async` /
    :meth:`stop_async` (same order of operations: subscribe + start monitoring,
    then start the butler; on stop, unsubscribe + stop monitoring, then stop the
    butler).
  * ``volatile bool _paused`` → a plain attribute guarded by the GIL; the
    transition logic (only log/flip on a real edge) is preserved.
  * ``Interlocked.CompareExchange`` double-stop guard → an idempotent
    ``_stopped`` flag; ``stop_async`` is safe to call multiple times.
  * The C# ``StateChanged`` event maps to the Python thermal service's
    ``add_state_changed_handler`` / ``remove_state_changed_handler``.
"""
from __future__ import annotations

from typing import Optional

from .ai_service import IAIService
from .thermal_throttle_service import (
    IThermalThrottleService,
    ThermalState,
)

__all__ = ["BackgroundInferenceWorker"]


class BackgroundInferenceWorker:
    """Binds an :class:`IAIService` to a host's start/stop lifecycle and pauses
    inference while the device is thermally throttled. Mirrors
    ``BackgroundInferenceWorker``.
    """

    __slots__ = ("_butler", "_thermal", "_paused", "_stopped", "_handler")

    def __init__(
        self,
        butler: IAIService,
        thermal: Optional[IThermalThrottleService] = None,
    ) -> None:
        if butler is None:
            raise ValueError("butler is required")
        self._butler = butler
        self._thermal = thermal
        self._paused = False
        self._stopped = False
        # Bound method reference kept so we can unsubscribe the exact callable.
        self._handler = self._on_thermal_state_changed

    @property
    def is_paused(self) -> bool:
        """``True`` while the device is in a thermally-throttled state
        (``SERIOUS`` or ``CRITICAL``). Callers that queue inference work should
        check this before submitting. Mirrors ``IsPaused``.
        """
        return self._paused

    # ── Lifecycle (C# IHostedService) ──────────────────────────────────────

    async def start_async(self, ct: object = None) -> None:
        """Start the butler (model load + optional warm-up) and, when a thermal
        service is available, begin monitoring device temperature. Mirrors
        ``StartAsync``.
        """
        if self._thermal is not None:
            self._thermal.add_state_changed_handler(self._handler)
            self._thermal.start_monitoring(ct)

        await self._butler.start_async(ct)

    async def stop_async(self, ct: object = None) -> None:
        """Stop the butler and thermal monitoring in order. Safe to call
        multiple times — subsequent calls are no-ops. Mirrors ``StopAsync``.
        """
        if self._stopped:
            return
        self._stopped = True

        if self._thermal is not None:
            remove = getattr(self._thermal, "remove_state_changed_handler", None)
            if remove is not None:
                remove(self._handler)
            self._thermal.stop_monitoring()

        await self._butler.stop_async(ct)

    async def dispose_async(self) -> None:
        """Async-dispose. Stops the worker (double-stop-guarded) and disposes
        the butler. Mirrors ``DisposeAsync``.
        """
        await self.stop_async()
        await self._butler.dispose_async()

    # ── Thermal event handler ───────────────────────────────────────────────

    def _on_thermal_state_changed(self, new_state: ThermalState) -> None:
        should_pause = new_state >= ThermalState.SERIOUS

        if should_pause and not self._paused:
            self._paused = True
        elif not should_pause and self._paused:
            self._paused = False
