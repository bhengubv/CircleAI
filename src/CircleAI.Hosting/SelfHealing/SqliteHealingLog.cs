// SqliteHealingLog.cs
//
// IHealingLog persisted to SQLite — the durable "no error is ever lost" store the
// Wolverine dashboard reads. One long-lived connection (as SqliteEpisodicStore does,
// which is required for :memory: test instances and cheaper for a file). Append-only
// but for the one handled flag a person can set.
//
// Lives here, in the desktop-buildable product, rather than an Android-only head, so
// the round-trip is exercised in a plain xUnit test with "Data Source=:memory:".

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace CircleAI.Hosting.SelfHealing;

/// <summary>SQLite-backed <see cref="IHealingLog"/>. Pass <c>"Data Source=:memory:"</c> for tests.</summary>
public sealed class SqliteHealingLog : IHealingLog, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();   // one connection; serialise access
    private bool _disposed;

    /// <param name="connectionString">e.g. <c>"Data Source=healing.db"</c>.</param>
    public SqliteHealingLog(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required.", nameof(connectionString));

        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS healing (
                id             TEXT PRIMARY KEY NOT NULL,
                at_utc         TEXT NOT NULL,
                signature      TEXT NOT NULL,
                source         TEXT NOT NULL,
                message        TEXT NOT NULL,
                kind           TEXT NOT NULL,
                category       TEXT NOT NULL,
                summary        TEXT NOT NULL,
                recommended    TEXT,
                action_taken   TEXT NOT NULL,
                outcome        TEXT NOT NULL,
                needs_human    INTEGER NOT NULL,
                handled        INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_healing_at ON healing (at_utc DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public Task AppendAsync(HealingRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO healing
                    (id, at_utc, signature, source, message, kind, category, summary,
                     recommended, action_taken, outcome, needs_human, handled)
                VALUES
                    ($id, $at, $sig, $src, $msg, $kind, $cat, $sum,
                     $rec, $act, $out, $needs, $handled);
                """;
            cmd.Parameters.AddWithValue("$id", record.Id);
            cmd.Parameters.AddWithValue("$at", record.At.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$sig", record.Signature);
            cmd.Parameters.AddWithValue("$src", record.Source);
            cmd.Parameters.AddWithValue("$msg", record.Message);
            cmd.Parameters.AddWithValue("$kind", record.Kind);
            cmd.Parameters.AddWithValue("$cat", record.Category);
            cmd.Parameters.AddWithValue("$sum", record.Summary);
            cmd.Parameters.AddWithValue("$rec", (object?)record.RecommendedAction ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$act", record.ActionTaken);
            cmd.Parameters.AddWithValue("$out", record.Outcome.ToString());
            cmd.Parameters.AddWithValue("$needs", record.NeedsHuman ? 1 : 0);
            cmd.Parameters.AddWithValue("$handled", record.Handled ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HealingRecord>> RecentAsync(int limit = 100, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, at_utc, signature, source, message, kind, category, summary,
                       recommended, action_taken, outcome, needs_human, handled
                FROM   healing
                ORDER  BY at_utc DESC
                LIMIT  $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            return Task.FromResult<IReadOnlyList<HealingRecord>>(Read(cmd));
        }
    }

    /// <inheritdoc />
    public Task MarkHandledAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE healing SET handled = 1, needs_human = 0 WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> CountSinceAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM healing WHERE at_utc >= $since;";
            cmd.Parameters.AddWithValue("$since", since.ToString("O", CultureInfo.InvariantCulture));
            var count = (long)cmd.ExecuteScalar()!;
            return Task.FromResult((int)count);
        }
    }

    private static List<HealingRecord> Read(SqliteCommand cmd)
    {
        var results = new List<HealingRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new HealingRecord(
                Id: reader.GetString(0),
                At: DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Signature: reader.GetString(2),
                Source: reader.GetString(3),
                Message: reader.GetString(4),
                Kind: reader.GetString(5),
                Category: reader.GetString(6),
                Summary: reader.GetString(7),
                RecommendedAction: reader.IsDBNull(8) ? null : reader.GetString(8),
                ActionTaken: reader.GetString(9),
                Outcome: Enum.TryParse<HealingOutcome>(reader.GetString(10), out var o) ? o : HealingOutcome.Recorded,
                NeedsHuman: reader.GetInt64(11) != 0,
                Handled: reader.GetInt64(12) != 0));
        }
        return results;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SqliteHealingLog));
    }

    /// <summary>Closes the underlying connection.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _conn.Dispose();
        }
    }
}
