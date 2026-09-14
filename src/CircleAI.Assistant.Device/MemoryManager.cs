// MemoryManager.cs
//
// The Memory Manager on the phone: the device figures, the footprint, and the
// budget, tied together where the setup path can ask it a question.
//
// It is a thin facade over three things that already exist:
//   DeviceResourcesReader  - the real disk/RAM (step zero, not the GC heap)
//   DeviceFootprint        - what Circle AI is using, and what is free to give back
//   MemoryBudget           - the pure prevention-first policy (CircleAI.Assistant)
//
// PHASE ONE IS FOOTPRINT, NOT SWAP. This budgets and reclaims disk, which is safe
// and works on any device. The disk-backed paging file (mmap on stock Android, a
// real swap only on Circle OS) is the next phase; MemoryBudget.SwapCapBytes is
// the ceiling it will honour, and it is deliberately not created here yet.

using System.IO;
using Android.Util;
using CircleAI.Assistant;

namespace CircleAI.Assistant.Device;

/// <summary>Circle AI's storage manager on this device.</summary>
public sealed class MemoryManager
{
    private const string Tag = "CircleAI.Memory";

    private readonly IDeviceResources _resources;

    public MemoryManager(IDeviceResources resources) => _resources = resources;

    /// <summary>The device's real storage and memory right now.</summary>
    public DeviceResources Device => _resources.Read();

    /// <summary>What Circle AI is using on disk right now.</summary>
    public Footprint Footprint => DeviceFootprint.Measure();

    /// <summary>
    /// Whether a download of <paramref name="bytes"/> may proceed - asked BEFORE
    /// it starts, so a phone is never filled and then apologised to.
    /// </summary>
    /// <remarks>
    /// The reclaimable figure comes from the footprint, so "reclaim first" is a
    /// real option the caller can act on rather than a hope.
    /// </remarks>
    public BudgetDecision CanDownload(long bytes)
        => MemoryBudget.ForDownload(_resources.Read(), bytes, DeviceFootprint.Measure().ReclaimableBytes);

    /// <summary>
    /// Free what costs the person nothing - the regenerable cache - and report
    /// how much came back.
    /// </summary>
    /// <remarks>
    /// ONLY THE CACHE, AND ONLY THE CACHE, on purpose. The skill library and the
    /// espeak data are also free to rebuild, but re-unpacking them is a visible
    /// pause on the next launch; the cache is generated audio and scratch that
    /// nobody waits on. Downloaded models and the person's memory are never
    /// touched here - that needs a say-so, because it costs data or cannot be got
    /// back at all.
    /// </remarks>
    public long ReclaimCaches()
    {
        long freed = 0;
        try
        {
            var cache = AppPaths.Cache;
            if (string.IsNullOrEmpty(cache) || !Directory.Exists(cache)) return 0;

            foreach (var file in Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    freed += size;
                }
                catch { /* a file in use is skipped, not fatal */ }
            }

            if (freed > 0) Log.Info(Tag, $"reclaimed {MemoryBudget.Human(freed)} of cache");
        }
        catch (System.Exception ex)
        {
            Log.Warn(Tag, "could not reclaim cache: " + ex.Message);
        }
        return freed;
    }

    /// <summary>
    /// Print the real numbers on launch - the same discipline as the models
    /// census, so a claim about disk is checkable against logcat rather than
    /// believed.
    /// </summary>
    public void LogCensus()
    {
        try
        {
            var d = Device;
            if (!d.Known)
            {
                Log.Info(Tag, "device: could not be read - no budget will be enforced");
                return;
            }

            string H(long b) => MemoryBudget.Human(b);

            Log.Info(Tag,
                $"device: disk {H(d.FreeDiskBytes)} free of {H(d.TotalDiskBytes)} "
                + $"(floor {H(MemoryBudget.FreeFloorBytes(d))}); "
                + $"ram {H(d.AvailableRamBytes)} available of {H(d.TotalRamBytes)}");

            var f = Footprint;
            Log.Info(Tag,
                $"footprint: {H(f.TotalBytes)} total - models {H(f.ModelsBytes)}, "
                + $"memory {H(f.MemoryBytes)}, skills {H(f.SkillsBytes)}, "
                + $"voice {H(f.VoiceDataBytes)}, cache {H(f.CacheBytes)}; "
                + $"reclaimable {H(f.ReclaimableBytes)}");
        }
        catch (System.Exception ex)
        {
            Log.Warn(Tag, "census failed: " + ex.Message);
        }
    }
}
