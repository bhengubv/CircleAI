// GenuineProductTests.cs
//
// The tests you point at when somebody asks whether any of this is real.
//
// TWO WAYS A THING LIKE THIS LIES, and they are different problems.
//
// THE FIRST is the memory inventing content - handing back a sentence nobody
// ever said, with exactly the confidence of one that was. That is the worse
// failure, because it is indistinguishable from working. The answer is not
// judgement, it is arithmetic: every atom must be a VERBATIM SUBSTRING of what
// was said, and recall must only ever return atoms that were put in. Both are
// exact checks. Neither has an opinion.
//
// THE SECOND is the product claiming things it has not done. A README can say
// "supports PostgreSQL" forever and no build will ever disagree. So the claims
// live in docs/CLAIMS.md as a table with a status column, and the tests below
// PARSE THAT TABLE: a row that says `tested` must name a test that exists, and
// a row that says `measured` must name the hardware and the date. Write a claim
// with nothing behind it and the build stops.
//
// The third failure - a suite that passes because it ran nothing - is the
// quietest of all, so the row counts are asserted too.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CircleAI.Memory;
using Xunit;

namespace CircleAI.Tests;

public class GenuineProductTests
{
    // ==================================================================
    // Nothing is invented
    // ==================================================================

    [Theory]
    [MemberData(nameof(RuleCorpora.Each), MemberType = typeof(RuleCorpora))]
    public void Nothing_it_kept_was_words_nobody_said(RuleCorpus corpus)
    {
        // THE ANTI-HALLUCINATION CHECK, and it is arithmetic rather than
        // judgement: every atom the extractor produces has to appear, character
        // for character, inside what was actually said. Not similar to it. In
        // it. A paraphrase fails this, an invention fails this, and a summary
        // fails this - which is why the extractor quotes.
        var said = string.Join("\n", corpus.Rules.Select(r => r.Statement));
        var episode = new EpisodicMemoryEntry { UserText = said, AssistantText = "Understood." };

        var candidates = new CueExtractor().Extract(episode);

        foreach (var candidate in candidates)
        {
            Assert.Contains(candidate.Atom.Text, said, StringComparison.Ordinal);
            Assert.Contains(candidate.Quote, said, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nothing_it_kept_was_words_nobody_said_even_from_prose()
    {
        // The same check against a paragraph rather than a list of rules,
        // because summarising is exactly what a paragraph invites.
        const string said =
            "I have been thinking about the deploy problem for a while now. " +
            "Never deploy with -t:Install, it wipes the models every single time. " +
            "We tried adb push last month and that did not work at all. " +
            "Anyway, the build is green and I am going to bed.";

        var candidates = new CueExtractor().Extract(new EpisodicMemoryEntry { UserText = said });

        Assert.NotEmpty(candidates);
        foreach (var candidate in candidates)
            Assert.Contains(candidate.Atom.Text, said, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RuleCorpora.Each), MemberType = typeof(RuleCorpora))]
    public async Task Recall_can_only_return_what_was_put_in(RuleCorpus corpus)
    {
        // A CLOSED WORLD. Ask it everything it could possibly be asked and the
        // union of every answer has to be a subset of what it was given. If a
        // sentence can appear that was never recorded, nothing above this
        // matters.
        using var store = new SqliteAtomStore("Data Source=:memory:");
        foreach (var rule in corpus.Rules) await store.AddAsync(rule.ToAtom());

        var given = corpus.Rules.Select(r => r.Statement).ToHashSet(StringComparer.Ordinal);
        var recall = new Recall(store);

        var asked = new List<Situation>();
        foreach (var rule in corpus.Rules)
        {
            var parts = (rule.Subject ?? "").Split(':', 2);
            asked.Add(parts.Length == 2 ? new Situation(parts[0], parts[1]) : new Situation(rule.Subject));
            asked.Add(new Situation(Text: rule.Statement));
        }
        asked.Add(new Situation(Text: "something nobody in this corpus ever mentioned"));

        foreach (var situation in asked)
        {
            var result = await recall.ForAsync(situation);
            foreach (var atom in result.Atoms.Concat(result.Tone))
                Assert.True(given.Contains(atom.Text),
                    $"[{corpus.Name}] recall produced text that was never recorded: {atom.Text}");
        }
    }

    [Theory]
    [MemberData(nameof(RuleCorpora.Each), MemberType = typeof(RuleCorpora))]
    public async Task Correcting_leaves_the_earlier_version_byte_exact(RuleCorpus corpus)
    {
        // History is what gives a current atom its weight. A memory that
        // rewrote what it used to think would be unfalsifiable - there would be
        // nothing left to check it against.
        using var store = new SqliteAtomStore("Data Source=:memory:");

        var versions = new List<(Guid Id, string Text)>();
        var current = corpus.Rules[0].ToAtom();
        await store.AddAsync(current);
        versions.Add((current.Id, current.Text));

        foreach (var rule in corpus.Rules.Skip(1))
        {
            current = await store.SupersedeAsync(current.Id, new MemoryAtom { Text = rule.Statement });
            versions.Add((current.Id, current.Text));
        }

        foreach (var (id, text) in versions)
        {
            var back = await store.GetAsync(id);
            Assert.NotNull(back);
            Assert.Equal(text, back!.Text);
        }
    }

    [Theory]
    [MemberData(nameof(RuleCorpora.Each), MemberType = typeof(RuleCorpora))]
    public void Every_atom_says_where_it_came_from(RuleCorpus corpus)
    {
        // A memory that cannot be audited is a rumour.
        var episode = new EpisodicMemoryEntry
        {
            UserText = string.Join("\n", corpus.Rules.Select(r => r.Statement)),
        };

        foreach (var candidate in new CueExtractor().Extract(episode))
        {
            Assert.Equal(episode.Id, candidate.Atom.SourceEpisode);
            Assert.False(string.IsNullOrWhiteSpace(candidate.Cue));
            Assert.False(string.IsNullOrWhiteSpace(candidate.Quote));
        }
    }

    // ==================================================================
    // The hook, where it can finally be tested
    // ==================================================================

    [Fact]
    public void The_hook_takes_the_words_out_of_an_editors_payload()
    {
        Assert.Equal(
            "Never restart a device",
            HookPayload.PromptFrom(
                """{"session_id":"a","cwd":"/x","prompt":"Never restart a device"}"""));
    }

    [Fact]
    public void An_envelope_with_no_message_is_not_something_somebody_said()
    {
        // Reading the envelope as if it were the message would file field names
        // as things a person said - a memory hallucinating from its own
        // plumbing.
        Assert.Equal("", HookPayload.PromptFrom("""{"session_id":"a","hook_event_name":"SessionStart"}"""));
        Assert.Equal("", HookPayload.PromptFrom("""{"prompt":null}"""));
        Assert.Equal("", HookPayload.PromptFrom("""{"prompt":42}"""));
        Assert.Equal("", HookPayload.PromptFrom("   "));
        Assert.Equal("", HookPayload.PromptFrom(null));
    }

    [Fact]
    public void The_hook_never_costs_somebody_their_prompt()
    {
        // A UserPromptSubmit hook that fails hard blocks the turn and ERASES
        // what was typed. Nothing this reads is worth that, so nothing it is
        // given may throw.
        foreach (var payload in new[]
        {
            "{ this is not json at all", "{", "}", "[]", "null", "\"just a string\"",
            "{\"prompt\":\"\"}", "not json, just words a person typed",
            "{\"prompt\":\"日本語のプロンプト\"}", new string('x', 100_000),
        })
        {
            var exception = Record.Exception(() => HookPayload.PromptFrom(payload));
            Assert.Null(exception);
        }

        // Prose that happens to start with a brace is prose.
        Assert.Equal("{ this is not json at all", HookPayload.PromptFrom("{ this is not json at all"));
    }

    // ==================================================================
    // The claims register
    // ==================================================================

    [Fact]
    public void Every_claim_marked_tested_names_a_test_that_exists()
    {
        // THE BUILD DISAGREEING WITH THE DOCUMENTATION. Write "verified" next
        // to something with nothing behind it and this stops.
        var known = typeof(GenuineProductTests).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttributes().Any(a =>
                a.GetType().Name is "FactAttribute" or "TheoryAttribute"))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var unbacked = Claims()
            .Where(c => c.Status == "tested")
            .Where(c => !known.Contains(c.Evidence))
            .ToList();

        Assert.True(unbacked.Count == 0,
            "claims marked tested with no such test:\n  " +
            string.Join("\n  ", unbacked.Select(c => $"{c.Text}  ->  {c.Evidence}")));
    }

    [Fact]
    public void Every_claim_marked_measured_says_on_what_and_when()
    {
        // A number with no provenance is a number somebody remembered.
        var vague = Claims()
            .Where(c => c.Status == "measured")
            .Where(c => !Regex.IsMatch(c.Evidence, @"\d{4}-\d{2}-\d{2}") ||
                        c.Evidence.Split(',')[0].Trim().Length < 4)
            .ToList();

        Assert.True(vague.Count == 0,
            "measurements with no device or no date:\n  " +
            string.Join("\n  ", vague.Select(c => $"{c.Text}  ->  {c.Evidence}")));
    }

    [Fact]
    public void Every_claim_marked_unproven_says_what_is_missing()
    {
        var silent = Claims()
            .Where(c => c.Status == "unproven")
            .Where(c => c.Evidence.Length < 10)
            .ToList();

        Assert.True(silent.Count == 0,
            "unproven claims that do not say why:\n  " +
            string.Join("\n  ", silent.Select(c => c.Text)));
    }

    [Fact]
    public void Nothing_is_claimed_in_a_status_nobody_defined()
    {
        var allowed = new[] { "tested", "measured", "unproven" };
        var strange = Claims().Where(c => !allowed.Contains(c.Status)).ToList();

        Assert.True(strange.Count == 0,
            "claims in an undefined status:\n  " +
            string.Join("\n  ", strange.Select(c => $"{c.Text}  ->  {c.Status}")));
    }

    [Fact]
    public void The_register_admits_what_has_not_been_done()
    {
        // A CLAIMS FILE WITH NOTHING UNPROVEN IN IT IS MARKETING. Four database
        // engines here have never seen a live server and the memory has no
        // decay model at all; a register that did not say so would be the exact
        // thing this file exists to catch.
        var claims = Claims();

        Assert.True(claims.Count >= 30, $"only {claims.Count} claims registered");
        Assert.Contains(claims, c => c.Status == "unproven");
        Assert.Contains(claims, c => c.Status == "measured");
    }

    // ==================================================================
    // The suite is not theatre
    // ==================================================================

    [Fact]
    public void The_properties_actually_ran_against_something()
    {
        // A [MemberData] that returns no rows PASSES, silently, forever. It is
        // the purest form of a green build that proves nothing, and it is worth
        // one assertion to make impossible.
        var corpora = RuleCorpora.All.ToList();
        var rules = RuleCorpora.EachRule().ToList();

        Assert.True(corpora.Count >= 5, $"only {corpora.Count} corpora");
        Assert.True(rules.Count >= 15, $"only {rules.Count} rules under test");
    }

    [Fact]
    public void The_fixtures_are_not_all_one_language_or_one_trade()
    {
        // "Any rule" has to mean any rule. Rules about deploying software, in
        // English, would make every property above a property of one person's
        // working week.
        var corpora = RuleCorpora.All.ToList();

        // More than one script. isiZulu and Sesotho are written in plain Latin
        // letters, so counting non-ASCII characters measures the alphabet and
        // not the language - which is why this only asks for one corpus that
        // leaves the Latin alphabet entirely.
        Assert.Contains(corpora, c => c.Rules.Any(r => r.Statement.Any(ch => ch > 0x2000)));

        // AND MOSTLY DISJOINT VOCABULARIES, which is the check that actually
        // means "different languages and different trades". Five corpora of
        // English rules about software would share most of their words while
        // looking varied.
        var vocabularies = corpora
            .Select(c => c.Rules
                .SelectMany(r => r.Statement.ToLowerInvariant()
                    .Split(Separators, StringSplitOptions.RemoveEmptyEntries))
                .Where(w => w.Length > 2)
                .ToHashSet(StringComparer.Ordinal))
            .ToList();

        var shared = vocabularies
            .SelectMany(v => v)
            .GroupBy(w => w, StringComparer.Ordinal)
            .Where(g => g.Count() > vocabularies.Count / 2)
            .Select(g => g.Key)
            .ToList();

        Assert.True(shared.Count == 0,
            "the same words run through most of the corpora, so they are not " +
            "really different languages: " + string.Join(", ", shared.Take(10)));

        var kinds = corpora.SelectMany(c => c.Rules).Select(r => r.Kind).Distinct().Count();
        Assert.True(kinds >= 3, $"only {kinds} kinds of rule under test");
    }

    // ==================================================================
    // Reading the register
    // ==================================================================

    /// <summary>Where one word ends and the next begins, in any of these languages.</summary>
    private static readonly char[] Separators =
        [' ', '\t', '\n', '\r', ',', ';', '.', '\''];

    private sealed record Claim(string Text, string Status, string Evidence);

    private static IReadOnlyList<Claim> Claims()
    {
        var path = Register();
        Assert.True(File.Exists(path), $"docs/CLAIMS.md is missing: {path}");

        var claims = new List<Claim>();

        foreach (var line in File.ReadAllLines(path))
        {
            if (!line.StartsWith('|')) continue;

            var cells = line.Split('|', StringSplitOptions.None)
                .Select(c => c.Trim())
                .Where(c => c.Length > 0 || true)
                .ToList();

            // | text | status | evidence |  ->  ["", text, status, evidence, ""]
            if (cells.Count != 5) continue;
            if (cells[1].StartsWith("---") || cells[1] == "Claim") continue;
            if (cells[2].Length == 0) continue;

            claims.Add(new Claim(cells[1], cells[2], cells[3]));
        }

        return claims;
    }

    private static string Register()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "CLAIMS.md");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return "docs/CLAIMS.md";
    }
}
