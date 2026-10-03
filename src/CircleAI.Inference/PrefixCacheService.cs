// PrefixCacheService.cs
//
// RT-06: cross-session prefix cache. The motivation:
//
// Every fresh conversation today re-pays the system-prompt prefill cost — on a
// Tier-0 device that's 2-3 seconds before the first token appears. The user
// perceives "slow." But the system prompt is almost always identical across
// chats with the same persona / app. So: snapshot the model's KV state once
// per (modelId, systemPrompt) pair, reload it on the next chat with the same
// pair, and skip the prefill entirely.
//
// The implementation uses MNN's existing mnn_llm_save_session / load_session
// primitives — no native bridge changes needed. The on-disk format is whatever
// MNN itself writes; we only own the indexing.
//
// Cache layout:
//   %LOCALAPPDATA%/CircleAI/prefix-cache/
//     <modelHash>_<systemHash>.session   ← MNN-native KV snapshot
//     <modelHash>_<systemHash>.meta      ← JSON metadata (createdAtUtc, modelId)
//
// Eviction policy v1: simple LRU by file mtime, cap at 500 MB total, evict the
// oldest first. Tier-0 phones get hit hard; the cap matters.

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Inference;

/// <summary>
/// Manages an on-disk cache of "warm" model sessions keyed by the hash of
/// (modelId, systemPrompt). Generators that opt in via
/// <see cref="GenerationOptions.UsePrefixCache"/> consult this service before
/// resetting the model handle for a new conversation.
/// <para>
/// The service is thread-safe and shared across generators; default instance
/// is <see cref="Default"/>. Override the root directory via the constructor
/// for tests.
/// </para>
/// </summary>
public sealed class PrefixCacheService
{
    private const int CapBytes = 500 * 1024 * 1024; // 500 MB
    private static readonly SemaphoreSlim _ioLock = new(1, 1);

    /// <summary>
    /// Where this service WOULD have put entries, and no longer does.
    /// </summary>
    /// <remarks>
    /// MNN DECIDES, AND IT DECIDES &lt;cwd&gt;/prefixcache - it prepends that to
    /// whatever name it is handed, so nothing has ever been written here. Kept
    /// because the constructor's fallback still proves the process can create a
    /// directory at all, and because a caller passing a root should not start
    /// throwing. Read EntriesDirectory for the real location.
    /// </remarks>
    private readonly string _root;

    /// <summary>
    /// The default per-app instance rooted at
    /// <c>%LOCALAPPDATA%/CircleAI/prefix-cache</c> on Windows and
    /// <c>~/.circleai/prefix-cache</c> on Unix / iOS / Android (the platform's
    /// home directory equivalent).
    /// </summary>
    public static PrefixCacheService Default { get; } = new(DefaultRoot());

    /// <summary>
    /// Construct a cache service rooted at <paramref name="root"/>. The directory
    /// is created on demand.
    /// </summary>
    public PrefixCacheService(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("root is required.", nameof(root));
        // A cache is an optimisation, never a reason to fail. If the chosen root
        // cannot be created — a sandbox that denies it, or a platform whose home
        // dir resolves oddly — fall back to a temp subdir so the service still
        // constructs and the model still loads (cold prefill, not a crash). The
        // Default instance is a static, so an unhandled throw here would surface
        // as a TypeInitializationException on the first model load.
        try
        {
            Directory.CreateDirectory(root);
        }
        catch
        {
            try
            {
                root = Path.Combine(Path.GetTempPath(), "CircleAI", "prefix-cache");
                Directory.CreateDirectory(root);
            }
            catch { /* even temp is denied — Has/Save degrade to no-ops below */ }
        }
        _root = root;
    }

    /// <summary>
    /// Compute the cache key for a (modelId, systemPrompt) pair. Returns
    /// <c>null</c> when <paramref name="systemPrompt"/> is null/empty — there
    /// is nothing to cache without a system prompt to key against.
    /// </summary>
    public static string? KeyFor(string modelId, string? systemPrompt)
    {
        if (string.IsNullOrWhiteSpace(modelId))    return null;
        if (string.IsNullOrEmpty(systemPrompt))    return null;

        var modelHash  = Sha256(modelId);
        var systemHash = Sha256(systemPrompt!);
        // First 16 hex chars per component — collision-free at the scale of
        // any single device's cache (≪ 10⁶ entries), much shorter on disk.
        return $"{modelHash[..16]}_{systemHash[..16]}";
    }

    /// <summary>
    /// The name to hand <c>setPrefixCacheFile</c> for <paramref name="key"/>.
    /// RELATIVE, on purpose - see the remarks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IT WAS ABSOLUTE AND THAT IS WHAT CRASHED THE PHONE. MNN does not use the
    /// path it is given; it PREPENDS a relative <c>prefixcache/</c> to it. Handed
    /// an absolute path, what it then tries to create is
    /// </para>
    /// <code>
    /// prefixcache//data/user/0/com.bhengubv.circleai.service/files/.circleai/
    ///   prefix-cache/a4638952cea37ebb_5da837afa18c1f67.session_0.k
    /// </code>
    /// <para>
    /// - the whole absolute tree recreated underneath a relative folder. It cannot,
    /// fails once per layer, and leaves a destroyed mutex that SIGSEGVs the next
    /// <c>pthread_mutex_lock</c> inside <c>MNN::ThreadPool::enqueue</c>. Measured on
    /// a P30 on 2026-09-22 and again on 2026-10-03, which is why <c>kvcache_mmap</c>
    /// has been off between those dates.
    /// </para>
    /// <para>
    /// A BARE FILENAME MAKES IT ONE LEVEL - <c>prefixcache/&lt;key&gt;.session</c> -
    /// which MNN creates happily, under whatever working directory the process is
    /// in. <see cref="NativeCacheDirectory"/> puts it beside the model.
    /// </para>
    /// <para>
    /// WHAT LANDS ON DISK IS NOT THIS NAME. MNN appends its own suffixes, one pair
    /// per layer: <c>&lt;key&gt;.session_0.k</c>, <c>_0.v</c>, <c>_1.k</c> and so
    /// on. Nothing is ever written at this exact path, which is why everything
    /// below looks for the prefix rather than the file.
    /// </para>
    /// </remarks>
    public string PathFor(string key) => $"{key}.session";

    /// <summary>
    /// Where MNN actually writes, which is not <c>_root</c> and never was.
    /// </summary>
    /// <remarks>
    /// READ AT CALL TIME, NOT CACHED. The working directory is set during the first
    /// model load, and this type is a static singleton that may well be touched
    /// before that - a captured value would be the one from before the move.
    /// </remarks>
    private static string EntriesDirectory
        => Path.Combine(Directory.GetCurrentDirectory(), "prefixcache");

    /// <summary>Every file MNN wrote for this key, across all layers.</summary>
    private static FileInfo[] EntryFiles(string key)
    {
        try
        {
            var dir = new DirectoryInfo(EntriesDirectory);
            return dir.Exists ? dir.GetFiles($"{key}.session*") : [];
        }
        catch { return []; }
    }

    /// <summary>
    /// <c>true</c> when a cached entry exists for <paramref name="key"/>.
    /// </summary>
    /// <remarks>
    /// ANY LAYER COUNTS. A half-written set is still something MNN can be pointed
    /// at, and it is the one who decides what to do with an incomplete one - we are
    /// not in a position to judge it and guessing wrong costs a cold prefill either
    /// way.
    /// </remarks>
    public Task<bool> HasEntryAsync(string key, CancellationToken ct = default)
        => Task.FromResult(EntryFiles(key).Length > 0);

    /// <summary>Same question, for callers already on a synchronous path.</summary>
    /// <remarks>
    /// IT IS A DIRECTORY LISTING, NOT IO WORTH AWAITING, and the one caller that
    /// needs it sits in the middle of building a generation request. The async
    /// twin stays for the public surface.
    /// </remarks>
    public bool HasEntry(string key) => EntryFiles(key).Length > 0;

    /// <summary>
    /// Touch the entry's mtime so LRU eviction treats it as recently used.
    /// Called after a successful load.
    /// </summary>
    public void Touch(string key)
    {
        foreach (var f in EntryFiles(key))
            try { f.LastWriteTimeUtc = DateTime.UtcNow; }
            catch { /* best effort; a cache is never a reason to fail */ }
    }

    /// <summary>
    /// Evict oldest entries until the directory is under
    /// <see cref="CapBytes"/>. Called after every successful Save to keep the
    /// cache bounded. Best-effort — failures are swallowed.
    /// </summary>
    public async Task EvictIfNeededAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // WHERE MNN WRITES, NOT WHERE WE ASKED IT TO. _root never held an
            // entry: MNN builds its own prefixcache/ under the working directory.
            var dir = new DirectoryInfo(EntriesDirectory);
            if (!dir.Exists) return;

            // "*.session*", because what lands on disk is <key>.session_0.k and its
            // siblings - the exact name PathFor returns is never a file. The old
            // glob was "*.session" and would have matched nothing for ever.
            var files = dir.EnumerateFiles("*.session*")
                           .OrderBy(f => f.LastWriteTimeUtc)
                           .ToList();

            long total = files.Sum(f => f.Length);
            int i = 0;
            while (total > CapBytes && i < files.Count)
            {
                var f = files[i++];
                try { total -= f.Length; f.Delete(); }
                catch { /* best effort */ }
            }
        }
        finally { _ioLock.Release(); }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static string Sha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var sb    = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string DefaultRoot()
    {
        // Windows: %LOCALAPPDATA%/CircleAI/prefix-cache
        // Unix-like (Linux/macOS/Android/iOS): ~/.circleai/prefix-cache
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(local))
            return Path.Combine(local!, "CircleAI", "prefix-cache");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".circleai", "prefix-cache");
    }
}
