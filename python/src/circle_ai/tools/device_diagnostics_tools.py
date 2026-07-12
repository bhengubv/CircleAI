# device_diagnostics_tools.py
#
# Port of CircleAI.Tools DeviceDiagnosticsTools.cs (C# — the EXACT spec).
#
# Tool definitions exposing on-device diagnostic data to the B! inference
# engine. Gives B! the ability to observe — and reason about — the physical
# health of the host device before scheduling heavy inference work.
#
# Porting note: the C# reads an IDeviceContext whose ThermalState is an enum;
# the Python IDeviceContext (circle_ai.device.device_probe.IDeviceContext)
# exposes `thermal_state` as Optional[str] already lower-cased-friendly, so the
# port emits it as a quoted JSON string directly (lower-cased to match
# `ThermalState.ToString().ToLowerInvariant()`), and `null` when absent.

from __future__ import annotations

from typing import List, Optional

from .tool_types import ToolDefinition, ToolParameter


class DeviceDiagnosticsTools:
    """Tool definitions for on-device diagnostics. Exposes CPU usage, memory,
    thermal state, and free storage so that B! can make informed scheduling
    decisions (e.g. defer a large-model load when the device is hot). Mirrors
    ``CircleAI.Tools.DeviceDiagnosticsTools`` (a static class).
    """

    @staticmethod
    def diagnostics() -> List[ToolDefinition]:
        """Return the single ``device.diagnose`` tool definition.

        Register this alongside :meth:`TheGeekNetworkTools.get_all_tools` when an
        ``IDeviceContext`` is available in the host.
        """
        return [
            ToolDefinition(
                name="device.diagnose",
                description=(
                    "Return a snapshot of the host device's health: CPU usage "
                    "fraction, available memory in MB, thermal state "
                    "(normal/warm/critical), and free storage in MB. Use before "
                    "scheduling heavy inference to avoid OOM conditions or OS "
                    "thermal throttling."
                ),
                parameters={},
                required_parameters=[],
            )
        ]

    @staticmethod
    def diagnose_from_context(ctx: object) -> str:
        """Read an ``IDeviceContext`` and produce a compact JSON string suitable
        for returning as tool output to the inference engine.

        Null members are serialised as JSON ``null`` so the model knows the data
        was unavailable, not zero. Mirrors ``DiagnoseFromContext``.
        """
        if ctx is None:
            raise ValueError("ctx must not be None")

        def frac(v: Optional[float]) -> str:
            return f"{v:.3f}" if v is not None else "null"

        def long_mb(v: Optional[int]) -> str:
            return str(int(v) // (1024 * 1024)) if v is not None else "null"

        def thermal(v: Optional[str]) -> str:
            return f'"{v.lower()}"' if v is not None else "null"

        cpu = getattr(ctx, "cpu_usage_percent", None)
        mem = getattr(ctx, "available_memory_bytes", None)
        therm = getattr(ctx, "thermal_state", None)
        storage = getattr(ctx, "storage_free_bytes", None)

        return (
            "{"
            f'"cpu_usage_fraction":{frac(cpu)},'
            f'"available_memory_mb":{long_mb(mem)},'
            f'"thermal_state":{thermal(therm)},'
            f'"storage_free_mb":{long_mb(storage)}'
            "}"
        )
