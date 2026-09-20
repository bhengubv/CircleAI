// MemoryManagerSafeFix.cs
//
// The v1 safe fix: free the regenerable caches. It is the one remedy that is safe,
// reversible, and useful POST-HOC — it needs nothing from the failed call, and on a
// 3 GB phone a memory- or cache-pressure failure is exactly what clearing them cures.
// The cache rebuilds on demand, so running it can never lose anything the person
// cannot get back. Registered once per category it handles ("cache" trims, "memory"
// reclaims all). Anything touching code, money, or security is never a safe fix.

using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting.SelfHealing;

namespace CircleAI.Assistant.Device;

/// <summary>A safe fix that frees Circle AI's regenerable caches.</summary>
public sealed class MemoryManagerSafeFix : ISafeFix
{
    private readonly MemoryManager _memory;
    private readonly bool _reclaimAll;

    /// <param name="memory">The memory manager that owns the caches.</param>
    /// <param name="category">The failure category this instance handles (e.g. "cache", "memory").</param>
    /// <param name="reclaimAll">True to free everything (for memory pressure); false to trim by age/cap.</param>
    public MemoryManagerSafeFix(MemoryManager memory, string category, bool reclaimAll)
    {
        _memory = memory ?? throw new System.ArgumentNullException(nameof(memory));
        Category = category ?? throw new System.ArgumentNullException(nameof(category));
        _reclaimAll = reclaimAll;
    }

    /// <inheritdoc />
    public string Category { get; }

    /// <inheritdoc />
    public Task<bool> TryFixAsync(FailureContext failure, CancellationToken ct = default)
    {
        try
        {
            if (_reclaimAll) _memory.ReclaimCaches();
            else _memory.TrimCache();
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);   // could not free — the loop escalates.
        }
    }
}
