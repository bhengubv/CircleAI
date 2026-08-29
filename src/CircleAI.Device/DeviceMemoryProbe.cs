// DeviceMemoryProbe.cs
//
// Telling Core how much memory this phone actually has.
//
// NOBODY HAD EVER SET THIS. DeviceProbe exposes a hook for exactly this purpose
// and carries a comment explaining why a mobile head must fill it, and no head
// in this repository ever did - so on every phone CircleAI has run on, the RAM
// figure has been the managed heap limit, around a hundred megabytes inside an
// Android sandbox.
//
// THAT IS NOT ONLY A WRONG NUMBER ON A SCREEN. Model selection reads it. A 4 GB
// phone reads as a wearable, the selector plans against a hundred megabytes, and
// what it concludes this device can run is a guess wearing the clothes of a
// measurement. DeviceProbe.MeasurementWarning exists to say so out loud, which
// is the only reason any of this was visible at all.
//
// SET IT BEFORE ANYTHING ASKS. The hook is static and read on every Snapshot,
// so a head installs it as the first thing it does - after which every part of
// the app gets the truth without knowing this file exists.

using Android.App;
using Android.Content;
using Android.OS;
using CircleAI.Core;

namespace CircleAI.Device;

/// <summary>Reads this device's real memory and storage, and tells Core.</summary>
public static class DeviceMemoryProbe
{
    /// <summary>
    /// Install the platform hook. Call once, as early as a head can.
    /// </summary>
    /// <remarks>
    /// Safe to call twice. Anything it cannot read is left null rather than
    /// invented, and Core then says the figure was inferred instead of
    /// reporting a guess as a measurement.
    /// </remarks>
    public static void Install() => DeviceProbe.PlatformMemoryProbe = Read;

    private static DeviceProbe.PlatformMemory Read()
    {
        long? total = null, available = null, free = null;

        try
        {
            // ActivityManager is the only thing that knows the hardware total.
            // GC has no idea - it reports the ceiling the runtime was given.
            if (Application.Context.GetSystemService(Context.ActivityService) is ActivityManager manager)
            {
                var info = new ActivityManager.MemoryInfo();
                manager.GetMemoryInfo(info);

                if (info.TotalMem > 0) total = info.TotalMem;
                if (info.AvailMem > 0) available = info.AvailMem;
            }
        }
        catch (System.Exception)
        {
            // A device that will not answer is not a reason to fail a launch.
        }

        try
        {
            // The app's OWN partition. DriveInfo is refused inside the sandbox,
            // which is the other half of why a phone read as a wearable.
            var files = Application.Context.FilesDir?.AbsolutePath;
            if (!string.IsNullOrEmpty(files))
            {
                var bytes = new StatFs(files).AvailableBytes;
                if (bytes > 0) free = bytes;
            }
        }
        catch (System.Exception)
        {
            // Report what was readable rather than nothing.
        }

        return new DeviceProbe.PlatformMemory(available, free, total);
    }
}
