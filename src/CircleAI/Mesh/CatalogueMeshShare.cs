// CatalogueMeshShare.cs
//
// The aethernet SENDER for the model catalogue. When this device learns of models
// from a NON-mesh source — its own internet discovery, a side-loaded file, the
// embedded seed — it shares that catalogue with the peers it can already see, so a
// data-connected phone brings its data-less neighbours up to date. A catalogue that
// arrived FROM the mesh is never re-shared, so propagation is a single hop and
// there is no gossip storm. Sends are point-to-point to each known peer, because
// the sealed Aether carrier refuses broadcasts — this is what lets the update
// travel over aethernet and not just LAN.
//
// Trust is the receiver's, not ours to assert: the peer decides whether to accept,
// and integrity is the per-row SHA-256 the downloader verifies. No central key.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core.Models;
using CircleAI.Networking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CircleAI.Mesh;

/// <summary>
/// Shares this device's model catalogue with mesh peers when it changes from a
/// non-mesh source. Registered as a hosted service; subscribes to
/// <see cref="ModelCatalogue.CatalogueArrived"/> for its lifetime.
/// </summary>
public sealed class CatalogueMeshShare : IHostedService, IDisposable
{
    private readonly INetworkTransport _transport;
    private readonly IMeshCapabilityRegistry _peers;
    private readonly MeshOffloadOptions _options;
    private readonly ILogger<CatalogueMeshShare> _logger;
    private bool _attached;

    // Only share to peers we have heard from recently — an advert older than this
    // is probably gone, and point-to-point to a dead peer is wasted radio.
    private static readonly TimeSpan PeerFreshness = TimeSpan.FromMinutes(5);

    public CatalogueMeshShare(
        INetworkTransport transport,
        IMeshCapabilityRegistry peers,
        IOptions<MeshOffloadOptions> options,
        ILogger<CatalogueMeshShare>? logger = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _peers = peers ?? throw new ArgumentNullException(nameof(peers));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? NullLogger<CatalogueMeshShare>.Instance;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) { Attach(); return Task.CompletedTask; }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) { Detach(); return Task.CompletedTask; }

    /// <summary>Subscribe to catalogue arrivals. Idempotent.</summary>
    public void Attach()
    {
        if (_attached) return;
        ModelCatalogue.CatalogueArrived += OnArrived;
        _attached = true;
    }

    /// <summary>Stop sharing.</summary>
    public void Detach()
    {
        if (!_attached) return;
        ModelCatalogue.CatalogueArrived -= OnArrived;
        _attached = false;
    }

    // One-hop propagation: share what we got from OUR own sources; never re-share
    // what a peer gave us (source "aethernet"), or catalogues would ricochet
    // around the mesh forever.
    private void OnArrived(ModelRegistry registry, string source)
    {
        if (string.Equals(source, "aethernet", StringComparison.Ordinal)) return;
        _ = ShareAsync(registry, CancellationToken.None);
    }

    /// <summary>
    /// Send <paramref name="registry"/> to every currently-known peer,
    /// point-to-point (so it crosses the sealed Aether mesh as well as LAN).
    /// Never throws; a peer that cannot be reached is skipped.
    /// </summary>
    public async Task ShareAsync(ModelRegistry registry, CancellationToken ct = default)
    {
        if (registry?.Models is not { Count: > 0 }) return;
        if (!_transport.IsAvailable) return;

        foreach (var peer in _peers.List(PeerFreshness))
        {
            if (string.IsNullOrWhiteSpace(peer.PeerId)) continue;
            if (string.Equals(peer.PeerId, _options.LocalNodeId, StringComparison.Ordinal)) continue;

            try
            {
                var payload = MeshOffloadWire.EncodeCatalogue(
                    _options.LocalNodeId, registry, peer.PeerId, _options.RequestTimeout);
                await _transport.SendAsync(payload, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Mesh offload: catalogue share to {Peer} failed.", peer.PeerId);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Detach();
}
