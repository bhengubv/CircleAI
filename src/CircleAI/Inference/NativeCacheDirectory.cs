// NativeCacheDirectory.cs
//
// MNN writes its caches to RELATIVE paths, and this process had nowhere to put them.
//
// TWO CACHES, ONE CAUSE. Measured on the device on 2026-10-03:
//
//     E/MNNJNI: Can't open file:./mnn_cachefile.bin
//     E/MNNJNI: Load Cache file error.
//
// That is the OpenCL KERNEL cache - the compiled GPU kernels, which otherwise have
// to be rebuilt from source on every single load. And from 2026-09-22, the same
// shape of failure one layer along:
//
//     E/MNNJNI: Failed to create prefix cache file dir: prefixcache
//     E/MNNJNI: Failed to memory-map the kvcache!
//     libc:     FORTIFY: pthread_mutex_lock called on a destroyed mutex
//
// Both are MNN building a path like "./something" or "prefixcache/something" and
// resolving it against the process working directory. On Android that directory is
// "/", which no app may write to, so every such open fails. The prefix-cache one
// failed badly enough to leave a destroyed mutex behind and SIGSEGV the threadpool,
// which is why kvcache_mmap has been off ever since.
//
// A LIBRARY CHANGING THE PROCESS WORKING DIRECTORY IS RUDE, and it is done here
// anyway, because it is the only lever: the paths are built inside a prebuilt .so
// that takes no configuration for them. Done ONCE, as early as the first model
// load, to a directory this app owns.
//
// IT IS SAFE HERE IN A WAY IT WOULD NOT BE ELSEWHERE. .NET resolves every relative
// path against the same directory, so this moves anything that uses one - and
// nothing in this product does: every path that reaches the filesystem is built
// from Context.FilesDir, a model directory, or Path.GetTempPath(). An app that
// does rely on the working directory should not call this.

namespace CircleAI.Inference;

/// <summary>Gives MNN a working directory it can actually write its caches into.</summary>
public static class NativeCacheDirectory
{
    private static readonly object Gate = new();

    /// <summary>Where it was pointed, or null if it has not been pointed anywhere.</summary>
    public static string? Current { get; private set; }

    /// <summary>
    /// Point the process at a writable directory beside <paramref name="nearPath"/>.
    /// </summary>
    /// <param name="nearPath">
    /// A file or directory this app owns - the model being loaded is the obvious
    /// one, because it was downloaded into a writable place by definition.
    /// </param>
    /// <returns>The directory now in use, or null if none could be made.</returns>
    /// <remarks>
    /// ONCE PER PROCESS. The second call is a no-op and returns the first answer:
    /// two models loading at once must not move the directory out from under each
    /// other, and MNN resolves these paths lazily, during load AND during run.
    ///
    /// BEST-EFFORT, LIKE EVERY OTHER NATIVE ACCOMMODATION HERE. A directory that
    /// cannot be created or entered leaves the working directory alone and the
    /// caches keep failing exactly as they did before - which is a slow model, not
    /// a broken one.
    /// </remarks>
    public static string? Use(string? nearPath)
    {
        if (string.IsNullOrWhiteSpace(nearPath)) return Current;

        lock (Gate)
        {
            if (Current is not null) return Current;

            try
            {
                var root = Directory.Exists(nearPath)
                    ? nearPath!
                    : Path.GetDirectoryName(nearPath) ?? ".";

                // NAMED FOR WHOSE CACHES THESE ARE. Somebody finding a folder called
                // "prefixcache" full of binary next to their models should be able to
                // tell at a glance that it is the engine's and not theirs.
                var dir = Path.Combine(root, "native-cache");
                Directory.CreateDirectory(dir);

                Directory.SetCurrentDirectory(dir);
                Current = dir;
                Console.WriteLine($"CIRCLEAI-CWD native caches at {dir}");
                return Current;
            }
            catch (Exception ex)
            {
                // A read-only store, or a platform that refuses. The model still
                // loads; it just rebuilds its kernels every time.
                Console.WriteLine($"CIRCLEAI-CWD could not be set: {ex.Message}");
                return null;
            }
        }
    }
}
