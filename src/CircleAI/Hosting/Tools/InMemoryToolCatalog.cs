// InMemoryToolCatalog.cs
//
// (2.0.3) Default in-memory IToolCatalog. Keyword-substring search over
// name + description + tags. Thread-safe via ConcurrentDictionary.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Hosting.Tools;

/// <summary>
/// (2.0.3) Default <see cref="IToolCatalog"/> — in-memory + keyword-substring
/// search. Sufficient for catalogs up to a few thousand tools; for larger
/// surfaces ship the semantic backend planned in 2.5.0.
/// </summary>
public sealed class InMemoryToolCatalog : IToolCatalog
{
    private readonly ConcurrentDictionary<string, ToolDescriptor> _byName
        = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public int Count => _byName.Count;

    /// <inheritdoc/>
    public ValueTask UpsertAsync(ToolDescriptor descriptor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Name);
        _byName[descriptor.Name] = descriptor;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<bool> RemoveAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ValueTask.FromResult(_byName.TryRemove(name, out _));
    }

    /// <inheritdoc/>
    public ValueTask<ToolDescriptor?> GetAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return ValueTask.FromResult<ToolDescriptor?>(null);
        _byName.TryGetValue(name, out var t);
        return ValueTask.FromResult(t);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ToolDescriptor> List()
        => _byName.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <inheritdoc/>
    public IReadOnlyList<ToolDescriptor> Search(string query, int topK = 10)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
            return Array.Empty<ToolDescriptor>();
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var scored = _byName.Values
            .Select(d => new { Tool = d, Score = ScoreMatch(d, terms) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Tool.Name, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .Select(x => x.Tool)
            .ToArray();
        return scored;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ToolDescriptor> ListByProvider(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return _byName.Values
            .Where(d => string.Equals(d.Provider, provider, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int ScoreMatch(ToolDescriptor d, string[] terms)
    {
        var name = d.Name        ?? "";
        var desc = d.Description ?? "";
        var tagBlob = d.Tags is null ? "" : string.Join(' ', d.Tags);

        int score = 0;
        foreach (var t in terms)
        {
            if (name.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 5;
            if (desc.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 2;
            if (tagBlob.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 3;
        }
        return score;
    }
}

/// <summary>
/// Convenience extension that drains an <see cref="IToolProvider"/> into
/// an <see cref="IToolCatalog"/>. Most hosts call this once at startup.
/// </summary>
public static class ToolCatalogExtensions
{
    /// <summary>
    /// Discover and import every tool from <paramref name="provider"/> into
    /// <paramref name="catalog"/>. Returns how many were imported.
    /// </summary>
    public static async Task<int> ImportFromAsync(
        this IToolCatalog catalog,
        IToolProvider     provider,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(provider);
        var tools = await provider.DiscoverAsync(ct).ConfigureAwait(false);
        var count = 0;
        foreach (var tool in tools)
        {
            ct.ThrowIfCancellationRequested();
            await catalog.UpsertAsync(tool, ct).ConfigureAwait(false);
            count++;
        }
        return count;
    }
}
