// NullImplementations.cs — (2.9.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.MediaServer;

public sealed class NullMediaLibrary : IMediaLibrary
{
    public static readonly NullMediaLibrary Instance = new();
    public string BackendId => "null";
    public ValueTask<MediaItem?> GetAsync(string id, CancellationToken ct = default) => ValueTask.FromResult<MediaItem?>(null);
    public ValueTask<IReadOnlyList<MediaItem>> SearchAsync(string q, int topK = 20, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<MediaItem>>(Array.Empty<MediaItem>());
}

public sealed class NullSyncedPlayback : ISyncedPlayback
{
    public static readonly NullSyncedPlayback Instance = new();
    public string BackendId => "null";
    public ValueTask JoinSessionAsync(string s, string u, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask BroadcastPositionAsync(string s, PlaybackPosition p, CancellationToken ct = default) => ValueTask.CompletedTask;
    public IDisposable Subscribe(string s, Func<PlaybackPosition, ValueTask> h) => EmptyDisposable.Instance;

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
