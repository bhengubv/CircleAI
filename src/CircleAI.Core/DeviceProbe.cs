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
    /// (3.1.0) Detected GPU/NPU memory in gigabytes. <c>null</c> means
    /// "unknown" — typical when the GPU is <see cref="GpuKind.None"/> or
    /// the host hasn't wired a probe that can query the GPU. Set by
    /// platform adapters (Metal device query on Apple, NVML on CUDA,
    /// Vulkan VkPhysicalDeviceMemoryProperties on Vulkan/AMD/Intel,
    /// <c>ActivityManager.getMemoryInfo()</c>-derived approximation on
    /// Android). Consumed by <c>IModelSelector.BestFit</c> to gate
    /// VRAM-bound entries — notably the <c>ChatCapability.Video</c>
    /// catalogue.
    /// </summary>
    public double? VramGb { get; init; }

    /// <summary>
    /// Total physical RAM in bytes — the DEVICE CLASS, used by <see cref="Classify"/>
    /// for tier (a 3 GB phone is a Phone even when momentarily busy). Distinct from
    /// <see cref="RamAvailableBytes"/>, which is FREE RAM and gates model FIT: a
    /// model needs its weight in free RAM to load, so selecting against total RAM
    /// OOM-kills the app on a phone with little free (observed: a 3.6 GB / 1.5 GB-free
    /// P30 picked a 4 B model and was OOM-killed on load). <c>0</c> = unknown; tier
    /// then falls back to <see cref="RamAvailableBytes"/> (desktop, where they are close).
    /// </summary>
    public long RamTotalBytes { get; init; }

    /// <summary>
    /// Fraction of FREE RAM the selector may commit to a model, leaving the rest
    /// as headroom for KV-cache growth during generation + native runtime
    /// overhead. Fitting a model into 100% of free RAM works until the first long
    /// output, when the growing KV cache OOMs it. 0.85 reserves ~15%. Tunable.
    /// </summary>
    public const double RamFitHeadroom = 0.85;

    /// <summary>
    /// Bytes per GB in the catalogue's units — decimal, 10^9.
    /// </summary>
    /// <remarks>
    /// Not 2^30, and the difference was a real bug. <c>MinRamGb</c> and
    /// <c>MinStorageGb</c> are derived from file sizes divided by 10^9, while every
    /// device-side number here was divided by 2^30, and then the two were compared
    /// to each other. A GiB figure is ~7% numerically smaller than the same
    /// quantity in GB, so the fit check was really demanding a model fit in 79.2%
    /// of free RAM while the constant next to it said 85%.
    ///
    /// It failed in the quiet direction: models were REFUSED that would have run
    /// fine, so the smallest phones — the ones this exists for — got told a
    /// capability was unavailable when it was not. Nothing crashed, so nothing
    /// pointed at it.
    ///
    /// The catalogue's unit wins because it is persisted: 78 entries already carry
    /// values derived at 10^9, and reinterpreting them would silently change what
    /// every one of them means.
    /// </remarks>
    public const double BytesPerGb = 1_000_000_000.0;

    /// <summary>
    /// Free RAM in GB the selector may actually commit to a model — free RAM
    /// scaled by <see cref="RamFitHeadroom"/>. Use this for MinRamGb fit checks,
    /// NOT the raw <see cref="RamAvailableBytes"/>, or a model that fits at load
    /// can still OOM once generation grows the KV cache.
    /// </summary>
    public double UsableRamGb => RamAvailableBytes * RamFitHeadroom / BytesPerGb;

    /// <summary>
    /// Free storage in GB, in the same units as <c>MinStorageGb</c>.
    /// </summary>
    /// <remarks>
    /// Exposed so no caller divides for itself. Five call sites each wrote their
    /// own <c>/ (1024.0 * 1024 * 1024)</c>, which is exactly how the unit drift got
    /// in and stayed in — one shared property is the only version of this that
    /// cannot go out of step again.
    /// </remarks>
    public double StorageFreeGb => StorageFreeBytes / BytesPerGb;

    /// <summary>Real device memory, supplied by a platform head that can read it. RamTotalBytes = device-class total; RamAvailableBytes = free RAM for fit.</summary>
    public readonly record struct PlatformMemory(long? RamAvailableBytes, long? StorageFreeBytes, long? RamTotalBytes = null);

    /// <summary>
    /// Optional platform hook. The platform-neutral Core cannot read a mobile
    /// device's real RAM/storage: <see cref="GC.GetGCMemoryInfo"/> reports the
    /// per-app GC heap limit (~100 MB in an Android sandbox) and
    /// <see cref="DriveInfo"/> denies the sandboxed data partition, so a 3 GB
    /// phone would be misclassified as a <see cref="DeviceTier.Wearable"/> and
    /// every model would come back <c>NothingFits</c>. An Android / iOS head
    /// sets this once at startup so every <see cref="Snapshot"/> reports real
    /// hardware. Left <c>null</c> on desktop / server, where the heuristics are
    /// accurate.
    /// </summary>
    public static Func<PlatformMemory>? PlatformMemoryProbe { get; set; }

    /// <summary>Where <see cref="RamAvailableBytes"/> actually came from.</summary>
    public enum RamMeasurement
    {
        /// <summary>A caller stated it outright (tests, hosts that already know).</summary>
        Explicit,

        /// <summary>Read from the device by a platform head via <see cref="PlatformMemoryProbe"/>.</summary>
        PlatformMeasured,

        /// <summary>Nobody supplied one, so it was inferred. On mobile that is a guess.</summary>
        Heuristic,
    }

    /// <summary>How the RAM figure was obtained. Defaults to <see cref="RamMeasurement.Explicit"/>.</summary>
    /// <remarks>
    /// A probe that GUESSED used to be indistinguishable from one that MEASURED, and
    /// every verdict downstream was then stated with full confidence about a number
    /// that is the GC heap limit — roughly 100 MB inside an Android sandbox. The
    /// device reads as a wearable, every model comes back NothingFits, and nothing
    /// anywhere says the input was invented. Recording the source is what lets the
    /// answer admit it.
    /// </remarks>
    public RamMeasurement RamSource { get; init; } = RamMeasurement.Explicit;

    /// <summary>
    /// A plain-language warning when the RAM figure is a guess that looks wrong, or
    /// null when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. The heuristic is perfectly good on desktop and server,
    /// where it returns GB-scale numbers, and warning there would be noise nobody
    /// reads. It fires only on the actual signature of the bug: an inferred figure
    /// too small for any real device, which is what a mobile head that never set
    /// <see cref="PlatformMemoryProbe"/> produces.
    /// </remarks>
    public string? MeasurementWarning =>
        RamSource == RamMeasurement.Heuristic && RamAvailableBytes < 512L * 1024 * 1024
            ? $"this device's RAM was not measured — {RamAvailableBytes / (1024.0 * 1024):0} MB is the " +
              "managed heap limit, not the hardware. The platform head has not set " +
              "DeviceProbe.PlatformMemoryProbe, so every size decision here is based on a guess"
            : null;

    /// <summary>
    /// Build a probe from runtime facts. Free, allocation-light, callable
    /// per-startup. <paramref name="modelCacheDirectory"/> defaults to
    /// <c>AppContext.BaseDirectory</c> — pass an explicit path when the
    /// caller knows where models will land.
    /// </summary>
    public static DeviceProbe Snapshot(
        string?  modelCacheDirectory = null,
        GpuKind? gpuOverride         = null,
        ThermalClass? thermalOverride = null,
        double?  vramGbOverride      = null,
        long?    ramBytesOverride     = null,
        long?    storageBytesOverride = null,
        long?    ramTotalBytesOverride = null)
    {
        // Real hardware first: explicit overrides, then the platform hook (set by
        // an Android / iOS head), then the platform-neutral heuristics. The
        // heuristics are accurate on desktop / server but read the GC heap limit
        // and the process drive, which a mobile sandbox reports as ~100 MB / 0 B.
        var platformSuppliedRam = false;
        if (ramBytesOverride is null || storageBytesOverride is null || ramTotalBytesOverride is null)
        {
            var pm = PlatformMemoryProbe?.Invoke();
            if (ramBytesOverride is null && pm?.RamAvailableBytes is > 0)
                platformSuppliedRam = true;

            ramBytesOverride      ??= pm?.RamAvailableBytes;
            storageBytesOverride  ??= pm?.StorageFreeBytes;
            ramTotalBytesOverride ??= pm?.RamTotalBytes;
        }

        long ram;
        RamMeasurement ramSource;
        if (ramBytesOverride is > 0)
        {
            ram = ramBytesOverride.Value;
            // Distinguishes "a head read the hardware" from "a caller passed a
            // number", because only the first says anything about this device.
            ramSource = platformSuppliedRam
                ? RamMeasurement.PlatformMeasured
                : RamMeasurement.Explicit;
        }
        else
        {
            var gcInfo = GC.GetGCMemoryInfo();
            ram = Math.Max(0L, gcInfo.TotalAvailableMemoryBytes);
            ramSource = RamMeasurement.Heuristic;
        }

        // Device-class RAM for tiering. Defaults to the available figure when the
        // host supplies no total (desktop / server, where free ≈ total).
        var ramTotal = ramTotalBytesOverride is > 0 ? ramTotalBytesOverride.Value : ram;

        long storage = 0;
        if (storageBytesOverride is > 0)
        {
            storage = storageBytesOverride.Value;
        }
        else
        {
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
            Connectivity:      conn)
        {
            VramGb = vramGbOverride,
            RamTotalBytes = ramTotal,
            RamSource = ramSource,
        };
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

        // Tier reflects the DEVICE CLASS (total RAM), not momentary free RAM — a
        // 3 GB phone is a Phone even when busy. Model FIT uses RamAvailableBytes.
        var classBytes = RamTotalBytes > 0 ? RamTotalBytes : RamAvailableBytes;

        // 2^30 here ON PURPOSE, unlike the fit checks which use BytesPerGb. The
        // thresholds below are hand-picked against how devices are SOLD — "a 4 GB
        // phone", "a 16 GB laptop" — and those figures are binary. Nothing is
        // compared against the catalogue here, so there is no unit to agree with;
        // switching this to 10^9 would silently move every tier boundary.
        var ramGb = classBytes / (1024.0 * 1024 * 1024);

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
