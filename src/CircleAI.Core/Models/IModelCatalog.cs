// IModelCatalog.cs
//
// THE SINGLE RUNTIME SOURCE OF TRUTH FOR "WHAT MODELS EXIST AND WHICH ONE TO
// USE." One row per model, two computed columns doing the work: `compatible`
// (can THIS device actually run it — engine shipped AND RAM/storage fit AND
// licence clean) and `rank` (quality/fit score). Selection is then just
// `Best(modality)` = the top compatible row — build-independent, so a better
// model reaches the device as a ROW from a signed feed, never an app release.
//
// This is the seam that kills the "outdated model" hostage: the embedded
// registry (CatalogueMerge's immutable spine) could only ever be APPENDED to,
// so a shipped model could never be re-pinned or replaced without shipping a new
// APK. A row in this table can be upserted freely; the device re-assesses and
// rewrites `compatible` / `rank`; the app just reads.
//
// Contract-only, so it lives in Core with no storage dependency. The SQLite
// implementation (SqliteModelCatalog) and the assessor that fills the columns
// (IModelAssessor) live in CircleAI.Inference alongside the selector.

using System.Collections.Generic;

namespace CircleAI.Core.Models;

/// <summary>
/// The runtime model catalogue: every known model as a row, with a per-device
/// <c>compatible</c> bit and a <c>rank</c>. Selection reads it; a signed feed and
/// the on-device assessor write it.
/// </summary>
public interface IModelCatalog
{
    // ── Write ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Insert or replace the row for <paramref name="entry"/>, keyed on
    /// <see cref="ModelEntry.Name"/>. FREELY REPLACES — there is no spine: a feed
    /// may re-pin a shipped model to a new version, and the new facts win. The
    /// row lands UNASSESSED (<c>compatible = 0</c>, <c>rank = 0</c>,
    /// <c>assessed_at = null</c>) because a just-arrived model has not yet been
    /// judged against this device; an existing <c>installed</c> flag is preserved
    /// when the version is unchanged and cleared when it changes (a re-pin makes
    /// the on-disk copy stale). Call the assessor afterwards to fill the columns.
    /// </summary>
    void Upsert(ModelEntry entry);

    /// <summary>
    /// Write the assessment columns for the model <paramref name="id"/>: whether
    /// it can run on this device now, and its rank among the compatible. Stamps
    /// <c>assessed_at</c>. No-op when the id is unknown.
    /// </summary>
    void SetAssessment(string id, bool compatible, double rank);

    /// <summary>
    /// Refuse this model on THIS device, from evidence, with the reason a person
    /// can be told.
    /// </summary>
    /// <remarks>
    /// AN ASSESSMENT IS A PREDICTION; THIS IS A RESULT, AND THE TWO MUST NOT SHARE
    /// A COLUMN. <c>compatible</c> is recomputed from declared metadata every time
    /// the assessor runs, so a verdict written there is erased by the next bootstrap
    /// and the device forgets what it learned the hard way.
    ///
    /// Measured on a Circle OS device (Tensor G2, 7.6 GB) on 2026-10-04: a 22.8 GB
    /// MoE declaring 2.5 GB of RAM aborted the process with signal 6 and set
    /// lowmemorykiller on a dozen system apps. The declared number was wrong, so no
    /// amount of recomputing from it would ever refuse the model. Only the attempt
    /// knows.
    ///
    /// So a refusal is a separate, durable fact that the assessor READS and never
    /// writes, and it outranks any later prediction.
    /// </remarks>
    void Refuse(string id, string reason);

    /// <summary>Withdraw a refusal, because a person said so.</summary>
    /// <remarks>
    /// Nothing retries on its own - a native abort during load is not transient and
    /// each attempt costs a dozen system apps. This exists so the device's verdict
    /// is reversible by a human rather than permanent by accident.
    /// </remarks>
    void Pardon(string id);

    /// <summary>What this device decided about one model, and why.</summary>
    AssessedModel? Assessment(string id);

    /// <summary>Everything this device holds, with its verdict, best first.</summary>
    IReadOnlyList<AssessedModel> AllAssessed();

    /// <summary>
    /// Record whether the model's bundle is currently present on disk. No-op when
    /// the id is unknown.
    /// </summary>
    void SetInstalled(string id, bool installed);

    // ── Read ─────────────────────────────────────────────────────────────────

    /// <summary>The row for <paramref name="id"/>, or <c>null</c> when unknown.</summary>
    ModelEntry? Get(string id);

    /// <summary>
    /// Whether the model's bundle is currently on disk — the selector reads this
    /// to set <c>RequiresDownload</c>. <c>false</c> when the id is unknown.
    /// </summary>
    bool IsInstalled(string id);

    /// <summary>Every row, best rank first. For diagnostics and "what could run here" listings.</summary>
    IReadOnlyList<ModelEntry> All();

    /// <summary>
    /// Every <c>compatible = 1</c> row for the given modality, best <c>rank</c>
    /// first. The selector applies any per-request capability / quality-floor
    /// filter over this ordered set.
    /// </summary>
    IReadOnlyList<ModelEntry> Compatible(ModelModality modality);

    /// <summary>
    /// The single best compatible model for the modality — the catalogue's
    /// headline query, <c>WHERE compatible = 1 AND modality = ? ORDER BY rank
    /// DESC LIMIT 1</c>. <c>null</c> when nothing compatible is catalogued for it.
    /// </summary>
    ModelEntry? Best(ModelModality modality);

    /// <summary>Total rows in the catalogue.</summary>
    int Count();
}

/// <summary>One catalogued model plus what THIS device decided about it.</summary>
/// <param name="Entry">The catalogued facts, identical on every device.</param>
/// <param name="Compatible">Whether the assessor predicts it runs here.</param>
/// <param name="Rank">The assessor's score; higher wins.</param>
/// <param name="Installed">Whether the bytes are on this device.</param>
/// <param name="AssessedAt">When the prediction was last made, or null if never.</param>
/// <param name="RefusedReason">
/// Why this device refuses it from evidence, or null. Set by <see cref="IModelCatalog.Refuse"/>
/// and never by the assessor.
/// </param>
/// <remarks>
/// THREE COLUMNS WERE WRITE-ONLY AND NOTHING COULD ASK. compatible, rank and
/// assessed_at were written on every assessment and projected by nothing: the
/// private column list stopped at mmap_resident_gb, and Get/All/Compatible/Best all
/// read into a plain ModelEntry. So no screen could say WHY a model won, no
/// diagnostic could say the assessment was six months stale, and nothing could be
/// told to a person except the catalogued size.
/// </remarks>
public sealed record AssessedModel(
    ModelEntry Entry,
    bool Compatible,
    double Rank,
    bool Installed,
    DateTimeOffset? AssessedAt,
    string? RefusedReason)
{
    /// <summary>Refused here from evidence, whatever the metadata predicts.</summary>
    public bool Refused => !string.IsNullOrWhiteSpace(RefusedReason);

    /// <summary>Usable on this device right now: predicted to fit, and not refused.</summary>
    public bool Usable => Compatible && !Refused;
}
