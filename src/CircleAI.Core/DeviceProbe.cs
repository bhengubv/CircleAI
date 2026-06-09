// DeviceProbe.cs
//
// A point-in-time snapshot of what the device can physically do. The
// SDK uses this to pick model size, context window, concurrency,
// and KV-cache compression. The consumer never constructs one
// directly — they call DeviceProbe.Snapshot() or supply nothing and
// let DefaultDeviceContext build one.

using System;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace CircleAI.Core;

/// <summary>
/// What kind of GPU acceleration the device exposes. Detected by the
/// inference server's CapabilityProbe in production; <see cref="DeviceProbe.Snapshot"/>
/// only sets this when caller supplies it — otherwise <see cref="None"/>.
/// </summary>
public enum GpuKind
{
    None    = 0,
    Vulkan  = 1,
    Metal   = 2,
    Cuda    = 3,
    OpenCL  = 4,
    Npu     = 5,
}

/// <summary>
/// How aggressively the device can sustain compute without throttling.
/// </summary>
public enum ThermalClass
{
    /// <summary>Smartwatch / fitness band. Single CPU core sustained.</summary>
    Wearable = 0,

    /// <summary>Phone, tablet, fanless laptop. Brief bursts; throttles under load.</summary>
    Passive  = 1,

    /// <summary>Desktop, workstation, fan-cooled laptop. Sustained full load.</summary>
    Active   = 2,
}

/// <summary>
/// Whether the device can reach the model registry / fetch new bundles
/// right now. Affects which transport <c>IModelSelector</c> can
/// assume for downloads.
/// </summary>
public enum Connectivity
{
    Offline = 0,
    Mesh    = 1,
    Online  = 2,
}

/// <summary>
/// Coarse device class derived from <see cref="DeviceProbe"/>. Used as
/// the lookup key for default context window, concurrency, and KV
/// compression mode.
/// </summary>
public enum DeviceTier
{
    Wearable    = 0,
    Phone       = 1,
    Tablet      = 2,
    Desktop     = 3,
    Workstation = 4,
}

/// <summary>
/// What the device can physically do right now. Constructed by
/// <see cref="Snapshot"/> on demand or supplied by a platform adapter.
/// </summary>
/// <param name="RamAvailableBytes">Total physical RAM available to managed code (GC view).</param>
/// <param name="StorageFreeBytes">Free space on the drive that hosts the model cache.</param>
/// <param name="Gpu">Detected GPU backend. <see cref="GpuKind.None"/> when unknown.</param>
/// <param name="CpuCores">Logical processor count.</param>
/// <param name="Thermal">Sustained-compute class — see <see cref="ThermalClass"/>.</param>
/// <param name="Connectivity">Whether the device can reach the registry right now.</param>
public sealed record DeviceProbe(
    long          RamAvailableBytes,
    long          StorageFreeBytes,
    GpuKind       Gpu,
    int           CpuCores,
    ThermalClass  Thermal,
    Connectivity  Connectivity)
{
    /// <summary>
    /// Build a probe from runtime facts. Free, allocation-light, callable
    /// per-startup. <paramref name="modelCacheDirectory"/> defaults to
    /// <c>AppContext.BaseDirectory</c> — pass an explicit path when the
    /// caller knows where models will land.
    /// </summary>
    public static DeviceProbe Snapshot(
        string?  modelCacheDirectory = null,
        GpuKind? gpuOverride         = null,
        ThermalClass? thermalOverride = null)
    {
        var gcInfo = GC.GetGCMemoryInfo();
        var ram = Math.Max(0L, gcInfo.TotalAvailableMemoryBytes);

        long storage = 0;
        try
        {
            var probePath = modelCacheDirectory ?? AppContext.BaseDirectory;
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(probePath));
            if (!string.IsNullOrWhiteSpace(driveRoot))
                storage = new DriveInfo(driveRoot).AvailableFreeSpace;
        }
        catch
        {
            // Some hosts (Docker squash, sandboxed apps) deny DriveInfo —
            // fall through with storage = 0; selector will skip size gating.
        }

        var conn = NetworkInterface.GetIsNetworkAvailable()
            ? Connectivity.Online
            : Connectivity.Offline;

        // Heuristic thermal class when caller didn't supply one. Wearables
        // and phones must be flagged by the host; we default to Active
        // (desktop assumption) because the SDK runs primarily on desktops
        // when no host wires the override.
        var thermal = thermalOverride ?? ThermalClass.Active;

        return new DeviceProbe(
            RamAvailableBytes: ram,
            StorageFreeBytes:  storage,
            Gpu:               gpuOverride ?? GpuKind.None,
            CpuCores:          Math.Max(1, Environment.ProcessorCount),
            Thermal:           thermal,
            Connectivity:      conn);
    }

    /// <summary>
    /// Classify this probe into a coarse <see cref="DeviceTier"/>. Used
    /// to look up context window, concurrency, and KV compression
    /// defaults.
    /// </summary>
    public DeviceTier Classify()
    {
        // Wearables are flagged explicitly by the host — no heuristic
        // catches a smartwatch from raw counts alone.
        if (Thermal == ThermalClass.Wearable)
            return DeviceTier.Wearable;

        var ramGb = RamAvailableBytes / (1024.0 * 1024 * 1024);

        // Workstation: 16+ cores, 32+ GB RAM, active cooling, GPU.
        if (CpuCores >= 16 && ramGb >= 32 && Thermal == ThermalClass.Active && Gpu != GpuKind.None)
            return DeviceTier.Workstation;

        // Desktop: 8+ cores, 16+ GB RAM, active cooling.
        if (CpuCores >= 8 && ramGb >= 16 && Thermal == ThermalClass.Active)
            return DeviceTier.Desktop;

        // Tablet: 6+ GB RAM, passive or active.
        if (ramGb >= 6)
            return DeviceTier.Tablet;

        // Phone: 3+ GB RAM.
        if (ramGb >= 3)
            return DeviceTier.Phone;

        // Anything smaller = wearable (low RAM constrained device).
        return DeviceTier.Wearable;
    }
}

/// <summary>
/// Default knobs derived from <see cref="DeviceTier"/>. Centralised here
/// so context window, concurrency, agentic iterations, and KV
/// compression mode all share the same tier table.
/// </summary>
public static class DeviceTierDefaults
{
    /// <summary>Inference context window in tokens.</summary>
    public static int ContextWindow(DeviceTier tier) => tier switch
    {
        DeviceTier.Wearable    => 2048,
        DeviceTier.Phone       => 4096,
        DeviceTier.Tablet      => 8192,
        DeviceTier.Desktop     => 32_768,
        DeviceTier.Workstation => 131_072,
        _                       => 4096,
    };

    /// <summary>
    /// Maximum concurrent dispatch slots for orchestration (LokiOrchestrator).
    /// </summary>
    public static int MaxConcurrency(DeviceTier tier, int cpuCores) => tier switch
    {
        DeviceTier.Wearable    => 1,
        DeviceTier.Phone       => 2,
        DeviceTier.Tablet      => 4,
        DeviceTier.Desktop     => 8,
        DeviceTier.Workstation => Math.Min(16, Math.Max(1, cpuCores - 2)),
        _                       => 2,
    };

    /// <summary>
    /// Maximum tool-call → re-prompt iterations in <c>AIService.AgenticChatAsync</c>.
    /// </summary>
    public static int AgenticMaxIterations(DeviceTier tier) => tier switch
    {
        DeviceTier.Wearable    => 2,
        DeviceTier.Phone       => 3,
        DeviceTier.Tablet      => 5,
        DeviceTier.Desktop     => 10,
        DeviceTier.Workstation => 10,
        _                       => 3,
    };
}
