// CatalogueFeedWriter.cs
//
// Applies a batch of catalogue rows from a TRUSTED source into the runtime
// catalogue, then re-assesses against the device. There is NO central signature
// and NO certificate authority: a "feed" here is just rows offered by a source
// the caller already trusts — a peer over the authenticated mesh, the device's
// own HuggingFace/ModelScope discovery, the embedded seed, or a side-loaded file.
// No single source is required; a device that has any one of them stays current.
//
// Trust is decentralised, split into the two jobs a central signature used to
// conflate:
//   • INTEGRITY — the exact bytes of a model — is the per-file SHA-256 already in
//     every row, verified by the downloader on fetch. A hash needs no authority.
//   • PROVENANCE — whether to trust the SOURCE — is the caller's to decide: the
//     mesh channel is authenticated to a peer's own node identity, and which peers
//     count is CircleAI's curated trusted set. This writer applies what a trusted
//     caller hands it; it neither mints nor checks certificates.
//
// A licence gate still runs at ingest (fully-free-open-source), and a batch that
// cannot be parsed is refused as Malformed. Outcomes surface via
// IModelCatalogObserver so the self-heal log / Wolverine sees them.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>The outcome of applying a batch of rows.</summary>
/// <param name="Accepted"><c>true</c> when the batch parsed and was applied.</param>
/// <param name="Upserted">Rows written.</param>
/// <param name="Compatible">Rows compatible with the device after re-assessment.</param>
/// <param name="LicenceRejected">Rows dropped at ingest because their licence is not free.</param>
/// <param name="Rejection">Why the whole batch was refused, when <see cref="Accepted"/> is <c>false</c>.</param>
public sealed record CatalogueFeedResult(
    bool Accepted,
    int Upserted,
    int Compatible,
    int LicenceRejected,
    CatalogFeedRejection? Rejection)
{
    public static CatalogueFeedResult Applied(int upserted, int compatible, int licenceRejected) =>
        new(true, upserted, compatible, licenceRejected, null);

    public static CatalogueFeedResult Rejected(CatalogFeedRejection reason) =>
        new(false, 0, 0, 0, reason);
}

/// <summary>
/// Applies catalogue rows from a trusted source and re-assesses them. No central
/// signature; provenance is the caller's, integrity is the per-row SHA-256 (see
/// the file header).
/// </summary>
public sealed class CatalogueFeedWriter
{
    private readonly IModelCatalog _catalog;
    private readonly IModelAssessor _assessor;
    private readonly IModelCatalogObserver _observer;
    private readonly ModelScopeCatalogOptions _licenceOptions;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Modality / Source / Engine / License read from their NAMES.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <param name="catalog">The catalogue rows are written into.</param>
    /// <param name="assessor">Runs after an upsert to score the new rows.</param>
    /// <param name="observer">Surfaces the outcome; defaults to a no-op.</param>
    /// <param name="licenceOptions">
    /// The licence policy (allowlist + denylist) enforced at ingest via
    /// <see cref="ModelScopeCatalogClient.LicenceAllowed(string?, ModelScopeCatalogOptions)"/>.
    /// Defaults to the standard fully-free-open-source policy. A row whose licence
    /// is not free — or is unstated — is dropped and never catalogued.
    /// </param>
    public CatalogueFeedWriter(
        IModelCatalog catalog,
        IModelAssessor assessor,
        IModelCatalogObserver? observer = null,
        ModelScopeCatalogOptions? licenceOptions = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _assessor = assessor ?? throw new ArgumentNullException(nameof(assessor));
        _observer = observer ?? NullModelCatalogObserver.Instance;
        _licenceOptions = licenceOptions ?? new ModelScopeCatalogOptions();
    }

    /// <summary>
    /// Parse a JSON batch (e.g. a side-loaded file or a peer's payload) and apply
    /// it. The caller is responsible for having obtained the bytes from a source it
    /// trusts.
    /// </summary>
    public CatalogueFeedResult Apply(byte[] batchJson, DeviceProbe probe, string source = "feed")
    {
        ArgumentNullException.ThrowIfNull(batchJson);
        ArgumentNullException.ThrowIfNull(probe);

        CatalogueFeed? feed;
        try { feed = JsonSerializer.Deserialize<CatalogueFeed>(batchJson, Json); }
        catch (JsonException) { feed = null; }

        if (feed is null || feed.SchemaVersion != CatalogueFeed.CurrentSchemaVersion || feed.Models is null)
        {
            _observer.OnFeedRejected(CatalogFeedRejection.Malformed, source,
                feed is null ? "unparseable batch" : $"unsupported schema {feed?.SchemaVersion}");
            return CatalogueFeedResult.Rejected(CatalogFeedRejection.Malformed);
        }

        return Apply(feed, probe, source);
    }

    /// <summary>
    /// Apply an already-parsed batch of rows from a trusted source (e.g. a mesh
    /// <c>Offer</c> from a curated peer).
    /// </summary>
    public CatalogueFeedResult Apply(CatalogueFeed feed, DeviceProbe probe, string source = "feed")
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(probe);

        if (feed.Models is null)
        {
            _observer.OnFeedRejected(CatalogFeedRejection.Malformed, source, "no rows");
            return CatalogueFeedResult.Rejected(CatalogFeedRejection.Malformed);
        }

        // Per row: licence gate ("may we ship this?") + engine derivation. Engine
        // gates device-fit later, in the assessor — it is not dropped here, so a
        // GGUF row is catalogued and simply stays compatible=0 until an engine ships.
        var upserted = 0;
        var licenceRejected = 0;
        foreach (var entry in feed.Models)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            if (!ModelScopeCatalogClient.LicenceAllowed(entry.License, _licenceOptions))
            {
                licenceRejected++;
                continue;
            }
            var engine = entry.Engine != ModelEngine.Mnn ? entry.Engine : CatalogueSeed.DeriveEngine(entry);
            _catalog.Upsert(engine == entry.Engine ? entry : entry with { Engine = engine });
            upserted++;
        }

        // Re-assess against the device — the new rows are unassessed until now.
        var assessment = _assessor.Assess(probe);

        _observer.OnFeedApplied(upserted, source);
        _observer.OnCatalogueAssessed(assessment.Compatible, assessment.Assessed, probe);
        if (_catalog.Best(ModelModality.Chat) is null)
            _observer.OnNoCompatibleModel(ModelModality.Chat, _catalog.Count(), probe);

        return CatalogueFeedResult.Applied(upserted, assessment.Compatible, licenceRejected);
    }
}
