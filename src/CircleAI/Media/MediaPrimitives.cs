// MediaPrimitives.cs
//
// (3.3.0) Real domain types + in-memory library for the Media
// vertical (audio + video + image asset catalog).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Media;

public enum MediaKind { Audio, Video, Image }

public sealed record MediaAsset(string AssetId, string Title, MediaKind Kind, TimeSpan? Duration, long Bytes, string Mime, DateTimeOffset CreatedAtUtc);

public interface IMediaLibrary
{
    void Add(MediaAsset a);
    MediaAsset? Get(string id);
    bool Remove(string id);
    int Count { get; }
    long TotalBytes { get; }
    IReadOnlyList<MediaAsset> ListByKind(MediaKind kind);
    IReadOnlyList<MediaAsset> ByMime(string mimePrefix);
    IReadOnlyList<MediaAsset> Search(string q, int topK = 20);
}

public sealed class InMemoryMediaLibrary : IMediaLibrary
{
    private readonly ConcurrentDictionary<string, MediaAsset> _items = new(StringComparer.Ordinal);

    public void Add(MediaAsset a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (string.IsNullOrWhiteSpace(a.AssetId)) throw new ArgumentException("AssetId required");
        _items[a.AssetId] = a;
    }

    public MediaAsset? Get(string id) => _items.GetValueOrDefault(id);

    /// <summary>Remove an asset by id. Returns true if it was present.</summary>
    public bool Remove(string id)
        => !string.IsNullOrEmpty(id) && _items.TryRemove(id, out _);

    /// <summary>Number of assets currently catalogued.</summary>
    public int Count => _items.Count;

    /// <summary>Total on-disk footprint of every catalogued asset, in bytes.</summary>
    public long TotalBytes => _items.Values.Sum(a => a.Bytes);

    public IReadOnlyList<MediaAsset> ListByKind(MediaKind kind)
        => _items.Values.Where(a => a.Kind == kind).OrderByDescending(a => a.CreatedAtUtc).ToArray();

    /// <summary>
    /// Assets whose MIME type starts with a given prefix (e.g. "image/", "audio/"),
    /// matched case-insensitively and returned newest-first. Empty prefix yields nothing.
    /// </summary>
    public IReadOnlyList<MediaAsset> ByMime(string mimePrefix)
    {
        if (string.IsNullOrEmpty(mimePrefix)) return Array.Empty<MediaAsset>();
        return _items.Values
            .Where(a => a.Mime.StartsWith(mimePrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.CreatedAtUtc)
            .ToArray();
    }

    public IReadOnlyList<MediaAsset> Search(string q, int topK = 20)
    {
        if (q is null) throw new ArgumentNullException(nameof(q));
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        return _items.Values
            .Where(a => a.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(topK)
            .ToArray();
    }
}
