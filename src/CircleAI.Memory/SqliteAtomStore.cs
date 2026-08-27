// SqliteAtomStore.cs
//
// IAtomStore on SQLite. The mobile-first implementation, and the default
// everywhere else.
//
// FTS5, AND A FALLBACK THAT IS NOT AN EXCUSE. Keyword search is what makes
// this work with no embedding model, so it is the primary mechanism rather
// than a degraded mode. FTS5 ships inside the SQLite that Microsoft.Data.Sqlite
// bundles - but a phone is not a place to assume a build flag, so the store
// probes for it once and falls back to LIKE. The fallback is slower and
// cruder; it is not broken, and a store that throws on a handset because a
// virtual table module is missing would be.
//
// A STANDALONE FTS TABLE, NOT EXTERNAL CONTENT. External-content FTS5 needs an
// INTEGER rowid to shadow, and atoms are keyed by GUID. Mapping between them
// costs a second index and a set of triggers to keep honest, for a table that
// holds a few thousand short rows on the biggest phone we care about. The
// duplicate text is cheaper than the machinery.
//
// SUPERSEDED ATOMS ARE NEVER DELETED. They stop being answers and stay
// readable, because the history is what gives a current atom its weight.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace CircleAI.Memory;

/// <summary>
/// SQLite-backed atom store.
/// Pass <c>"Data Source=:memory:"</c> for an in-process test instance.
/// </summary>
public sealed class SqliteAtomStore : IAtomStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly bool _fts;
    private bool _disposed;

    // ------------------------------------------------------------------
    // Construction / Schema
    // ------------------------------------------------------------------

    /// <param name="connectionString">
    /// SQLite connection string, e.g. <c>"Data Source=memory.db"</c>.
    /// </param>
    public SqliteAtomStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required.", nameof(connectionString));

        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        EnsureSchema();
        _fts = TryEnableFts();
    }

    /// <summary>Whether full-text search is available, or LIKE is standing in.</summary>
    /// <remarks>
    /// Exposed so a caller can report the difference rather than wonder about
    /// it: recall quality is materially better with FTS5 and somebody
    /// diagnosing a thin result should be able to see which path ran.
    /// </remarks>
    public bool FullTextAvailable => _fts;

    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS atoms (
                id                 TEXT PRIMARY KEY NOT NULL,
                kind               TEXT NOT NULL,
                text               TEXT NOT NULL,
                subject            TEXT,
                source_episode     TEXT,
                recorded_at_utc    TEXT NOT NULL,
                machine            TEXT,
                corrections        INTEGER NOT NULL DEFAULT 0,
                last_corrected_utc TEXT,
                superseded_by      TEXT,
                challenge          TEXT,
                outcome            TEXT,
                verify             TEXT,
                verified_at_utc    TEXT,
                verified_ok        INTEGER
            );

            -- Recall filters on "current and about this subject" before it ranks
            -- anything, so that pair is the index that matters.
            CREATE INDEX IF NOT EXISTS ix_atoms_subject
                ON atoms (subject, superseded_by);
            CREATE INDEX IF NOT EXISTS ix_atoms_kind
                ON atoms (kind, superseded_by, recorded_at_utc DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    private bool TryEnableFts()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS atoms_fts USING fts5(
                    atom_id UNINDEXED,
                    text,
                    subject,
                    challenge,
                    tokenize = 'porter'
                );
                """;
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            // No FTS5 module in this build. Keyword search falls back to LIKE,
            // which is worse at ranking and fine at finding.
            return false;
        }
    }

    // ------------------------------------------------------------------
    // IAtomStore — writing
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task AddAsync(MemoryAtom atom, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(atom);
        ct.ThrowIfCancellationRequested();

        Insert(atom);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<MemoryAtom> SupersedeAsync(
        Guid oldAtomId, MemoryAtom replacement, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(replacement);
        ct.ThrowIfCancellationRequested();

        var previous = Read(oldAtomId);

        // THE COUNT CARRIES FORWARD. Superseding is what a correction looks
        // like in storage, and losing the tally would throw away the signal
        // that makes a repeatedly-corrected atom outrank a fresh one.
        var carried = new MemoryAtom
        {
            Id            = replacement.Id,
            Text          = replacement.Text,
            SourceEpisode = replacement.SourceEpisode,
            RecordedAtUtc = replacement.RecordedAtUtc,
            Verify        = replacement.Verify ?? previous?.Verify,
            VerifiedAtUtc = replacement.VerifiedAtUtc,
            VerifiedOk    = replacement.VerifiedOk,

            // The kind is inherited unless the replacement names one: a
            // correction usually restates the same sort of thing, and silently
            // demoting a ruling to a preference because a caller left the
            // default in place would lose the reason it ranked first.
            Machine   = replacement.Machine ?? previous?.Machine,
            Kind      = previous?.Kind ?? replacement.Kind,
            Subject   = replacement.Subject ?? previous?.Subject,
            Challenge = replacement.Challenge ?? previous?.Challenge,
            Outcome   = replacement.Outcome ?? previous?.Outcome,

            Corrections      = Math.Max(replacement.Corrections, (previous?.Corrections ?? 0) + 1),
            LastCorrectedUtc = replacement.LastCorrectedUtc ?? DateTimeOffset.UtcNow,
        };

        using var tx = _conn.BeginTransaction();

        Insert(carried, tx);

        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE atoms SET superseded_by = $new WHERE id = $old;";
            cmd.Parameters.AddWithValue("$new", carried.Id.ToString("N"));
            cmd.Parameters.AddWithValue("$old", oldAtomId.ToString("N"));
            cmd.ExecuteNonQuery();
        }

        // The old row stays readable but stops being findable: recall must not
        // return a decision that has been replaced.
        if (_fts)
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM atoms_fts WHERE atom_id = $old;";
            cmd.Parameters.AddWithValue("$old", oldAtomId.ToString("N"));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return Task.FromResult(carried);
    }

    /// <inheritdoc />
    public Task MarkVerifiedAsync(Guid id, bool ok, DateTimeOffset whenUtc, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE atoms
            SET    verified_ok = $ok, verified_at_utc = $when
            WHERE  id = $id;
            """;
        cmd.Parameters.AddWithValue("$ok",   ok ? 1 : 0);
        cmd.Parameters.AddWithValue("$when", whenUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$id",   id.ToString("N"));
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // IAtomStore — reading
    // ------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// SUBJECT FIRST, THEN WORDS. An atom filed under the subject of the action
    /// is relevant by construction; one that merely shares vocabulary might be.
    /// Both are returned, subject matches ahead of keyword matches, and the
    /// ranking that decides what a caller actually sees lives in
    /// <see cref="Recall"/> rather than here - this returns candidates.
    /// </remarks>
    public Task<IReadOnlyList<MemoryAtom>> MatchAsync(
        Situation situation, int limit = 20, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(situation);
        ct.ThrowIfCancellationRequested();

        if (situation.IsEmpty)
            return Task.FromResult<IReadOnlyList<MemoryAtom>>(Array.Empty<MemoryAtom>());

        var found = new Dictionary<Guid, MemoryAtom>();

        foreach (var atom in BySubject(situation.Keys, limit))
            found[atom.Id] = atom;

        if (found.Count < limit)
        {
            foreach (var atom in ByKeyword(situation.Query, limit - found.Count))
                found.TryAdd(atom.Id, atom);
        }

        return Task.FromResult<IReadOnlyList<MemoryAtom>>(found.Values.ToList());
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MemoryAtom>> ByKindAsync(
        AtomKind kind, int limit = 50, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns}
            FROM   atoms
            WHERE  kind = $kind AND superseded_by IS NULL
            ORDER  BY recorded_at_utc DESC
            LIMIT  $limit;
            """;
        cmd.Parameters.AddWithValue("$kind",  kind.ToString());
        cmd.Parameters.AddWithValue("$limit", limit);

        return Task.FromResult<IReadOnlyList<MemoryAtom>>(ReadAtoms(cmd));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MemoryAtom>> AllAsync(
        bool includeSuperseded = false, int limit = 500, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns}
            FROM   atoms
            WHERE  ($all = 1 OR superseded_by IS NULL)
            ORDER  BY recorded_at_utc DESC
            LIMIT  $limit;
            """;
        cmd.Parameters.AddWithValue("$all",   includeSuperseded ? 1 : 0);
        cmd.Parameters.AddWithValue("$limit", limit);

        return Task.FromResult<IReadOnlyList<MemoryAtom>>(ReadAtoms(cmd));
    }

    /// <inheritdoc />
    public Task<MemoryAtom?> GetAsync(Guid id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Read(id));
    }

    /// <inheritdoc />
    public Task<int> CountAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM atoms WHERE superseded_by IS NULL;";
        return Task.FromResult((int)(long)cmd.ExecuteScalar()!);
    }

    // ------------------------------------------------------------------
    // IDisposable
    // ------------------------------------------------------------------

    /// <summary>Closes the underlying SQLite connection.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conn.Dispose();
    }

    // ------------------------------------------------------------------
    // Helpers — searching
    // ------------------------------------------------------------------

    private List<MemoryAtom> BySubject(IReadOnlyList<string> keys, int limit)
    {
        if (keys.Count == 0) return new List<MemoryAtom>();

        // Most specific key first, so "deploy:android/p30" outranks "deploy".
        var results = new List<MemoryAtom>();
        foreach (var key in keys)
        {
            if (results.Count >= limit) break;

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT {Columns}
                FROM   atoms
                WHERE  subject = $key AND superseded_by IS NULL
                ORDER  BY corrections DESC, recorded_at_utc DESC
                LIMIT  $limit;
                """;
            cmd.Parameters.AddWithValue("$key",   key);
            cmd.Parameters.AddWithValue("$limit", limit - results.Count);
            results.AddRange(ReadAtoms(cmd));
        }
        return results;
    }

    private List<MemoryAtom> ByKeyword(string query, int limit)
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(query)) return new List<MemoryAtom>();

        return _fts ? ByFts(query, limit) : ByLike(query, limit);
    }

    private List<MemoryAtom> ByFts(string query, int limit)
    {
        var match = ToMatchExpression(query);
        if (match.Length == 0) return new List<MemoryAtom>();

        try
        {
            using var cmd = _conn.CreateCommand();
            // bm25() ranks lower-is-better, so ordering ascending puts the best
            // match first. Joining back to atoms keeps one source of truth for
            // the row itself; the FTS table only decides which rows.
            cmd.CommandText = $"""
                SELECT {QualifiedColumns}
                FROM   atoms_fts
                JOIN   atoms ON atoms.id = atoms_fts.atom_id
                WHERE  atoms_fts MATCH $q AND atoms.superseded_by IS NULL
                ORDER  BY bm25(atoms_fts) ASC
                LIMIT  $limit;
                """;
            cmd.Parameters.AddWithValue("$q",     match);
            cmd.Parameters.AddWithValue("$limit", limit);
            return ReadAtoms(cmd);
        }
        catch (SqliteException)
        {
            // A malformed MATCH expression is a query problem, not a store
            // problem: fall through to LIKE rather than failing the recall.
            return ByLike(query, limit);
        }
    }

    private List<MemoryAtom> ByLike(string query, int limit)
    {
        // One OR per term. Crude, and it does not rank - but it finds, and on a
        // few thousand short rows the difference is not measurable by a person.
        var terms = Terms(query).Take(6).ToList();
        if (terms.Count == 0) return new List<MemoryAtom>();

        using var cmd = _conn.CreateCommand();
        var clauses = string.Join(" OR ", terms.Select((_, i) =>
            $"text LIKE $t{i} OR subject LIKE $t{i} OR IFNULL(challenge, '') LIKE $t{i}"));
        cmd.CommandText = $"""
            SELECT {Columns}
            FROM   atoms
            WHERE  superseded_by IS NULL AND ({clauses})
            ORDER  BY corrections DESC, recorded_at_utc DESC
            LIMIT  $limit;
            """;
        for (var i = 0; i < terms.Count; i++)
            cmd.Parameters.AddWithValue($"$t{i}", $"%{terms[i]}%");
        cmd.Parameters.AddWithValue("$limit", limit);

        return ReadAtoms(cmd);
    }

    /// <summary>Words worth searching on.</summary>
    /// <remarks>
    /// Single characters and punctuation are dropped: they match everything,
    /// which in a recall means returning noise ahead of the one atom that
    /// mattered.
    /// </remarks>
    private static IEnumerable<string> Terms(string query) =>
        query.Split(new[] { ' ', '\t', '\n', '\r', ',', ';', '(', ')', '"', '\'' },
                    StringSplitOptions.RemoveEmptyEntries)
             .Select(t => t.Trim())
             .Where(t => t.Length > 1);

    /// <summary>An FTS5 MATCH expression that cannot be a syntax error.</summary>
    /// <remarks>
    /// Every term is quoted and OR-ed. Unquoted user text reaches FTS5 as
    /// operators - a stray hyphen, quote or NEAR is a thrown exception rather
    /// than a poor result, and a recall must never take down the action it was
    /// supposed to inform.
    /// </remarks>
    private static string ToMatchExpression(string query) =>
        string.Join(" OR ", Terms(query)
            .Take(8)
            .Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));

    // ------------------------------------------------------------------
    // Helpers — rows
    // ------------------------------------------------------------------

    private const string Columns =
        "id, kind, text, subject, source_episode, recorded_at_utc, corrections, " +
        "last_corrected_utc, superseded_by, challenge, outcome, verify, " +
        "verified_at_utc, verified_ok, machine";

    // Qualified, for the FTS join: atoms_fts also has "text" and "subject", so
    // the bare list is ambiguous there and SQLite is entitled to pick either.
    private const string QualifiedColumns =
        "atoms.id, atoms.kind, atoms.text, atoms.subject, atoms.source_episode, " +
        "atoms.recorded_at_utc, atoms.corrections, atoms.last_corrected_utc, " +
        "atoms.superseded_by, atoms.challenge, atoms.outcome, atoms.verify, " +
        "atoms.verified_at_utc, atoms.verified_ok, atoms.machine";

    private void Insert(MemoryAtom atom, SqliteTransaction? tx = null)
    {
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO atoms
                    (id, kind, text, subject, source_episode, recorded_at_utc,
                     corrections, last_corrected_utc, superseded_by,
                     challenge, outcome, verify, verified_at_utc, verified_ok,
                     machine)
                VALUES
                    ($id, $kind, $text, $subject, $source, $recorded,
                     $corrections, $lastCorrected, $superseded,
                     $challenge, $outcome, $verify, $verifiedAt, $verifiedOk,
                     $machine);
                """;
            cmd.Parameters.AddWithValue("$id",            atom.Id.ToString("N"));
            cmd.Parameters.AddWithValue("$kind",          atom.Kind.ToString());
            cmd.Parameters.AddWithValue("$text",          atom.Text);
            cmd.Parameters.AddWithValue("$subject",       (object?)atom.Subject ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source",        (object?)atom.SourceEpisode?.ToString("N") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$recorded",      atom.RecordedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$corrections",   atom.Corrections);
            cmd.Parameters.AddWithValue("$lastCorrected", (object?)atom.LastCorrectedUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$superseded",    (object?)atom.SupersededBy?.ToString("N") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$challenge",     (object?)atom.Challenge ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$outcome",       (object?)atom.Outcome?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$verify",        (object?)atom.Verify ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$verifiedAt",    (object?)atom.VerifiedAtUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$verifiedOk",    atom.VerifiedOk is null ? DBNull.Value : atom.VerifiedOk.Value ? 1 : 0);
            cmd.Parameters.AddWithValue("$machine",       (object?)atom.Machine ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        if (!_fts) return;

        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM atoms_fts WHERE atom_id = $id;
                INSERT INTO atoms_fts (atom_id, text, subject, challenge)
                VALUES ($id, $text, $subject, $challenge);
                """;
            cmd.Parameters.AddWithValue("$id",        atom.Id.ToString("N"));
            cmd.Parameters.AddWithValue("$text",      atom.Text);
            cmd.Parameters.AddWithValue("$subject",   (object?)atom.Subject ?? string.Empty);
            // THE CHALLENGE IS THE SEARCHABLE HALF. "Have we been here before"
            // is asked against what came up, not against what was decided.
            cmd.Parameters.AddWithValue("$challenge", (object?)atom.Challenge ?? string.Empty);
            cmd.ExecuteNonQuery();
        }
    }

    private MemoryAtom? Read(Guid id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM atoms WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("N"));
        return ReadAtoms(cmd).FirstOrDefault();
    }

    private static List<MemoryAtom> ReadAtoms(SqliteCommand cmd)
    {
        var results = new List<MemoryAtom>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new MemoryAtom
            {
                Id               = Guid.Parse(reader.GetString(0)),
                Kind             = Enum.TryParse<AtomKind>(reader.GetString(1), out var k) ? k : AtomKind.Fact,
                Text             = reader.GetString(2),
                Subject          = reader.IsDBNull(3) ? null : reader.GetString(3),
                SourceEpisode    = reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                RecordedAtUtc    = ParseTime(reader.GetString(5)),
                Corrections      = reader.GetInt32(6),
                LastCorrectedUtc = reader.IsDBNull(7) ? null : ParseTime(reader.GetString(7)),
                SupersededBy     = reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
                Challenge        = reader.IsDBNull(9) ? null : reader.GetString(9),
                Outcome          = reader.IsDBNull(10) ? null
                                     : Enum.TryParse<DecisionOutcome>(reader.GetString(10), out var o) ? o : null,
                Verify           = reader.IsDBNull(11) ? null : reader.GetString(11),
                VerifiedAtUtc    = reader.IsDBNull(12) ? null : ParseTime(reader.GetString(12)),
                VerifiedOk       = reader.IsDBNull(13) ? null : reader.GetInt32(13) == 1,
                Machine          = reader.IsDBNull(14) ? null : reader.GetString(14),
            });
        }
        return results;
    }

    private static DateTimeOffset ParseTime(string raw) =>
        DateTimeOffset.Parse(raw, null, DateTimeStyles.RoundtripKind);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SqliteAtomStore));
    }
}
