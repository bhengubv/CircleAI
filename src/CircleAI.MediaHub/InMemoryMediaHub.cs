// InMemoryMediaHub.cs
//
// (3.3.0) Real in-memory IMediaLibrary + ISyncedPlayback. Title-substring
// search; subscribe/broadcast pub-sub for synced playback positions.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.MediaHub;

/// <summary>(3.3.0) Title-substring searchable media library backed by a dictionary.</summary>
public sealed class InMemoryMediaLibrary : IMediaLibrary
{
    private readonly ConcurrentDictionary<string, MediaItem> _items = new(StringComparer.Ordinal);

    public string BackendId => "in-memory";

    /// <summary>Seed the library with items.</summary>
    public void Add(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items[item.ItemId] = item;
    }

    public ValueTask<MediaItem?> GetAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id required", nameof(id));
        _items.TryGetValue(id, out var item);
        return ValueTask.FromResult(item);
    }

    public ValueTask<IReadOnlyList<MediaItem>> SearchAsync(string query, int topK = 20, CancellationToken ct = default)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));

        var hits = _items.Values
            .Where(i => i.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<MediaItem>>(hits);
    }
}

/// <summary>(3.3.0) In-memory broadcast/subscribe playback sync.</summary>
public sealed class InMemorySyncedPlayback : ISyncedPlayback
{
    private sealed record SessionState(HashSet<string> Members, List<Func<PlaybackPosition, ValueTask>> Subscribers);

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    public string BackendId => "in-memory";

    public ValueTask JoinSessionAsync(string sessionId, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("sessionId required", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(userId))    throw new ArgumentException("userId required",    nameof(userId));

        var state = _sessions.GetOrAdd(sessionId, _ => new SessionState(new HashSet<string>(StringComparer.Ordinal), new List<Func<PlaybackPosition, ValueTask>>()));
        lock (state)
        {
            state.Members.Add(userId);
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask BroadcastPositionAsync(string sessionId, PlaybackPosition pos, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pos);
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("sessionId required", nameof(sessionId));
        if (!_sessions.TryGetValue(sessionId, out var state)) return;

        Func<PlaybackPosition, ValueTask>[] snapshot;
        lock (state) { snapshot = state.Subscribers.ToArray(); }
        foreach (var sub in snapshot)
        {
            try { await sub(pos).ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CircleAI.MediaHub] playback subscriber threw: {ex.Message}"); }
        }
    }

    public IDisposable Subscribe(string sessionId, Func<PlaybackPosition, ValueTask> handler)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("sessionId required", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(handler);
        var state = _sessions.GetOrAdd(sessionId, _ => new SessionState(new HashSet<string>(StringComparer.Ordinal), new List<Func<PlaybackPosition, ValueTask>>()));
        lock (state) { state.Subscribers.Add(handler); }
        return new SubscriptionToken(this, sessionId, handler);
    }

    private sealed class SubscriptionToken : IDisposable
    {
        private readonly InMemorySyncedPlayback _owner;
        private readonly string _sessionId;
        private readonly Func<PlaybackPosition, ValueTask> _handler;
        public SubscriptionToken(InMemorySyncedPlayback owner, string sid, Func<PlaybackPosition, ValueTask> h)
        { _owner = owner; _sessionId = sid; _handler = h; }
        public void Dispose()
        {
            if (_owner._sessions.TryGetValue(_sessionId, out var state))
            {
                lock (state) { state.Subscribers.Remove(_handler); }
            }
        }
    }
}
