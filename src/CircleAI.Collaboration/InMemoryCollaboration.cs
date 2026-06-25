// InMemoryCollaboration.cs
//
// (3.3.0) Real in-memory channel/message/presence stores. Messages
// kept per-channel; presence has online + last-seen timestamps.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Collaboration;

public sealed class InMemoryChannelStore : IChannelStore
{
    private readonly ConcurrentDictionary<string, Channel> _items = new(StringComparer.Ordinal);
    public string BackendId => "in-memory";

    public void Upsert(Channel c) { ArgumentNullException.ThrowIfNull(c); _items[c.ChannelId] = c; }

    public ValueTask<Channel?> GetAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id required", nameof(id));
        _items.TryGetValue(id, out var c);
        return ValueTask.FromResult(c);
    }

    public ValueTask<IReadOnlyList<Channel>> ListForTeamAsync(string teamId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(teamId)) throw new ArgumentException("teamId required", nameof(teamId));
        return ValueTask.FromResult<IReadOnlyList<Channel>>(
            _items.Values.Where(c => c.TeamId == teamId).OrderBy(c => c.Name).ToArray());
    }
}

public sealed class InMemoryMessageStore : IMessageStore
{
    private readonly ConcurrentDictionary<string, List<Message>> _byChannel = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    public string BackendId => "in-memory";

    public ValueTask<Message> PostAsync(Message msg, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(msg);
        if (string.IsNullOrWhiteSpace(msg.ChannelId)) throw new ArgumentException("ChannelId required");
        lock (_lock)
        {
            var list = _byChannel.GetOrAdd(msg.ChannelId, _ => new List<Message>());
            list.Add(msg);
        }
        return ValueTask.FromResult(msg);
    }

    public ValueTask<IReadOnlyList<Message>> ReadAsync(string channelId, int limit = 100, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("channelId required", nameof(channelId));
        lock (_lock)
        {
            if (!_byChannel.TryGetValue(channelId, out var list)) return ValueTask.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>());
            return ValueTask.FromResult<IReadOnlyList<Message>>(
                list.OrderByDescending(m => m.AtUtc).Take(limit).ToArray());
        }
    }
}

public sealed class InMemoryPresence : IPresence
{
    private readonly ConcurrentDictionary<string, PresenceState> _states = new(StringComparer.Ordinal);
    public string BackendId => "in-memory";

    public void Set(PresenceState s) { ArgumentNullException.ThrowIfNull(s); _states[s.UserId] = s; }

    public ValueTask<PresenceState?> GetAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("userId required", nameof(userId));
        _states.TryGetValue(userId, out var s);
        return ValueTask.FromResult(s);
    }
}
