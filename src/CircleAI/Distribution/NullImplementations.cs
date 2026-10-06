// NullImplementations.cs — (2.9.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Distribution;

public sealed class NullFileSync : IFileSync
{
    public static readonly NullFileSync Instance = new();
    public string BackendId => "null";
    public ValueTask<bool> HasAsync(string h, CancellationToken ct = default) => ValueTask.FromResult(false);
    public ValueTask<ReadOnlyMemory<byte>?> FetchAsync(string h, CancellationToken ct = default) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
    public ValueTask AnnounceAsync(FileMetadata m, ReadOnlyMemory<byte> p, CancellationToken ct = default) => ValueTask.CompletedTask;
}

public sealed class NullPeerAdvertiser : IPeerAdvertiser
{
    public static readonly NullPeerAdvertiser Instance = new();
    public string BackendId => "null";
    public ValueTask<IReadOnlyList<Peer>> DiscoverAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Peer>>(Array.Empty<Peer>());
}
