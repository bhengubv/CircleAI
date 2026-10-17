// SqliteAskStore.cs
//
// THE ACCEPTED QUESTIONS, ON DISK BEFORE ANYBODY IS TOLD THEY WERE ACCEPTED.
//
// One file in the service's private storage (asks.db), one long-lived connection and a
// lock around every call - the same arrangement as the model catalogue, which has run
// that way on the P30 for weeks. WAL with synchronous=FULL: an ask the store returned is
// on disk, through a power cut, not merely in the page cache. The cost is an fsync per
// state change; the answer text is written through at most every few hundred
// milliseconds by the coordinator, so that is a handful per answer.
//
// THE SEQUENCE IS THE STORE'S, NOT THE CLOCK'S. Every write takes the next number, so
// a client asking "anything newer than N" can never miss a change that happened in the
// same millisecond as the one it saw.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CircleAI.Assistant.Lifecycle;
using Microsoft.Data.Sqlite;

namespace CircleAI.Assistant.Requests;

/// <summary>SQLite-backed <see cref="IAskStore"/>. Thread-safe; one writer process.</summary>
public sealed class SqliteAskStore : IAskStore
{
    private readonly object _gate = new();
    private readonly SqliteConnection _db;
    private long _seq;
    private bool _disposed;

    private const string Columns =
        "id, caller_key, client_request_id, session_id, lane, message, question, spoken, agentic, side_effect_free, "
        + "state, seq, text, kind, sentence, needs_permission, model_id, attempts, created_ms, updated_ms, started_ms, finished_ms, "
        + "t_queue, t_wait, t_enrich, t_prefill, t_ptok, t_decode, t_otok, t_ttft, t_total, t_model, t_kv, t_finish";

    /// <summary>Opens (creating when needed) the store at <paramref name="databasePath"/>.</summary>
    public SqliteAskStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _db = new SqliteConnection("Data Source=" + databasePath);
        _db.Open();
        Exec("PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;");
        Exec(@"CREATE TABLE IF NOT EXISTS asks(
                 id TEXT PRIMARY KEY, caller_key TEXT NOT NULL, client_request_id TEXT, session_id TEXT NOT NULL,
                 lane INTEGER NOT NULL, message TEXT NOT NULL, question TEXT, spoken INTEGER NOT NULL,
                 agentic INTEGER NOT NULL, side_effect_free INTEGER NOT NULL, state INTEGER NOT NULL,
                 seq INTEGER NOT NULL, text TEXT NOT NULL, kind INTEGER, sentence TEXT, needs_permission TEXT,
                 model_id TEXT, attempts INTEGER NOT NULL, created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL,
                 started_ms INTEGER, finished_ms INTEGER,
                 t_queue INTEGER, t_wait INTEGER, t_enrich INTEGER, t_prefill INTEGER, t_ptok INTEGER,
                 t_decode INTEGER, t_otok INTEGER, t_ttft INTEGER, t_total INTEGER, t_model TEXT, t_kv TEXT, t_finish TEXT);
               CREATE UNIQUE INDEX IF NOT EXISTS ix_asks_rid ON asks(caller_key, client_request_id) WHERE client_request_id IS NOT NULL;
               CREATE INDEX IF NOT EXISTS ix_asks_caller ON asks(caller_key, created_ms);
               CREATE INDEX IF NOT EXISTS ix_asks_state ON asks(state);
               CREATE INDEX IF NOT EXISTS ix_asks_seq ON asks(seq);");

        using var max = _db.CreateCommand();
        max.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM asks;";
        _seq = Convert.ToInt64(max.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public long LastSeq { get { lock (_gate) return _seq; } }

    /// <inheritdoc />
    public AskRecord? Get(string id)
    {
        lock (_gate) return One("SELECT " + Columns + " FROM asks WHERE id = $a;", id);
    }

    /// <inheritdoc />
    public AskRecord? FindByClientRequest(string callerKey, string clientRequestId)
    {
        lock (_gate) return One("SELECT " + Columns + " FROM asks WHERE caller_key = $a AND client_request_id = $b;",
                                callerKey, clientRequestId);
    }

    /// <inheritdoc />
    public AskRecord Insert(AskRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (record.ClientRequestId is { } rid
                && One("SELECT " + Columns + " FROM asks WHERE caller_key = $a AND client_request_id = $b;",
                       record.CallerKey, rid) is { } existing)
                return existing;

            var stored = record with { Seq = ++_seq };
            Write("INSERT INTO asks(" + Columns + ") VALUES(" + Placeholders() + ");", stored);
            return stored;
        }
    }

    /// <inheritdoc />
    public AskRecord? Update(string id, Func<AskRecord, AskRecord> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            ThrowIfDisposed();
            var current = One("SELECT " + Columns + " FROM asks WHERE id = $a;", id);
            if (current is null) return null;

            var next = change(current);
            if (ReferenceEquals(next, current) || next == current) return current;

            // A FINAL ASK IS NEVER CHANGED BACK. A late write from a generation that
            // was cancelled, or a recovery racing a completion, must not resurrect an
            // answer somebody has already been given.
            if (current.IsFinal && next.State != current.State) return current;

            var stored = next with { Id = current.Id, Seq = ++_seq };
            Write("UPDATE asks SET " + Assignments() + " WHERE id = $id;", stored);
            return stored;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AskRecord> List(string? callerKey, int limit, bool openOnly = false)
    {
        var open = openOnly ? " AND state < " + (int)AskState.Succeeded : string.Empty;
        lock (_gate)
            return callerKey is null
                ? Many("SELECT " + Columns + " FROM asks WHERE 1=1" + open + " ORDER BY created_ms DESC, seq DESC LIMIT $n;", Math.Max(1, limit))
                : Many("SELECT " + Columns + " FROM asks WHERE caller_key = $a" + open + " ORDER BY created_ms DESC, seq DESC LIMIT $n;", Math.Max(1, limit), callerKey);
    }

    /// <inheritdoc />
    public IReadOnlyList<AskRecord> Open()
    {
        lock (_gate)
            return Many("SELECT " + Columns + " FROM asks WHERE state < " + (int)AskState.Succeeded
                        + " ORDER BY created_ms ASC, seq ASC LIMIT $n;", 10_000);
    }

    /// <inheritdoc />
    public IReadOnlyList<AskRecord> ChangedSince(long seq, int limit)
    {
        lock (_gate)
            return Many("SELECT " + Columns + " FROM asks WHERE seq > $a ORDER BY seq ASC LIMIT $n;", Math.Max(1, limit), seq);
    }

    /// <inheritdoc />
    public int Purge(DateTimeOffset finishedBefore)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM asks WHERE state >= " + (int)AskState.Succeeded
                              + " AND COALESCE(finished_ms, updated_ms) < $t;";
            cmd.Parameters.AddWithValue("$t", finishedBefore.ToUnixTimeMilliseconds());
            return cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public int Forget(string callerKey)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM asks WHERE caller_key = $a;";
            cmd.Parameters.AddWithValue("$a", callerKey);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _db.Dispose();
        }
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static readonly string[] Names = Columns.Split(',', StringSplitOptions.TrimEntries);

    private static string Placeholders() => string.Join(", ", Array.ConvertAll(Names, n => "$" + n));

    private static string Assignments() =>
        string.Join(", ", Array.FindAll(Names, n => n != "id").Select(n => n + " = $" + n));

    private void Write(string sql, AskRecord r)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        var t = r.Timing;
        void P(string name, object? value) => cmd.Parameters.AddWithValue("$" + name, value ?? DBNull.Value);
        P("id", r.Id); P("caller_key", r.CallerKey); P("client_request_id", r.ClientRequestId);
        P("session_id", r.SessionId ?? string.Empty); P("lane", (int)r.Lane); P("message", r.Message);
        P("question", r.Question); P("spoken", r.Spoken ? 1 : 0); P("agentic", r.Agentic ? 1 : 0);
        P("side_effect_free", r.SideEffectFree ? 1 : 0); P("state", (int)r.State); P("seq", r.Seq);
        P("text", r.Text ?? string.Empty); P("kind", r.Kind is { } k ? (int)k : null); P("sentence", r.Sentence);
        P("needs_permission", r.NeedsPermission); P("model_id", r.ModelId); P("attempts", r.Attempts);
        P("created_ms", r.CreatedAt.ToUnixTimeMilliseconds()); P("updated_ms", r.UpdatedAt.ToUnixTimeMilliseconds());
        P("started_ms", r.StartedAt?.ToUnixTimeMilliseconds()); P("finished_ms", r.FinishedAt?.ToUnixTimeMilliseconds());
        P("t_queue", t?.QueueMs); P("t_wait", t?.LoadWaitMs); P("t_enrich", t?.EnrichMs); P("t_prefill", t?.PrefillMs);
        P("t_ptok", t?.PromptTokens); P("t_decode", t?.DecodeMs); P("t_otok", t?.OutputTokens); P("t_ttft", t?.TtftMs);
        P("t_total", t?.TotalMs); P("t_model", t?.ModelId); P("t_kv", t?.KvPath); P("t_finish", t?.Finish);
        cmd.ExecuteNonQuery();
    }

    private AskRecord? One(string sql, params object[] args)
    {
        var list = Query(sql, 1, args);
        return list.Count > 0 ? list[0] : null;
    }

    private List<AskRecord> Many(string sql, int limit, params object[] args) => Query(sql, limit, args);

    private List<AskRecord> Query(string sql, int limit, object[] args)
    {
        ThrowIfDisposed();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        if (args.Length > 0) cmd.Parameters.AddWithValue("$a", args[0]);
        if (args.Length > 1) cmd.Parameters.AddWithValue("$b", args[1]);
        if (sql.Contains("$n", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("$n", limit);

        var list = new List<AskRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(Map(reader));
        return list;
    }

    private static AskRecord Map(SqliteDataReader r)
    {
        static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        static long? L(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        static DateTimeOffset? T(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(i));

        TurnTiming? timing = null;
        if (!r.IsDBNull(30) || !r.IsDBNull(33))
            timing = new TurnTiming(
                QueueMs: L(r, 22) ?? -1, LoadWaitMs: L(r, 23) ?? -1, EnrichMs: L(r, 24) ?? -1,
                PrefillMs: L(r, 25) ?? -1, PromptTokens: (int)(L(r, 26) ?? -1), DecodeMs: L(r, 27) ?? -1,
                OutputTokens: (int)(L(r, 28) ?? -1), TtftMs: L(r, 29) ?? -1, TotalMs: L(r, 30) ?? -1,
                ModelId: S(r, 31), KvPath: S(r, 32), Finish: S(r, 33));

        return new AskRecord(
            Id: r.GetString(0), CallerKey: r.GetString(1), ClientRequestId: S(r, 2), SessionId: r.GetString(3),
            Lane: (AskLane)r.GetInt32(4), Message: r.GetString(5), Question: S(r, 6),
            Spoken: r.GetInt32(7) != 0, Agentic: r.GetInt32(8) != 0, SideEffectFree: r.GetInt32(9) != 0,
            State: (AskState)r.GetInt32(10), Seq: r.GetInt64(11), Text: r.GetString(12),
            Kind: r.IsDBNull(13) ? null : (FailureKind)r.GetInt32(13), Sentence: S(r, 14), NeedsPermission: S(r, 15),
            ModelId: S(r, 16), Attempts: r.GetInt32(17),
            CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(18)),
            UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(19)),
            StartedAt: T(r, 20), FinishedAt: T(r, 21), Timing: timing);
    }
}
