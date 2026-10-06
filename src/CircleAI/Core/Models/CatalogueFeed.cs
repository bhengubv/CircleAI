// CatalogueFeed.cs
//
// THE SIGNED-FEED CONTRACT. This is the wire shape of the curated catalogue feed
// that ships new models to devices as ROWS — the payload whose canonical UTF-8
// JSON is signed (ECDSA P-256) and whose detached signature the
// EcdsaCatalogSignatureVerifier checks before CatalogueFeedWriter writes a single
// row. Central curation, peer-to-peer distribution: the feed is published to
// GitHub releases ([[circleai-voices-github-store]]) and shared over the mesh.
//
// v1 is hand-curated (5–10 vetted device models); v2 automates discovery from the
// HuggingFace Hub API + ModelScope + a leaderboard. The contract is fixed now so
// the automation only has to produce this shape.
//
// A model row is a ModelEntry — which already carries id/version/quant/repo/
// source/modality/engine/size/per-file SHA-256/RAM+storage+VRAM floors/
// capabilities/qualityRank/fallback/architecture/language, and now Engine +
// License. Every field a curator vets is therefore in the row it signs.

using System;
using System.Collections.Generic;

namespace CircleAI.Core.Models;

/// <summary>
/// A curated, signable catalogue feed. Its canonical JSON is what the detached
/// signature covers; verify the bytes, THEN deserialise into this.
/// </summary>
/// <param name="SchemaVersion">
/// The contract version. <see cref="CurrentSchemaVersion"/> today; a device
/// refuses a feed whose schema it does not understand rather than guessing.
/// </param>
/// <param name="IssuedAt">When the feed was published (UTC), for staleness / rollback checks.</param>
/// <param name="Models">The catalogue rows. Each is upserted by <see cref="ModelEntry.Name"/>.</param>
public sealed record CatalogueFeed(
    int SchemaVersion,
    DateTimeOffset IssuedAt,
    IReadOnlyList<ModelEntry> Models)
{
    /// <summary>The schema version this build produces and accepts.</summary>
    public const int CurrentSchemaVersion = 1;
}
