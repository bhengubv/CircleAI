// Contracts.cs — (2.9.0) Media-server contracts.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.MediaServer;

public sealed record MediaItem(string ItemId, string Title, string Kind, TimeSpan Duration, string MimeType);
public sealed record PlaybackPosition(string ItemId, TimeSpan Position, DateTimeOffset AtUtc);

public interface IMediaLibrary
{
    string BackendId { get; }
    ValueTask<MediaItem?> GetAsync(string id, CancellationToken ct = default);
    ValueTask<IReadOnlyList<MediaItem>> SearchAsync(string query, int topK = 20, CancellationToken ct = default);
}

public interface ISyncedPlayback
{
    string BackendId { get; }
    ValueTask JoinSessionAsync(string sessionId, string userId, CancellationToken ct = default);
    ValueTask BroadcastPositionAsync(string sessionId, PlaybackPosition pos, CancellationToken ct = default);
    IDisposable Subscribe(string sessionId, Func<PlaybackPosition, ValueTask> handler);
}
