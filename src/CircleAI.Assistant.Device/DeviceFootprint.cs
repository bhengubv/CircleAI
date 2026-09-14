// DeviceFootprint.cs
//
// What Circle AI is actually taking up on this phone, and what of it costs the
// person nothing to give back.
//
// THE POINT OF THE BREAKDOWN IS THE RECLAIMABLE / PRECIOUS LINE. Freeing space
// is only safe when it costs the person nothing: the models were DOWNLOADED and
// cost data to fetch again - on a data-poor user, maybe impossible - so they are
// precious and never reclaimed without a say-so. The skill library and the
// espeak data were UNPACKED from the APK and the cache is regenerable audio;
// all three come back for free, so they are what the budget may reclaim.

using System.IO;

namespace CircleAI.Assistant.Device;

/// <summary>Circle AI's on-disk footprint, by category, in bytes.</summary>
/// <param name="ModelsBytes">Downloaded models - precious, costs data to replace.</param>
/// <param name="MemoryBytes">The person's memory store - precious, irreplaceable.</param>
/// <param name="SkillsBytes">The skill library - unpacked from the APK, free to rebuild.</param>
/// <param name="VoiceDataBytes">espeak dictionaries - unpacked from the APK, free to rebuild.</param>
/// <param name="CacheBytes">Generated audio and scratch - regenerable at no cost.</param>
public readonly record struct Footprint(
    long ModelsBytes,
    long MemoryBytes,
    long SkillsBytes,
    long VoiceDataBytes,
    long CacheBytes)
{
    /// <summary>Everything Circle AI holds on disk.</summary>
    public long TotalBytes => ModelsBytes + MemoryBytes + SkillsBytes + VoiceDataBytes + CacheBytes;

    /// <summary>What can be freed at NO cost to the person - regenerable, no network.</summary>
    public long ReclaimableBytes => SkillsBytes + VoiceDataBytes + CacheBytes;

    /// <summary>What must not be touched without a say-so - downloaded or irreplaceable.</summary>
    public long PreciousBytes => ModelsBytes + MemoryBytes;
}

/// <summary>Measures where Circle AI's storage goes.</summary>
public static class DeviceFootprint
{
    /// <summary>Add up each category from the actual directories on disk.</summary>
    public static Footprint Measure()
    {
        var models = DirSize(ModelStore.Path);
        var memory = DirSize(Path.Combine(AppPaths.Data, "CircleAI", "memory"));
        var skills = DirSize(Path.Combine(AppPaths.Data, "skills"));
        var voice  = DirSize(Path.Combine(AppPaths.Data, "espeak"));
        var cache  = DirSize(AppPaths.Cache);

        return new Footprint(models, memory, skills, voice, cache);
    }

    /// <summary>Sum of a directory tree, or 0 - never throws on one bad file.</summary>
    private static long DirSize(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return 0;

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* a file that vanished or cannot be stat'd is not worth failing over */ }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }
}
