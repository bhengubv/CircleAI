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
    IReadOnlyList<MediaAsset> ListByKind(MediaKind kind);
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

    public IReadOnlyList<MediaAsset> ListByKind(MediaKind kind)
        => _items.Values.Where(a => a.Kind == kind).OrderByDescending(a => a.CreatedAtUtc).ToArray();

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
