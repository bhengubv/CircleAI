// AdoAtomStoreTests.cs
//
// The shared case: the same memory on somebody's PostgreSQL, SQL Server, MySQL
// or Oracle.
//
// WHAT IS RUN AND WHAT IS NOT, said plainly. The shared implementation is run
// end to end against a real engine here - SQLite through the same ADO path, the
// same DbConnection, DbCommand and DbDataReader every other engine goes
// through - so the logic that actually holds a memory is exercised, not
// reasoned about. What each dialect emits is checked as SQL, because no
// PostgreSQL, SQL Server, MySQL or Oracle server is available to this test run.
//
// Those two together cover the code; they do not cover the engines. Until one
// of these has been pointed at a live server, treat the four as written and
// unproven rather than as working.

using System;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Memory;
using CircleAI.Memory.Sql;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CircleAI.Tests;

public class AdoAtomStoreTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly AdoAtomStore _store;

    public AdoAtomStoreTests()
    {
        // A long-lived connection: an in-memory SQLite database exists only as
        // long as something is holding it open.
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _store = new AdoAtomStore(_conn, SqlDialect.Sqlite);
    }

    public void Dispose() => _conn.Dispose();

    private static MemoryAtom Decision(string challenge, string text, string subject) => new()
    {
        Kind = AtomKind.Decision,
        Challenge = challenge,
        Text = text,
        Subject = subject,
        Outcome = DecisionOutcome.Resolved,
        Machine = "windows-desk",
    };

    // ------------------------------------------------------------------
    // The shared implementation, against a real engine
    // ------------------------------------------------------------------

    [Fact]
    public async Task It_remembers_and_gives_it_back()
    {
        var atom = Decision(
            "-t:Install wiped 817 MB of models",
            "Use -t:InstallKeepingData when iterating",
            "deploy:android");

        await _store.AddAsync(atom);

        var found = await _store.MatchAsync(new Situation("deploy", "android"));

        Assert.Single(found);
        Assert.Equal(atom.Text, found[0].Text);
        Assert.Equal(atom.Challenge, found[0].Challenge);
        Assert.Equal(DecisionOutcome.Resolved, found[0].Outcome);
        Assert.Equal("windows-desk", found[0].Machine);
    }

    [Fact]
    public async Task Every_field_survives_the_round_trip()
    {
        // The failure this catches is a column read at the wrong ordinal, which
        // does not throw - it quietly puts the challenge in the verify column
        // and nobody notices until a recall reads strangely.
        var atom = new MemoryAtom
        {
            Kind             = AtomKind.Fact,
            Text             = "The P30 Lite is UTKDU19919000815",
            Subject          = "device:p30",
            Challenge        = "Which handset is the benchmark",
            Outcome          = DecisionOutcome.Open,
            SourceEpisode    = Guid.NewGuid(),
            Machine          = "mac-build",
            Verify           = "adb devices",
            Corrections      = 3,
            LastCorrectedUtc = DateTimeOffset.UtcNow.AddDays(-2),
            VerifiedAtUtc    = DateTimeOffset.UtcNow.AddDays(-1),
            VerifiedOk       = false,
        };

        await _store.AddAsync(atom);
        var back = await _store.GetAsync(atom.Id);

        Assert.NotNull(back);
        Assert.Equal(atom.Kind, back!.Kind);
        Assert.Equal(atom.Text, back.Text);
        Assert.Equal(atom.Subject, back.Subject);
        Assert.Equal(atom.Challenge, back.Challenge);
        Assert.Equal(atom.Outcome, back.Outcome);
        Assert.Equal(atom.SourceEpisode, back.SourceEpisode);
        Assert.Equal(atom.Machine, back.Machine);
        Assert.Equal(atom.Verify, back.Verify);
        Assert.Equal(atom.Corrections, back.Corrections);
        Assert.Equal(false, back.VerifiedOk);
        Assert.True(back.IsStale);
    }

    [Fact]
    public async Task A_correction_carries_the_count_forward()
    {
        // The signal that makes a repeatedly-corrected atom outrank a fresh
        // one. A memory that behaved differently on two engines would be worse
        // than one that only ran on a phone.
        var first = Decision("How do we deploy?", "Use -t:Install", "deploy:android");
        await _store.AddAsync(first);

        var second = await _store.SupersedeAsync(first.Id,
            Decision("How do we deploy?", "Use -t:InstallKeepingData", "deploy:android"));
        var third = await _store.SupersedeAsync(second.Id,
            Decision("How do we deploy?", "Set EmbedAssembliesIntoApk too", "deploy:android"));

        Assert.Equal(1, second.Corrections);
        Assert.Equal(2, third.Corrections);

        var current = await _store.MatchAsync(new Situation("deploy", "android"));
        Assert.Single(current);
        Assert.Equal("Set EmbedAssembliesIntoApk too", current[0].Text);
    }

    [Fact]
    public async Task A_superseded_atom_stops_answering_and_stays_readable()
    {
        var first = Decision("q", "the original", "s");
        await _store.AddAsync(first);
        await _store.SupersedeAsync(first.Id, Decision("q", "the correction", "s"));

        Assert.DoesNotContain(await _store.MatchAsync(new Situation("s")),
            a => a.Text == "the original");

        var traced = await _store.GetAsync(first.Id);
        Assert.NotNull(traced);
        Assert.False(traced!.IsCurrent);
        Assert.Equal("the original", traced.Text);
    }

    [Fact]
    public async Task The_kind_is_inherited_when_a_correction_does_not_name_one()
    {
        // Silently demoting a ruling to a decision because a caller left the
        // default in place would lose the reason it ranked first.
        var ruling = new MemoryAtom
        {
            Kind = AtomKind.Ruling,
            Text = "Never restart a device without asking",
            Subject = "device:state",
        };
        await _store.AddAsync(ruling);

        var corrected = await _store.SupersedeAsync(ruling.Id, new MemoryAtom
        {
            Text = "Never restart a device or toggle its radios without asking",
        });

        Assert.Equal(AtomKind.Ruling, corrected.Kind);
        Assert.Equal("device:state", corrected.Subject);
    }

    [Fact]
    public async Task Subject_matches_arrive_before_keyword_matches()
    {
        // Matching what the action is about against what the atom is about is
        // not a guess. Searching prose for relevance is.
        await _store.AddAsync(Decision("something else", "mentions android in passing", "unrelated"));
        await _store.AddAsync(Decision("the deploy", "Use -t:InstallKeepingData", "deploy:android"));

        var found = await _store.MatchAsync(new Situation("deploy", "android"));

        Assert.Equal("Use -t:InstallKeepingData", found[0].Text);
    }

    [Fact]
    public async Task A_subject_one_level_up_is_still_found()
    {
        await _store.AddAsync(Decision("the deploy", "Uninstall first", "deploy:android"));

        var found = await _store.MatchAsync(new Situation("deploy", "android/p30"));

        Assert.Single(found);
    }

    [Fact]
    public async Task Search_survives_the_punctuation_this_memory_is_full_of()
    {
        // Half of what gets remembered here is flags and paths: -t:Install,
        // --no-incremental, /sdcard/. A search that throws on those is a search
        // that fails exactly when it is needed.
        await _store.AddAsync(Decision(
            "-t:Install wiped the models", "Use -t:InstallKeepingData", "deploy:android"));

        var found = await _store.MatchAsync(
            new Situation(Text: "-t:Install --no-incremental /sdcard/ \"quoted\" (parens)"));

        Assert.NotEmpty(found);
    }

    [Fact]
    public async Task Writing_the_same_atom_twice_leaves_one()
    {
        // Replay re-inserts everything it reads. Without this a rebuild would
        // grow a duplicate on every sync.
        var atom = Decision("q", "a", "s");

        await _store.AddAsync(atom);
        await _store.AddAsync(atom);

        Assert.Equal(1, await _store.CountAsync());
    }

    [Fact]
    public async Task It_can_be_read_as_well_as_queried()
    {
        await _store.AddAsync(Decision("q", "a decision", "s"));
        await _store.AddAsync(new MemoryAtom { Kind = AtomKind.Ruling, Text = "a rule" });

        var all = await _store.AllAsync();
        Assert.Equal(2, all.Count);

        var rulings = await _store.ByKindAsync(AtomKind.Ruling);
        Assert.Single(rulings);
    }

    [Fact]
    public async Task A_failed_check_marks_a_fact_rather_than_deleting_it()
    {
        var fact = new MemoryAtom
        {
            Kind = AtomKind.Fact,
            Text = "The P30 Lite is UTKDU19919000815",
            Subject = "device:p30",
            Verify = "adb devices",
        };
        await _store.AddAsync(fact);

        await _store.MarkVerifiedAsync(fact.Id, ok: false, DateTimeOffset.UtcNow);

        var back = await _store.GetAsync(fact.Id);
        Assert.True(back!.IsStale);
        Assert.Equal("The P30 Lite is UTKDU19919000815", back.Text);
    }

    [Fact]
    public async Task Recall_works_over_it_the_same_way()
    {
        // The seam is the point: everything above the store must not care which
        // engine is underneath.
        var failed = new MemoryAtom
        {
            Kind = AtomKind.Decision,
            Text = "Use adb install -r",
            Subject = "deploy:android",
            Challenge = "How do we keep the models",
            Outcome = DecisionOutcome.Failed,
        };
        await _store.AddAsync(failed);
        await _store.AddAsync(Decision("How do we keep the models", "Use -t:InstallKeepingData", "deploy:android"));
        await _store.AddAsync(new MemoryAtom
        {
            Kind = AtomKind.Relationship,
            Text = "Blunt. Hates being asked twice.",
            Subject = "style",
        });

        var result = await new Recall(_store).ForAsync(new Situation("deploy", "android"));

        Assert.Equal(2, result.Atoms.Count);
        Assert.Single(result.Tone);

        // The road already tried and found closed comes first.
        Assert.True(result.Atoms[0].Failed);
    }

    [Fact]
    public void It_says_which_engine_it_is_talking_to()
    {
        Assert.Equal("SQLite", _store.Engine);
    }

    // ------------------------------------------------------------------
    // The dialects
    // ------------------------------------------------------------------
    //
    // No server for these four, so what is checked is the SQL each one emits.
    // That catches the mistakes that are actually made here - a keyword quoted
    // the wrong way, a LIMIT an engine does not have, a term starting with a
    // hyphen meaning NOT - and it does not prove the engine accepts it.

    public static TheoryData<SqlDialect> Dialects => new()
    {
        SqlDialect.PostgreSql, SqlDialect.SqlServer,
        SqlDialect.MySql, SqlDialect.Oracle, SqlDialect.Sqlite,
    };

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Every_dialect_quotes_the_columns_that_are_keywords(SqlDialect sql)
    {
        // "text" is reserved somewhere in every one of these.
        var ddl = sql.CreateTable("atoms");

        Assert.Contains(sql.Quote("text"), ddl, StringComparison.Ordinal);
        Assert.DoesNotContain(" text ", ddl, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Every_dialect_creates_every_column(SqlDialect sql)
    {
        var ddl = sql.CreateTable("atoms");

        foreach (var column in new[]
        {
            "id", "kind", "text", "subject", "source_episode", "recorded_at_utc",
            "corrections", "last_corrected_utc", "superseded_by", "challenge",
            "outcome", "verify", "verified_at_utc", "verified_ok", "machine",
        })
            Assert.Contains(sql.Quote(column), ddl, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Every_dialect_can_cap_a_result(SqlDialect sql)
    {
        var limit = sql.Limit(5);
        Assert.Contains("5", limit, StringComparison.Ordinal);
        Assert.NotEqual("", limit.Trim());
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Every_dialect_binds_its_search_terms(SqlDialect sql)
    {
        // The injection question, asked of all five: the words being searched
        // for must arrive as parameters, never pasted into the SQL. Half of
        // what gets remembered here contains quotes and hyphens.
        var (where, parameters) = sql.Search(new[] { "install", "o'brien\"; DROP TABLE atoms; --" });

        Assert.NotEmpty(parameters);
        Assert.DoesNotContain("DROP TABLE", where, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("o'brien", where, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySql_quotes_every_term_because_a_leading_hyphen_means_NOT()
    {
        // Unquoted, "-t:Install" asks MySQL to EXCLUDE the very thing being
        // looked for - a search that silently returns the opposite of what was
        // asked, on the exact strings this memory is full of.
        var (_, parameters) = SqlDialect.MySql.Search(new[] { "-t:Install" });

        Assert.Equal("\"-t:Install\"", parameters[0].Value);
    }

    [Fact]
    public void Postgres_and_MySql_do_full_text_and_the_other_two_say_they_do_not()
    {
        // Postgres and MySQL build their index in the schema with nothing
        // installed. SQL Server's CONTAINS needs the Full-Text feature on the
        // instance and Oracle's needs a CTXSYS index, and neither is a safe
        // assumption about somebody else's server - so they say so rather than
        // failing at startup.
        Assert.True(SqlDialect.PostgreSql.FullText);
        Assert.True(SqlDialect.MySql.FullText);
        Assert.False(SqlDialect.SqlServer.FullText);
        Assert.False(SqlDialect.Oracle.FullText);
    }

    [Fact]
    public void Oracle_names_its_parameters_the_way_its_driver_wants()
    {
        Assert.Equal(":id", SqlDialect.Oracle.Parameter("id"));
        Assert.Equal("id", SqlDialect.Oracle.ParameterName("id"));
        Assert.Equal("$id", SqlDialect.Sqlite.Parameter("id"));
        Assert.Equal("$id", SqlDialect.Sqlite.ParameterName("id"));
    }

    [Fact]
    public void Oracle_asks_about_its_table_in_upper_case()
    {
        // Unquoted identifiers fold to upper case there, so a lower-case
        // lookup in user_tables finds nothing and the store rebuilds a table
        // that already exists.
        Assert.Contains("'ATOMS'", SqlDialect.Oracle.TableExists("atoms"), StringComparison.Ordinal);
    }

    [Fact]
    public void No_dialect_leaves_a_LIKE_search_unparameterised()
    {
        foreach (var sql in new[] { SqlDialect.SqlServer, SqlDialect.Oracle, SqlDialect.Sqlite })
        {
            var (where, parameters) = sql.Search(new[] { "install" });
            Assert.Equal(1, parameters.Count);
            Assert.Contains("LIKE", where, StringComparison.Ordinal);
            Assert.DoesNotContain("install", where, StringComparison.OrdinalIgnoreCase);
        }
    }
}
