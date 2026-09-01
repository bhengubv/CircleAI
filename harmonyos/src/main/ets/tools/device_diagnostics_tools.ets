/**
 * tools/device_diagnostics_tools
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// tools/device_diagnostics_tools.ts
//
// Tool definitions exposing on-device diagnostic data to the B! inference
// engine. Port of CircleAI.Tools.DeviceDiagnosticsTools. Gives B! the ability to
// observe — and reason about — the physical health of the host device before
// scheduling heavy inference work.

import type { IDeviceContext } from "../device/index";
import type { ToolDefinition } from "./index";

/**
 * Tool definitions for on-device diagnostics. Mirrors
 * `CircleAI.Tools.DeviceDiagnosticsTools`.
 */
export const DeviceDiagnosticsTools = {
  /**
   * Returns the single `device.diagnose` tool definition. Register this
   * alongside {@link TheGeekNetworkTools.getAllTools} when an
   * {@link IDeviceContext} is available in the host.
   */
  diagnostics(): readonly ToolDefinition[] {
    return [
      {
        name: "device.diagnose",
        description:
          "Return a snapshot of the host device's health: CPU usage fraction, " +
          "available memory in MB, thermal state (normal/warm/critical), and " +
          "free storage in MB. Use before scheduling heavy inference to avoid " +
          "OOM conditions or OS thermal throttling.",
        parameters: {},
        requiredParameters: [],
      },
    ];
  },

  /**
   * Reads an {@link IDeviceContext} and produces a compact JSON string suitable
   * for returning as tool output to the inference engine. Null members are
   * serialised as JSON `null` so the model knows the data was unavailable, not
   * zero. Byte-exact with the C# `DiagnoseFromContext` string builder.
   */
  diagnoseFromContext(ctx: IDeviceContext): string {
    if (ctx === null || ctx === undefined) throw new Error("ctx is required.");

    const frac = (v: number | null): string => (v === null ? "null" : formatFixed3(v));
    const longMb = (v: number | null): string =>
      v === null ? "null" : String(Math.trunc(v / (1024 * 1024)));
    const thermal = (v: string | null): string => (v === null ? "null" : `"${v.toLowerCase()}"`);

    return (
      "{" +
      `"cpu_usage_fraction":${frac(ctx.cpuUsagePercent)},` +
      `"available_memory_mb":${longMb(ctx.availableMemoryBytes)},` +
      `"thermal_state":${thermal(ctx.thermalState)},` +
      `"storage_free_mb":${longMb(ctx.storageFreeBytes)}` +
      "}"
    );
  },
};

/**
 * Formats `v` with exactly 3 fractional digits using invariant `.` decimal
 * separator — the analogue of C# `v.ToString("0.000", InvariantCulture)`.
 */
function formatFixed3(v: number): string {
  return v.toFixed(3);
}
