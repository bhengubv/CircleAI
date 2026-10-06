// Contracts.cs — (2.8.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Collaboration;

public sealed record Channel(string ChannelId, string Name, string TeamId);
public sealed record Message(string MessageId, string ChannelId, string AuthorId, string Body, DateTimeOffset AtUtc);

public interface IChannelStore
{
    string BackendId { get; }
    ValueTask<Channel?> GetAsync(string id, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Channel>> ListForTeamAsync(string teamId, CancellationToken ct = default);
}

public interface IMessageStore
{
    string BackendId { get; }
    ValueTask<Message> PostAsync(Message msg, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Message>> ReadAsync(string channelId, int limit = 100, CancellationToken ct = default);
}

public sealed record PresenceState(string UserId, bool Online, DateTimeOffset LastSeenUtc);

public interface IPresence
{
    string BackendId { get; }
    ValueTask<PresenceState?> GetAsync(string userId, CancellationToken ct = default);
}
