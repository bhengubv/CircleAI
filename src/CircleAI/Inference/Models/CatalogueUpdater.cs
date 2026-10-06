// CatalogueUpdater.cs
//
// The sink that makes the runtime catalogue update FROM ANY CONNECTION. A model
// catalogue reaches a device two ways, and both must work:
//   • INTERNET  — the device's own discovery (ModelScope today) via
//                 ModelCatalogue.RefreshAsync.
//   • AETHERNET — a peer hands one over the mesh, or it is side-loaded, via
//                 ModelCatalogue.Offer.
// Both fire ModelCatalogue.CatalogueArrived; this updater subscribes to that one
// seam and applies whatever arrives into the SQLite catalogue (CatalogueFeedWriter
// → licence gate → upsert → re-assess). No source is required and none is
// privileged — a phone on Wi-Fi and a phone that has only ever seen a peer both
// stay current. No central authority: integrity is the per-row SHA-256, provenance
// is the source that handed the rows over.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>
/// Applies catalogue arrivals (internet or aethernet) into the runtime catalogue.
/// <see cref="Attach"/> once at startup so every arrival flows in automatically.
/// </summary>
public sealed class CatalogueUpdater : IDisposable
{
    private readonly CatalogueFeedWriter _writer;
    private readonly Func<DeviceProbe> _probe;
    private bool _attached;

    /// <param name="writer">The applier rows are written through.</param>
    /// <param name="probe">
    /// Supplies a fresh device snapshot for each apply — device state (free RAM /
    /// storage) moves, so the assessment must use the state at apply time.
    /// </param>
    public CatalogueUpdater(CatalogueFeedWriter writer, Func<DeviceProbe> probe)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <summary>
    /// Subscribe to <see cref="ModelCatalogue.CatalogueArrived"/> so an internet
    /// refresh and an aethernet offer both apply into the catalogue. Idempotent.
    /// </summary>
    public void Attach()
    {
        if (_attached) return;
        ModelCatalogue.CatalogueArrived += OnArrived;
        _attached = true;
    }

    /// <summary>Stop applying arrivals.</summary>
    public void Detach()
    {
        if (!_attached) return;
        ModelCatalogue.CatalogueArrived -= OnArrived;
        _attached = false;
    }

    private void OnArrived(ModelRegistry registry, string source) => ApplyRegistry(registry, source);

    /// <summary>
    /// Apply a catalogue's rows into the runtime catalogue and re-assess. The
    /// unified sink for every source — also the direct entry point for a
    /// side-loaded registry. The caller vouches for the source; this converts and
    /// applies.
    /// </summary>
    public CatalogueFeedResult ApplyRegistry(ModelRegistry registry, string source = "update")
    {
        ArgumentNullException.ThrowIfNull(registry);
        var models = registry.Models ?? (IReadOnlyList<ModelEntry>)Array.Empty<ModelEntry>();
        var feed = new CatalogueFeed(CatalogueFeed.CurrentSchemaVersion, registry.LastUpdated, models);
        return _writer.Apply(feed, _probe(), source);
    }

    /// <summary>
    /// Kick an internet self-discovery refresh (ModelScope, cadence/backoff-gated).
    /// The arrival fires <see cref="ModelCatalogue.CatalogueArrived"/>, which — when
    /// <see cref="Attach"/>ed — applies it. Offline is a quiet no-op; never throws.
    /// </summary>
    public Task SyncFromInternetAsync(ModelScopeCatalogOptions? options = null, CancellationToken ct = default)
        => ModelCatalogue.RefreshAsync(options, ct);

    /// <inheritdoc />
    public void Dispose() => Detach();
}
