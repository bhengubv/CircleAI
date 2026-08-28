// MemoryRuleTests.cs
//
// Properties that must hold for anybody's rules.
//
// EVERY MEMORY TEST BEFORE THIS ONE USED ONE PERSON'S RULES AS LITERALS. That
// proves the memory works for that person. A nurse's rules about handovers, a
// farmer's about when to plant, a teacher's in Sesotho - none of them are in
// this repository, and all of them have to work exactly as well.
//
// So nothing below asserts a sentence. Each one asserts a RELATIONSHIP that has
// to hold whatever the words are: a rule filed under a subject comes back for
// that subject; what goes in comes out unchanged; more-corrected outranks
// less-corrected. Swap the corpus and every assertion still means something.
//
// The corpora are in RuleCorpus.cs, and one of them is the markdown on disk -
// so a rule written down tomorrow is under test tomorrow.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Memory;
using Xunit;

namespace CircleAI.Tests;

public class MemoryRuleTests : IDisposable
{
    private readonly string _dir;

    public MemoryRuleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "circleai-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static async Task<SqliteAtomStore> Loaded(RuleCorpus corpus)
    {
        var store = new SqliteAtomStore("Data Source=:memory:");
        foreach (var rule in corpus.Rules) await store.AddAsync(rule.ToAtom());
        return store;
    }

    /// <summary>The situation somebody would be in when this rule matters.</summary>
    /// <remarks>
    /// Built from the rule's own subject rather than from its words, so it is
    /// the same construction for every language.
    /// </remarks>
    private static Situation Moment(Rule rule)
    {
        var parts = (rule.Subject ?? "").Split(':', 2);
        return parts.Length == 2
            ? new Situation(Verb: parts[0], Target: parts[1])
            : new Situation(Verb: rule.Subject);
    }

    // ==================================================================
    // Holding a rule
    // ==================================================================

    [Theory]
    [MemberData(nameof(RuleCorpora.EachRule), MemberType = typeof(RuleCorpora))]
    public async Task Any_rule_comes_back_for_the_situation_it_was_filed_under(string corpus, Rule rule)
    {
        // THE ONE PROMISE. Everything else is an optimisation of this: a rule
        // that was written down is in front of you when it applies.
        using var store = new SqliteAtomStore("Data Source=:memory:");
        await store.AddAsync(rule.ToAtom());

        var found = await store.MatchAsync(Moment(rule));

        Assert.True(found.Any(a => a.Text == rule.Statement),
            $"[{corpus}] '{rule.Name}' was not returned for {Moment(rule).Key}");
    }

    [Theory]
    [MemberData(nameof(RuleCorpora.EachRule), MemberType = typeof(RuleCorpora))]
    public async Task Any_rule_comes_back_in_the_words_it_was_written_in(string corpus, Rule rule)
    {
        // A memory that alters what it was told is worse than one that forgets,
        // because it hands back the alteration with the same confidence.
        using var store = new SqliteAtomStore("Data Source=:memory:");
        var atom = rule.ToAtom();
        await store.AddAsync(atom);

        var back = await store.GetAsync(atom.Id);

        Assert.NotNull(back);
        Assert.Equal(rule.Statement, back!.Text);
        Assert.Equal(rule.Kind, back.Kind);
        Assert.Equal(rule.Subject, back.Subject);
    }

    [Theory]
    [MemberData(nameof(RuleCorpora.Each), MemberType = typeof(RuleCorpora))]
    public async Task Every_rule_somebody_has_is_still_there_afterwards(RuleCorpus corpus)
    {
        using var store = await Loaded(corpus);

        var held = await store.AllAsync(limit: 5000);
        var texts = held.Select(a => a.Text).ToHashSet(StringComparer.Ordinal);

        var missing = corpus.Rules.Where(r => !texts.Contains(r.Statement)).ToList();
        Assert.True(missing.Count == 0,
            $"[{corpus.Name}] lost: {string.Join(" | ", missing.Select(r => r.Name))}");
    }

    // ==================================================================
    // Order
    // ==================================================================

    [Theory]
    [MemberData(nameof(RuleCorpora.Each), MemberType = typeof(RuleCorpora))]
    public async Task A_standing_rule_outranks_a_leaning_on_the_same_moment(RuleCorpus corpus)
    {
        // Content-independent by construction: the same subject, the same
        // moment, differing only in kind.
        var subject = corpus.Rules[0].Subject;
        using var store = new SqliteAtomStore("Data Source=:memory:");

        await store.AddAsync(new MemoryAtom
        {
            Kind = AtomKind.Preference, Subject = subject,
            Text = corpus.Rules[^1].Statement,
        });
        await store.AddAsync(new MemoryAtom
        {
            Kind = AtomKind.Ruling, Subject = subject,
            Text = corpus.Rules[0].Statement,
        });

        var result = await new Recall(store).ForAsync(Moment(corpus.Rules[0]));

        Assert.Equal(AtomKind.Ruling, result.Atoms[0].Kind);
    }

    [Theory]
    [MemberData(nameof(RuleCorpora.Each), MemberType = typeof(RuleCorpora))]
    public async Task What_had_to_be_repeated_arrives_first(RuleCorpus corpus)
    {
        // The one signal an agent could never have judged for itself: it did
        // not see the corrections coming.
        var subject = corpus.Rules[0].Subject;
        using var store = new SqliteAtomStore("Data Source=:memory:");

        var quiet = new MemoryAtom
        {
            Kind = AtomKind.Ruling, Subject = subject, Text = corpus.Rules[0].Statement,
        };
        var repeated = new MemoryAtom
        {
            Kind = AtomKind.Ruling, Subject = subject, Text = corpus.Rules[^1].Statement,
        };

        await store.AddAsync(quiet);
        await store.AddAsync(repeated);

        var current = repeated;
        for (var i = 0; i < 3; i++)
            current = await store.SupersedeAsync(current.Id, new MemoryAtom { Text = repeated.Text });

        var result = await new Recall(store).ForAsync(Moment(corpus.Rules[0]));

        Assert.Equal(repeated.Text, result.Atoms[0].Text);
        Assert.True(result.Atoms[0].Corrections >= 3);
    }

    [Theory]
    [MemberData(nameof(RuleCorpora.Each), MemberType = typeof(RuleCorpora))]
    public async Task Recall_stays_inside_its_budget_however_many_rules_there_are(RuleCorpus corpus)
    {
        // A memory that returns everything it knows has told you nothing.
        using var store = await Loaded(corpus);
        var budget = new RecallBudget(MaxAtoms: 3, MaxCharacters: 400);

        foreach (var rule in corpus.Rules)
        {
            var result = await new Recall(store).ForAsync(Moment(rule), budget);

            Assert.True(result.Atoms.Count <= budget.MaxAtoms);
            Assert.True(result.Atoms.Sum(a => a.Text.Length) <= budget.MaxCharacters ||
                        result.Atoms.Count == 1);
        }
    }

    // ==================================================================
    // Correcting
    // ==================================================================

    [Theory]
    [MemberData(nameof(RuleCorpora.Each), MemberType = typeof(RuleCorpora))]
    public async Task A_rule_that_changed_stops_answering_and_stays_readable(RuleCorpus corpus)
    {
        var rule = corpus.Rules[0];
        using var store = new SqliteAtomStore("Data Source=:memory:");

        var original = rule.ToAtom();
        await store.AddAsync(original);

        // The replacement is another real rule from the same corpus, so this is
        // never a test about one contrived sentence.
        var replacement = corpus.Rules[^1].ToAtom();
        await store.SupersedeAsync(original.Id, replacement);

        var current = await store.MatchAsync(Moment(rule));
        Assert.DoesNotContain(current, a => a.Text == rule.Statement && a.Id == original.Id);

        var traced = await store.GetAsync(original.Id);
        Assert.Equal(rule.Statement, traced!.Text);
        Assert.False(traced.IsCurrent);
    }

    // ==================================================================
    // Across machines
    // ==================================================================

    [Theory]
    [MemberData(nameof(RuleCorpora.Each), MemberType = typeof(RuleCorpora))]
    public async Task Rules_written_on_three_machines_read_as_one_memory(RuleCorpus corpus)
    {
        // Split any rule set across three logs the way three machines would,
        // and a fourth machine seeing all of them must hold exactly the set.
        var machines = new[] { "linux-box", "windows-desk", "mac-build" };

        for (var i = 0; i < corpus.Rules.Count; i++)
        {
            var sync = new MemorySync(new MemoryFolder(_dir, machines[i % machines.Length]));
            using var store = new SqliteAtomStore("Data Source=:memory:");
            await sync.RecordAsync(store, corpus.Rules[i].ToAtom());
        }

        var arriving = new MemorySync(new MemoryFolder(_dir, "a-fourth-machine"));
        using var fresh = new SqliteAtomStore("Data Source=:memory:");
        var report = await arriving.RebuildAsync(fresh);

        Assert.Equal(corpus.Rules.Count, report.Current);

        var held = (await fresh.AllAsync(limit: 5000)).Select(a => a.Text).ToHashSet(StringComparer.Ordinal);
        foreach (var rule in corpus.Rules)
            Assert.Contains(rule.Statement, held);
    }

    [Theory]
    [MemberData(nameof(RuleCorpora.Each), MemberType = typeof(RuleCorpora))]
    public void Any_rule_is_readable_in_the_log_that_carries_it(RuleCorpus corpus)
    {
        // Half the reason the log is text. A rule somebody cannot read in their
        // own language is a rule they cannot check, and the sovereignty
        // argument goes with it.
        var folder = new MemoryFolder(_dir, "windows-desk");
        var log = new AtomLog(folder);

        foreach (var rule in corpus.Rules) log.Append(rule.ToAtom());

        var written = File.ReadAllText(folder.OwnLog);

        foreach (var rule in corpus.Rules)
            Assert.Contains(rule.Statement, written, StringComparison.Ordinal);

        Assert.DoesNotContain(@"\u", written, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RuleCorpora.Each), MemberType = typeof(RuleCorpora))]
    public async Task Losing_the_index_never_loses_a_rule(RuleCorpus corpus)
    {
        var sync = new MemorySync(new MemoryFolder(_dir, "windows-desk"));

        using (var store = new SqliteAtomStore("Data Source=:memory:"))
            foreach (var rule in corpus.Rules)
                await sync.RecordAsync(store, rule.ToAtom());

        var afterwards = sync.Current().Select(a => a.Text).ToHashSet(StringComparer.Ordinal);

        foreach (var rule in corpus.Rules)
            Assert.Contains(rule.Statement, afterwards);
    }

    // ==================================================================
    // Finding a rule by its own words
    // ==================================================================

    [Theory]
    [MemberData(nameof(RuleCorpora.EachRule), MemberType = typeof(RuleCorpora))]
    public async Task Any_rule_is_findable_by_the_words_it_is_made_of(string corpus, Rule rule)
    {
        // The path taken when nobody knew the subject key - which is most of
        // the time. It is also where a tokeniser that cannot split a language
        // shows up, and that is worth knowing rather than assuming.
        using var store = new SqliteAtomStore("Data Source=:memory:");
        await store.AddAsync(rule.ToAtom());

        var fragment = Fragment(rule.Statement);
        Assert.True(fragment.Length > 0, $"[{corpus}] '{rule.Name}' has nothing to search for");

        var found = await store.MatchAsync(new Situation(Text: fragment));

        Assert.True(found.Any(a => a.Text == rule.Statement),
            $"[{corpus}] '{rule.Name}' could not be found by '{fragment}'");
    }

    /// <summary>
    /// A PART of a statement, which is all anybody ever remembers.
    /// </summary>
    /// <remarks>
    /// DELIBERATELY NOT THE WHOLE THING. Searching for a statement in full
    /// matches it whatever the tokeniser does, so the test would pass on every
    /// language and prove nothing at all. A fragment is what a real query looks
    /// like, and it is where a tokeniser that cannot split a language shows up.
    ///
    /// Taken from the middle, because the start of a sentence is also what a
    /// plain prefix match would find.
    /// </remarks>
    private static readonly char[] Separators = [' ', '\t', '\n', '\r', ',', ';', '.'];

    private static string Fragment(string statement)
    {
        var words = statement
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .ToList();

        if (words.Count >= 3) return string.Join(" ", words.Skip(words.Count / 2).Take(2));
        if (words.Count == 2) return words[1];

        // Nothing to split on - Japanese, Chinese. Take a few characters from
        // the middle and find out whether the store can reach them.
        var text = statement.Trim();
        return text.Length >= 6 ? text.Substring(text.Length / 3, 3) : text;
    }

    // ==================================================================
    // Reading rules out of a person's own files
    // ==================================================================

    [Fact]
    public void A_persons_markdown_is_read_the_same_way_whoever_wrote_it()
    {
        // The parser is the swappable part: point it at somebody else's
        // directory and it is somebody else's corpus, with no code change.
        var theirs = Path.Combine(_dir, "their-rules");
        Directory.CreateDirectory(theirs);

        File.WriteAllText(Path.Combine(theirs, "one.md"), """
            ---
            name: no-credit-without-a-name
            description: Moet nooit krediet gee sonder 'n naam en 'n nommer nie
            type: feedback
            ---

            Body text that is not the rule.
            """);

        File.WriteAllText(Path.Combine(theirs, "two.md"), """
            ---
            name: stock-count
            description: "Tel die voorraad Vrydagaand, nie Maandagoggend nie"
            metadata:
              type: project
            ---
            """);

        // Not a rule file at all, and must not become one.
        File.WriteAllText(Path.Combine(theirs, "readme.md"), "# Just notes\n\nNothing structured.\n");

        var corpus = RuleCorpora.FromMarkdown("theirs", theirs);

        Assert.Equal(2, corpus.Rules.Count);
        Assert.Equal("Moet nooit krediet gee sonder 'n naam en 'n nommer nie", corpus.Rules[0].Statement);
        Assert.Equal(AtomKind.Ruling, corpus.Rules[0].Kind);

        // A nested metadata.type still says what sort of thing it is.
        Assert.Equal(AtomKind.Decision, corpus.Rules[1].Kind);
        Assert.Equal("Tel die voorraad Vrydagaand, nie Maandagoggend nie", corpus.Rules[1].Statement);
    }

    [Fact]
    public void There_is_more_than_one_persons_rules_under_test()
    {
        // The guard on the whole idea. If this ever drops to one corpus, every
        // property below it has quietly gone back to describing one person.
        var corpora = RuleCorpora.All.ToList();

        Assert.True(corpora.Count >= 5, $"only {corpora.Count} corpora");
        Assert.True(corpora.Sum(c => c.Rules.Count) >= 15);

        // And they must not all be in one language, or "any rule" means
        // "any English rule".
        var latin = corpora.SelectMany(c => c.Rules)
            .Count(r => r.Statement.All(ch => ch < 128));
        Assert.True(latin < corpora.Sum(c => c.Rules.Count),
            "every rule under test is plain ASCII");
    }
}
