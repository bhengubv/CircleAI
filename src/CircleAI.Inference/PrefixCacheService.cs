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
        _root = root;
        Directory.CreateDirectory(_root);
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
    /// Returns the cache path for <paramref name="key"/>. The returned path
    /// may or may not exist; use <see cref="HasEntryAsync"/> to check.
    /// </summary>
    public string PathFor(string key) => Path.Combine(_root, $"{key}.session");

    /// <summary>
    /// <c>true</c> when a cached entry exists for <paramref name="key"/>.
    /// </summary>
    public Task<bool> HasEntryAsync(string key, CancellationToken ct = default)
        => Task.FromResult(File.Exists(PathFor(key)));

    /// <summary>
    /// Touch the entry's mtime so LRU eviction treats it as recently used.
    /// Called after a successful load.
    /// </summary>
    public void Touch(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path))
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
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
            var dir = new DirectoryInfo(_root);
            if (!dir.Exists) return;

            var files = dir.EnumerateFiles("*.session")
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
