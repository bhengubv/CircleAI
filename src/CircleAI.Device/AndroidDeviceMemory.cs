// AndroidDeviceMemory.cs
//
// The one line every Android head has to run, in a place they can actually run it.
//
// CircleAI.Core cannot read Android's memory by itself — it is platform-neutral,
// so it falls back to the GC heap limit, which inside an app sandbox is about
// 100 MB. A phone then classifies as a wearable and every model comes back
// NothingFits. The fix has always been for the head to set
// DeviceProbe.PlatformMemoryProbe, and the fix has always been a paragraph of
// ActivityManager code each head was expected to copy.
//
// Across a 13-app estate, zero heads copied it. That is not thirteen oversights;
// it is a design that asked every app to remember something and gave no signal
// when it forgot. This is the same block, written once, as one call — and
// CircleNeuronService calls it for anything hosted in the device service, so an
// app that uses the service does not have to know this exists at all.

using Android.App;
using Android.Content;
using Android.OS;
using CircleAI.Core;

namespace CircleAI.Device;

/// <summary>Teaches <see cref="DeviceProbe"/> how to read this phone.</summary>
public static class AndroidDeviceMemory
{
    /// <summary>
    /// Installs the platform memory probe. Safe to call more than once and from
    /// any thread; the last caller wins and they all read the same hardware.
    /// </summary>
    /// <param name="context">Any context — application context is used internally.</param>
    /// <remarks>
    /// TWO DISTINCT NUMBERS, and conflating them is how a phone gets OOM-killed:
    ///
    ///   AvailMem (FREE RAM)   gates model FIT. A model needs its weight in free
    ///                         RAM to load. Selecting against total RAM once picked
    ///                         a 4B model on a 3.6 GB phone with ~1.5 GB free and
    ///                         the process was killed on load.
    ///   TotalMem (DEVICE RAM) gates TIER. A 3.6 GB phone is a Phone even while it
    ///                         is momentarily busy.
    ///
    /// Call it before anything asks the selector a question — in
    /// <c>Application.OnCreate</c> or the launcher activity's <c>OnCreate</c>.
    /// </remarks>
    public static void Install(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var app = context.ApplicationContext ?? context;

        DeviceProbe.PlatformMemoryProbe = () =>
        {
            long? avail = null, total = null, storage = null;

            try
            {
                if (app.GetSystemService(Context.ActivityService) is ActivityManager am)
                {
                    var mi = new ActivityManager.MemoryInfo();
                    am.GetMemoryInfo(mi);
                    avail = mi.AvailMem;
                    total = mi.TotalMem;
                }
            }
            catch
            {
                // Deny or an OEM quirk: fall through and let Core's heuristic
                // answer. It will be wrong, and DeviceProbe.MeasurementWarning
                // will say so rather than pretending otherwise.
            }

            try
            {
                var dir = app.FilesDir?.AbsolutePath;
                if (!string.IsNullOrEmpty(dir))
                    storage = new StatFs(dir).AvailableBytes;
            }
            catch { /* same — an unreadable figure is reported, not invented */ }

            return new DeviceProbe.PlatformMemory(avail, storage, total);
        };
    }

    /// <summary>
    /// A one-line health check a host can log or show: what the probe reports and
    /// whether anything is wrong with it.
    /// </summary>
    /// <remarks>
    /// Exists because the failure it describes is silent. A head that never called
    /// <see cref="Install"/> looks identical to one that did, right up until every
    /// model is refused for a reason that names the model.
    /// </remarks>
    public static string Describe()
    {
        var p = DeviceProbe.Snapshot();
        var warning = p.MeasurementWarning;
        return warning is null
            ? $"device: {p.RamAvailableBytes / 1_000_000.0:0} MB free of " +
              $"{p.RamTotalBytes / 1_000_000.0:0} MB, tier {p.Classify()} ({p.RamSource})"
            : $"device: UNMEASURED — {warning}";
    }
}
