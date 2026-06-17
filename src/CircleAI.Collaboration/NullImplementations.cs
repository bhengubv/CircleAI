// NullImplementations.cs — (2.8.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Collaboration;

public sealed class NullChannelStore : IChannelStore
{
    public static readonly NullChannelStore Instance = new();
    public string BackendId => "null";
    public ValueTask<Channel?> GetAsync(string id, CancellationToken ct = default) => ValueTask.FromResult<Channel?>(null);
    public ValueTask<IReadOnlyList<Channel>> ListForTeamAsync(string teamId, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Channel>>(Array.Empty<Channel>());
}

public sealed class NullMessageStore : IMessageStore
{
    public static readonly NullMessageStore Instance = new();
    public string BackendId => "null";
    public ValueTask<Message> PostAsync(Message m, CancellationToken ct = default) => ValueTask.FromResult(m);
    public ValueTask<IReadOnlyList<Message>> ReadAsync(string ch, int limit = 100, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>());
}

public sealed class NullPresence : IPresence
{
    public static readonly NullPresence Instance = new();
    public string BackendId => "null";
    public ValueTask<PresenceState?> GetAsync(string userId, CancellationToken ct = default)
        => ValueTask.FromResult<PresenceState?>(null);
}
