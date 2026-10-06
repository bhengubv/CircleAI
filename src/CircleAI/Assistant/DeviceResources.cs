// DeviceResources.cs
//
// What the device actually has to spare - read for real, not from the GC heap.
//
// STEP ZERO OF THE MEMORY MANAGER, AND THE ONE THAT MUST BE RIGHT. There is a
// standing gotcha on this machine: the old device probe reads the managed heap,
// not physical RAM (deviceprobe-mobile-ram-hook). A budget computed against the
// wrong number is worse than no budget - it will refuse a download a phone can
// take, or wave through one it cannot. So this is the honest figure, and the
// head that can read the hardware (StatFs, ActivityManager.MemoryInfo on
// Android) is the only thing that fills it in.
//
// BCL only, in the shared assembly, so the policy that reads it (MemoryBudget)
// is testable without a phone and the same on every head.

namespace CircleAI.Assistant;

/// <summary>The device's real storage and memory, in bytes.</summary>
/// <param name="TotalDiskBytes">Total internal storage.</param>
/// <param name="FreeDiskBytes">Storage a normal app may still write to.</param>
/// <param name="TotalRamBytes">Total physical RAM.</param>
/// <param name="AvailableRamBytes">
/// RAM the system reports as available right now - not "free", which on Android
/// is almost always near zero because the OS uses spare RAM for caches it will
/// hand back under pressure.
/// </param>
public readonly record struct DeviceResources(
    long TotalDiskBytes,
    long FreeDiskBytes,
    long TotalRamBytes,
    long AvailableRamBytes)
{
    /// <summary>Whether the figures look real, so a caller can decline to act on junk.</summary>
    /// <remarks>
    /// A head that cannot read the hardware returns the empty value rather than a
    /// guess, and the budget then makes no decision - the honest outcome is "I do
    /// not know", not a made-up ceiling.
    /// </remarks>
    public bool Known => TotalDiskBytes > 0 && TotalRamBytes > 0;

    /// <summary>Nothing known. The head could not read the device.</summary>
    public static DeviceResources Unknown => default;

    /// <summary>Free storage as a fraction of total, or 0 when unknown.</summary>
    public double FreeDiskFraction => TotalDiskBytes > 0
        ? (double)FreeDiskBytes / TotalDiskBytes
        : 0;
}

/// <summary>Reads the device's real storage and memory.</summary>
/// <remarks>
/// One method, and it is synchronous and cheap - StatFs and MemoryInfo are
/// microseconds. A head with no way to read the hardware (a browser tab, the
/// console) returns <see cref="DeviceResources.Unknown"/>.
/// </remarks>
public interface IDeviceResources
{
    /// <summary>The device's storage and memory right now.</summary>
    DeviceResources Read();
}

/// <summary>A head that cannot see the hardware. Says so rather than guessing.</summary>
public sealed class UnknownDeviceResources : IDeviceResources
{
    /// <summary>The shared instance; it holds no state.</summary>
    public static UnknownDeviceResources Instance { get; } = new();

    /// <inheritdoc />
    public DeviceResources Read() => DeviceResources.Unknown;
}
