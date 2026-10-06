// HostProfile.cs
//
// The canonical, richer-than-DeviceCapabilities snapshot returned by an
// ICapabilityProbe. CircleAI.Hosting.InferenceBridge.DeviceCapabilities is
// the lean public-API record that consumers see; HostProfile is the
// internal probe result that BackendSelector and NativeRuntimeFetcher
// consume directly. Conversion is one-way (HostProfile -> DeviceCapabilities)
// because DeviceCapabilities omits a few fields that are useful for
// backend selection but not for end-callers (CPU model, driver version,
// core-count split).

namespace CircleAI.Runtime.Capabilities;

/// <summary>
/// OS family the probe recognised.
/// </summary>
public enum OperatingSystemKind
{
    /// <summary>Probe could not identify the OS.</summary>
    Unknown = 0,
    /// <summary>Microsoft Windows desktop / Server.</summary>
    Windows = 1,
    /// <summary>Any Linux distribution.</summary>
    Linux = 2,
    /// <summary>Apple macOS.</summary>
    MacOS = 3,
    /// <summary>Google Android (including Android-derived OSes that report as Linux + Bionic).</summary>
    Android = 4,
    /// <summary>Apple iOS / iPadOS / tvOS / watchOS.</summary>
    IOS = 5,
    /// <summary>Huawei HarmonyOS / OpenHarmony.</summary>
    HarmonyOS = 6,
}

/// <summary>CPU architecture family.</summary>
public enum ArchitectureKind
{
    /// <summary>Probe could not identify the architecture.</summary>
    Unknown = 0,
    /// <summary>32-bit Intel/AMD.</summary>
    X86 = 1,
    /// <summary>64-bit Intel/AMD (AMD64 / Intel 64).</summary>
    X64 = 2,
    /// <summary>32-bit ARM (Cortex-A, etc.).</summary>
    Arm = 3,
    /// <summary>64-bit ARM (ARMv8 / Apple Silicon / Cortex-A76+).</summary>
    Arm64 = 4,
    /// <summary>Loongson LoongArch64 (mainland China sovereign arch).</summary>
    Loong64 = 5,
}

/// <summary>GPU vendor identifier.</summary>
public enum GpuVendor
{
    /// <summary>No GPU detected, or vendor unknown.</summary>
    None = 0,
    /// <summary>NVIDIA Corp.</summary>
    Nvidia = 1,
    /// <summary>Advanced Micro Devices.</summary>
    Amd = 2,
    /// <summary>Intel Corp. (integrated and Arc).</summary>
    Intel = 3,
    /// <summary>Apple Silicon GPU (M1/M2/M3/M4 family).</summary>
    Apple = 4,
    /// <summary>Qualcomm Adreno (Snapdragon mobile / compute).</summary>
    Qualcomm = 5,
    /// <summary>Huawei Maleoon / Mali-licensed GPUs on Kirin SoCs.</summary>
    Huawei = 6,
    /// <summary>ARM Mali (third-party SoCs not covered by other vendors).</summary>
    Arm = 7,
    /// <summary>Vendor was identified but is not in this enum yet.</summary>
    Other = 99,
}

/// <summary>NPU / neural accelerator vendor identifier.</summary>
public enum NpuVendor
{
    /// <summary>No NPU detected.</summary>
    None = 0,
    /// <summary>Apple Neural Engine (ANE) on Apple Silicon.</summary>
    AppleNeuralEngine = 1,
    /// <summary>Qualcomm Hexagon DSP / NPU.</summary>
    QualcommHexagon = 2,
    /// <summary>Huawei Ascend (data-centre + Atlas + Kirin NPU).</summary>
    HuaweiAscend = 3,
    /// <summary>Intel VPU (Movidius / Meteor Lake NPU).</summary>
    IntelVpu = 4,
    /// <summary>Cambricon MLU.</summary>
    CambriconMlu = 5,
    /// <summary>Vendor was identified but is not in this enum yet.</summary>
    Other = 99,
}

/// <summary>Discovered GPU details.</summary>
/// <param name="Vendor">Vendor family.</param>
/// <param name="Model">Marketing name (e.g. <c>"NVIDIA GeForce RTX 4080"</c>).</param>
/// <param name="VramBytes">Dedicated video memory in bytes. <c>0</c> when probe could not determine.</param>
/// <param name="DriverVersion">Driver version string when known.</param>
public sealed record GpuInfo(
    GpuVendor Vendor,
    string Model,
    long VramBytes,
    string? DriverVersion);

/// <summary>Discovered NPU details.</summary>
/// <param name="Vendor">Vendor family.</param>
/// <param name="Model">Marketing name (e.g. <c>"Apple Neural Engine 16-core"</c>).</param>
public sealed record NpuInfo(
    NpuVendor Vendor,
    string Model);

/// <summary>
/// Full host capability snapshot — the result of an
/// <see cref="ICapabilityProbe.ProbeAsync"/> call.
/// </summary>
/// <param name="Os">OS family.</param>
/// <param name="OsVersion">OS version string (e.g. <c>"10.0.22631"</c>, <c>"14.4.1"</c>).</param>
/// <param name="Arch">CPU architecture family.</param>
/// <param name="CpuModel">CPU marketing name (e.g. <c>"Apple M2 Pro"</c>, <c>"AMD Ryzen 9 7950X"</c>).</param>
/// <param name="LogicalCoreCount">Logical CPU core count (includes SMT siblings on x86 HT).</param>
/// <param name="PhysicalCoreCount">Physical CPU core count (HT pairs counted once).</param>
/// <param name="TotalPhysicalMemoryBytes">Installed RAM in bytes.</param>
/// <param name="Gpu">GPU details. <c>null</c> when no usable GPU was detected.</param>
/// <param name="Npu">NPU details. <c>null</c> when no NPU was detected.</param>
/// <param name="ProbedAt">UTC timestamp the probe was taken at.</param>
public sealed record HostProfile(
    OperatingSystemKind Os,
    string OsVersion,
    ArchitectureKind Arch,
    string CpuModel,
    int LogicalCoreCount,
    int PhysicalCoreCount,
    long TotalPhysicalMemoryBytes,
    GpuInfo? Gpu,
    NpuInfo? Npu,
    DateTimeOffset ProbedAt)
{
    /// <summary>
    /// Convenience flag — true when <see cref="Gpu"/> is present and has at least
    /// <paramref name="minimumVramBytes"/> of dedicated VRAM.
    /// </summary>
    public bool HasUsableGpu(long minimumVramBytes = 2L * 1024 * 1024 * 1024) =>
        Gpu is not null && Gpu.VramBytes >= minimumVramBytes;

    /// <summary>True when the host runs on a 64-bit architecture (X64, Arm64, Loong64).</summary>
    public bool Is64Bit => Arch is ArchitectureKind.X64 or ArchitectureKind.Arm64 or ArchitectureKind.Loong64;
}
