// MeshCapabilityRegistry.cs
//
// (RT-12 v1) Mesh capability discovery — peers broadcast what they have
// loaded ("I have Qwen3-1.7B-MNN with 2048 tokens of free KV budget on
// a Tier=Phone device"). v1 ships the contracts + an in-memory registry;
// the AetherNet broadcast transport lands in 2.7.0 with RT-12 v2 actual
// offload.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;

namespace CircleAI.AetherNet;

/// <summary>
/// (RT-12 v1) One peer's advertisement of what it can serve right now.
/// Pure data — no execution state.
/// </summary>
/// <param name="PeerId">Stable opaque identifier for the advertising peer.</param>
/// <param name="ModelId">The model the peer has loaded, e.g. <c>"Qwen3-1.7B-MNN"</c>.</param>
/// <param name="FreeKvTokens">How many tokens of KV-cache budget the peer has spare.</param>
/// <param name="Tier">The peer's device tier (Wearable .. ServerFarm).</param>
/// <param name="ContextWindowTokens">The model's configured context window.</param>
/// <param name="AdvertisedAtUtc">When the peer last published this advertisement.</param>
/// <param name="LatencyHintMs">Optional round-trip estimate; null when unknown.</param>
public sealed record MeshCapabilityAdvertisement(
    string         PeerId,
    string         ModelId,
    int            FreeKvTokens,
    DeviceTier     Tier,
    int            ContextWindowTokens,
    DateTimeOffset AdvertisedAtUtc,
    int?           LatencyHintMs = null);

/// <summary>
/// (RT-12 v1) Holds the latest advertisement per peer + supports
/// filtered query. The AetherNet transport (v2, 2.7.0) feeds this
/// registry as peers broadcast. v1 lets hosting layers query and
/// reason about availability without yet routing.
/// </summary>
public interface IMeshCapabilityRegistry
{
    /// <summary>
    /// Publish or replace an advertisement. Called by the transport on
    /// receipt of a peer broadcast.
    /// </summary>
    ValueTask UpsertAsync(MeshCapabilityAdvertisement ad, CancellationToken ct = default);

    /// <summary>
    /// Remove a peer (e.g. on explicit disconnect). Idempotent.
    /// </summary>
    ValueTask<bool> RemoveAsync(string peerId, CancellationToken ct = default);

    /// <summary>
    /// Return every advertisement currently known. Use <paramref name="staleAfter"/>
    /// to filter out entries older than this many seconds. Default 60s
    /// matches a reasonable beacon cadence.
    /// </summary>
    IReadOnlyList<MeshCapabilityAdvertisement> List(
        TimeSpan? staleAfter = null);

    /// <summary>
    /// Find every peer that has loaded <paramref name="modelId"/> with at
    /// least <paramref name="minFreeKvTokens"/> of spare KV budget. Sorted
    /// by spare budget descending — the most-capable peer comes first.
    /// </summary>
    IReadOnlyList<MeshCapabilityAdvertisement> Find(
        string    modelId,
        int       minFreeKvTokens = 0,
        TimeSpan? staleAfter      = null);
}

/// <summary>
/// (RT-12 v1) Default <see cref="IMeshCapabilityRegistry"/> — in-memory,
/// thread-safe. The AetherNet transport plugs into this; without a
/// transport, the registry just stays empty (no peers).
/// </summary>
public sealed class InMemoryMeshCapabilityRegistry : IMeshCapabilityRegistry
{
    private readonly ConcurrentDictionary<string, MeshCapabilityAdvertisement> _entries
        = new(StringComparer.Ordinal);

    /// <summary>Optional clock override for tests.</summary>
    public Func<DateTimeOffset> NowUtc { get; init; } = () => DateTimeOffset.UtcNow;

    /// <inheritdoc/>
    public ValueTask UpsertAsync(MeshCapabilityAdvertisement ad, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ad);
        ArgumentException.ThrowIfNullOrWhiteSpace(ad.PeerId);
        _entries[ad.PeerId] = ad;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<bool> RemoveAsync(string peerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        return ValueTask.FromResult(_entries.TryRemove(peerId, out _));
    }

    /// <inheritdoc/>
    public IReadOnlyList<MeshCapabilityAdvertisement> List(TimeSpan? staleAfter = null)
    {
        if (!staleAfter.HasValue) return _entries.Values.ToArray();
        var cutoff = NowUtc() - staleAfter.Value;
        return _entries.Values.Where(a => a.AdvertisedAtUtc >= cutoff).ToArray();
    }

    /// <inheritdoc/>
    public IReadOnlyList<MeshCapabilityAdvertisement> Find(
        string    modelId,
        int       minFreeKvTokens = 0,
        TimeSpan? staleAfter      = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var cutoff = staleAfter.HasValue ? NowUtc() - staleAfter.Value : DateTimeOffset.MinValue;
        return _entries.Values
            .Where(a => string.Equals(a.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
            .Where(a => a.FreeKvTokens >= minFreeKvTokens)
            .Where(a => a.AdvertisedAtUtc >= cutoff)
            .OrderByDescending(a => a.FreeKvTokens)
            .ToArray();
    }
}

/// <summary>
/// (RT-12 v1) Contract for the broadcaster that publishes OUR
/// advertisement to the mesh. v1 ships a no-op default; the AetherNet
/// transport binding (v2) supersedes it.
/// </summary>
public interface IMeshCapabilityBroadcaster
{
    /// <summary>
    /// Publish our current advertisement to the mesh. v1 may be a no-op
    /// when no transport is registered.
    /// </summary>
    ValueTask BroadcastAsync(MeshCapabilityAdvertisement ad, CancellationToken ct = default);
}

/// <summary>
/// Default broadcaster — does nothing. Used when no AetherNet transport
/// is bound. Existing CircleAI deployments work unchanged.
/// </summary>
public sealed class NullMeshCapabilityBroadcaster : IMeshCapabilityBroadcaster
{
    public static readonly NullMeshCapabilityBroadcaster Instance = new();
    public ValueTask BroadcastAsync(MeshCapabilityAdvertisement ad, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
