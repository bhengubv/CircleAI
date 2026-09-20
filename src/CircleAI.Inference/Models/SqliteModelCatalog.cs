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
        EnsureSchema();
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
                compatible         INTEGER NOT NULL DEFAULT 0,
                rank               REAL NOT NULL DEFAULT 0,
                installed          INTEGER NOT NULL DEFAULT 0,
                assessed_at        TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_models_pick
                ON models (modality, compatible, rank DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    // The full column list, in one place so read/write cannot drift.
    private const string Columns =
        "id, version, quantization, url, checksum, repo, source, modality, engine, " +
        "total_bytes, bundle_files, min_ram_gb, min_storage_gb, min_vram_gb, " +
        "capabilities, quality_rank, fallback_model_id, memory_hint_bytes, " +
        "architecture, language, license";

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
                    ({Columns}, compatible, rank, installed, assessed_at)
                VALUES
                    ($id, $version, $quantization, $url, $checksum, $repo, $source, $modality, $engine,
                     $totalBytes, $bundleFiles, $minRamGb, $minStorageGb, $minVramGb,
                     $capabilities, $qualityRank, $fallbackModelId, $memoryHintBytes,
                     $architecture, $language, $license,
                     0, 0, $installed, NULL);
                """;

            BindEntry(cmd, entry);
            cmd.Parameters.AddWithValue("$installed", installed ? 1 : 0);
            cmd.ExecuteNonQuery();
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

    // ── Read ───────────────────────────────────────────────────────────────

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
            cmd.CommandText = $"""
                SELECT {Columns} FROM models
                WHERE compatible = 1 AND modality = $modality
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
                WHERE compatible = 1 AND modality = $modality
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
    }

    // Column order MUST match `Columns`.
    private static List<ModelEntry> ReadEntries(SqliteCommand cmd)
    {
        var results = new List<ModelEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var bundleFiles = reader.IsDBNull(10)
                ? null
                : JsonSerializer.Deserialize<List<BundleFile>>(reader.GetString(10), Json);
            var capabilities = reader.IsDBNull(14)
                ? null
                : JsonSerializer.Deserialize<List<string>>(reader.GetString(14), Json);

            results.Add(new ModelEntry(
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
            });
        }
        return results;
    }
}
