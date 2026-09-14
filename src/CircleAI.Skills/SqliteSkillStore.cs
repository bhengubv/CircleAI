// SqliteSkillStore.cs
//
// Fourteen hundred skills, searched in one indexed query.
//
// WHY NOT FileSkillStore. It holds one .md per skill in a directory and answers
// SearchAsync by reading EVERY file and running a substring match. That is fine
// for the sixty-nine curated skills it was written for and it is not fine for
// the 1,405 in the library: the search runs on the turn path, before a person
// hears a word, and a directory walk plus 7.5 MB of reads is not something to do
// while somebody waits.
//
// So the corpus lives in SQLite with an FTS5 index over name, description, tags
// and instructions, ranked by bm25. FTS5 is a compile-time option and not every
// SQLite build has it, so the table is created inside a try and a LIKE fallback
// stands in when it is absent - slower, unranked, and correct. That is the exact
// shape SqliteAtomStore already uses in this repo, and the reason is the same:
// a missing module must degrade search, not break the app.
//
// WHAT THIS IS NOT. It does not decide WHICH skills reach the prompt - that is
// SkillContextBuilder, which caps the block at 1500 characters however many
// skills exist. Growing the corpus from 69 to 1,405 costs nothing in prefill.
// What it costs is search, which is what the index is for.

using Microsoft.Data.Sqlite;

namespace CircleAI.Skills;

/// <summary>An <see cref="ISkillStore"/> over a SQLite database.</summary>
public sealed class SqliteSkillStore : ISkillStore, IDisposable
{
    /// <summary>
    /// Bumped when the shape below changes.
    /// </summary>
    /// <remarks>
    /// DROP AND REBUILD, NOT MIGRATE, AND THAT IS SAFE HERE FOR ONE REASON: this
    /// database is DERIVED. It is built from SKILL.md files and shipped as an
    /// asset, so throwing it away costs an unpack, not somebody's data. The same
    /// call in SqliteAtomStore carries a much heavier justification because there
    /// the log is the durable half; here there is no durable half to protect.
    /// </remarks>
    private const int SchemaVersion = 1;

    private readonly SqliteConnection _conn;
    private readonly bool _fts;
    private readonly bool _identifyingOnly;
    private bool _disposed;

    /// <summary>Whether full-text search is available, or LIKE is standing in.</summary>
    public bool FullTextAvailable => _fts;

    /// <summary>Open or create the database at <paramref name="path"/>.</summary>
    /// <param name="identifyingMatchOnly">
    /// WHAT COUNTS AS A MATCH, AND IT IS A PRODUCT DECISION, NOT A STORE ONE.
    /// <para>
    /// Default false: a query matches a skill's name, tags OR description - the
    /// store's general contract, and what Search_finds_a_skill_by_its_description
    /// and the edit/punctuation/scale tests rely on.
    /// </para>
    /// <para>
    /// True narrows a match to name and tags - what a skill IS, not what its
    /// prose happens to say. The LIBRARY is opened this way, because its only
    /// runtime path is turn-time injection into every chat turn, and a P30 got
    /// hunt-ssrf injected for "What is the capital of France" - "capital"
    /// Porter-stems into "capitalize" inside that skill's description. A general
    /// question names no skill's task, so under this mode it pulls nothing.
    /// Measured 2026-09-14.
    /// </para>
    /// </param>
    public SqliteSkillStore(string path, bool identifyingMatchOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _identifyingOnly = identifyingMatchOnly;
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        Tune();
        ResetIfStale();
        CreateSchema();
        _fts = TryEnableFts();
    }

    /// <summary>WAL, so a reader and a writer stop blocking each other.</summary>
    private void Tune()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "PRAGMA journal_mode = WAL; " +
                "PRAGMA synchronous  = NORMAL; " +
                "PRAGMA temp_store   = MEMORY;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // An in-memory database has no journal to configure, and a read-only
            // mount will refuse. Neither is a reason not to start.
        }
    }

    /// <summary>Throw away an index this build no longer understands.</summary>
    /// <remarks>
    /// CREATE TABLE IF NOT EXISTS QUIETLY DECLINES TO CHANGE A TABLE. A shipped
    /// database whose columns moved would be read with the old shape and fail at
    /// the first query, on a phone, with nothing useful in the log - which is the
    /// failure SqliteAtomStore records having had.
    /// </remarks>
    private void ResetIfStale()
    {
        try
        {
            int found;
            using (var read = _conn.CreateCommand())
            {
                read.CommandText = "PRAGMA user_version;";
                found = Convert.ToInt32(read.ExecuteScalar() ?? 0);
            }
            if (found == SchemaVersion) return;

            using var drop = _conn.CreateCommand();
            drop.CommandText =
                "DROP TABLE IF EXISTS skills_fts; " +
                "DROP TABLE IF EXISTS skills; " +
                $"PRAGMA user_version = {SchemaVersion};";
            drop.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // A read-only database cannot be reset. Reading it with the shape it
            // has is still better than refusing to start.
        }
    }

    private void CreateSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS skills (
                id            TEXT PRIMARY KEY,
                name          TEXT NOT NULL,
                description   TEXT NOT NULL,
                instructions  TEXT NOT NULL,
                tags          TEXT NOT NULL,
                source        INTEGER NOT NULL,
                modified_utc  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_skills_name ON skills (name);
            """;
        cmd.ExecuteNonQuery();
    }

    private bool TryEnableFts()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS skills_fts USING fts5(
                    skill_id UNINDEXED,
                    name,
                    description,
                    tags,
                    instructions,
                    tokenize = 'porter'
                );
                """;
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            // No FTS5 module in this build. Search falls back to LIKE, which is
            // worse at ranking and fine at finding.
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, name, description, tags, source FROM skills ORDER BY name;";
        return Task.FromResult(ReadSummaries(cmd, cancellationToken));
    }

    /// <inheritdoc />
    public Task<SkillDetail?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult<SkillDetail?>(null);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, description, instructions, tags, source, modified_utc
            FROM   skills WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return Task.FromResult<SkillDetail?>(null);

        return Task.FromResult<SkillDetail?>(new SkillDetail(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            SplitTags(r.GetString(4)),
            (SkillSource)r.GetInt32(5),
            DateTimeOffset.TryParse(r.GetString(6), out var when) ? when : default));
    }

    /// <summary>Skills matching <paramref name="query"/>, best first.</summary>
    /// <remarks>
    /// THE HOT PATH, AND THE REASON THIS CLASS EXISTS. It runs on every turn,
    /// before the person hears anything, over 1,405 rows.
    /// <para>
    /// bm25() ranks lower-is-better, so ordering ascending puts the best match
    /// first. A malformed MATCH expression is a query problem rather than a store
    /// problem, so it falls through to LIKE rather than failing the turn.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<SkillSummary>> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<SkillSummary>>([]);

        // NOTHING WORTH SEARCHING FOR IS NOT THE SAME AS NO MATCH, and the gap
        // between them is what the model gets told about itself. See Stopwords.
        var terms = Terms(query);
        if (terms.Count == 0)
            return Task.FromResult<IReadOnlyList<SkillSummary>>([]);

        // A WORD IN FOUR SKILLS OUT OF FIVE IS NOT A SEARCH TERM.
        //
        // Stopwords is a fixed list and cannot know this corpus. "name" is on no
        // stopword list anywhere and appears in 79% of these 1,378 skills, so
        // "Name three colours" matched 1,082 of them and a P30 was handed
        // ui-ux-design, threejs and inspira-ui - 1,593 characters of web
        // development dropped into a 4096-token window on a 0.6B model. The
        // answer came back "Here is my name as an AI and the current weather as
        // shown on your screen." Measured 2026-09-13.
        //
        // NOT A RANKING CHANGE. bm25 already weights a rare term far above a
        // common one and gets the order right - OPEN-GAPS E15 is the scar from
        // "fixing" the ranker instead, and SkillRelevanceMeasure keeps that exact
        // query as a guard. This drops terms that carry no information BEFORE
        // the match, so the hit SET means something; what survives is ranked
        // exactly as it was.
        terms = Discriminating(terms);


        if (_fts)
        {
            // OR, RANKED BY bm25, AND NOT "AND" - WHICH I TRIED AND HAD TO BACK OUT.
            //
            // Measured against the real 1,378-skill corpus, "threat modeling for
            // a mobile app":
            //
            //   OR  -> threat-modeling, security-threat-model, security-compliance,
            //          app-security-engineering, config-hardening        (correct)
            //   AND -> osint-methodology, offensive-osint, bb-local-toolkit,
            //          bug-bounty                                        (wrong)
            //
            // AND requires every word, so "mobile" and "app" - which are
            // incidental context, not the subject - EXCLUDED the two skills
            // literally named for the thing being asked about. bm25 already does
            // the right job: a rare term like "threat" carries far more weight
            // than a common one like "app", so covering the rare words wins
            // without having to demand all of them.
            //
            // WHY I ADDED THE AND ANYWAY: a device run returned 1,106 hits of
            // game-development skills, and I diagnosed the ranking from it. The
            // query was "VTHREAT modeling for a mobile app" - a stray keystroke
            // from the test harness had eaten the word "threat", so the search
            // was ranking on "modeling/mobile/app" and game development was the
            // CORRECT answer to the question actually asked. The search was never
            // broken; the measurement was. Left here because the next person to
            // look at a bad result list should check the query before the ranker.
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    SELECT s.id, s.name, s.description, s.tags, s.source
                    FROM   skills_fts f
                    JOIN   skills s ON s.id = f.skill_id
                    WHERE  skills_fts MATCH $q
                    ORDER  BY bm25(skills_fts) ASC;
                    """;
                cmd.Parameters.AddWithValue("$q", SafeMatch(terms));
                var hits = ReadSummaries(cmd, cancellationToken);
                if (hits.Count > 0) return Task.FromResult(hits);
            }
            catch (SqliteException)
            {
                // A malformed MATCH is a query problem, not a store problem: fall
                // through to LIKE rather than failing the turn.
            }
        }

        return Task.FromResult(Like(terms, cancellationToken));
    }

    /// <summary>Substring search, for a build with no FTS5 and for a miss.</summary>
    /// <remarks>
    /// KEPT AS A FALLBACK FOR MISSES TOO, NOT ONLY FOR MISSING FTS5. The porter
    /// tokeniser matches words; somebody typing half a word, or a hyphenated
    /// identifier out of a tag, gets nothing from MATCH and something sensible
    /// from LIKE. Unranked is better than empty.
    /// </remarks>
    private IReadOnlyList<SkillSummary> Like(List<string> terms, CancellationToken ct)
    {
        if (terms.Count == 0) return [];

        using var cmd = _conn.CreateCommand();
        // Same scope as the FTS path — see Identifying — so relevance does not
        // depend on whether this build has FTS5.
        var where = string.Join(" OR ", terms.Select((_, i) => _identifyingOnly
            ? $"name LIKE $t{i} OR tags LIKE $t{i}"
            : $"name LIKE $t{i} OR description LIKE $t{i} OR tags LIKE $t{i}"));
        cmd.CommandText =
            $"SELECT id, name, description, tags, source FROM skills WHERE {where} ORDER BY name;";
        for (var i = 0; i < terms.Count; i++)
            cmd.Parameters.AddWithValue($"$t{i}", "%" + terms[i] + "%");

        return ReadSummaries(cmd, ct);
    }

    /// <summary>The words in a question that are worth searching for.</summary>
    /// <remarks>
    /// MEASURED ON A P30, 2026-09-12, AND IT BROKE THE ASSISTANT'S SELF-KNOWLEDGE.
    /// Asked "what can you do", this store OR'd every term - "do" and "you"
    /// included - and matched hundreds of the 1,378 community skills.
    /// <para>
    /// That is not just a bad result list. SkillContextBuilder switches to a
    /// COMPACT mode when a search returns NOTHING, listing capability names so the
    /// model at least knows what it is; a search returning junk instead puts it in
    /// FULL mode over five arbitrary community skills, and the capability manifest
    /// never reaches the prompt at all. The phone answered "my primary
    /// capabilities include assisting with various queries related to user needs",
    /// which is a model with nothing to go on.
    /// </para>
    /// <para>
    /// So a query made only of stopwords matches nothing, deliberately, and the
    /// builder's no-match path does its job. The list is small and English-only on
    /// purpose: it exists to stop "do you" matching a corpus, not to do
    /// linguistics, and dropping a real word costs one term of an OR.
    /// </para>
    /// </remarks>
    // Stopwords and Separators MOVED TO CircleAI.Core.SearchTerms. They used to
    // live here as a private list; then the memory store hit the same spurious-
    // match problem from the other side and needed the same list, and a copied
    // stopword list is a second owner that drifts. One owner now, in the
    // assembly both stores reference. See Terms below.
    /// <summary>
    /// Below this many skills, how often a word appears says nothing.
    /// </summary>
    /// <remarks>
    /// Not tuned against the shipping library - chosen so that a share is a
    /// share. The library is 1,378; a hand-built store in a test is one or two,
    /// and a manifest is a few dozen.
    /// </remarks>
    private const int SmallestCorpusWorthCounting = 100;

    /// <summary>How common a term is in this corpus, counted once and kept.</summary>
    /// <remarks>
    /// CACHED FOR THE LIFE OF THE STORE because the library is shipped read-only
    /// and this is the hot path - it runs on every turn, before the person hears
    /// anything. Cleared by a write, since a write can change the answer.
    /// </remarks>
    private readonly Dictionary<string, int> _seenIn = new(StringComparer.OrdinalIgnoreCase);
    private int _corpus = -1;

    /// <summary>Terms worth searching for: the ones not in most of the library.</summary>
    /// <remarks>
    /// NEVER RETURNS EMPTY WHEN IT WAS GIVEN SOMETHING. A question whose every
    /// word is common is still a question, and answering it with nothing would
    /// be strictly worse than the flooding this exists to stop - so the rarest
    /// term survives. That keeps the floor at today's behaviour.
    /// </remarks>
    private List<string> Discriminating(List<string> terms)
    {
        if (terms.Count <= 1) return terms;

        var total = Corpus();

        // A SHARE OF NOTHING IS NOT A FREQUENCY, AND THREE TESTS CAUGHT THIS.
        //
        // The first cut applied the threshold whatever the corpus size. In a
        // store holding one skill, every word that matches it is in 100% of the
        // library and was dropped as noise - so "how do I docker compose things"
        // threw away "docker" and "compose", kept "things", and found nothing.
        // In a store of two, "threat" and "modeling" sat at 50% and went the
        // same way.
        //
        // 79% of 1,378 is evidence. 50% of two is an accident of there being two.
        // Below this floor the filter is off entirely, which is exactly the
        // behaviour that existed before it.
        if (total < SmallestCorpusWorthCounting) return terms;
        if (total <= 0) return terms;

        var kept = new List<string>(terms.Count);
        var rarest = terms[0];
        var rarestSeen = int.MaxValue;

        foreach (var term in terms)
        {
            var seen = SeenIn(term);
            if (seen < rarestSeen) { rarestSeen = seen; rarest = term; }

            // A THIRD OF THE LIBRARY IS THE LINE. Past it a term is not
            // narrowing anything: it is a word the corpus happens to like.
            if ((double)seen / total <= 0.33) kept.Add(term);
        }

        return kept.Count > 0 ? kept : [rarest];
    }

    private int Corpus()
    {
        if (_corpus >= 0) return _corpus;
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM skills;";
            _corpus = Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqliteException) { _corpus = 0; }
        return _corpus;
    }

    private int SeenIn(string term)
    {
        if (_seenIn.TryGetValue(term, out var cached)) return cached;

        var count = 0;
        if (_fts)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM skills_fts WHERE skills_fts MATCH $q;";
                cmd.Parameters.AddWithValue("$q", SafeMatch([term]));
                count = Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (SqliteException)
            {
                // Unknown frequency: treat it as rare, so the term is kept and
                // behaviour falls back to what it was.
                count = 0;
            }
        }

        _seenIn[term] = count;
        return count;
    }

    /// <summary>The searchable words of a query, stopwords removed.</summary>
    /// <remarks>
    /// ONE OWNER: CircleAI.Core.SearchTerms, which the memory store also uses,
    /// so the two cannot drift.
    /// </remarks>
    private static List<string> Terms(string query)
        => CircleAI.Core.SearchTerms.Significant(query);

    /// <summary>An FTS5 MATCH expression that cannot be a syntax error.</summary>
    /// <remarks>
    /// FTS5 treats quotes, asterisks, colons, parentheses, NEAR, AND, OR and NOT
    /// as syntax. A person's question contains those, so every term is wrapped in
    /// double quotes as a literal phrase and embedded quotes are doubled.
    /// </remarks>
    /// <summary>
    /// The columns that say what a skill IS, rather than what it goes on to say.
    /// </summary>
    /// <remarks>
    /// THE TWO SEARCH PATHS IN THIS CLASS DISAGREED, AND THE FTS ONE WAS THE
    /// OUTLIER. The LIKE fallback a few lines below has always matched
    /// `name`, `description`, `tags` and never `instructions`; the FTS path
    /// matched all four. So whether a skill was considered relevant depended on
    /// whether the device had FTS5 compiled in.
    ///
    /// It matters because `instructions` is the whole body text of a SKILL.md.
    /// Asked "Name three colours", the phone was handed ui-ux-design, threejs
    /// and inspira-ui - all genuine lexical matches, because the word "colours"
    /// appears somewhere inside a web-design skill's body. None of them is ABOUT
    /// colours. A skill is relevant to a question when the question is about
    /// what the skill IS, and its name, description and tags are what say so.
    ///
    /// Measured against the shipping 1,378-skill library - SkillRelevanceMeasure
    /// holds the numbers, including OPEN-GAPS E15's query as a guard, because
    /// "threat modeling" must still find threat-modeling: that skill matches on
    /// its NAME, which is exactly the distinction being drawn here.
    /// </remarks>
    /// <summary>The FTS5 columns a match is scoped to — see the constructor.</summary>
    private string Identifying => _identifyingOnly ? "{name tags}" : "{name description tags}";

    /// <summary>An FTS5 MATCH expression that cannot be a syntax error.</summary>
    /// <remarks>Instance, not static: the column scope depends on this store's mode.</remarks>
    private string SafeMatch(IEnumerable<string> terms)
        => Identifying + " : ("
         + string.Join(" OR ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""))
         + ")";

    private static IReadOnlyList<SkillSummary> ReadSummaries(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<SkillSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            list.Add(new SkillSummary(
                r.GetString(0), r.GetString(1), r.GetString(2),
                SplitTags(r.GetString(3)),
                (SkillSource)r.GetInt32(4)));
        }
        return list;
    }

    // ------------------------------------------------------------------
    // Writing
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<SkillDetail> UpsertAsync(
        string? id, SkillDraft draft, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(draft);

        // THE FREQUENCY CACHE IS NOW STALE. It is what decides which words are
        // worth searching for, and a write changes the corpus it was counted
        // against - see Discriminating.
        _seenIn.Clear();
        _corpus = -1;


        var key = string.IsNullOrWhiteSpace(id) ? Slug(draft.Name) : id!.Trim();
        var when = DateTimeOffset.UtcNow;
        var tags = string.Join(",", draft.Tags ?? []);

        using var tx = _conn.BeginTransaction();

        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO skills (id, name, description, instructions, tags, source, modified_utc)
                VALUES ($id, $name, $desc, $ins, $tags, $src, $when)
                ON CONFLICT(id) DO UPDATE SET
                    name = $name, description = $desc, instructions = $ins,
                    tags = $tags, modified_utc = $when;
                """;
            cmd.Parameters.AddWithValue("$id", key);
            cmd.Parameters.AddWithValue("$name", draft.Name ?? "");
            cmd.Parameters.AddWithValue("$desc", draft.Description ?? "");
            cmd.Parameters.AddWithValue("$ins", draft.Instructions ?? "");
            cmd.Parameters.AddWithValue("$tags", tags);
            // FILE, NOT SOME NEW "DATABASE" SOURCE. SkillSource says where a
            // skill CAME FROM, and every row in here was parsed out of a
            // SKILL.md - the database is where it is kept, not its origin. A
            // fourth enum member would have made the store's storage choice
            // visible to callers that only want to know whose skill it is.
            cmd.Parameters.AddWithValue("$src", (int)SkillSource.File);
            cmd.Parameters.AddWithValue("$when", when.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        if (_fts) WriteFts(tx, key, draft, tags);

        tx.Commit();

        return Task.FromResult(new SkillDetail(
            key, draft.Name ?? "", draft.Description ?? "", draft.Instructions ?? "",
            draft.Tags ?? [], SkillSource.File, when));
    }

    /// <summary>
    /// Keep the index in step with the table.
    /// </summary>
    /// <remarks>
    /// DELETE THEN INSERT, because an external-content FTS5 table is not what
    /// this is - the index holds its own copy, and an upsert that only wrote the
    /// row would leave the old text searchable forever. That is the failure mode
    /// where search returns a skill whose body no longer says what the hit
    /// matched on.
    /// </remarks>
    private void WriteFts(SqliteTransaction tx, string key, SkillDraft draft, string tags)
    {
        using var del = _conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM skills_fts WHERE skill_id = $id;";
        del.Parameters.AddWithValue("$id", key);
        del.ExecuteNonQuery();

        using var ins = _conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO skills_fts (skill_id, name, description, tags, instructions)
            VALUES ($id, $name, $desc, $tags, $ins);
            """;
        ins.Parameters.AddWithValue("$id", key);
        ins.Parameters.AddWithValue("$name", draft.Name ?? "");
        ins.Parameters.AddWithValue("$desc", draft.Description ?? "");
        ins.Parameters.AddWithValue("$tags", tags);
        ins.Parameters.AddWithValue("$ins", draft.Instructions ?? "");
        ins.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(id)) return Task.CompletedTask;

        // THE FREQUENCY CACHE IS NOW STALE. It is what decides which words are
        // worth searching for, and a write changes the corpus it was counted
        // against - see Discriminating.
        _seenIn.Clear();
        _corpus = -1;


        using var tx = _conn.BeginTransaction();
        foreach (var sql in new[]
        {
            "DELETE FROM skills WHERE id = $id;",
            "DELETE FROM skills_fts WHERE skill_id = $id;",
        })
        {
            if (sql.Contains("_fts") && !_fts) continue;
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------

    /// <summary>A stable id from a name, matching the other stores' shape.</summary>
    private static string Slug(string name)
    {
        var chars = (name ?? "").Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        return slug.Length == 0 ? Guid.NewGuid().ToString("N")[..8] : slug;
    }

    private static IReadOnlyList<string> SplitTags(string tags)
        => tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // CLEARPOOL, OR THE FILE STAYS LOCKED. Microsoft.Data.Sqlite pools
        // connections, so disposing one does not necessarily close the handle -
        // which on Windows means a test that deletes its temp database fails, and
        // on Android means an unpack cannot replace the file.
        _conn.Close();
        SqliteConnection.ClearPool(_conn);
        _conn.Dispose();
    }
}
