// MeshOffloadRouter.cs
//
// The policy half of the hand-off. It decides WHO should run a turn and WHEN to
// give up on the mesh:
//   1. estimate the KV budget the turn needs;
//   2. ask the (already-audited) IMeshCapabilityRegistry.Find for peers that
//      have the model loaded with at least that much spare budget;
//   3. try the best peers in turn via IMeshOffloadClient;
//   4. fall back to the local / smaller brain when no peer is capable or every
//      attempt fails.
//
// The mechanics of talking to a peer (wire, correlation, transport) live in
// MeshOffloadClient. Discovering that a peer exists at all is AetherNet's job
// (aether-protocol repo) - this router only consumes the registry AetherNet
// fills.

using System.Diagnostics;
using CircleAI.AetherNet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CircleAI.Mesh;

/// <summary>
/// Default <see cref="IOffloadRouter"/>: registry-driven peer selection with a
/// local fallback.
/// </summary>
public sealed class MeshOffloadRouter : IOffloadRouter
{
    private readonly IMeshCapabilityRegistry _registry;
    private readonly IMeshOffloadClient _client;
    private readonly ILocalInferenceFallback _localFallback;
    private readonly MeshOffloadOptions _options;
    private readonly ILogger<MeshOffloadRouter> _logger;

    public MeshOffloadRouter(
        IMeshCapabilityRegistry registry,
        IMeshOffloadClient client,
        ILocalInferenceFallback localFallback,
        IOptions<MeshOffloadOptions> options,
        ILogger<MeshOffloadRouter>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _localFallback = localFallback ?? throw new ArgumentNullException(nameof(localFallback));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? NullLogger<MeshOffloadRouter>.Instance;
    }

    /// <inheritdoc/>
    public async Task<OffloadResult> RouteAsync(OffloadTurn turn, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(turn);

        int estimate = Math.Max(0, _options.EstimateKvTokens(turn));
        int minFreeKv = (int)Math.Ceiling(estimate * _options.KvHeadroomFactor);
        if (minFreeKv < 0) minFreeKv = 0;

        IReadOnlyList<MeshCapabilityAdvertisement> candidates =
            _registry.Find(turn.ModelId, minFreeKv, _options.StaleAfter);

        if (candidates.Count == 0)
        {
            _logger.LogInformation(
                "Mesh offload: no peer advertises {Model} with >= {Kv} free KV tokens; using local fallback.",
                turn.ModelId, minFreeKv);
            return await FallBackLocalAsync(turn, "No capable peer advertised.", ct).ConfigureAwait(false);
        }

        var pool = candidates.ToList();
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var reasons = new List<string>();
        int attempts = Math.Max(1, _options.MaxPeerAttempts);

        for (int i = 0; i < attempts && pool.Count > 0; i++)
        {
            MeshCapabilityAdvertisement pick = _options.SelectPeer(pool) ?? pool[0];
            pool.RemoveAll(p => string.Equals(p.PeerId, pick.PeerId, StringComparison.Ordinal));
            if (!tried.Add(pick.PeerId)) continue;

            _logger.LogDebug("Mesh offload: attempting peer {Peer} ({Tier}, {Kv} free KV) for {Model}.",
                pick.PeerId, pick.Tier, pick.FreeKvTokens, turn.ModelId);

            OffloadResult remote;
            try
            {
                remote = await _client.RequestAsync(pick.PeerId, turn, _options.RequestTimeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                reasons.Add($"{pick.PeerId}: {ex.Message}");
                continue;
            }

            if (remote.Success)
            {
                _logger.LogInformation("Mesh offload: peer {Peer} served {Model} in {Ms:0}ms.",
                    pick.PeerId, turn.ModelId, remote.ElapsedMilliseconds);
                return remote;
            }

            reasons.Add($"{pick.PeerId}: {remote.FailureReason}");
        }

        string why = "All peer attempts failed: " + string.Join("; ", reasons);
        _logger.LogInformation("Mesh offload: {Why}; using local fallback.", why);
        return await FallBackLocalAsync(turn, why, ct).ConfigureAwait(false);
    }

    private async Task<OffloadResult> FallBackLocalAsync(OffloadTurn turn, string why, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            OffloadResult local = await _localFallback.CompleteAsync(turn, ct).ConfigureAwait(false);
            sw.Stop();

            // Normalise: a fallback engine that reports success but leaves
            // ServedBy at None is really a local serve.
            if (local.Success && local.ServedBy == OffloadServedBy.None)
            {
                local = local with { ServedBy = OffloadServedBy.LocalFallback };
            }

            if (!local.Success && string.IsNullOrEmpty(local.FailureReason))
            {
                local = local with { FailureReason = why };
            }
            return local;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return OffloadResult.Fail(
                $"{why} Local fallback also failed: {ex.Message}", OffloadServedBy.None, sw.Elapsed.TotalMilliseconds);
        }
    }
}
