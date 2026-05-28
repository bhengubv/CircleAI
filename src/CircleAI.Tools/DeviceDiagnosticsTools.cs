// DeviceDiagnosticsTools.cs
//
// Tool definitions exposing on-device diagnostic data to the B! inference
// engine. Gives B! the ability to observe — and reason about — the physical
// health of the host device before scheduling heavy inference work.

using System.Collections.Generic;
using CircleAI.Core;

namespace CircleAI.Tools
{
    /// <summary>
    /// Tool definitions for on-device diagnostics. Exposes CPU usage, memory,
    /// thermal state, and free storage so that B! can make informed scheduling
    /// decisions (e.g. defer a large-model load when the device is hot).
    /// </summary>
    public static class DeviceDiagnosticsTools
    {
        private static ToolParameter Param(string type, string description, string[]? @enum = null) =>
            new() { Type = type, Description = description, Enum = @enum };

        /// <summary>
        /// Returns the single <c>device.diagnose</c> tool definition.
        /// Register this alongside <see cref="TheGeekNetworkTools.GetAllTools"/> when
        /// an <see cref="IDeviceContext"/> is available in the host.
        /// </summary>
        public static IReadOnlyList<ToolDefinition> Diagnostics() => new[]
        {
            new ToolDefinition
            {
                Name        = "device.diagnose",
                Description =
                    "Return a snapshot of the host device's health: CPU usage fraction, " +
                    "available memory in MB, thermal state (normal/warm/critical), and " +
                    "free storage in MB. Use before scheduling heavy inference to avoid " +
                    "OOM conditions or OS thermal throttling.",
                Parameters           = new Dictionary<string, ToolParameter>(),
                RequiredParameters   = System.Array.Empty<string>()
            }
        };

        /// <summary>
        /// Reads an <see cref="IDeviceContext"/> and produces a compact JSON string
        /// suitable for returning as tool output to the inference engine.
        /// </summary>
        /// <param name="ctx">
        /// The device context snapshot. Null members are serialised as JSON
        /// <c>null</c> so the model knows the data was unavailable, not zero.
        /// </param>
        public static string DiagnoseFromContext(IDeviceContext ctx)
        {
            ArgumentNullException.ThrowIfNull(ctx);

            static string Frac(float? v) =>
                v.HasValue
                    ? v.Value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                    : "null";

            static string LongMb(long? v) =>
                v.HasValue ? (v.Value / (1024L * 1024L)).ToString() : "null";

            static string Thermal(ThermalState? v) =>
                v.HasValue ? $"\"{v.Value.ToString().ToLowerInvariant()}\"" : "null";

            return
                $"{{" +
                $"\"cpu_usage_fraction\":{Frac(ctx.CpuUsagePercent)}," +
                $"\"available_memory_mb\":{LongMb(ctx.AvailableMemoryBytes)}," +
                $"\"thermal_state\":{Thermal(ctx.ThermalState)}," +
                $"\"storage_free_mb\":{LongMb(ctx.StorageFreeBytes)}" +
                $"}}";
        }
    }
}
