// Contracts.cs — (2.9.0) Distribution contracts.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Distribution;

public sealed record FileMetadata(string ContentHash, string Name, long SizeBytes);
public sealed record Peer(string PeerId, string Endpoint, IReadOnlyList<string> AvailableHashes);

public interface IFileSync
{
    string BackendId { get; }
    ValueTask<bool> HasAsync(string contentHash, CancellationToken ct = default);
    ValueTask<ReadOnlyMemory<byte>?> FetchAsync(string contentHash, CancellationToken ct = default);
    ValueTask AnnounceAsync(FileMetadata metadata, ReadOnlyMemory<byte> payload, CancellationToken ct = default);
}

public interface IPeerAdvertiser
{
    string BackendId { get; }
    ValueTask<IReadOnlyList<Peer>> DiscoverAsync(CancellationToken ct = default);
}
