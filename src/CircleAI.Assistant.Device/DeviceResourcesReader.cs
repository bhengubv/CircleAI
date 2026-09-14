// DeviceResourcesReader.cs
//
// The real device figures, from the hardware - the honest input the budget needs.
//
// THIS IS THE STEP ZERO THE WHOLE MODULE STANDS ON. Get this wrong and every
// budget decision is wrong. The standing trap (deviceprobe-mobile-ram-hook) is a
// probe that reads the MANAGED HEAP and calls it device RAM; a budget built on
// that refuses downloads a phone can take. So this reads the platform directly:
// StatFs for the storage a normal app may still write to, and
// ActivityManager.MemoryInfo for physical RAM - never the GC.

using Android.App;
using Android.OS;
using CircleAI.Assistant;

namespace CircleAI.Assistant.Device;

/// <summary>Reads the phone's real storage and memory.</summary>
public sealed class DeviceResourcesReader : IDeviceResources
{
    /// <inheritdoc />
    public DeviceResources Read()
    {
        try
        {
            var ctx = Application.Context;

            // STORAGE the app can actually use. StatFs on the files dir, and
            // AvailableBlocks (not free blocks) so the OS reserve a non-root app
            // may NOT touch is already excluded - which is exactly "free a normal
            // app may write to".
            var path = ctx.FilesDir?.AbsolutePath;
            if (string.IsNullOrEmpty(path)) return DeviceResources.Unknown;

            var stat = new StatFs(path);
            var block = stat.BlockSizeLong;
            var totalDisk = stat.BlockCountLong * block;
            var freeDisk = stat.AvailableBlocksLong * block;

            // PHYSICAL RAM, not the heap. TotalMem is the machine's RAM; AvailMem
            // is what the system says is available now - which on Android is the
            // honest figure, because "free" is near zero (spare RAM is cache the
            // OS reclaims under pressure).
            long totalRam = 0, availRam = 0;
            if (ctx.GetSystemService(Android.Content.Context.ActivityService) is ActivityManager am)
            {
                using var mem = new ActivityManager.MemoryInfo();
                am.GetMemoryInfo(mem);
                totalRam = mem.TotalMem;
                availRam = mem.AvailMem;
            }

            return new DeviceResources(totalDisk, freeDisk, totalRam, availRam);
        }
        catch (System.Exception ex)
        {
            // A reading that fails is "I do not know", never a guess. The budget
            // then makes no decision, which is the safe outcome.
            Android.Util.Log.Warn("CircleAI.Memory", "could not read device resources: " + ex.Message);
            return DeviceResources.Unknown;
        }
    }
}
