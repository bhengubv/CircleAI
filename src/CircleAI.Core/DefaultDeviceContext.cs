// DefaultDeviceContext.cs
//
// IDeviceContext that actually probes the host. Used as the default in
// AIService when AIOptions.DeviceContext is not supplied. Replaces
// NullDeviceContext as the "no host wired up" baseline.
//
// Anything platform-specific (GPS, locale, battery, foreground app)
// stays null here — those still need a MAUI / web / CLI adapter. What
// DefaultDeviceContext does provide is the SDK-level hardware story:
// RAM, storage, CPU cores, thermal hint, connectivity. That alone is
// enough for IModelSelector / context-window derivation / concurrency
// derivation to do their jobs.

using System;
using System.IO;
using System.Net.NetworkInformation;

namespace CircleAI.Core;

/// <summary>
/// <see cref="IDeviceContext"/> backed by runtime probes (RAM, storage,
/// CPU cores, connectivity). Use this in headless / CLI / test hosts
/// that don't ship a platform adapter but still want the SDK to size
/// inference for the real hardware. Platform-specific sensors (GPS,
/// locale, battery, active app, location hint) stay <c>null</c>.
/// </summary>
public sealed class DefaultDeviceContext : IDeviceContext
{
    private readonly string _modelCacheDir;
    private readonly ThermalClass _thermalHint;

    /// <summary>
    /// Construct a default context. <paramref name="modelCacheDir"/>
    /// drives the DriveInfo lookup for <see cref="StorageFreeBytes"/>.
    /// <paramref name="thermalHint"/> defaults to <see cref="ThermalClass.Active"/>
    /// (assumes desktop / fan-cooled host). Mobile / wearable hosts should
    /// pass the appropriate value so device-tier derivation gives the
    /// right answer.
    /// </summary>
    public DefaultDeviceContext(
        string?       modelCacheDir = null,
        ThermalClass  thermalHint   = ThermalClass.Active)
    {
        _modelCacheDir = modelCacheDir ?? AppContext.BaseDirectory;
        _thermalHint   = thermalHint;
    }

    /// <summary>
    /// Shared instance with default settings (active thermal class,
    /// model cache rooted at AppContext.BaseDirectory). Safe to reuse —
    /// stateless apart from the probe values which are computed per call.
    /// </summary>
    public static readonly DefaultDeviceContext Instance = new();

    /// <summary>
    /// Build a <see cref="DeviceProbe"/> from the live runtime state plus
    /// this context's thermal hint. The hint is the only sticky value;
    /// everything else is re-read on every call.
    /// </summary>
    public DeviceProbe BuildProbe(GpuKind? gpuOverride = null) =>
        DeviceProbe.Snapshot(
            modelCacheDirectory: _modelCacheDir,
            gpuOverride:         gpuOverride,
            thermalOverride:     _thermalHint);

    // ------------------------------------------------------------------
    // IDeviceContext — sensorium
    // ------------------------------------------------------------------

    public string? ActiveAppId   => null;
    public string? Locale        => System.Globalization.CultureInfo.CurrentCulture.Name;
    public string? TimeZoneId    => TimeZoneInfo.Local.Id;
    public DateTimeOffset? LocalTime => DateTimeOffset.Now;

    public double? Latitude     => null;
    public double? Longitude    => null;
    public string? LocationHint => null;

    public float? BatteryLevel  => null;
    public bool?  IsCharging    => null;

    /// <summary>
    /// "online" when <see cref="NetworkInterface.GetIsNetworkAvailable"/>
    /// reports a usable interface, "none" otherwise. Hosts that detect a
    /// mesh transport should override.
    /// </summary>
    public string? NetworkType
    {
        get
        {
            try { return NetworkInterface.GetIsNetworkAvailable() ? "online" : "none"; }
            catch { return null; }
        }
    }

    public float? CpuUsagePercent => null;

    /// <summary>RAM available to managed code per <see cref="GC.GetGCMemoryInfo"/>.</summary>
    public long? AvailableMemoryBytes
    {
        get
        {
            try { return Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes); }
            catch { return null; }
        }
    }

    /// <summary>
    /// Sticky hint from construction (no live thermal sensor on most hosts).
    /// Always reports <see cref="ThermalState.Normal"/> when a thermal
    /// class is set — hosts that detect real throttling should supply
    /// their own <see cref="IDeviceContext"/> instead.
    /// </summary>
    ThermalState? IDeviceContext.ThermalState => ThermalState.Normal;

    /// <summary>Free space on the drive that holds the model cache directory.</summary>
    public long? StorageFreeBytes
    {
        get
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(_modelCacheDir));
                return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root).AvailableFreeSpace;
            }
            catch { return null; }
        }
    }

    public DateTimeOffset? LastActiveUtc => null;
}
