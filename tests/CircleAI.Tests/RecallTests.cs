// RecallTests.cs
//
// The memory has to answer the question it was built for.
//
// THE CASE THESE TESTS USE IS REAL. Deploying CircleAI to the P30 wiped 817 MB
// of downloaded models on every build for a full day, because "dotnet build
// -t:Install" uninstalls first when EmbedAssembliesIntoApk is on. That fact was
// discovered, written down, and then not present at the next deploy - which is
// exactly the failure this store exists to stop. So the fixture is that day,
// and the test is whether the store would have said something before the
// command ran.

using System;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Memory;
using Xunit;

namespace CircleAI.Tests;

public class RecallTests
{
    private static SqliteAtomStore NewStore() => new("Data Source=:memory:");

    private static MemoryAtom Atom(
        AtomKind kind,
        string text,
        string? subject = null,
        int corrections = 0,
        bool? verifiedOk = null) => new()
    {
        Kind = kind,
        Text = text,
        Subject = subject,
        Corrections = corrections,
        VerifiedOk = verifiedOk,
        VerifiedAtUtc = verifiedOk is null ? null : DateTimeOffset.UtcNow,
    };

    // ------------------------------------------------------------------
    // The store
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_atom_comes_back_for_the_situation_it_was_filed_under()
    {
        using var store = NewStore();
        await store.AddAsync(Atom(AtomKind.Fact,
            "-t:Install uninstalls first, wiping the models", "deploy:android"));

        var found = await store.MatchAsync(new Situation("deploy", "android"));

        Assert.Single(found);
    }

    [Fact]
    public async Task A_narrower_situation_still_finds_the_broader_atom()
    {
        // THE ROLL-UP MATTERS. An atom filed under "deploy:android" is written
        // for every Android deploy, and a situation that knows which handset it
        // is on must not become blind to it.
        using var store = NewStore();
        await store.AddAsync(Atom(AtomKind.Ruling,
            "Never wipe the models to ship a UI change", "deploy:android"));

        var found = await store.MatchAsync(new Situation("deploy", "android/p30"));

        Assert.Single(found);
    }

    [Fact]
    public async Task Keyword_search_finds_an_atom_with_no_subject_at_all()
    {
        // Not everything worth keeping arrives neatly filed. Words are the
        // fallback, and on a phone with no embedding model they are also the
        // only mechanism - so they cannot be an afterthought.
        using var store = NewStore();
        await store.AddAsync(Atom(AtomKind.Fact,
            "InstallKeepingData preserves the data directory"));

        var found = await store.MatchAsync(
            new Situation("deploy", "android", Text: "InstallKeepingData"));

        Assert.Single(found);
    }

    [Fact]
    public async Task A_superseded_atom_stops_being_an_answer_but_stays_readable()
    {
        using var store = NewStore();
        var wrong = Atom(AtomKind.Fact, "AndroidPreserveUserData defaults false", "deploy:android");
        await store.AddAsync(wrong);

        var right = await store.SupersedeAsync(wrong.Id,
            Atom(AtomKind.Fact, "It defaults true, but fast deployment is off", "deploy:android"));

        var found = await store.MatchAsync(new Situation("deploy", "android"));

        Assert.Single(found);
        Assert.Equal(right.Id, found[0].Id);

        // Still there, so the decision can be traced rather than just trusted.
        var old = await store.GetAsync(wrong.Id);
        Assert.NotNull(old);
        Assert.False(old!.IsCurrent);
        Assert.Equal(right.Id, old.SupersededBy);
    }

    [Fact]
    public async Task Superseding_counts_the_correction()
    {
        // The count is the signal that makes a much-corrected atom outrank a
        // fresh one, so losing it on replacement would defeat the ranking.
        using var store = NewStore();
        var first = Atom(AtomKind.Preference, "Answer first", "style");
        await store.AddAsync(first);

        var second = await store.SupersedeAsync(first.Id, Atom(AtomKind.Preference, "Answer first, then explain", "style"));
        var third = await store.SupersedeAsync(second.Id, Atom(AtomKind.Preference, "Answer first. Be brief.", "style"));

        Assert.Equal(1, second.Corrections);
        Assert.Equal(2, third.Corrections);
    }

    [Fact]
    public async Task Superseding_keeps_the_kind_when_the_replacement_does_not_name_one()
    {
        // AtomKind.Fact is the default on a bare MemoryAtom, so a caller who
        // only restates the text would silently demote a ruling and lose the
        // reason it ranked first.
        using var store = NewStore();
        var ruling = Atom(AtomKind.Ruling, "The two apps must be identical", "design");
        await store.AddAsync(ruling);

        var restated = await store.SupersedeAsync(ruling.Id,
            new MemoryAtom { Text = "The two apps must be identical, to the tee" });

        Assert.Equal(AtomKind.Ruling, restated.Kind);
        Assert.Equal("design", restated.Subject);
    }

    // ------------------------------------------------------------------
    // The ranking
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_ruling_outranks_a_preference_on_the_same_situation()
    {
        using var store = NewStore();
        await store.AddAsync(Atom(AtomKind.Preference, "Prefer the shorter command", "deploy:android"));
        await store.AddAsync(Atom(AtomKind.Ruling, "Never wipe the models to ship a UI change", "deploy:android"));

        var recall = new Recall(store);
        var result = await recall.ForAsync(new Situation("deploy", "android"));

        Assert.Equal(AtomKind.Ruling, result.Atoms[0].Kind);
    }

    [Fact]
    public async Task What_has_been_corrected_most_arrives_first()
    {
        // THE ONE THING THE AGENT COULD NEVER HAVE JUDGED. It did not see the
        // corrections coming, which is the same reason it would not have
        // recorded them.
        using var store = NewStore();
        await store.AddAsync(Atom(AtomKind.Fact, "The APK is signed in Release", "deploy:android"));
        await store.AddAsync(Atom(AtomKind.Fact,
            "-t:Install wipes the models", "deploy:android", corrections: 3));

        var recall = new Recall(store);
        var result = await recall.ForAsync(new Situation("deploy", "android"));

        Assert.Contains("wipes the models", result.Atoms[0].Text);
    }

    [Fact]
    public async Task Relationship_is_never_quoted_back_at_the_person()
    {
        // It shapes tone and how much to ask. Repeating somebody's own manner
        // to them is not recall.
        using var store = NewStore();
        await store.AddAsync(Atom(AtomKind.Relationship, "Blunt. Hates being asked twice.", "style"));
        await store.AddAsync(Atom(AtomKind.Preference, "Answer first, then explain", "style"));

        var recall = new Recall(store);
        var result = await recall.ForAsync(new Situation("style"));

        Assert.All(result.Atoms, a => Assert.NotEqual(AtomKind.Relationship, a.Kind));
        Assert.Single(result.Tone);
    }

    [Fact]
    public async Task A_stale_fact_still_surfaces_carrying_its_doubt()
    {
        // Hiding it leaves somebody acting on the belief they already had,
        // which is worse than showing a doubt.
        using var store = NewStore();
        await store.AddAsync(Atom(AtomKind.Fact,
            "The brain is 548 MB", "deploy:android", verifiedOk: false));

        var recall = new Recall(store);
        var result = await recall.ForAsync(new Situation("deploy", "android"));

        Assert.Single(result.Atoms);
        Assert.True(result.Atoms[0].IsStale);
    }

    [Fact]
    public async Task Recall_stays_inside_its_budget()
    {
        // On a phone the context window is the scarcest thing in the building.
        using var store = NewStore();
        for (var i = 0; i < 20; i++)
            await store.AddAsync(Atom(AtomKind.Fact, $"Fact number {i} about deploying", "deploy:android"));

        var recall = new Recall(store);
        var result = await recall.ForAsync(new Situation("deploy", "android"), new RecallBudget(MaxAtoms: 3));

        Assert.Equal(3, result.Atoms.Count);
    }

    [Fact]
    public async Task Nothing_known_returns_nothing_rather_than_noise()
    {
        using var store = NewStore();
        await store.AddAsync(Atom(AtomKind.Fact, "Wi-Fi Direct carries voice", "radio"));

        var recall = new Recall(store);
        var result = await recall.ForAsync(new Situation("bake", "a-cake"));

        Assert.False(result.Any);
    }

    // ------------------------------------------------------------------
    // The day this was built for
    // ------------------------------------------------------------------

    [Fact]
    public async Task It_would_have_answered_before_the_deploy_that_cost_a_day()
    {
        // Everything below was learned on 2026-08-27, after the models had been
        // re-downloaded three times. The test is whether a store holding it
        // would have put it in front of the fourth deploy.
        using var store = NewStore();

        await store.AddAsync(Atom(AtomKind.Ruling,
            "Never wipe the models to ship a UI change", "deploy:android", corrections: 3));
        await store.AddAsync(Atom(AtomKind.Fact,
            "-t:Install uninstalls first when EmbedAssembliesIntoApk is on", "deploy:android"));
        await store.AddAsync(Atom(AtomKind.Fact,
            "Use -t:InstallKeepingData to iterate; it preserves the data directory", "deploy:android"));
        await store.AddAsync(Atom(AtomKind.Preference,
            "Prefer the Microsoft toolchain over raw adb", "deploy"));
        await store.AddAsync(Atom(AtomKind.Relationship,
            "Blunt. Stopping to report wastes time.", "style"));

        var recall = new Recall(store);
        var result = await recall.ForAsync(
            new Situation("deploy", "android/p30", "shell", "dotnet build -t:Install"));

        Assert.True(result.Any);

        // The ruling leads, because it is the thing that was corrected three
        // times and the thing that costs 817 MB to get wrong.
        Assert.Equal(AtomKind.Ruling, result.Atoms[0].Kind);

        // The fix has to be in the answer, not just the warning - otherwise it
        // says "do not do that" and leaves somebody with no way forward.
        Assert.Contains(result.Atoms, a => a.Text.Contains("InstallKeepingData", StringComparison.Ordinal));

        // Small enough to sit in front of every command.
        Assert.True(result.Atoms.Sum(a => a.Text.Length) <= RecallBudget.Default.MaxCharacters);

        // And the manner is carried separately from the content.
        Assert.Single(result.Tone);
    }

    // ------------------------------------------------------------------
    // Mobile
    // ------------------------------------------------------------------

    [Fact]
    public void Full_text_search_is_available_in_this_build()
    {
        // A guard on the fallback, not on FTS5. If this ever goes false the
        // store still works - LIKE stands in - but recall quality drops and
        // somebody should find out from a test rather than from a phone.
        using var store = NewStore();
        Assert.True(store.FullTextAvailable);
    }

    [Fact]
    public async Task A_query_full_of_punctuation_does_not_take_down_the_recall()
    {
        // Unquoted user text reaches FTS5 as operators. A stray hyphen or quote
        // is a thrown exception rather than a poor result, and a recall must
        // never break the action it was supposed to inform.
        using var store = NewStore();
        await store.AddAsync(Atom(AtomKind.Fact, "InstallKeepingData preserves data", "deploy:android"));

        var recall = new Recall(store);
        var result = await recall.ForAsync(new Situation(
            "deploy", "android", "shell",
            "dotnet build -c Release -t:Install -p:AdbTarget=\"-s UTKDU19919000815\" (again)"));

        Assert.True(result.Any);
    }
}
