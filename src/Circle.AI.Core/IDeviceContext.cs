// IDeviceContext.cs
//
// Sensorium — the device-level context snapshot that B! can observe.
// Platform adapters (MAUI, web, CLI) implement this interface and inject
// it via AIOptions so that B! is aware of the user's real-world state
// without the AI core depending on any platform SDK.
//
// All members are nullable so that implementations can return null for
// sensors they don't have access to (e.g. a CLI host has no GPS).

using System;

namespace Circle.AI.Core
{
    /// <summary>
    /// A snapshot of device/environment state available to B! at inference
    /// time. Implement this interface in the platform host (MAUI, web) and
    /// inject it via <c>AIOptions.DeviceContext</c>.
    /// </summary>
    public interface IDeviceContext
    {
        // ------------------------------------------------------------------
        // Identity / locale
        // ------------------------------------------------------------------

        /// <summary>
        /// Identifier of the app currently in the foreground (e.g.
        /// <c>"tgn.sdpkt"</c>, <c>"tgn.bidbaas"</c>). Helps B! understand
        /// cross-app intent.
        /// </summary>
        string? ActiveAppId { get; }

        /// <summary>IETF BCP 47 locale of the device (e.g. <c>"en-ZA"</c>).</summary>
        string? Locale { get; }

        /// <summary>IANA timezone identifier (e.g. <c>"Africa/Johannesburg"</c>).</summary>
        string? TimeZoneId { get; }

        /// <summary>
        /// Current local time at the device. When non-null, B! can reason
        /// about time-of-day ("it's 11 pm — are you sure?").
        /// </summary>
        DateTimeOffset? LocalTime { get; }

        // ------------------------------------------------------------------
        // Location
        // ------------------------------------------------------------------

        /// <summary>WGS-84 latitude in decimal degrees, or <c>null</c> if unavailable.</summary>
        double? Latitude { get; }

        /// <summary>WGS-84 longitude in decimal degrees, or <c>null</c> if unavailable.</summary>
        double? Longitude { get; }

        /// <summary>
        /// Human-readable location hint (city, suburb, or address fragment).
        /// Populated by the platform layer; never fetched by the AI core.
        /// </summary>
        string? LocationHint { get; }

        // ------------------------------------------------------------------
        // Device health
        // ------------------------------------------------------------------

        /// <summary>Battery level 0.0–1.0, or <c>null</c> if unavailable.</summary>
        float? BatteryLevel { get; }

        /// <summary><c>true</c> if charging, <c>false</c> if on battery, <c>null</c> if unknown.</summary>
        bool? IsCharging { get; }

        /// <summary>
        /// Network connectivity type (e.g. <c>"wifi"</c>, <c>"cellular"</c>,
        /// <c>"none"</c>, <c>"mesh"</c>). Lets B! defer heavy tasks on metered
        /// or absent connections.
        /// </summary>
        string? NetworkType { get; }

        // ------------------------------------------------------------------
        // Diagnostics
        // ------------------------------------------------------------------

        /// <summary>
        /// CPU usage as a fraction 0.0–1.0 (overall or average across cores),
        /// or <c>null</c> if the platform cannot report it. B! uses this to
        /// defer compute-heavy inference tasks when the CPU is saturated.
        /// </summary>
        float? CpuUsagePercent { get; }

        /// <summary>
        /// Available physical memory in bytes, or <c>null</c> if unavailable.
        /// B! skips large model loads when available memory is critically low.
        /// </summary>
        long? AvailableMemoryBytes { get; }

        /// <summary>
        /// Current thermal condition of the device, or <c>null</c> if the
        /// platform does not expose thermal data. See <see cref="ThermalState"/>
        /// for the three-level scale. B! backs off inference when
        /// <see cref="ThermalState.Critical"/> is reported.
        /// </summary>
        ThermalState? ThermalState { get; }

        /// <summary>
        /// Free storage space in bytes, or <c>null</c> if unavailable.
        /// Used by the model downloader to gate downloads before starting them.
        /// </summary>
        long? StorageFreeBytes { get; }

        // ------------------------------------------------------------------
        // User signals
        // ------------------------------------------------------------------

        /// <summary>
        /// UTC timestamp of the last recorded user interaction. Lets B! know
        /// when the user was last active and tailor proactive messages.
        /// </summary>
        DateTimeOffset? LastActiveUtc { get; }
    }
}
