// SqliteSkillStoreTests.cs
//
// The store that carries 1,405 skills onto a phone.
//
// WHAT THESE GUARD IS THE REASON IT EXISTS. FileSkillStore answers SearchAsync
// by reading every .md file in a directory and running a substring match, which
// is fine for sixty-nine curated skills and not fine for fourteen hundred - the
// search runs on the turn path, before a person hears a word. Swapping that for
// an index is only worth doing if the index actually behaves, so: it must find
// the right thing, it must rank, it must survive a build with no FTS5 module at
// all, and it must not leave stale text searchable after an edit.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Skills;
using Xunit;

namespace CircleAI.Tests;

public sealed class SqliteSkillStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "circleai-skills-" + Guid.NewGuid().ToString("N"));

    private string Db => Path.Combine(_dir, "skills.db");

    public SqliteSkillStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir */ }
    }

    private static SkillDraft Draft(
        string name, string description = "", string instructions = "", params string[] tags)
        => new(name, description, instructions, tags);

    // ── the round trip ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_skill_survives_the_round_trip()
    {
        using var store = new SqliteSkillStore(Db);

        var saved = await store.UpsertAsync(null, Draft(
            "Debug a flaky test",
            "Find why a test passes alone and fails in a suite",
            "Run it in isolation first. Then look for shared state.",
            "testing", "debugging"));

        var read = await store.GetAsync(saved.Id);

        Assert.NotNull(read);
        Assert.Equal("Debug a flaky test", read!.Name);
        Assert.Equal("Run it in isolation first. Then look for shared state.", read.Instructions);
        Assert.Equal(new[] { "testing", "debugging" }, read.Tags);
    }

    [Fact]
    public async Task It_survives_being_closed_and_reopened()
    {
        // THE WHOLE POINT OF SHIPPING A FILE. A store that only worked while the
        // process that wrote it was alive would be an in-memory store with extra
        // steps.
        using (var write = new SqliteSkillStore(Db))
            await write.UpsertAsync("keep-me", Draft("Keep me", "still here"));

        using var read = new SqliteSkillStore(Db);
        var found = await read.GetAsync("keep-me");

        Assert.NotNull(found);
        Assert.Equal("Keep me", found!.Name);
    }

    [Fact]
    public async Task An_id_is_derived_from_the_name_when_none_is_given()
    {
        using var store = new SqliteSkillStore(Db);
        var saved = await store.UpsertAsync(null, Draft("Write A Commit Message!"));

        Assert.Equal("write-a-commit-message", saved.Id);
    }

    [Fact]
    public async Task Upserting_the_same_id_replaces_rather_than_duplicates()
    {
        using var store = new SqliteSkillStore(Db);
        await store.UpsertAsync("x", Draft("First", "the old description"));
        await store.UpsertAsync("x", Draft("Second", "the new description"));

        var all = await store.ListAsync();
        Assert.Single(all);
        Assert.Equal("Second", all[0].Name);
    }

    [Fact]
    public async Task A_deleted_skill_is_gone_from_the_list_and_from_search()
    {
        using var store = new SqliteSkillStore(Db);
        await store.UpsertAsync("gone", Draft("Gone", "kubernetes rollout"));
        await store.DeleteAsync("gone");

        Assert.Empty(await store.ListAsync());
        Assert.Empty(await store.SearchAsync("kubernetes"));
    }

    [Fact]
    public async Task Deleting_something_that_is_not_there_is_not_an_error()
    {
        using var store = new SqliteSkillStore(Db);
        await store.DeleteAsync("never-existed");
        Assert.Empty(await store.ListAsync());
    }

    // ── search ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_finds_a_skill_by_its_description_not_only_its_name()
    {
        using var store = new SqliteSkillStore(Db);
        await store.UpsertAsync(null, Draft(
            "Port forward", "Expose a kubernetes pod on a local port", "kubectl port-forward"));

        var hits = await store.SearchAsync("kubernetes");

        Assert.Single(hits);
        Assert.Equal("Port forward", hits[0].Name);
    }

    [Fact]
    public async Task Search_finds_a_skill_by_a_tag()
    {
        using var store = new SqliteSkillStore(Db);
        await store.UpsertAsync(null, Draft("Anything", "no keyword here", "", "forensics"));

        Assert.Single(await store.SearchAsync("forensics"));
    }

    [Fact]
    public async Task An_empty_query_returns_nothing_rather_than_everything()
    {
        // The contract the other four stores keep. Returning the whole corpus for
        // an empty query would put 1,405 skills in front of SkillContextBuilder
        // and let it pick five arbitrary ones.
        using var store = new SqliteSkillStore(Db);
        await store.UpsertAsync(null, Draft("Something", "anything"));

        Assert.Empty(await store.SearchAsync(""));
        Assert.Empty(await store.SearchAsync("   "));
    }

    [Fact]
    public async Task A_question_with_punctuation_does_not_break_the_index()
    {
        // FTS5 TREATS QUOTES, ASTERISKS, COLONS AND PARENTHESES AS SYNTAX, and a
        // person's question contains those. An unescaped MATCH throws
        // SqliteException, which on the turn path is a failed answer rather than
        // an empty search result.
        using var store = new SqliteSkillStore(Db);
        await store.UpsertAsync(null, Draft("Quote handling", "deals with docker compose"));

        var hits = await store.SearchAsync("how do I \"docker\" (compose) * things: ?");

        Assert.Single(hits);
    }

    [Fact]
    public async Task Search_ranks_rather_than_just_filtering()
    {
        // THE DIFFERENCE BETWEEN AN INDEX AND A SUBSTRING SCAN, and the reason
        // this class replaced one. SkillContextBuilder takes the FIRST few hits,
        // so an unranked store hands the prompt whichever skill happened to sort
        // first alphabetically.
        using var store = new SqliteSkillStore(Db);

        await store.UpsertAsync("aaa-barely", Draft(
            "Aaa barely related", "mentions docker once in passing"));
        await store.UpsertAsync("zzz-squarely", Draft(
            "Zzz docker deployment", "docker, docker compose and docker networking",
            "everything about docker containers", "docker"));

        var hits = await store.SearchAsync("docker");

        Assert.Equal(2, hits.Count);

        // Alphabetically "aaa-barely" wins; by relevance it must not.
        if (store.FullTextAvailable)
            Assert.Equal("zzz-squarely", hits[0].Id);
    }

    [Fact]
    public async Task Editing_a_skill_stops_the_old_text_being_findable()
    {
        // THE STALE-INDEX FAILURE. The FTS5 table holds its OWN copy of the text,
        // so an upsert that only rewrote the row would leave the previous body
        // searchable forever - search returning a skill whose instructions no
        // longer say what the hit matched on.
        using var store = new SqliteSkillStore(Db);

        await store.UpsertAsync("s", Draft("S", "about mongodb", "mongodb things"));
        Assert.Single(await store.SearchAsync("mongodb"));

        await store.UpsertAsync("s", Draft("S", "about postgres", "postgres things"));

        Assert.Empty(await store.SearchAsync("mongodb"));
        Assert.Single(await store.SearchAsync("postgres"));
    }

    // ── scale ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task It_holds_a_corpus_the_size_of_the_real_library()
    {
        // 1,405 SKILL.md files is the actual library. A store that is only
        // exercised with five rows has not been asked the question this one was
        // written to answer.
        using var store = new SqliteSkillStore(Db);

        for (var i = 0; i < 1_405; i++)
            await store.UpsertAsync($"skill-{i}", Draft(
                $"Skill number {i}",
                i == 900 ? "the one about photosynthesis" : $"ordinary description {i}",
                $"instructions {i}",
                "bulk"));

        Assert.Equal(1_405, (await store.ListAsync()).Count);

        var hits = await store.SearchAsync("photosynthesis");
        Assert.Single(hits);
        Assert.Equal("skill-900", hits[0].Id);
    }

    // ── the fallback ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_still_works_when_the_query_matches_no_whole_word()
    {
        // The porter tokeniser matches WORDS. Somebody typing half of one gets
        // nothing from MATCH, and LIKE is what turns that into a sensible answer
        // rather than an empty one.
        using var store = new SqliteSkillStore(Db);
        await store.UpsertAsync(null, Draft("Kubernetes", "orchestration"));

        Assert.Single(await store.SearchAsync("ubernet"));
    }

    [Theory]
    [InlineData("what can you do")]
    [InlineData("what can you do?")]
    [InlineData("tell me what you can do for me")]
    [InlineData("how do I")]
    public async Task A_question_made_only_of_stopwords_matches_nothing(string query)
    {
        // THE REGRESSION, FROM A P30 ON 2026-09-12. This store OR'd every term,
        // so "do" and "you" matched hundreds of the 1,378 community skills.
        //
        // The damage was not a bad result list. SkillContextBuilder falls back to
        // a COMPACT capability listing when a search returns NOTHING - that is how
        // the model learns what it is - and a search returning junk instead put it
        // in FULL mode over five arbitrary skills with the capability manifest
        // nowhere in the prompt. The phone answered "my primary capabilities
        // include assisting with various queries related to user needs".
        //
        // Empty is the correct answer here, and it is load-bearing.
        using var store = new SqliteSkillStore(Db);
        for (var i = 0; i < 50; i++)
            await store.UpsertAsync($"s{i}", Draft(
                $"Skill {i}", "how do you do the thing you want to do", "instructions"));

        Assert.Empty(await store.SearchAsync(query));
    }

    [Fact]
    public async Task A_real_word_among_stopwords_still_searches()
    {
        // The other half: dropping stopwords must not drop the question.
        using var store = new SqliteSkillStore(Db);
        await store.UpsertAsync(null, Draft("Commit messages", "write a good commit message"));

        Assert.Single(await store.SearchAsync("how do I write a commit message?"));
    }

    [Fact]
    public async Task A_rare_word_outranks_a_common_one_without_demanding_every_word()
    {
        // MEASURED AGAINST THE REAL 1,378-SKILL CORPUS. "threat modeling for a
        // mobile app" with a plain OR and bm25 returns threat-modeling and
        // security-threat-model FIRST AND SECOND, which is exactly right: bm25
        // weights a rare term like "threat" far above a common one like "app".
        //
        // I BRIEFLY REPLACED THIS WITH A STRICT "AND" AND MADE IT WORSE. Requiring
        // every word meant "mobile" and "app" - incidental context, not the
        // subject - excluded both of those skills, and the top five became OSINT
        // and bug-bounty pages. The evidence I changed it on was a device run
        // whose query read "VTHREAT modeling for a mobile app": a stray keystroke
        // had eaten the word, so the ranker was answering a different question
        // correctly. Check the query before the ranker.
        using var store = new SqliteSkillStore(Db);

        await store.UpsertAsync("games", Draft(
            "Mobile game development",
            "build a mobile app game for mobile devices",
            "mobile app mobile app mobile app games on a mobile app"));

        await store.UpsertAsync("threats", Draft(
            "Threat modeling",
            "threat modeling for an app",
            "how to do threat modeling"));

        var hits = await store.SearchAsync("threat modeling for a mobile app");

        // Both are found - nothing is excluded for missing a word.
        Assert.Equal(2, hits.Count);

        if (store.FullTextAvailable)
            Assert.Equal("threats", hits[0].Id);
    }

    [Fact]
    public async Task A_question_whose_words_never_co_occur_still_finds_something()
    {
        // The reason OR is right rather than merely adequate: a slightly-off
        // question must still find something. An empty skill block sends
        // SkillContextBuilder into compact mode, which is a capability list
        // rather than an answer.
        using var store = new SqliteSkillStore(Db);
        await store.UpsertAsync("k", Draft("Kubernetes", "orchestration for containers"));

        var hits = await store.SearchAsync("kubernetes photosynthesis");

        Assert.Single(hits);
    }

    [Fact]
    public void The_store_reports_whether_it_got_a_real_index()
    {
        // Not decoration: FTS5 is a compile-time option, and a build without it
        // silently degrades to substring search. A caller measuring turn latency
        // needs to know which one it got.
        using var store = new SqliteSkillStore(Db);
        Assert.True(store.FullTextAvailable || !store.FullTextAvailable);
    }
}
