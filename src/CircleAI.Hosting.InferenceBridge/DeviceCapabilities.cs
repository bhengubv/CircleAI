// DeviceCapabilities.cs
//
// A snapshot of what the underlying hardware can do. The bridge surfaces this
// once so callers can make routing decisions (which model to request, whether
// to fall back to a cloud generator, etc.).

namespace CircleAI.Hosting.InferenceBridge;

/// <summary>
/// Static-ish capabilities report from the device hosting the bridge.
/// Values are sampled at bridge start and are not expected to change at
/// runtime (memory and core counts are stable; GPU/NPU presence does not
/// fluctuate). Callers are free to re-query on demand.
/// </summary>
/// <param name="OsName">
/// Human-readable OS family. Expected values: <c>"Android"</c>, <c>"iOS"</c>,
/// <c>"Windows"</c>, <c>"Linux"</c>, <c>"macOS"</c>. Other strings are allowed
/// but unrecognised by automated routing.
/// </param>
/// <param name="OsVersion">OS version string (e.g. <c>"14.0"</c>, <c>"10.0.22631"</c>).</param>
/// <param name="PhysicalMemoryBytes">Total physical RAM in bytes.</param>
/// <param name="CpuCoreCount">Logical CPU core count.</param>
/// <param name="HasGpu">Whether a discrete or integrated GPU is present and usable.</param>
/// <param name="GpuName">GPU model string when known.</param>
/// <param name="GpuMemoryBytes">VRAM in bytes when known.</param>
/// <param name="HasNpu">
/// Whether a dedicated neural accelerator is present (Apple Neural Engine,
/// Qualcomm Hexagon, Intel NPU, etc.).
/// </param>
/// <param name="NpuName">NPU model string when known.</param>
/// <param name="HasTransportLayerEncryption">
/// <c>true</c> when the IPC channel between caller and bridge is encrypted
/// (e.g. macOS XPC, Android Binder with SEAndroid, named-pipe ACL). Callers
/// can use this to decide whether to add their own encryption layer.
/// </param>
public sealed record DeviceCapabilities(
    string OsName,
    string OsVersion,
    long PhysicalMemoryBytes,
    int CpuCoreCount,
    bool HasGpu,
    string? GpuName,
    long? GpuMemoryBytes,
    bool HasNpu,
    string? NpuName,
    bool HasTransportLayerEncryption);
