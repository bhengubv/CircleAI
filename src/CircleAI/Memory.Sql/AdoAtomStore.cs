// AdoAtomStore.cs
//
// IAtomStore on any ADO.NET engine: PostgreSQL, SQL Server, MySQL, Oracle.
//
// THE SHARED CASE, NOT THE DEFAULT ONE. A phone runs SqliteAtomStore and always
// will - no server, ships in the app, works with the aeroplane mode on. This is
// for the other situation: a team, or a machine somebody already runs, where the
// memory should live where the rest of their data lives.
//
// THE CALLER BRINGS THE CONNECTION. This project references no driver, so it
// pulls Oracle's client into nothing, and an engine nobody here has heard of
// works by writing a SqlDialect rather than by us shipping a package for it.
//
// UPSERT IS DELETE-THEN-INSERT IN A TRANSACTION, not MERGE. Four engines spell
// MERGE four ways and two of them have footguns in it; delete-then-insert is
// the same everywhere, is exactly the idempotence a replay needs, and costs one
// extra statement on a table that takes a handful of writes a day.
//
// SUPERSEDED ATOMS ARE NEVER DELETED, same as everywhere else. They stop being
// answers and stay readable, because the history is what gives a current atom
// its weight.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Sql;

/// <summary>An atom store on any ADO.NET connection.</summary>
public sealed class AdoAtomStore : IAtomStore
{
    private const string Table = "atoms";

    private readonly DbConnection _conn;
    private readonly SqlDialect _sql;

    /// <param name="connection">
    /// An open connection, or one this store may open. It is not disposed here:
    /// whoever created it owns its lifetime, and a store that closed somebody
    /// else's pooled connection would be a hard bug to find.
    /// </param>
    /// <param name="dialect">Which engine it is.</param>
    public AdoAtomStore(DbConnection connection, SqlDialect dialect)
    {
        _conn = connection ?? throw new ArgumentNullException(nameof(connection));
        _sql = dialect ?? throw new ArgumentNullException(nameof(dialect));

        if (_conn.State != ConnectionState.Open) _conn.Open();
        EnsureSchema();
    }

    /// <summary>Which engine this is talking to.</summary>
    public string Engine => _sql.Name;

    /// <summary>Whether keyword search is a real index or the LIKE floor.</summary>
    public bool FullTextAvailable { get; private set; }

    // ------------------------------------------------------------------
    // Schema
    // ------------------------------------------------------------------

    private void EnsureSchema()
    {
        if (Scalar(_sql.TableExists(Table)) > 0)
        {
            // Already built. Whether the full-text index came up is not
            // knowable portably, so trust what the dialect claims it does.
            FullTextAvailable = _sql.FullText;
            return;
        }

        Execute(_sql.CreateTable(Table));

        // EACH INDEX ON ITS OWN, AND A FAILURE IS NOT FATAL. A server that
        // refuses to build a full-text index still has to serve a memory, and
        // it does - through the LIKE floor. Throwing here would turn a missing
        // optimisation into a store that will not start.
        var built = true;
        foreach (var statement in _sql.Indexes(Table))
        {
            try { Execute(statement); }
            catch (DbException) { built = false; }
        }

        FullTextAvailable = _sql.FullText && built;
    }

    // ------------------------------------------------------------------
    // IAtomStore - writing
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task AddAsync(MemoryAtom atom, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(atom);
        ct.ThrowIfCancellationRequested();

        using var tx = _conn.BeginTransaction();
        Upsert(atom, tx);
        tx.Commit();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<MemoryAtom> SupersedeAsync(
        Guid oldAtomId, MemoryAtom replacement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ct.ThrowIfCancellationRequested();

        var previous = Read(oldAtomId);

        // THE COUNT CARRIES FORWARD, exactly as it does on SQLite. Losing the
        // tally would throw away the signal that makes a repeatedly-corrected
        // atom outrank a fresh one, and a memory that behaves differently on
        // two engines is worse than one that only runs on one.
        var carried = new MemoryAtom
        {
            Id            = replacement.Id,
            Text          = replacement.Text,
            SourceEpisode = replacement.SourceEpisode,
            RecordedAtUtc = replacement.RecordedAtUtc,
            Machine       = replacement.Machine ?? previous?.Machine,
            Verify        = replacement.Verify ?? previous?.Verify,
            VerifiedAtUtc = replacement.VerifiedAtUtc,
            VerifiedOk    = replacement.VerifiedOk,

            Kind      = previous?.Kind ?? replacement.Kind,
            Subject   = replacement.Subject ?? previous?.Subject,
            Challenge = replacement.Challenge ?? previous?.Challenge,
            Outcome   = replacement.Outcome ?? previous?.Outcome,

            Corrections      = Math.Max(replacement.Corrections, (previous?.Corrections ?? 0) + 1),
            LastCorrectedUtc = replacement.LastCorrectedUtc ?? DateTimeOffset.UtcNow,
        };

        using var tx = _conn.BeginTransaction();

        Upsert(carried, tx);

        using (var cmd = Command(
            $"UPDATE {_sql.Quote(Table)} SET {_sql.Quote("superseded_by")} = {_sql.Parameter("next")} " +
            $"WHERE {_sql.Quote("id")} = {_sql.Parameter("old")}", tx))
        {
            Bind(cmd, "next", carried.Id.ToString("N"));
            Bind(cmd, "old", oldAtomId.ToString("N"));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return Task.FromResult(carried);
    }

    /// <inheritdoc />
    public Task MarkVerifiedAsync(Guid id, bool ok, DateTimeOffset whenUtc, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = Command(
            $"UPDATE {_sql.Quote(Table)} " +
            $"SET {_sql.Quote("verified_ok")} = {_sql.Parameter("ok")}, " +
            $"    {_sql.Quote("verified_at_utc")} = {_sql.Parameter("when")} " +
            $"WHERE {_sql.Quote("id")} = {_sql.Parameter("id")}");

        Bind(cmd, "ok", ok ? 1 : 0);
        Bind(cmd, "when", whenUtc.ToString("O", CultureInfo.InvariantCulture));
        Bind(cmd, "id", id.ToString("N"));
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // IAtomStore - reading
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<IReadOnlyList<MemoryAtom>> MatchAsync(
        Situation situation, int limit = 20, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(situation);
        ct.ThrowIfCancellationRequested();

        var results = new List<MemoryAtom>();
        var seen = new HashSet<Guid>();

        // SUBJECT FIRST, MOST SPECIFIC FIRST. Matching what the action is about
        // against what the atom is about is not a guess; searching prose for
        // relevance is. Keyword search fills in behind it.
        foreach (var key in situation.Keys)
        {
            if (results.Count >= limit) break;

            using var cmd = Command(
                $"SELECT {Columns} FROM {_sql.Quote(Table)} " +
                $"WHERE {_sql.Quote("superseded_by")} IS NULL " +
                $"  AND {_sql.Quote("subject")} = {_sql.Parameter("key")} " +
                $"ORDER BY {_sql.Quote("recorded_at_utc")} DESC " +
                _sql.Limit(limit - results.Count));

            Bind(cmd, "key", key);
            Take(cmd, results, seen);
        }

        if (results.Count < limit)
        {
            var terms = Terms(situation.Query);
            if (terms.Count > 0)
            {
                var (where, parameters) = _sql.Search(terms);

                using var cmd = Command(
                    $"SELECT {Columns} FROM {_sql.Quote(Table)} " +
                    $"WHERE {_sql.Quote("superseded_by")} IS NULL AND ({where}) " +
                    $"ORDER BY {_sql.Quote("recorded_at_utc")} DESC " +
                    _sql.Limit(limit));

                foreach (var (name, value) in parameters) Bind(cmd, name, value);

                try { Take(cmd, results, seen); }
                catch (DbException)
                {
                    // A malformed full-text query is a thin result, not an
                    // outage. The subject matches above already stand.
                }
            }
        }

        return Task.FromResult<IReadOnlyList<MemoryAtom>>(results.Take(limit).ToList());
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MemoryAtom>> ByKindAsync(
        AtomKind kind, int limit = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = Command(
            $"SELECT {Columns} FROM {_sql.Quote(Table)} " +
            $"WHERE {_sql.Quote("superseded_by")} IS NULL " +
            $"  AND {_sql.Quote("kind")} = {_sql.Parameter("kind")} " +
            $"ORDER BY {_sql.Quote("recorded_at_utc")} DESC " +
            _sql.Limit(limit));

        Bind(cmd, "kind", kind.ToString());
        return Task.FromResult<IReadOnlyList<MemoryAtom>>(ReadAtoms(cmd));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MemoryAtom>> AllAsync(
        bool includeSuperseded = false, int limit = 500, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var filter = includeSuperseded ? "" : $"WHERE {_sql.Quote("superseded_by")} IS NULL ";

        using var cmd = Command(
            $"SELECT {Columns} FROM {_sql.Quote(Table)} {filter}" +
            $"ORDER BY {_sql.Quote("recorded_at_utc")} DESC " +
            _sql.Limit(limit));

        return Task.FromResult<IReadOnlyList<MemoryAtom>>(ReadAtoms(cmd));
    }

    /// <inheritdoc />
    public Task<bool> KnowsAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(false);

        // Indexed, because learning asks this of every sentence it spots and
        // learning runs on every turn of a conversation.
        using var cmd = Command(
            $"SELECT {_sql.Quote("id")} FROM {_sql.Quote(Table)} " +
            $"WHERE {_sql.Quote("text_key")} = {_sql.Parameter("key")} " +
            $"  AND {_sql.Quote("superseded_by")} IS NULL " +
            _sql.Limit(1));

        Bind(cmd, "key", CueExtractor.Normalise(text));
        return Task.FromResult(cmd.ExecuteScalar() is not null);
    }

    /// <inheritdoc />
    public Task<MemoryAtom?> GetAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Read(id));
    }

    /// <inheritdoc />
    public Task<int> CountAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Scalar(
            $"SELECT COUNT(*) FROM {_sql.Quote(Table)} WHERE {_sql.Quote("superseded_by")} IS NULL"));
    }

    // ------------------------------------------------------------------
    // Rows
    // ------------------------------------------------------------------

    private string Columns => string.Join(", ", new[]
    {
        "id", "kind", "text", "subject", "source_episode", "recorded_at_utc",
        "corrections", "last_corrected_utc", "superseded_by", "challenge",
        "outcome", "verify", "verified_at_utc", "verified_ok", "machine",
    }.Select(_sql.Quote));

    private void Upsert(MemoryAtom atom, DbTransaction tx)
    {
        using (var cmd = Command(
            $"DELETE FROM {_sql.Quote(Table)} WHERE {_sql.Quote("id")} = {_sql.Parameter("id")}", tx))
        {
            Bind(cmd, "id", atom.Id.ToString("N"));
            cmd.ExecuteNonQuery();
        }

        var names = new[]
        {
            "id", "kind", "text", "subject", "source_episode", "recorded_at_utc",
            "corrections", "last_corrected_utc", "superseded_by", "challenge",
            "outcome", "verify", "verified_at_utc", "verified_ok", "machine", "text_key",
        };

        using (var cmd = Command(
            $"INSERT INTO {_sql.Quote(Table)} ({string.Join(", ", names.Select(_sql.Quote))}) " +
            $"VALUES ({string.Join(", ", names.Select(_sql.Parameter))})", tx))
        {
            Bind(cmd, "id",                 atom.Id.ToString("N"));
            Bind(cmd, "kind",               atom.Kind.ToString());
            Bind(cmd, "text",               atom.Text);
            Bind(cmd, "subject",            atom.Subject);
            Bind(cmd, "source_episode",     atom.SourceEpisode?.ToString("N"));
            Bind(cmd, "recorded_at_utc",    atom.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            Bind(cmd, "corrections",        atom.Corrections);
            Bind(cmd, "last_corrected_utc", atom.LastCorrectedUtc?.ToString("O", CultureInfo.InvariantCulture));
            Bind(cmd, "superseded_by",      atom.SupersededBy?.ToString("N"));
            Bind(cmd, "challenge",          atom.Challenge);
            Bind(cmd, "outcome",            atom.Outcome?.ToString());
            Bind(cmd, "verify",             atom.Verify);
            Bind(cmd, "verified_at_utc",    atom.VerifiedAtUtc?.ToString("O", CultureInfo.InvariantCulture));
            Bind(cmd, "verified_ok",        atom.VerifiedOk is null ? null : atom.VerifiedOk.Value ? 1 : 0);
            Bind(cmd, "machine",            atom.Machine);
            Bind(cmd, "text_key",           CueExtractor.Normalise(atom.Text));
            cmd.ExecuteNonQuery();
        }
    }

    private MemoryAtom? Read(Guid id)
    {
        using var cmd = Command(
            $"SELECT {Columns} FROM {_sql.Quote(Table)} WHERE {_sql.Quote("id")} = {_sql.Parameter("id")}");
        Bind(cmd, "id", id.ToString("N"));
        return ReadAtoms(cmd).FirstOrDefault();
    }

    private static List<MemoryAtom> ReadAtoms(DbCommand cmd)
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
                Subject          = Text(reader, 3),
                SourceEpisode    = Text(reader, 4) is { } src ? Guid.Parse(src) : null,
                RecordedAtUtc    = Time(reader.GetString(5)) ?? DateTimeOffset.MinValue,
                // Engines disagree about the CLR type of an integer column -
                // int, long and decimal all turn up - so it is read as a
                // number rather than cast to one.
                Corrections      = Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
                LastCorrectedUtc = Text(reader, 7) is { } lc ? Time(lc) : null,
                SupersededBy     = Text(reader, 8) is { } sup ? Guid.Parse(sup) : null,
                Challenge        = Text(reader, 9),
                Outcome          = Text(reader, 10) is { } o &&
                                   Enum.TryParse<DecisionOutcome>(o, out var outcome) ? outcome : null,
                Verify           = Text(reader, 11),
                VerifiedAtUtc    = Text(reader, 12) is { } va ? Time(va) : null,
                VerifiedOk       = reader.IsDBNull(13)
                                     ? null
                                     : Convert.ToInt32(reader.GetValue(13), CultureInfo.InvariantCulture) == 1,
                Machine          = Text(reader, 14),
            });
        }

        return results;
    }

    private static void Take(DbCommand cmd, List<MemoryAtom> into, HashSet<Guid> seen)
    {
        foreach (var atom in ReadAtoms(cmd))
            if (seen.Add(atom.Id))
                into.Add(atom);
    }

    private static string? Text(DbDataReader reader, int i) =>
        reader.IsDBNull(i) ? null : reader.GetString(i);

    private static DateTimeOffset? Time(string raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var when) ? when : null;

    /// <summary>Words worth searching for.</summary>
    /// <remarks>
    /// One-and-two-character tokens are dropped: they match everything, which
    /// on an engine using LIKE means a scan that returns the whole table.
    /// </remarks>
    private static IReadOnlyList<string> Terms(string query) =>
        query.Split(new[] { ' ', '\t', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
             .Where(t => t.Length > 2)
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .Take(8)
             .ToList();

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    private DbCommand Command(string sql, DbTransaction? tx = null)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private void Bind(DbCommand cmd, string name, object? value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = _sql.ParameterName(name);
        parameter.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(parameter);
    }

    private void Execute(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    private int Scalar(string sql)
    {
        using var cmd = Command(sql);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}
