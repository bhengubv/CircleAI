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
