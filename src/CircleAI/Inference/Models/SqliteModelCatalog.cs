// SqliteModelCatalog.cs
//
// The runtime model catalogue as ONE SQLite table — the single source of truth
// for model selection (see IModelCatalog). One row per model; two computed
// columns, `compatible` and `rank`, decide selection. Mirrors the storage shape
// of SqliteEpisodicStore: a single long-lived connection (required for
// "Data Source=:memory:", cheaper for file databases), parameterised commands,
// dispose closes the connection.
//
// THE ANTI-HOSTAGE PROPERTY lives here: Upsert is INSERT OR REPLACE keyed on the
// model id, so a signed feed can re-pin or replace a shipped model to a newer
// version and Best() returns the new one after re-assessment — no app release,
// no immutable spine. CatalogueMerge's append-only merge is what this replaces.

using System;
using System.Collections.Generic;
using System.Text.Json;
using CircleAI.Core;
using CircleAI.Core.Models;
using Microsoft.Data.Sqlite;

namespace CircleAI.Inference;

/// <summary>
/// SQLite-backed <see cref="IModelCatalog"/>. Pass
/// <c>"Data Source=:memory:"</c> for an in-process test instance, or
/// <c>"Data Source=models.db"</c> for the on-device catalogue. Dispose to close
/// the connection.
/// </summary>
public sealed class SqliteModelCatalog : IModelCatalog, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions Json = new();

    /// <param name="connectionString">SQLite connection string, e.g. <c>"Data Source=models.db"</c>.</param>
    /// <param name="clock">Clock for the <c>assessed_at</c> stamp; defaults to <see cref="TimeProvider.System"/>.</param>
    public SqliteModelCatalog(string connectionString, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required.", nameof(connectionString));

        _clock = clock ?? TimeProvider.System;
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        UseWriteAheadLog();
        EnsureSchema();
    }

    /// <summary>Write-ahead log, synced at checkpoints rather than on every write.</summary>
    /// <remarks>
    /// SIXTEEN SECONDS OF STARTUP WAS DISK SYNCS. Every write here is its own
    /// transaction, and in SQLite's default rollback-journal mode each one is a
    /// journal file created, synced and deleted. Startup makes about 180 of them:
    /// a refresh of every row from the registry, then an assessment of every row.
    /// Measured on the P30 on 2026-10-16: the refresh landed 9 s after the service
    /// started and the assessment at 16 s. The brain had chosen its model at 1 s,
    /// from ranks the refresh was about to correct.
    /// <para>
    /// NORMAL SYNC IN WAL MODE STILL SURVIVES A PROCESS CRASH, and that is the
    /// durability this table depends on. A refusal or a strike is written because
    /// the process may die in the next instant, and a commit in WAL mode is in the
    /// OS's hands as soon as the write returns. What NORMAL gives up is the last
    /// commits on a power cut, and every launch re-derives this table anyway.
    /// </para>
    /// <para>
    /// Best-effort: an in-memory database answers "memory" and stays as it is,
    /// which is the right outcome for the tests.
    /// </para>
    /// </remarks>
    private void UseWriteAheadLog()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            cmd.ExecuteNonQuery();
        }
        catch { /* a catalogue in the default journal mode is slower, not wrong */ }
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS models (
                id                 TEXT PRIMARY KEY NOT NULL,
                version            TEXT NOT NULL,
                quantization       TEXT NOT NULL,
                url                TEXT,
                checksum           TEXT,
                repo               TEXT,
                source             INTEGER NOT NULL,
                modality           INTEGER NOT NULL,
                engine             INTEGER NOT NULL,
                total_bytes        INTEGER NOT NULL,
                bundle_files       TEXT,
                min_ram_gb         REAL NOT NULL,
                min_storage_gb     REAL NOT NULL,
                min_vram_gb        REAL,
                capabilities       TEXT,
                quality_rank       INTEGER NOT NULL,
                fallback_model_id  TEXT,
                memory_hint_bytes  INTEGER NOT NULL,
                architecture       TEXT,
                language           TEXT,
                license            TEXT,
                -- Resident RAM when weights are mmap'd, AS MEASURED on a device.
                -- Nullable and normally null: unmeasured is the honest default, and
                -- a model without one gets no paging discount at all.
                mmap_resident_gb   REAL,
                compatible         INTEGER NOT NULL DEFAULT 0,
                rank               REAL NOT NULL DEFAULT 0,
                installed          INTEGER NOT NULL DEFAULT 0,
                assessed_at        TEXT,
                -- WHY THIS DEVICE REFUSES IT, from an attempt rather than a
                -- prediction. Written only by Refuse(); the assessor reads it and
                -- never touches it, because `compatible` is recomputed on every
                -- bootstrap and a verdict stored there is forgotten by the next one.
                refused_reason     TEXT,
                refused_at         TEXT,
                -- HOW MANY TIMES IN A ROW THE PROCESS DIED WHILE LOADING IT. A death
                -- is evidence, not a verdict: see CrashVerdict for why one is not
                -- enough to refuse a model on a phone the OS kills for memory.
                load_strikes       INTEGER NOT NULL DEFAULT 0,
                -- A REGISTRY FACT: this LLM is below the product's floor ("nothing
                -- less than Qwen3.5 is the LLM") and is never selected as the brain.
                retired            INTEGER NOT NULL DEFAULT 0,
                -- WHEN A LOAD LAST COMPLETED ON THIS DEVICE. Proof the model runs
                -- here, which no prediction is. Housekeeping will not remove a model
                -- in favour of one that has never loaded.
                loaded_at          TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_models_pick
                ON models (modality, compatible, rank DESC);
            """;
        cmd.ExecuteNonQuery();

        // AND THE SAME TABLE ON A DEVICE THAT PREDATES THOSE TWO COLUMNS.
        // CREATE TABLE IF NOT EXISTS is a no-op on an existing table, so a column
        // added later never arrives and every query naming it fails - on the phone,
        // not here. There was no migration step at all before this.
        AddColumnIfMissing("refused_reason", "TEXT");
        AddColumnIfMissing("refused_at", "TEXT");
        AddColumnIfMissing("load_strikes", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("retired", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("loaded_at", "TEXT");
    }

    /// <summary>Adds a column to <c>models</c> when it is not there yet.</summary>
    /// <remarks>
    /// Best-effort by design: a duplicate-column error means another process won the
    /// race and the column exists, which is the outcome we wanted.
    /// </remarks>
    private void AddColumnIfMissing(string name, string type)
    {
        try
        {
            using var check = _conn.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('models') WHERE name = $n;";
            check.Parameters.AddWithValue("$n", name);
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;

            using var add = _conn.CreateCommand();
            add.CommandText = $"ALTER TABLE models ADD COLUMN {name} {type};";
            add.ExecuteNonQuery();
        }
        catch { /* already there, or added underneath us */ }
    }

    // The full column list, in one place so read/write cannot drift.
    private const string Columns =
        "id, version, quantization, url, checksum, repo, source, modality, engine, " +
        "total_bytes, bundle_files, min_ram_gb, min_storage_gb, min_vram_gb, " +
        "capabilities, quality_rank, fallback_model_id, memory_hint_bytes, " +
        "architecture, language, license, mmap_resident_gb, " +   // appended: keeps ordinals stable
        "retired";                                                   // 22

    // WHAT THIS DEVICE DECIDED, which nothing could read before. Selected AFTER
    // Columns, so the entry ordinals 0-22 that MapEntry reads are untouched; these
    // start at 23 (ReadAssessed). Appending an entry column moves them by one.
    private const string Verdict = "compatible, rank, installed, assessed_at, refused_reason, loaded_at";

    // ── Write ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Upsert(ModelEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Name))
            throw new ArgumentException("ModelEntry.Name (the catalogue id) is required.", nameof(entry));

        lock (_gate)
        {
            ThrowIfDisposed();

            // A re-pin makes any on-disk copy stale, so `installed` only survives
            // when the version is unchanged. Read the prior row's facts first.
            var (hadRow, priorVersion, priorInstalled) = ReadInstalledState(entry.Name);
            var installed = hadRow && string.Equals(priorVersion, entry.Version, StringComparison.Ordinal)
                ? priorInstalled
                : false;

            using var cmd = _conn.CreateCommand();
            // A fresh row is UNASSESSED — compatible/rank/assessed_at are left at
            // their defaults so a just-arrived model is never offered before the
            // assessor has judged it against this device.
            cmd.CommandText = $"""
                INSERT OR REPLACE INTO models
                    ({Columns}, compatible, rank, installed, assessed_at,
                     refused_reason, refused_at)
                VALUES
                    ($id, $version, $quantization, $url, $checksum, $repo, $source, $modality, $engine,
                     $totalBytes, $bundleFiles, $minRamGb, $minStorageGb, $minVramGb,
                     $capabilities, $qualityRank, $fallbackModelId, $memoryHintBytes,
                     $architecture, $language, $license, $mmapResidentGb,
                     $retired,
                     0, 0, $installed, NULL, NULL, NULL);
                """;

            BindEntry(cmd, entry);
            cmd.Parameters.AddWithValue("$installed", installed ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public bool ReSync(ModelEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Name)) return false;

        lock (_gate)
        {
            ThrowIfDisposed();

            var (hadRow, priorVersion, priorInstalled) = ReadInstalledState(entry.Name);
            if (!hadRow) return false;

            // Same rule as Upsert: a re-pin makes any copy on disk stale.
            var installed = string.Equals(priorVersion, entry.Version, StringComparison.Ordinal)
                && priorInstalled;

            using var cmd = _conn.CreateCommand();
            // EVERY REGISTRY COLUMN, AND NOT ONE VERDICT COLUMN. compatible, rank,
            // assessed_at, refused_reason and refused_at are what THIS DEVICE
            // decided, and refreshing what the registry says about a model is no
            // reason to forget them. BindEntry binds the same parameters Upsert
            // uses, so the two cannot drift apart.
            cmd.CommandText = """
                UPDATE models SET
                    version = $version, quantization = $quantization, url = $url,
                    checksum = $checksum, repo = $repo, source = $source,
                    modality = $modality, engine = $engine, total_bytes = $totalBytes,
                    bundle_files = $bundleFiles, min_ram_gb = $minRamGb,
                    min_storage_gb = $minStorageGb, min_vram_gb = $minVramGb,
                    capabilities = $capabilities, quality_rank = $qualityRank,
                    fallback_model_id = $fallbackModelId,
                    memory_hint_bytes = $memoryHintBytes, architecture = $architecture,
                    language = $language, license = $license,
                    mmap_resident_gb = $mmapResidentGb, retired = $retired,
                    installed = $installed,
                    -- A proof of loading belongs to the version that loaded. SET
                    -- expressions read the OLD row, so this compares old to new.
                    loaded_at = CASE WHEN version = $version THEN loaded_at ELSE NULL END
                WHERE id = $id;
                """;

            BindEntry(cmd, entry);
            cmd.Parameters.AddWithValue("$installed", installed ? 1 : 0);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <inheritdoc />
    public void SetAssessment(string id, bool compatible, double rank)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE models
                SET compatible = $compatible, rank = $rank, assessed_at = $assessedAt
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$compatible", compatible ? 1 : 0);
            cmd.Parameters.AddWithValue("$rank", rank);
            cmd.Parameters.AddWithValue("$assessedAt", _clock.GetUtcNow().ToString("O"));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public void SetInstalled(string id, bool installed)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE models SET installed = $installed WHERE id = $id;";
            cmd.Parameters.AddWithValue("$installed", installed ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public void Refuse(string id, string reason)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            // NOT compatible = 0, deliberately. The assessor rewrites that column on
            // every bootstrap from the declared metadata - which is the thing that
            // was wrong - so a verdict put there survives until the next launch and
            // no longer. This column is the assessor's input, never its output.
            cmd.CommandText = """
                UPDATE models
                SET refused_reason = $reason, refused_at = $at
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$reason",
                string.IsNullOrWhiteSpace(reason) ? "refused on this device" : reason);
            cmd.Parameters.AddWithValue("$at", _clock.GetUtcNow().ToString("O"));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public int Strike(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return 0;
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE models SET load_strikes = load_strikes + 1 WHERE id = $id;
                SELECT load_strikes FROM models WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            var n = cmd.ExecuteScalar();
            return n is null or DBNull ? 0 : Convert.ToInt32(n, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <inheritdoc />
    public void RecordLoad(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE models SET load_strikes = 0, loaded_at = $at WHERE id = $id;";
            cmd.Parameters.AddWithValue("$at", _clock.GetUtcNow().ToString("O"));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public int ForgiveFirstStrikes(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return 0;
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            // ONLY REFUSALS THE OLD RULE MADE, and they are identified exactly: the
            // old reason AND no strikes recorded, because the column did not exist
            // when they were written. A refusal the two-strike rule makes always has
            // load_strikes >= 2, so it can never be caught by this.
            cmd.CommandText = """
                UPDATE models
                SET refused_reason = NULL, refused_at = NULL, load_strikes = 1
                WHERE refused_reason = $reason AND load_strikes = 0;
                """;
            cmd.Parameters.AddWithValue("$reason", reason);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public void Pardon(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                // A PARDON IS A FRESH START, so the strikes go with the verdict. Left
                // behind, one more death would refuse a model a person had just
                // chosen to give another chance.
                "UPDATE models SET refused_reason = NULL, refused_at = NULL, load_strikes = 0 WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    // ── Read ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public AssessedModel? Assessment(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {Columns}, {Verdict} FROM models WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            var rows = ReadAssessed(cmd);
            return rows.Count > 0 ? rows[0] : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AssessedModel> AllAssessed()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                $"SELECT {Columns}, {Verdict} FROM models ORDER BY rank DESC, quality_rank DESC, id ASC;";
            return ReadAssessed(cmd);
        }
    }


    /// <inheritdoc />
    public ModelEntry? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM models WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            var rows = ReadEntries(cmd);
            return rows.Count > 0 ? rows[0] : null;
        }
    }

    /// <inheritdoc />
    public bool IsInstalled(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT installed FROM models WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            var result = cmd.ExecuteScalar();
            return result is not null and not DBNull && (long)result != 0;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelEntry> All()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM models ORDER BY rank DESC, quality_rank DESC, id ASC;";
            return ReadEntries(cmd);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelEntry> Compatible(ModelModality modality)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            // NOT RETIRED: the owner's floor ("nothing less than Qwen3.5 is the LLM")
            // is enforced here, where every catalogue selection reads, rather than in
            // each caller that might forget it.
            cmd.CommandText = $"""
                SELECT {Columns} FROM models
                WHERE compatible = 1 AND modality = $modality AND refused_reason IS NULL
                  AND retired = 0
                ORDER BY rank DESC, min_ram_gb ASC, id ASC;
                """;
            cmd.Parameters.AddWithValue("$modality", (int)modality);
            return ReadEntries(cmd);
        }
    }

    /// <inheritdoc />
    public ModelEntry? Best(ModelModality modality)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT {Columns} FROM models
                WHERE compatible = 1 AND modality = $modality AND refused_reason IS NULL
                  AND retired = 0
                ORDER BY rank DESC, min_ram_gb ASC, id ASC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$modality", (int)modality);
            var rows = ReadEntries(cmd);
            return rows.Count > 0 ? rows[0] : null;
        }
    }

    /// <inheritdoc />
    public int Count()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM models;";
            return (int)(long)cmd.ExecuteScalar()!;
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    /// <summary>Closes the underlying SQLite connection.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _conn.Dispose();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SqliteModelCatalog));
    }

    private (bool HadRow, string? Version, bool Installed) ReadInstalledState(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT version, installed FROM models WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (false, null, false);
        return (true, reader.GetString(0), reader.GetInt64(1) != 0);
    }

    private static void BindEntry(SqliteCommand cmd, ModelEntry e)
    {
        cmd.Parameters.AddWithValue("$id", e.Name);
        cmd.Parameters.AddWithValue("$version", e.Version);
        cmd.Parameters.AddWithValue("$quantization", e.Quantization);
        cmd.Parameters.AddWithValue("$url", (object?)e.Url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$checksum", (object?)e.Checksum ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$repo", (object?)e.Repo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", (int)e.Source);
        cmd.Parameters.AddWithValue("$modality", (int)e.Modality);
        cmd.Parameters.AddWithValue("$engine", (int)e.Engine);
        cmd.Parameters.AddWithValue("$totalBytes", e.TotalBytes);
        cmd.Parameters.AddWithValue("$bundleFiles",
            e.BundleFiles is { Count: > 0 } ? JsonSerializer.Serialize(e.BundleFiles, Json) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$minRamGb", e.MinRamGb);
        cmd.Parameters.AddWithValue("$minStorageGb", e.MinStorageGb);
        cmd.Parameters.AddWithValue("$minVramGb", (object?)e.MinVramGb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$capabilities",
            e.Capabilities is { Count: > 0 } ? JsonSerializer.Serialize(e.Capabilities, Json) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$qualityRank", e.QualityRank);
        cmd.Parameters.AddWithValue("$fallbackModelId", (object?)e.FallbackModelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$memoryHintBytes", e.MemoryHintBytes);
        cmd.Parameters.AddWithValue("$architecture", (object?)e.Architecture ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$language", (object?)e.Language ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$license", (object?)e.License ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mmapResidentGb", (object?)e.MmapResidentGb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$retired", e.Retired ? 1 : 0);
    }

    // Column order MUST match `Columns`.
    /// <summary>Reads rows selected as <c>{Columns}, {Verdict}</c>.</summary>
    private static List<AssessedModel> ReadAssessed(SqliteCommand cmd)
    {
        var results = new List<AssessedModel>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            results.Add(new AssessedModel(
                Entry:         MapEntry(r),
                Compatible:    !r.IsDBNull(23) && r.GetInt64(23) != 0,
                Rank:          r.IsDBNull(24) ? 0 : r.GetDouble(24),
                Installed:     !r.IsDBNull(25) && r.GetInt64(25) != 0,
                AssessedAt:    r.IsDBNull(26) ? null : ReadStamp(r.GetString(26)),
                RefusedReason: r.IsDBNull(27) ? null : r.GetString(27),
                LoadedAt:      r.IsDBNull(28) ? null : ReadStamp(r.GetString(28))));
        return results;
    }

    /// <summary>A round-trip stamp, or null when it is not one.</summary>
    private static DateTimeOffset? ReadStamp(string text)
        => DateTimeOffset.TryParse(text,
               System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.RoundtripKind, out var at)
           ? at : null;

    private static List<ModelEntry> ReadEntries(SqliteCommand cmd)
    {
        var results = new List<ModelEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(MapEntry(reader));
        return results;
    }

    /// <summary>Ordinals 0-22, the {Columns} list, mapped once.</summary>
    /// <remarks>
    /// WRITTEN TWICE IS WRITTEN WRONG. Twenty-two ordinals read by hand is exactly
    /// the shape that drifts when a column is appended, and a second copy in the
    /// assessed reader would have had to be kept in step by somebody noticing.
    /// </remarks>
    private static ModelEntry MapEntry(SqliteDataReader reader)
    {
        var bundleFiles = reader.IsDBNull(10)
            ? null
            : JsonSerializer.Deserialize<List<BundleFile>>(reader.GetString(10), Json);
        var capabilities = reader.IsDBNull(14)
            ? null
            : JsonSerializer.Deserialize<List<string>>(reader.GetString(14), Json);

        return new ModelEntry(
            Name:         reader.GetString(0),
            Version:      reader.GetString(1),
            Quantization: reader.GetString(2),
            Url:          reader.IsDBNull(3) ? null : reader.GetString(3),
            Checksum:     reader.IsDBNull(4) ? null : reader.GetString(4))
        {
            Repo            = reader.IsDBNull(5) ? null : reader.GetString(5),
            Source          = (ModelSource)reader.GetInt64(6),
            Modality        = (ModelModality)reader.GetInt64(7),
            Engine          = (ModelEngine)reader.GetInt64(8),
            TotalBytes      = reader.GetInt64(9),
            BundleFiles     = bundleFiles,
            MinRamGb        = reader.GetDouble(11),
            MinStorageGb    = reader.GetDouble(12),
            MinVramGb       = reader.IsDBNull(13) ? null : reader.GetDouble(13),
            Capabilities    = capabilities,
            QualityRank     = (int)reader.GetInt64(15),
            FallbackModelId = reader.IsDBNull(16) ? null : reader.GetString(16),
            MemoryHintBytes = reader.GetInt64(17),
            Architecture    = reader.IsDBNull(18) ? null : reader.GetString(18),
            Language        = reader.IsDBNull(19) ? null : reader.GetString(19),
            License         = reader.IsDBNull(20) ? null : reader.GetString(20),
            MmapResidentGb  = reader.IsDBNull(21) ? null : reader.GetDouble(21),
            Retired         = !reader.IsDBNull(22) && reader.GetInt64(22) != 0,
        };
    }
}
