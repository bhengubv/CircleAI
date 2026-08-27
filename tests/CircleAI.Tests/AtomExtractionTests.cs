// AtomExtractionTests.cs
//
// Would it have caught what was actually said?
//
// The fixtures are real lines out of real sessions, not invented ones. An
// extractor tested against sentences written to suit it passes every time and
// catches nothing, and the failure mode that matters here is silent: a memory
// that fills itself with the wrong things looks exactly like one that is
// working.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Memory;
using Xunit;

namespace CircleAI.Tests;

public class AtomExtractionTests
{
    private static readonly CueExtractor Extractor = new();

    private static EpisodicMemoryEntry Said(string text, string? context = null) => new()
    {
        UserText = text,
        AssistantText = "Understood.",
        AppContext = context,
        RecordedAtUtc = DateTimeOffset.UtcNow,
    };

    private static IReadOnlyList<AtomCandidate> From(string text, string? subject = null) =>
        Extractor.Extract(Said(text), subject);

    // ------------------------------------------------------------------
    // What it must catch
    // ------------------------------------------------------------------

    [Theory]
    // A rule, stated outright.
    [InlineData("Never restart a device or toggle its radios without asking me first")]
    [InlineData("Always uninstall before deploying or stale assemblies mask the fix")]
    [InlineData("Do not use a central API to catalog or rate anything")]
    // The same rules as people actually type them, without the apostrophe.
    [InlineData("Dont wait for me to prompt you, do everything until you have delivered")]
    [InlineData("We only ever ship what has run on the device, nothing else counts")]
    // Being told again. The highest-value line in any transcript.
    [InlineData("Why do you keep stopping when you have no blockers to report")]
    [InlineData("I told you this already about the wake phrase language")]
    // A road tried and found closed.
    [InlineData("The adb push approach did not work, it silently wrote nothing")]
    [InlineData("Stopping peer discovery before connect doesnt work, it empties the list")]
    // How somebody wants to be worked with.
    [InlineData("I prefer brief feedback because obfuscation helps no one")]
    [InlineData("I hate being handed a wall of text when one line would do")]
    // Something settled.
    [InlineData("From now on we use the InstallKeepingData target when iterating")]
    public void It_catches_what_was_actually_said(string line)
    {
        Assert.NotEmpty(From(line));
    }

    [Fact]
    public void A_rule_is_filed_as_a_ruling()
    {
        var found = From("Never restart a device or toggle its radios without asking");

        Assert.Single(found);
        Assert.Equal(AtomKind.Ruling, found[0].Atom.Kind);
        Assert.True(found[0].Certain);
    }

    [Fact]
    public void A_road_that_closed_is_filed_as_failed()
    {
        // The whole reason a failed decision ranks UP: knowing what was tried
        // arrives too late by default.
        var found = From("The adb push approach did not work, it silently wrote nothing");

        Assert.Single(found);
        Assert.Equal(AtomKind.Decision, found[0].Atom.Kind);
        Assert.True(found[0].Atom.Failed);
    }

    [Fact]
    public void A_preference_is_not_filed_as_a_rule()
    {
        // "I prefer" is how somebody likes things; "never" is a requirement.
        // Collapsing the two would give a preference a ruling's rank and put it
        // above the rules it should sit under.
        var found = From("I prefer brief feedback because obfuscation helps no one");

        Assert.Single(found);
        Assert.Equal(AtomKind.Preference, found[0].Atom.Kind);
    }

    [Fact]
    public void It_keeps_the_words_that_were_used()
    {
        // Paraphrasing is where extraction starts inventing, and an invented
        // memory is handed back with the same confidence as a true one.
        const string line = "Never restart a device or toggle its radios without asking";
        var found = From(line);

        Assert.Equal(line, found[0].Atom.Text);
        Assert.Equal(line, found[0].Quote);
    }

    [Fact]
    public void It_says_which_words_triggered_it()
    {
        // So a wrong extraction can be diagnosed rather than argued about.
        var found = From("Never restart a device or toggle its radios without asking");
        Assert.Equal("never", found[0].Cue);
    }

    [Fact]
    public void It_lifts_the_sentence_out_of_the_paragraph_around_it()
    {
        var found = From(
            "I looked at the deploy logs this morning and they are a mess. " +
            "Never restart a device or toggle its radios without asking. " +
            "Anyway the build is green now.");

        Assert.Single(found);
        Assert.Equal("Never restart a device or toggle its radios without asking", found[0].Atom.Text);
    }

    [Fact]
    public void It_reads_rules_written_as_bullet_points()
    {
        // People write requirements as lists far more often than they end them
        // with a full stop.
        var found = From(
            "Here are the rules:\n" +
            "- Never restart a device without asking me about it first\n" +
            "- Always uninstall before deploying so stale assemblies cannot hide\n");

        Assert.Equal(2, found.Count);
        Assert.All(found, c => Assert.Equal(AtomKind.Ruling, c.Atom.Kind));
    }

    // ------------------------------------------------------------------
    // What it must not catch
    // ------------------------------------------------------------------

    [Theory]
    // No cue at all.
    [InlineData("The build finished in about four minutes on this machine")]
    [InlineData("Can you show me the settings screen on the phone please")]
    // A cue inside a longer word. "whenever" is not "never"; "because" is not "use".
    [InlineData("Whenever the radio drops the reconnect takes about thirty seconds")]
    [InlineData("It reconnected because the peer list had not been emptied yet")]
    [InlineData("The house always wins, as the saying goes, and misuse is common")]
    public void It_leaves_alone_what_is_not_a_memory(string line)
    {
        Assert.Empty(From(line));
    }

    [Fact]
    public void A_reaction_with_no_content_is_not_an_atom()
    {
        // "I told you this" is the highest-signal thing a person says and the
        // least useful thing to file: as an atom it states nothing. What was
        // told is in the sentence that follows, not in the complaint.
        Assert.Empty(From("I told you this"));
        Assert.Empty(From("stop it"));
        Assert.Empty(From("never mind"));
    }

    [Fact]
    public void A_paragraph_that_happens_to_contain_the_word_is_not_a_rule()
    {
        // 600 characters is the whole recall budget. One of these would eat it.
        var essay = "I want to explain the reasoning here at some length because " +
                    string.Join(" ", Enumerable.Repeat("it matters to how the system is understood", 8));

        Assert.Empty(From(essay));
    }

    [Fact]
    public void It_does_not_listen_to_the_assistant()
    {
        // What an assistant said it would do is a plan; what the person said is
        // the requirement. Reading both lets the thing that was corrected file
        // its own version of events beside the correction.
        var episode = new EpisodicMemoryEntry
        {
            UserText = "check the logs",
            AssistantText = "I will never deploy without uninstalling first, and I prefer to run tests before that.",
        };

        Assert.Empty(Extractor.Extract(episode));
    }

    [Fact]
    public void One_sentence_makes_one_atom_however_many_cues_it_carries()
    {
        // "I told you" and "you keep" arrive together constantly. Filing the
        // sentence twice makes one complaint look like a pattern, and the
        // correction count is what pushes an atom to the top of a recall.
        var found = From("I told you this already and you keep doing it anyway every single time");

        Assert.Single(found);
    }

    // ------------------------------------------------------------------
    // Subjects
    // ------------------------------------------------------------------

    [Fact]
    public void It_files_under_the_subject_it_was_given()
    {
        var found = From("Never restart a device or toggle its radios without asking", "device:state");
        Assert.Equal("device:state", found[0].Atom.Subject);
    }

    [Fact]
    public void It_does_not_invent_a_subject()
    {
        // A WRONG KEY IS WORSE THAN NONE: it makes an atom findable in the
        // wrong situation and invisible in the right one. Keyword search still
        // reaches an unfiled atom; a misfiled one is lost in plain sight.
        var found = From("Never restart a device or toggle its radios without asking");
        Assert.Null(found[0].Atom.Subject);
    }

    [Fact]
    public void It_falls_back_to_the_app_the_exchange_happened_in()
    {
        var episode = Said("Never restart a device or toggle its radios", context: "circleai.it");
        var found = Extractor.Extract(episode);

        Assert.Equal("circleai.it", found[0].Atom.Subject);
    }

    [Fact]
    public void An_atom_can_be_walked_back_to_what_was_said()
    {
        // A memory that cannot be audited is a rumour.
        var episode = Said("Never restart a device or toggle its radios without asking");
        var found = Extractor.Extract(episode);

        Assert.Equal(episode.Id, found[0].Atom.SourceEpisode);
        Assert.Equal(episode.RecordedAtUtc, found[0].Atom.RecordedAtUtc);
    }

    // ------------------------------------------------------------------
    // Keeping
    // ------------------------------------------------------------------

    private static async Task<(LearnReport Report, List<MemoryAtom> Kept)> Learn(
        IEnumerable<EpisodicMemoryEntry> episodes,
        IReadOnlyList<MemoryAtom>? known = null,
        string? subject = null)
    {
        var kept = new List<MemoryAtom>();
        var report = await new AtomLearner().LearnAsync(
            episodes,
            (atom, _) => { kept.Add(atom); return Task.CompletedTask; },
            known ?? Array.Empty<MemoryAtom>(),
            subject);
        return (report, kept);
    }

    [Fact]
    public async Task It_keeps_what_it_is_sure_of()
    {
        var (report, kept) = await Learn(new[]
        {
            Said("Never restart a device or toggle its radios without asking"),
            Said("The adb push approach did not work, it silently wrote nothing"),
        });

        Assert.Equal(2, report.Recorded.Count);
        Assert.Equal(2, kept.Count);
        Assert.Empty(report.Offered);
    }

    [Fact]
    public async Task It_offers_what_it_is_not_sure_of_instead_of_keeping_it()
    {
        // The costs are not symmetrical: a missed atom means somebody says it
        // again, a wrong one means the memory hands back something untrue at
        // the moment it is most trusted.
        var (report, kept) = await Learn(new[]
        {
            Said("Use the release configuration for anything going to the phone"),
        });

        Assert.Empty(kept);
        Assert.Single(report.Offered);
        Assert.False(report.Offered[0].Certain);
    }

    [Fact]
    public async Task Learning_the_same_conversation_twice_keeps_one_of_each()
    {
        // Runs after a crash, after a pull, or simply a second pass. If this
        // were not true the memory would grow a duplicate every time, and a
        // duplicate is not harmless - it doubles a thing's weight in recall.
        var conversation = new[]
        {
            Said("Never restart a device or toggle its radios without asking"),
            Said("The adb push approach did not work, it silently wrote nothing"),
        };

        var (_, first) = await Learn(conversation);
        var (report, second) = await Learn(conversation, known: first);

        Assert.Empty(second);
        Assert.Equal(2, report.AlreadyKnown.Count);
    }

    [Fact]
    public async Task Something_already_remembered_is_not_offered_back_as_a_question()
    {
        // Asking somebody to confirm what they already told us is the same
        // failure as asking them twice.
        var known = new[]
        {
            new MemoryAtom
            {
                Kind = AtomKind.Decision,
                Text = "Use the release configuration for anything going to the phone",
            },
        };

        var (report, _) = await Learn(
            new[] { Said("Use the release configuration for anything going to the phone") },
            known);

        Assert.Empty(report.Offered);
        Assert.Single(report.AlreadyKnown);
    }

    [Fact]
    public async Task Punctuation_and_spacing_do_not_make_it_a_new_memory()
    {
        var known = new[]
        {
            new MemoryAtom
            {
                Kind = AtomKind.Ruling,
                Text = "Never restart a device or toggle its radios without asking",
            },
        };

        var (report, kept) = await Learn(
            new[] { Said("Never  restart a device or toggle its radios without asking.") },
            known);

        Assert.Empty(kept);
        Assert.Single(report.AlreadyKnown);
    }

    [Fact]
    public async Task It_never_supersedes_on_its_own()
    {
        // Superseding rewrites an atom's history and climbs its rank. Doing
        // that because two sentences looked similar would let a misreading
        // quietly replace something a person actually said.
        var (_, kept) = await Learn(new[]
        {
            Said("Never restart a device or toggle its radios without asking"),
        });

        Assert.All(kept, atom => Assert.Null(atom.SupersededBy));
        Assert.All(kept, atom => Assert.Equal(0, atom.Corrections));
    }

    [Fact]
    public async Task What_it_kept_can_be_recalled()
    {
        // The end of the loop: something said becomes something recalled at the
        // moment it matters, without anybody typing it in.
        using var store = new SqliteAtomStore("Data Source=:memory:");

        await new AtomLearner().LearnAsync(
            new[]
            {
                Said("Never deploy with -t:Install, it wipes the models every time"),
                Said("The adb install -r approach did not work, it kept the old permissions"),
            },
            (atom, ct) => store.AddAsync(atom, ct),
            Array.Empty<MemoryAtom>(),
            subject: "deploy:android");

        var result = await new Recall(store).ForAsync(new Situation("deploy", "android"));

        Assert.Equal(2, result.Atoms.Count);

        // And the road already closed is the one put first.
        Assert.True(result.Atoms[0].Failed || result.Atoms[0].Kind == AtomKind.Ruling);
    }
}
