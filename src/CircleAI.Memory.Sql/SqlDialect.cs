// SqlDialect.cs
//
// The five engines, as the handful of things they actually disagree about.
//
// THE DIFFERENCES ARE SMALLER THAN THEY LOOK. An atom store needs a table, an
// upsert, a keyword search and a row limit. Everything else - connection
// pooling, drivers, authentication - belongs to whoever owns the server, not
// to us. So a dialect is four or five overrides, and adding a sixth engine is
// a class rather than a project.
//
// NO DRIVER PACKAGES LIVE HERE. The caller hands in an open DbConnection, so
// this project depends on nothing but System.Data.Common. That keeps Oracle's
// driver and its licence out of a phone build, keeps Npgsql out of a SQL Server
// deployment, and means adding an engine we did not anticipate needs no change
// on our side at all.
//
// TIMESTAMPS ARE ISO-8601 STRINGS, not native date types. Five engines have
// five opinions about time zones, precision and what a driver hands back, and
// every one of those opinions is a way for a memory to come back an hour wrong.
// A sortable string is the same everywhere and reads correctly in a dump.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Memory.Sql;

/// <summary>What one database engine wants, where they differ.</summary>
public abstract class SqlDialect
{
    /// <summary>PostgreSQL, with real full-text search.</summary>
    public static SqlDialect PostgreSql { get; } = new PostgreSqlDialect();

    /// <summary>SQL Server.</summary>
    public static SqlDialect SqlServer { get; } = new SqlServerDialect();

    /// <summary>MySQL and MariaDB, with a FULLTEXT index.</summary>
    public static SqlDialect MySql { get; } = new MySqlDialect();

    /// <summary>Oracle Database.</summary>
    public static SqlDialect Oracle { get; } = new OracleDialect();

    /// <summary>SQLite through the same ADO path, which is how this is tested.</summary>
    public static SqlDialect Sqlite { get; } = new SqliteDialect();

    /// <summary>What this engine is called, for a diagnostic.</summary>
    public abstract string Name { get; }

    /// <summary>How a parameter is written in SQL: <c>@name</c>, <c>:name</c>.</summary>
    public virtual string Parameter(string name) => "@" + name;

    /// <summary>How a parameter is named on a DbParameter, which is not always the same.</summary>
    /// <remarks>
    /// ADO drivers disagree about whether the prefix belongs on the name.
    /// Npgsql and SqlClient accept it either way; Oracle's does not want it.
    /// </remarks>
    public virtual string ParameterName(string name) => name;

    /// <summary>How an identifier is escaped, because "text" is a keyword somewhere.</summary>
    public virtual string Quote(string identifier) => "\"" + identifier + "\"";

    /// <summary>The column type for prose - a decision, a challenge.</summary>
    protected virtual string TextType => "TEXT";

    /// <summary>The column type for a short tag - a kind, an id, a machine name.</summary>
    protected virtual string TagType(int length) => $"VARCHAR({length})";

    /// <summary>The column type for a count.</summary>
    protected virtual string IntType => "INTEGER";

    /// <summary>A row cap, appended to a SELECT.</summary>
    public virtual string Limit(int rows) => $"LIMIT {rows}";

    /// <summary>
    /// Whether this engine's keyword search is better than LIKE.
    /// </summary>
    /// <remarks>
    /// EXPOSED RATHER THAN ASSUMED. Recall quality is materially different
    /// between a real index and a substring scan, and somebody looking at a
    /// thin result should be able to see which one ran.
    /// </remarks>
    public virtual bool FullText => false;

    /// <summary>
    /// A WHERE clause matching the search terms, and the parameters it needs.
    /// </summary>
    /// <remarks>
    /// LIKE is the floor, not an excuse. It is slower and cruder at ranking and
    /// it is perfectly capable of finding things, which is why a store on an
    /// engine with no usable full-text index still works rather than throwing.
    /// </remarks>
    public virtual (string Where, IReadOnlyList<(string Name, object Value)> Parameters) Search(
        IReadOnlyList<string> terms)
    {
        var parameters = new List<(string, object)>();
        var clauses = new List<string>();

        for (var i = 0; i < terms.Count; i++)
        {
            var p = Parameter($"t{i}");
            clauses.Add(
                $"{Quote("text")} LIKE {p} OR {Quote("subject")} LIKE {p} OR {Quote("challenge")} LIKE {p}");
            parameters.Add(($"t{i}", "%" + terms[i] + "%"));
        }

        return ("(" + string.Join(") OR (", clauses) + ")", parameters);
    }

    /// <summary>Statements that must run once, after the table exists.</summary>
    /// <remarks>
    /// Where a full-text index is created. Each is run on its own and a failure
    /// is swallowed: a server that will not build the index still has to serve
    /// a memory, and it does that through <see cref="Search"/>'s LIKE floor.
    /// </remarks>
    public virtual IReadOnlyList<string> Indexes(string table) => new[]
    {
        $"CREATE INDEX ix_atoms_subject ON {Quote(table)} ({Quote("subject")}, {Quote("superseded_by")})",
        $"CREATE INDEX ix_atoms_kind ON {Quote(table)} ({Quote("kind")}, {Quote("superseded_by")})",
    };

    /// <summary>The table, as this engine spells it.</summary>
    public virtual string CreateTable(string table) => $"""
        CREATE TABLE {Quote(table)} (
            {Quote("id")}                 {TagType(32)}  NOT NULL PRIMARY KEY,
            {Quote("kind")}               {TagType(32)}  NOT NULL,
            {Quote("text")}               {TextType}     NOT NULL,
            {Quote("subject")}            {TagType(200)},
            {Quote("source_episode")}     {TagType(32)},
            {Quote("recorded_at_utc")}    {TagType(40)}  NOT NULL,
            {Quote("corrections")}        {IntType}      NOT NULL,
            {Quote("last_corrected_utc")} {TagType(40)},
            {Quote("superseded_by")}      {TagType(32)},
            {Quote("challenge")}          {TextType},
            {Quote("outcome")}            {TagType(16)},
            {Quote("verify")}             {TextType},
            {Quote("verified_at_utc")}    {TagType(40)},
            {Quote("verified_ok")}        {IntType},
            {Quote("machine")}            {TagType(120)}
        )
        """;

    /// <summary>Whether the table is already there.</summary>
    /// <remarks>
    /// Asked rather than guarded by IF NOT EXISTS, because Oracle has no such
    /// clause and SQL Server's is a version away from being portable.
    /// </remarks>
    public abstract string TableExists(string table);
}

// ----------------------------------------------------------------------
// PostgreSQL
// ----------------------------------------------------------------------

file sealed class PostgreSqlDialect : SqlDialect
{
    public override string Name => "PostgreSQL";

    public override string TableExists(string table) =>
        $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{table}'";

    // Postgres full-text needs nothing installed and no external catalogue: a
    // generated tsvector column and a GIN index, both in the schema. That is
    // why it gets the real thing where SQL Server and Oracle do not.
    public override bool FullText => true;

    public override IReadOnlyList<string> Indexes(string table) => new[]
    {
        $"ALTER TABLE {Quote(table)} ADD COLUMN {Quote("search")} tsvector " +
        $"GENERATED ALWAYS AS (to_tsvector('english', " +
        $"coalesce({Quote("text")}, '') || ' ' || coalesce({Quote("subject")}, '') || ' ' || " +
        $"coalesce({Quote("challenge")}, ''))) STORED",

        $"CREATE INDEX ix_atoms_search ON {Quote(table)} USING GIN ({Quote("search")})",
        $"CREATE INDEX ix_atoms_subject ON {Quote(table)} ({Quote("subject")}, {Quote("superseded_by")})",
        $"CREATE INDEX ix_atoms_kind ON {Quote(table)} ({Quote("kind")}, {Quote("superseded_by")})",
    };

    public override (string, IReadOnlyList<(string, object)>) Search(IReadOnlyList<string> terms)
    {
        // websearch_to_tsquery takes what a person would type and does not
        // throw on punctuation - which matters when half the memory is about
        // flags like -t:InstallKeepingData.
        var query = string.Join(" or ", terms);
        return ($"{Quote("search")} @@ websearch_to_tsquery('english', {Parameter("q")})",
                new[] { ("q", (object)query) });
    }
}

// ----------------------------------------------------------------------
// SQL Server
// ----------------------------------------------------------------------

file sealed class SqlServerDialect : SqlDialect
{
    public override string Name => "SQL Server";
    public override string Quote(string identifier) => "[" + identifier + "]";
    protected override string TextType => "NVARCHAR(MAX)";
    protected override string TagType(int length) => $"NVARCHAR({length})";
    protected override string IntType => "INT";

    // TOP would be simpler; OFFSET/FETCH is what composes with an ORDER BY that
    // is written by the caller rather than pasted in.
    public override string Limit(int rows) => $"OFFSET 0 ROWS FETCH NEXT {rows} ROWS ONLY";

    public override string TableExists(string table) =>
        $"SELECT COUNT(*) FROM sys.tables WHERE name = '{table}'";

    // NOT full text on purpose: CONTAINS needs the Full-Text Search feature
    // installed on the instance and a catalogue created, and neither is a safe
    // assumption about somebody else's server. LIKE finds things; a store that
    // throws at startup because a feature is absent does not.
}

// ----------------------------------------------------------------------
// MySQL and MariaDB
// ----------------------------------------------------------------------

file sealed class MySqlDialect : SqlDialect
{
    public override string Name => "MySQL";
    public override string Quote(string identifier) => "`" + identifier + "`";
    protected override string TextType => "TEXT";
    protected override string IntType => "INT";

    public override string TableExists(string table) =>
        $"SELECT COUNT(*) FROM information_schema.tables " +
        $"WHERE table_schema = DATABASE() AND table_name = '{table}'";

    // A FULLTEXT index on InnoDB needs nothing installed, so MySQL gets the
    // real thing alongside Postgres.
    public override bool FullText => true;

    public override IReadOnlyList<string> Indexes(string table) => new[]
    {
        $"CREATE FULLTEXT INDEX ix_atoms_search ON {Quote(table)} " +
        $"({Quote("text")}, {Quote("subject")}, {Quote("challenge")})",

        $"CREATE INDEX ix_atoms_subject ON {Quote(table)} ({Quote("subject")}, {Quote("superseded_by")})",
        $"CREATE INDEX ix_atoms_kind ON {Quote(table)} ({Quote("kind")}, {Quote("superseded_by")})",
    };

    public override (string, IReadOnlyList<(string, object)>) Search(IReadOnlyList<string> terms)
    {
        // Boolean mode, and every term quoted: an unquoted term starting with a
        // hyphen means NOT in this syntax, so "-t:Install" would ask MySQL to
        // exclude the very thing being looked for.
        var query = string.Join(" ", terms.Select(t => "\"" + t.Replace("\"", " ") + "\""));
        return ($"MATCH ({Quote("text")}, {Quote("subject")}, {Quote("challenge")}) " +
                $"AGAINST ({Parameter("q")} IN BOOLEAN MODE)",
                new[] { ("q", (object)query) });
    }
}

// ----------------------------------------------------------------------
// Oracle
// ----------------------------------------------------------------------

file sealed class OracleDialect : SqlDialect
{
    public override string Name => "Oracle";
    public override string Parameter(string name) => ":" + name;
    public override string ParameterName(string name) => name;
    protected override string TextType => "CLOB";
    protected override string TagType(int length) => $"VARCHAR2({length})";
    protected override string IntType => "NUMBER(10)";

    public override string Limit(int rows) => $"FETCH FIRST {rows} ROWS ONLY";

    public override string TableExists(string table) =>
        $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{table.ToUpperInvariant()}'";

    // NOT full text on purpose: Oracle Text is a separate index type owned by
    // CTXSYS, and creating one needs a privilege a memory has no business
    // asking for. LIKE on a CLOB is slower and it works.
}

// ----------------------------------------------------------------------
// SQLite through ADO
// ----------------------------------------------------------------------

file sealed class SqliteDialect : SqlDialect
{
    public override string Name => "SQLite";
    public override string Parameter(string name) => "$" + name;
    public override string ParameterName(string name) => "$" + name;

    public override string TableExists(string table) =>
        $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}'";

    // No FTS5 here on purpose. SqliteAtomStore is the SQLite implementation and
    // it uses FTS5; this dialect exists so the shared ADO path can be run
    // against a real engine in a test rather than only reasoned about.
}
