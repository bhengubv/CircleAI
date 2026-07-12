// DeviceDiagnosticsTools.kt
//
// Kotlin port of CircleAI.Tools/DeviceDiagnosticsTools.cs.
//
// Tool definitions exposing on-device diagnostic data to the B! inference
// engine. Gives B! the ability to observe — and reason about — the physical
// health of the host device before scheduling heavy inference work.
//
// Adapted to the Kotlin IDeviceContext shape: thermalState is a String? here
// (not a ThermalState enum), and the memory/storage members are raw byte counts.

package com.bhengubv.circleai.tools

import com.bhengubv.circleai.device.IDeviceContext
import java.util.Locale

/**
 * Tool definitions for on-device diagnostics. Exposes CPU usage, memory,
 * thermal state, and free storage so that B! can make informed scheduling
 * decisions (e.g. defer a large-model load when the device is hot).
 */
object DeviceDiagnosticsTools {

    /**
     * Returns the single `device.diagnose` tool definition. Register this
     * alongside [TheGeekNetworkTools.getAllTools] when an [IDeviceContext] is
     * available in the host.
     */
    fun diagnostics(): List<ToolDefinition> = listOf(
        ToolDefinition(
            name = "device.diagnose",
            description =
                "Return a snapshot of the host device's health: CPU usage fraction, " +
                    "available memory in MB, thermal state (normal/warm/critical), and " +
                    "free storage in MB. Use before scheduling heavy inference to avoid " +
                    "OOM conditions or OS thermal throttling.",
            parameters = emptyMap(),
            requiredParameters = emptyList(),
        ),
    )

    /**
     * Reads an [IDeviceContext] and produces a compact JSON string suitable for
     * returning as tool output to the inference engine. Null members are
     * serialised as JSON `null` so the model knows the data was unavailable, not
     * zero.
     */
    fun diagnoseFromContext(ctx: IDeviceContext): String {
        fun frac(v: Float?): String =
            if (v != null) String.format(Locale.ROOT, "%.3f", v) else "null"

        fun longMb(v: Long?): String =
            if (v != null) (v / (1024L * 1024L)).toString() else "null"

        fun thermal(v: String?): String =
            if (v != null) "\"${v.lowercase(Locale.ROOT)}\"" else "null"

        return "{" +
            "\"cpu_usage_fraction\":${frac(ctx.cpuUsagePercent)}," +
            "\"available_memory_mb\":${longMb(ctx.availableMemoryBytes)}," +
            "\"thermal_state\":${thermal(ctx.thermalState)}," +
            "\"storage_free_mb\":${longMb(ctx.storageFreeBytes)}" +
            "}"
    }
}
