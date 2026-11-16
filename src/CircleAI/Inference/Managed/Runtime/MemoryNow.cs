// MemoryNow.cs
//
// HOW MUCH RAM THE PHONE CAN GIVE RIGHT NOW: /proc/meminfo's MemAvailable - what Linux could hand a process without
// swapping, page cache it can drop included. The recipe reads it at the start of a turn to decide how many of a model's
// layers stay warm and how many stream from flash (LayerStream). Null where there is no /proc (the desktop legs read it
// from the runtime instead).

namespace CircleAI.Inference.Managed.Runtime;

/// <summary>What the phone says of its memory now; each null where it does not say.</summary>
/// <param name="Available">Bytes the system could give without swapping.</param>
/// <param name="SwapFree">Bytes of swap not in use.</param>
/// <param name="SwapTotal">Bytes of swap in all; 0 on a device with none.</param>
public readonly record struct PhoneMemory(long? Available, long? SwapFree, long? SwapTotal);

/// <summary>The RAM available now.</summary>
public static class MemoryNow
{
    /// <summary>
    /// What the phone says of its memory now - what is available, and how much of its swap is free - read from
    /// /proc/meminfo in one look; every part null where there is no /proc or it cannot be read.
    /// </summary>
    /// <remarks>
    /// SWAP AS WELL AS WHAT IS AVAILABLE. On the P30 on 2026-11-13 the first unit of the 27B's indexing took the phone's
    /// swap from 898 MB free to 7 MB of 2,585 while MemAvailable still read 485 MB - page cache it could drop - and
    /// Android was ending other apps. Work that waits for an idle phone asks this while it runs (IndexingWhen.ShortOfMemory).
    /// </remarks>
    public static PhoneMemory Phone()
    {
        if (ReadingForTest is { } given) return given();
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid()) return default;
        try { return Parse(File.ReadLines("/proc/meminfo")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }

    /// <summary>
    /// Gives back what this process no longer uses: every generation collected and the heap compacted, the large
    /// objects with it.
    /// </summary>
    /// <remarks>
    /// A HEAP'S DEAD OBJECTS ARE STILL THE PROCESS'S UNTIL A COLLECTION, and a brain's are hundreds of megabytes: the
    /// 27B's working memory alone is reserved for 32,768 words. On the P30 on 2026-11-13 the inference process swapped
    /// one brain for another three times in fifteen minutes and collected nothing in between; the phone went from
    /// 898 MB of free swap to 7. Called when a brain has been let go, and by the indexing between its units.
    /// </remarks>
    public static void GiveBack()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    // /proc/meminfo's lines, as "Name:   12345 kB".
    internal static PhoneMemory Parse(IEnumerable<string> lines)
    {
        long? available = null, swapFree = null, swapTotal = null;
        foreach (var line in lines)
        {
            if (Kilobytes(line, "MemAvailable:") is { } a) available = a;
            else if (Kilobytes(line, "SwapFree:") is { } f) swapFree = f;
            else if (Kilobytes(line, "SwapTotal:") is { } t) swapTotal = t;
        }
        return new PhoneMemory(available, swapFree, swapTotal);

        static long? Kilobytes(string line, string name)
        {
            if (!line.StartsWith(name, StringComparison.Ordinal)) return null;
            var digits = line.AsSpan(name.Length).Trim();
            var end = digits.IndexOf(' ');
            if (end > 0) digits = digits[..end];
            return long.TryParse(digits, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var kb) ? kb * 1024 : null;
        }
    }

    /// <summary>
    /// A reading given in place of the device's, for a test of what the engine does with it: a phone's memory on a
    /// machine that has no /proc. Null reads the device.
    /// </summary>
    internal static Func<PhoneMemory>? ReadingForTest { get; set; }

    /// <summary>Bytes the system could give without swapping; null when it does not say.</summary>
    public static long? AvailableBytes()
    {
        if (ReadingForTest is { } given) return given().Available;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
        {
            try
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
                    var digits = line.AsSpan("MemAvailable:".Length).Trim();
                    var end = digits.IndexOf(' ');
                    if (end > 0) digits = digits[..end];
                    return long.TryParse(digits, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var kb)
                        ? kb * 1024
                        : null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            return null;
        }
        var info = GC.GetGCMemoryInfo();
        return info.TotalAvailableMemoryBytes > 0 ? info.TotalAvailableMemoryBytes - info.MemoryLoadBytes : null;
    }
}
