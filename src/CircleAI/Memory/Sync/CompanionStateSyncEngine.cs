// CompanionStateSyncEngine.cs
//
// Orchestration loop. Subscribes to the channel, responds to envelopes,
// and exposes WriteLocalAsync + SyncNowAsync for the host.
//
// Protocol — convergent in <= 2 round-trips per peer pair:
//   1. SyncNowAsync   → broadcast Announce(localStateVector)
//   2. Peer receives Announce → diff against own vector → reply Request(missing)
//   3. We receive Request → gather entries via store.GetSinceAsync → Push
//   4. Peer receives Push → ApplyAsync for each entry
//   5. Peer broadcasts Announce again if anything applied — converges.
//
// All entries are content-hashed (SHA-256 of payload) at write time so the
// tiebreaker for equal-Version conflicts is deterministic everywhere.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Sync;

/// <summary>
/// Default <see cref="ICompanionStateSyncEngine"/>.
/// </summary>
public sealed class CompanionStateSyncEngine : ICompanionStateSyncEngine
{
    private readonly ICompanionStateChannel _channel;
    private readonly ISyncableEntryStore _store;
    private readonly HybridLogicalClock _clock;
    private readonly Func<DateTimeOffset> _wallClock;
    private IDisposable? _subscription;
    private bool _disposed;

    public CompanionStateSyncEngine(
        ICompanionStateChannel channel,
        ISyncableEntryStore store,
        HybridLogicalClock clock,
        Func<DateTimeOffset>? wallClock = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _wallClock = wallClock ?? (() => DateTimeOffset.UtcNow);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        _subscription ??= _channel.Subscribe(HandleEnvelopeAsync);
        return Task.CompletedTask;
    }

    public async Task SyncNowAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var vector = await _store.GetStateVectorAsync(ct).ConfigureAwait(false);
        await _channel.SendAsync(new SyncEnvelope(
            Kind: SyncEnvelopeKind.Announce,
            FromNodeId: _channel.LocalNodeId,
            StateVector: vector,
            Requests: null,
            Entries: null), ct).ConfigureAwait(false);
    }

    public async Task<SyncableEntry> WriteLocalAsync(
        string entityType, string entityId, string payload,
        bool isTombstone = false, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(entityType))
            throw new ArgumentException("entityType required", nameof(entityType));
        if (string.IsNullOrWhiteSpace(entityId))
            throw new ArgumentException("entityId required", nameof(entityId));

        var entry = new SyncableEntry(
            EntityType: entityType,
            EntityId: entityId,
            Version: _clock.Tick(),
            IsTombstone: isTombstone,
            ContentHash: ComputeHash(payload ?? string.Empty),
            Payload: payload ?? string.Empty,
            SourceNodeId: _channel.LocalNodeId,
            AuthoredAt: _wallClock());

        await _store.ApplyAsync(entry, ct).ConfigureAwait(false);

        if (_subscription is not null)
        {
            await _channel.SendAsync(new SyncEnvelope(
                Kind: SyncEnvelopeKind.Push,
                FromNodeId: _channel.LocalNodeId,
                StateVector: null,
                Requests: null,
                Entries: new[] { entry }), ct).ConfigureAwait(false);
        }
        return entry;
    }

    // ── Inbound envelope handling ────────────────────────────────────────

    private async Task HandleEnvelopeAsync(SyncEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Kind)
        {
            case SyncEnvelopeKind.Announce:
                await HandleAnnounceAsync(envelope, ct).ConfigureAwait(false);
                break;
            case SyncEnvelopeKind.Request:
                await HandleRequestAsync(envelope, ct).ConfigureAwait(false);
                break;
            case SyncEnvelopeKind.Push:
                await HandlePushAsync(envelope, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleAnnounceAsync(SyncEnvelope envelope, CancellationToken ct)
    {
        if (envelope.StateVector is null) return;
        var local = await _store.GetStateVectorAsync(ct).ConfigureAwait(false);
        var localMap = local.ToDictionary(v => v.EntityType, v => v.MaxKnownVersion);

        var requests = new List<RequestItem>();
        foreach (var peer in envelope.StateVector)
        {
            localMap.TryGetValue(peer.EntityType, out var ourMax);
            if (peer.MaxKnownVersion > ourMax)
            {
                requests.Add(new RequestItem(peer.EntityType, ourMax));
            }
        }
        if (requests.Count == 0) return;

        await _channel.SendAsync(new SyncEnvelope(
            Kind: SyncEnvelopeKind.Request,
            FromNodeId: _channel.LocalNodeId,
            StateVector: null,
            Requests: requests,
            Entries: null), ct).ConfigureAwait(false);
    }

    private async Task HandleRequestAsync(SyncEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Requests is null || envelope.Requests.Count == 0) return;
        var collected = new List<SyncableEntry>();
        foreach (var req in envelope.Requests)
        {
            var newer = await _store.GetSinceAsync(req.EntityType, req.SinceVersion, ct)
                .ConfigureAwait(false);
            collected.AddRange(newer);
        }
        if (collected.Count == 0) return;

        await _channel.SendAsync(new SyncEnvelope(
            Kind: SyncEnvelopeKind.Push,
            FromNodeId: _channel.LocalNodeId,
            StateVector: null,
            Requests: null,
            Entries: collected), ct).ConfigureAwait(false);
    }

    private async Task HandlePushAsync(SyncEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Entries is null) return;
        bool anyApplied = false;
        foreach (var e in envelope.Entries)
        {
            _clock.Observe(e.Version);
            var applied = await _store.ApplyAsync(e, ct).ConfigureAwait(false);
            anyApplied |= applied;
        }
        // If anything applied, re-announce so other peers can converge too.
        if (anyApplied) await SyncNowAsync(ct).ConfigureAwait(false);
    }

    // ── IAsyncDisposable ─────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _subscription?.Dispose();
        _subscription = null;
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CompanionStateSyncEngine));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string ComputeHash(string payload)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(payload), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
