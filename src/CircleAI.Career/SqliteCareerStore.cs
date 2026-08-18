#nullable enable

// SqliteCareerStore.cs
//
// The profile, the job specs, and every version of every document ever approved.
//
// WHY A SCHEMA AND NOT A JSON BLOB. The whole point of the profile is that it is
// queryable and reusable: the same facts answer "draft me a CV for this security
// job" today and "which of my jobs match this one" next month. A blob can be
// rendered and cannot be reasoned about, and it is exactly what people already
// have — a CV.doc they edit and re-save until nobody knows which one they sent.
//
// APPROVED DOCUMENTS ARE KEPT AS BOTH. The rendered file is what the person owns
// and can send; the facts and the SELECTION that produced it are kept beside it,
// because a blob alone cannot be re-tailored. Applying for a second job should
// start from the last approval, not from nothing.
//
// It also makes the record honest: for any application there is a row saying
// which facts were claimed, to whom, and when. If somebody is ever asked "where
// did this come from", the answer exists.
//
// ON-DEVICE, AND THERE IS NO SYNC. See CareerProfile — employment history and
// contact details are personal information, and this file is the one most able
// to do harm if it travelled. Nothing here opens a socket.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace CircleAI.Career;

/// <summary>A job advertisement somebody wants to apply for.</summary>
/// <param name="Title">As advertised.</param>
/// <param name="Employer">Optional — many adverts do not say.</param>
/// <param name="Text">The full text, as received.</param>
/// <param name="Source">"whatsapp", "photo", "typed" — how it arrived.</param>
public sealed record JobSpec(
    string  Title,
    string? Employer,
    string  Text,
    string  Source        = "typed",
    DateTimeOffset? Added = null,
    long    Id            = 0);

/// <summary>A document that was generated, reviewed and approved.</summary>
/// <param name="SpecId">The job it was aimed at, or null for a general CV.</param>
/// <param name="Pdf">The rendered file, as owned by the person.</param>
/// <param name="SelectedFacts">
/// Which history and skill rows were used. What makes a later version
/// explainable rather than merely reproducible.
/// </param>
public sealed record ApprovedDocument(
    long?           SpecId,
    byte[]          Pdf,
    IReadOnlyList<long> SelectedFacts,
    DateTimeOffset  Approved,
    long            Id = 0);

/// <summary>The on-device career record.</summary>
public sealed class SqliteCareerStore : IDisposable
{
    private readonly SqliteConnection _db;
    private bool _disposed;

    /// <summary>Opens (and creates) the store at a path.</summary>
    /// <param name="databasePath">A file under the app's private storage.</param>
    public SqliteCareerStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var dir = System.IO.Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

        _db = new SqliteConnection($"Data Source={databasePath}");
        _db.Open();
        CreateSchema();
    }

    private void CreateSchema()
    {
        Execute("""
            -- ONE ROW, ENFORCED. A person has one career profile on their own
            -- phone; a table that permits two invites the bug where half the app
            -- reads the other one.
            CREATE TABLE IF NOT EXISTS profile (
                id        INTEGER PRIMARY KEY CHECK (id = 1),
                full_name TEXT NOT NULL DEFAULT '',
                headline  TEXT NOT NULL DEFAULT '',
                phone     TEXT,
                email     TEXT,
                location  TEXT,
                summary   TEXT
            );
            INSERT OR IGNORE INTO profile (id) VALUES (1);

            -- Organisation is nullable and formal is a flag: piece work, a family
            -- business and a season on a farm are all work history.
            CREATE TABLE IF NOT EXISTS history (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                role         TEXT NOT NULL,
                organisation TEXT,
                formal       INTEGER NOT NULL DEFAULT 1,
                start_text   TEXT,
                end_text     TEXT,
                achievements TEXT NOT NULL DEFAULT '',
                ordinal      INTEGER NOT NULL DEFAULT 0
            );

            -- evidence_history_id ties a skill to where it was used, so a CV can
            -- cite it instead of asserting a level nobody can check.
            CREATE TABLE IF NOT EXISTS skill (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                name                TEXT NOT NULL,
                years               REAL,
                evidence_history_id INTEGER REFERENCES history(id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS education (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                qualification TEXT NOT NULL,
                institution   TEXT,
                year          TEXT,
                completed     INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS certification (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                name    TEXT NOT NULL,
                issuer  TEXT,
                year    TEXT,
                expires TEXT
            );

            CREATE TABLE IF NOT EXISTS language (
                id    INTEGER PRIMARY KEY AUTOINCREMENT,
                name  TEXT NOT NULL,
                level TEXT
            );

            -- Specs are kept, not consumed. Applying to a similar job later
            -- should start from one that already worked.
            CREATE TABLE IF NOT EXISTS job_spec (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                title    TEXT NOT NULL,
                employer TEXT,
                body     TEXT NOT NULL,
                source   TEXT NOT NULL DEFAULT 'typed',
                added_utc TEXT NOT NULL
            );

            -- The document AND what went into it. selected_facts is why a second
            -- application can start from the first instead of from scratch.
            CREATE TABLE IF NOT EXISTS approved_document (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                spec_id        INTEGER REFERENCES job_spec(id) ON DELETE SET NULL,
                pdf            BLOB NOT NULL,
                selected_facts TEXT NOT NULL DEFAULT '',
                approved_utc   TEXT NOT NULL
            );
            """);
    }

    // ── profile ──────────────────────────────────────────────────────────────

    /// <summary>Everything known, as one object.</summary>
    public CareerProfile Load()
    {
        ThrowIfDisposed();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT full_name, headline, phone, email, location, summary FROM profile WHERE id = 1";
        using var r = cmd.ExecuteReader();

        var identity = r.Read()
            ? new ProfileIdentity(
                r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5))
            : new ProfileIdentity();
        r.Close();

        return new CareerProfile(
            identity, LoadHistory(), LoadSkills(), LoadEducation(),
            LoadCertifications(), LoadLanguages());
    }

    /// <summary>Replaces the identity fields, leaving everything else alone.</summary>
    public void SaveIdentity(ProfileIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ThrowIfDisposed();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE profile SET full_name = $n, headline = $h, phone = $p,
                               email = $e, location = $l, summary = $s
            WHERE id = 1
            """;
        cmd.Parameters.AddWithValue("$n", identity.FullName ?? "");
        cmd.Parameters.AddWithValue("$h", identity.Headline ?? "");
        cmd.Parameters.AddWithValue("$p", (object?)identity.Phone    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$e", (object?)identity.Email    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$l", (object?)identity.Location ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", (object?)identity.Summary  ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Adds a period of work and returns its id.</summary>
    public long AddHistory(ProfileHistory h)
    {
        ArgumentNullException.ThrowIfNull(h);
        ThrowIfDisposed();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history (role, organisation, formal, start_text, end_text, achievements, ordinal)
            VALUES ($r, $o, $f, $s, $e, $a, (SELECT COALESCE(MAX(ordinal), 0) + 1 FROM history));
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$r", h.Role);
        cmd.Parameters.AddWithValue("$o", (object?)h.Organisation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", h.Formal ? 1 : 0);
        cmd.Parameters.AddWithValue("$s", (object?)h.Start ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$e", (object?)h.End   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", string.Join("\n", h.Achievements ?? Array.Empty<string>()));
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Adds a skill, optionally pointing at where it was used.</summary>
    public long AddSkill(ProfileSkill s)
    {
        ArgumentNullException.ThrowIfNull(s);
        ThrowIfDisposed();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO skill (name, years, evidence_history_id) VALUES ($n, $y, $h);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$n", s.Name);
        cmd.Parameters.AddWithValue("$y", (object?)s.Years ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", (object?)s.EvidenceHistoryId ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Adds a qualification.</summary>
    public long AddEducation(ProfileEducation e)
    {
        ArgumentNullException.ThrowIfNull(e);
        ThrowIfDisposed();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO education (qualification, institution, year, completed) VALUES ($q, $i, $y, $c);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$q", e.Qualification);
        cmd.Parameters.AddWithValue("$i", (object?)e.Institution ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$y", (object?)e.Year ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", e.Completed ? 1 : 0);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Adds a licence, ticket or certificate.</summary>
    public long AddCertification(ProfileCertification c)
    {
        ArgumentNullException.ThrowIfNull(c);
        ThrowIfDisposed();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO certification (name, issuer, year, expires) VALUES ($n, $i, $y, $e);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$n", c.Name);
        cmd.Parameters.AddWithValue("$i", (object?)c.Issuer  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$y", (object?)c.Year    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$e", (object?)c.Expires ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Adds a spoken language.</summary>
    public long AddLanguage(ProfileLanguage l)
    {
        ArgumentNullException.ThrowIfNull(l);
        ThrowIfDisposed();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO language (name, level) VALUES ($n, $l); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", l.Name);
        cmd.Parameters.AddWithValue("$l", (object?)l.Level ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Removes one row of one kind. Used by "take that out" during review.</summary>
    public void Remove(string table, long id)
    {
        ThrowIfDisposed();
        if (table is not ("history" or "skill" or "education" or "certification" or "language"))
            throw new ArgumentException($"Not a removable table: {table}", nameof(table));

        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ── job specs and approvals ──────────────────────────────────────────────

    /// <summary>Keeps a job advert and returns its id.</summary>
    public long AddSpec(JobSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ThrowIfDisposed();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO job_spec (title, employer, body, source, added_utc)
            VALUES ($t, $e, $b, $s, $a);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$t", spec.Title);
        cmd.Parameters.AddWithValue("$e", (object?)spec.Employer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$b", spec.Text);
        cmd.Parameters.AddWithValue("$s", spec.Source);
        cmd.Parameters.AddWithValue("$a", (spec.Added ?? DateTimeOffset.UtcNow).ToString("O"));
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Every spec, newest first.</summary>
    public IReadOnlyList<JobSpec> Specs()
    {
        ThrowIfDisposed();
        var list = new List<JobSpec>();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, title, employer, body, source, added_utc FROM job_spec ORDER BY id DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new JobSpec(
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.GetString(3), r.GetString(4),
                DateTimeOffset.TryParse(r.GetString(5), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var t) ? t : null,
                r.GetInt64(0)));
        return list;
    }

    /// <summary>Stores an approved document and what went into it.</summary>
    public long Approve(long? specId, byte[] pdf, IReadOnlyList<long> selectedFacts)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ThrowIfDisposed();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO approved_document (spec_id, pdf, selected_facts, approved_utc)
            VALUES ($s, $p, $f, $a);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$s", (object?)specId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", pdf);
        cmd.Parameters.AddWithValue("$f", string.Join(",", selectedFacts ?? Array.Empty<long>()));
        cmd.Parameters.AddWithValue("$a", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Approved documents, newest first. The person's own record.</summary>
    public IReadOnlyList<ApprovedDocument> Approved()
    {
        ThrowIfDisposed();
        var list = new List<ApprovedDocument>();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, spec_id, pdf, selected_facts, approved_utc FROM approved_document ORDER BY id DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var facts = r.GetString(3)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.TryParse(s, out var v) ? v : 0)
                .Where(v => v != 0)
                .ToList();

            list.Add(new ApprovedDocument(
                r.IsDBNull(1) ? null : r.GetInt64(1),
                (byte[])r["pdf"], facts,
                DateTimeOffset.TryParse(r.GetString(4), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var t) ? t : DateTimeOffset.UtcNow,
                r.GetInt64(0)));
        }
        return list;
    }

    // ── reading the lists ────────────────────────────────────────────────────

    private IReadOnlyList<ProfileHistory> LoadHistory()
    {
        var list = new List<ProfileHistory>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id, role, organisation, formal, start_text, end_text, achievements
            FROM history ORDER BY ordinal DESC
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ProfileHistory(
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.GetInt64(3) != 0,
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.GetString(6).Split('\n', StringSplitOptions.RemoveEmptyEntries),
                r.GetInt64(0)));
        return list;
    }

    private IReadOnlyList<ProfileSkill> LoadSkills()
    {
        var list = new List<ProfileSkill>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, name, years, evidence_history_id FROM skill ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ProfileSkill(
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetDouble(2),
                r.IsDBNull(3) ? null : r.GetInt64(3),
                r.GetInt64(0)));
        return list;
    }

    private IReadOnlyList<ProfileEducation> LoadEducation()
    {
        var list = new List<ProfileEducation>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, qualification, institution, year, completed FROM education ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ProfileEducation(
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetInt64(4) != 0,
                r.GetInt64(0)));
        return list;
    }

    private IReadOnlyList<ProfileCertification> LoadCertifications()
    {
        var list = new List<ProfileCertification>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, name, issuer, year, expires FROM certification ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ProfileCertification(
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.GetInt64(0)));
        return list;
    }

    private IReadOnlyList<ProfileLanguage> LoadLanguages()
    {
        var list = new List<ProfileLanguage>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, name, level FROM language ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ProfileLanguage(
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.GetInt64(0)));
        return list;
    }

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _db.Dispose();
    }
}
