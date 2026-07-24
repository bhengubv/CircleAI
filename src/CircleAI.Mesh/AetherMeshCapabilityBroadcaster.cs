// AetherMeshCapabilityBroadcaster.cs
//
// The real IMeshCapabilityBroadcaster - it REPLACES CircleAI.AetherNet's RT-12
// v1 NullMeshCapabilityBroadcaster (which did nothing). BroadcastAsync
// serialises OUR advertisement and sends it, destination-less, over the
// INetworkTransport so every reachable peer's ingest loop folds it into their
// registry. MeshAdvertisementBeacon re-broadcasts on a cadence so we never age
// out of a peer's freshness window.
//
// As everywhere in this package: we publish over a transport the host already
// wired. We do not discover peers. Zero-infrastructure BLE / Wi-Fi Direct
// discovery is AetherNet's job (aether-protocol repo).

using CircleAI.AetherNet;
using CircleAI.Networking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CircleAI.Mesh;

/// <summary>
/// Publishes our capability advertisement over an <see cref="INetworkTransport"/>.
/// The real replacement for <c>NullMeshCapabilityBroadcaster</c>.
/// </summary>
public sealed class AetherMeshCapabilityBroadcaster : IMeshCapabilityBroadcaster
{
    private readonly INetworkTransport _transport;
    private readonly MeshOffloadOptions _options;
    private readonly ILogger<AetherMeshCapabilityBroadcaster> _logger;

    public AetherMeshCapabilityBroadcaster(
        INetworkTransport transport,
        IOptions<MeshOffloadOptions> options,
        ILogger<AetherMeshCapabilityBroadcaster>? logger = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? NullLogger<AetherMeshCapabilityBroadcaster>.Instance;
    }

    /// <inheritdoc/>
    public async ValueTask BroadcastAsync(MeshCapabilityAdvertisement ad, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ad);

        if (!_transport.IsAvailable)
        {
            _logger.LogDebug("Mesh advert: transport {Kind} unavailable; skipping broadcast.", _transport.Kind);
            return;
        }

        // Stamp our node id + a fresh timestamp so peers dedupe on PeerId and
        // measure staleness from the moment we actually sent it.
        MeshCapabilityAdvertisement stamped = ad with
        {
            PeerId = _options.LocalNodeId,
            AdvertisedAtUtc = DateTimeOffset.UtcNow,
        };

        var env = new MeshAdvertEnvelope(
            PeerId: stamped.PeerId,
            ModelId: stamped.ModelId,
            FreeKvTokens: stamped.FreeKvTokens,
            Tier: (int)stamped.Tier,
            ContextWindowTokens: stamped.ContextWindowTokens,
            AdvertisedAtUtc: stamped.AdvertisedAtUtc,
            LatencyHintMs: stamped.LatencyHintMs);

        // TTL == freshness window: a peer that stops hearing us expires us anyway.
        NetworkPayload payload = MeshOffloadWire.EncodeAdvert(_options.LocalNodeId, env, _options.StaleAfter);

        try
        {
            await _transport.SendAsync(payload, ct).ConfigureAwait(false);
            _logger.LogDebug("Mesh advert: broadcast {Model} ({Kv} free KV) over {Kind}.",
                stamped.ModelId, stamped.FreeKvTokens, _transport.Kind);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mesh advert: broadcast failed over {Kind}.", _transport.Kind);
        }
    }
}

/// <summary>
/// Periodically re-broadcasts our advertisement (from
/// <see cref="MeshOffloadOptions.OurAdvertisement"/>) so peers keep us in their
/// freshness window. Does nothing when no advertisement provider is configured -
/// a borrow-only node never advertises.
/// </summary>
public sealed class MeshAdvertisementBeacon : IHostedService, IAsyncDisposable
{
    private readonly IMeshCapabilityBroadcaster _broadcaster;
    private readonly MeshOffloadOptions _options;
    private readonly ILogger<MeshAdvertisementBeacon> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MeshAdvertisementBeacon(
        IMeshCapabilityBroadcaster broadcaster,
        IOptions<MeshOffloadOptions> options,
        ILogger<MeshAdvertisementBeacon>? logger = null)
    {
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? NullLogger<MeshAdvertisementBeacon>.Instance;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.OurAdvertisement is null)
        {
            _logger.LogInformation("Mesh advert beacon: no advertisement provider; this node borrows only, never advertises.");
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        CancellationToken loopToken = _cts.Token;
        _loop = Task.Run(() => LoopAsync(loopToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                MeshCapabilityAdvertisement? ad = _options.OurAdvertisement?.Invoke();
                if (ad is not null)
                {
                    await _broadcaster.BroadcastAsync(ad, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mesh advert beacon: tick failed.");
            }

            try { await Task.Delay(_options.BroadcastInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { _logger.LogDebug(ex, "Mesh advert beacon stopped with an exception."); }
            _loop = null;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Mesh advert beacon: dispose stop failed."); }
        _cts?.Dispose();
    }
}
