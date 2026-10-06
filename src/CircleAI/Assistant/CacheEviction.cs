// CacheEviction.cs
//
// Keeping the regenerable cache in bounds without a soul having to ask - the
// Telegram move, made honest for a phone that keeps NO copy of anything.
//
// TWO POLICIES, BOTH PURE. "Keep" ages a file out once it has sat unused past a
// window; "max" caps the whole cache and evicts oldest-first when it is over.
// Either can be switched off - keep = TimeSpan.MaxValue is "forever", maxBytes
// <= 0 is "no limit" - which is exactly Telegram's two off-switches.
//
// THIS ONLY EVER SEES THE CACHE. The caller (MemoryManager.TrimCache) hands it
// files enumerated from AppPaths.Cache and nothing else, so the person's memory
// store and the downloaded models are out of reach BY CONSTRUCTION, not by a
// filter that could regress. Privacy-first means there is no copy of memory
// anywhere - so memory is not a cache, and is never managed here.
//
// Pure arithmetic over (bytes, time): no I/O, no clock of its own - nowUtc is
// passed in - so every rule below is a unit test, not a phone run. The device
// side reads the files and does the deleting; this decides which ones.

namespace CircleAI.Assistant;

/// <summary>One file in the cache, as the evictor needs to see it.</summary>
/// <param name="Path">Where it is, so the caller can delete exactly this one.</param>
/// <param name="Bytes">How much freeing it gives back.</param>
/// <param name="LastUsedUtc">
/// When it was last used. On Android this is the file's last-WRITE time: access
/// time is not tracked on a <c>noatime</c>/<c>relatime</c> mount, and the cache
/// holds write-once generated audio that is spoken as it is made and not replayed
/// - so "written N ago" and "unused N ago" are the same file. A cache that ever
/// holds RE-read assets would need a real touch-on-serve to tell them apart.
/// </param>
public readonly record struct CacheFile(string Path, long Bytes, DateTime LastUsedUtc);

/// <summary>What the evictor decided, and why - the caller acts on this.</summary>
/// <param name="Delete">The files to remove, oldest first. Empty means leave it be.</param>
/// <param name="FreedBytes">What removing them all would give back.</param>
/// <param name="Reason">A short note for the log - "aged out", "over the cap", both, or empty.</param>
public readonly record struct CachePlan(
    IReadOnlyList<string> Delete, long FreedBytes, string Reason);

/// <summary>Deciding which cache files to drop, before anything is deleted.</summary>
public static class CacheEviction
{
    /// <summary>
    /// How long an untouched cache file is kept before it ages out.
    /// </summary>
    /// <remarks>
    /// Conservative on purpose: generated audio a person has not needed in a
    /// month is safe to regenerate for free. Telegram offers 3 days / 1 week /
    /// 1 month; this is the roomy end of that, fixed rather than a picker,
    /// because on our target the cache is kilobytes and the window is a guard,
    /// not a knob anyone would reach for.
    /// </remarks>
    public static readonly TimeSpan KeepDefault = TimeSpan.FromDays(30);

    /// <summary>
    /// The ceiling on the whole cache; oldest is evicted first once it is over.
    /// </summary>
    /// <remarks>
    /// A runaway-guard, not a routine pressure - the measured cache on a P30 was
    /// under a megabyte. Flat, like Telegram's 5/16/32 GB, and small, because
    /// this cache is scratch and generated audio, never a media library.
    /// </remarks>
    public const long MaxCacheDefaultBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Which of <paramref name="files"/> to drop, given the keep window and the
    /// cap, as of <paramref name="nowUtc"/>.
    /// </summary>
    /// <param name="files">The cache, as measured on disk. Order does not matter.</param>
    /// <param name="maxBytes">The cache ceiling; <c>&lt;= 0</c> means no limit.</param>
    /// <param name="keep">
    /// How long an untouched file is kept; <see cref="System.TimeSpan.MaxValue"/> means forever.
    /// </param>
    /// <param name="nowUtc">The clock, passed in so this stays pure and testable.</param>
    public static CachePlan Plan(
        IReadOnlyList<CacheFile> files, long maxBytes, TimeSpan keep, DateTime nowUtc)
    {
        if (files is null || files.Count == 0)
            return new CachePlan([], 0, string.Empty);

        var doomed = new HashSet<string>(StringComparer.Ordinal);

        // STEP ONE - AGE. A file untouched longer than the window goes, unless the
        // window is "forever". A future timestamp (clock skew) reads as age zero
        // and is kept, which is the safe way to be wrong.
        var byAge = false;
        if (keep < TimeSpan.MaxValue)
        {
            foreach (var f in files)
            {
                if (nowUtc - f.LastUsedUtc > keep && doomed.Add(f.Path))
                    byAge = true;
            }
        }

        // STEP TWO - CAP. What survived step one, still over the ceiling? Drop the
        // oldest first until it is under. maxBytes <= 0 is "no limit" and skips.
        var byCap = false;
        if (maxBytes > 0)
        {
            long surviving = 0;
            foreach (var f in files)
                if (!doomed.Contains(f.Path)) surviving += f.Bytes;

            if (surviving > maxBytes)
            {
                // Oldest first, then by path so equal timestamps are deterministic.
                var oldest = new List<CacheFile>(files.Count);
                foreach (var f in files)
                    if (!doomed.Contains(f.Path)) oldest.Add(f);
                oldest.Sort((a, b) =>
                {
                    var t = a.LastUsedUtc.CompareTo(b.LastUsedUtc);
                    return t != 0 ? t : string.CompareOrdinal(a.Path, b.Path);
                });

                foreach (var f in oldest)
                {
                    if (surviving <= maxBytes) break;
                    if (doomed.Add(f.Path))
                    {
                        surviving -= f.Bytes;
                        byCap = true;
                    }
                }
            }
        }

        if (doomed.Count == 0)
            return new CachePlan([], 0, string.Empty);

        // The delete list, oldest first, with the total it frees. Re-walked from
        // the input so the output order is stable regardless of which step marked
        // a file - a plan a test can pin.
        var delete = new List<CacheFile>(doomed.Count);
        long freed = 0;
        foreach (var f in files)
        {
            if (doomed.Contains(f.Path))
            {
                delete.Add(f);
                freed += f.Bytes;
            }
        }
        delete.Sort((a, b) =>
        {
            var t = a.LastUsedUtc.CompareTo(b.LastUsedUtc);
            return t != 0 ? t : string.CompareOrdinal(a.Path, b.Path);
        });

        var reason = (byAge, byCap) switch
        {
            (true, true) => "aged out and over the cap",
            (true, false) => "aged out",
            (false, true) => "over the cap",
            _ => string.Empty,
        };

        var paths = new List<string>(delete.Count);
        foreach (var f in delete) paths.Add(f.Path);
        return new CachePlan(paths, freed, reason);
    }
}
