// AppCache.cs
//
// This app's own cache, and nothing else's.
//
// WHAT IT REPLACED AND WHY. MemoryManager did this job here, and it was the wrong
// object for a thin app: it measures Circle AI's whole footprint - downloaded
// models, voices, the skill database - answers "can I download this", and trims.
// All of that belongs to the process that owns the models, so MemoryManager went
// to CircleAI.Assistant.Device with the rest of the service half.
//
// BUT THE CACHE STILL NEEDS TIDYING, because this app has one of its own: the web
// view's, the scratch a share leaves behind, decoded audio. It is regenerable, it
// grows across sessions, and on a phone that is already full nobody notices until
// something fails. So the one behaviour that was genuinely about THIS app's disk
// stays, sized by the person's own keep/cap, and the rest left with the models.
//
// THE POLICY IS PURE AND ALREADY TESTED. CacheEviction.Plan decides what goes; this
// only supplies the files and deletes what it is told to. Nothing here enumerates a
// model or the person's memory - it cannot, because it only ever looks in one
// directory.

using System;
using System.Collections.Generic;
using System.IO;
using CircleAI.Assistant;

namespace CircleAI.Assistant.Device;

/// <summary>Age out and cap this app's own cache directory.</summary>
public static class AppCache
{
    private const string Tag = "CircleAI.Cache";

    /// <summary>
    /// Delete what the person's keep/cap says may go, and report the bytes freed.
    /// </summary>
    /// <param name="maxBytes">The cap, or null for <see cref="CacheEviction.MaxCacheDefaultBytes"/>.</param>
    /// <param name="keep">How long scratch survives, or null for <see cref="CacheEviction.KeepDefault"/>.</param>
    /// <remarks>
    /// NEVER THROWS. This runs from onTrimMemory and from launch, and a cache tidy
    /// that can take the app down with it is worse than a full cache. A file some
    /// other thread still holds is skipped, not fatal.
    /// </remarks>
    public static long Trim(long? maxBytes = null, TimeSpan? keep = null)
    {
        long freed = 0;
        try
        {
            var files = Files();
            if (files.Count == 0) return 0;

            var plan = CacheEviction.Plan(
                files,
                maxBytes ?? CacheEviction.MaxCacheDefaultBytes,
                keep ?? CacheEviction.KeepDefault,
                DateTime.UtcNow);

            if (plan.Delete.Count == 0) return 0;

            foreach (var path in plan.Delete)
            {
                try
                {
                    var size = new FileInfo(path).Length;
                    File.Delete(path);
                    freed += size;
                }
                catch { /* a file in use is skipped, not fatal */ }
            }

            if (freed > 0)
                Android.Util.Log.Info(Tag,
                    $"trimmed {MemoryBudget.Human(freed)} of cache ({plan.Reason})");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn(Tag, "could not trim cache: " + ex.Message);
        }
        return freed;
    }

    /// <summary>Empty the cache directory outright, for a severe memory signal.</summary>
    /// <remarks>
    /// The whole cache, because "running critical" means the OS is about to start
    /// killing things and a policy that keeps thirty days of scratch is not the
    /// answer to that. Everything in here is regenerable by definition.
    /// </remarks>
    public static long Empty()
    {
        long freed = 0;
        try
        {
            foreach (var file in Files())
            {
                try
                {
                    File.Delete(file.Path);
                    freed += file.Bytes;
                }
                catch { /* in use: skip */ }
            }

            if (freed > 0)
                Android.Util.Log.Info(Tag, $"reclaimed {MemoryBudget.Human(freed)} of cache");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn(Tag, "could not reclaim cache: " + ex.Message);
        }
        return freed;
    }

    /// <summary>Every file under this app's cache directory, recursively.</summary>
    /// <remarks>
    /// LAST-USED IS THE LATER OF ACCESS AND WRITE. Android mounts with relatime, so
    /// an access time can lag a write; taking the later of the two is what keeps a
    /// file written this morning from looking a month old and being aged out from
    /// under something still using it.
    /// </remarks>
    private static IReadOnlyList<CacheFile> Files()
    {
        var dir = AppPaths.Cache;
        var files = new List<CacheFile>();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return files;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    var used = fi.LastAccessTimeUtc > fi.LastWriteTimeUtc
                        ? fi.LastAccessTimeUtc
                        : fi.LastWriteTimeUtc;
                    files.Add(new CacheFile(file, fi.Length, used));
                }
                catch { /* a file that vanished or cannot be stat'd is skipped */ }
            }
        }
        catch { /* the whole walk failing is not fatal either */ }

        return files;
    }
}
