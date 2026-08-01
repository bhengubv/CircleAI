// PersonalRespellingsTests.cs
//
// Learning how one person says a borrowed word, by listening to them use it.
//
// The thresholds are the design, so they are what these tests pin. Five hearings
// must AGREE before anything changes, because the first few scatter with accent —
// the same speaker varies, and a shared phone carries several voices. A sixth
// hearing then checks the change against a version we have already altered, so
// adoption is a hypothesis with a test after it rather than a leap.
//
// The failure this guards against is the quiet one: learning a single speaker's
// accent from noise and imposing it on a household, permanently, with nobody
// having asked for it.

using CircleAI.Voice;
using Xunit;

namespace CircleAI.Companion.Tests;

public class PersonalRespellingsTests
{
    private const string Word = "WiFi";
    private const string Ours = "wayufayu";     // what we say today
    private const string Theirs = "wayifayi";   // what this person says

    private static PersonalRespellings Heard(int times, string form, string? current = Ours)
    {
        var p = new PersonalRespellings();
        for (var i = 0; i < times; i++) p.Observe(Word, form, current);
        return p;
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Fewer_than_five_agreeing_hearings_change_nothing(int times)
    {
        // One to four can be accent variation within a single speaker's range.
        Assert.Null(Heard(times, Theirs).Respell(Word));
    }

    [Fact]
    public void The_fifth_agreeing_hearing_adopts_it()
    {
        var p = Heard(5, Theirs);
        Assert.Equal(Theirs, p.Respell(Word));
        Assert.Equal(LearningState.Adopted, p.All().Single().State);
    }

    [Fact]
    public void The_sixth_hearing_confirms_the_change()
    {
        var p = Heard(5, Theirs);
        p.Observe(Word, Theirs);                       // the check
        Assert.Equal(LearningState.Confirmed, p.All().Single().State);
        Assert.Equal(Theirs, p.Respell(Word));
    }

    [Fact]
    public void A_sixth_hearing_that_DISAGREES_undoes_the_adoption()
    {
        // The whole point of the check: we changed how the word is said, and the
        // next use tells us we were wrong. Revert rather than live with it.
        var p = Heard(5, Theirs);
        p.Observe(Word, "wayifeyi");

        Assert.Null(p.Respell(Word));
        Assert.Equal(LearningState.Listening, p.All().Single().State);
    }

    [Fact]
    public void Scattered_hearings_never_accumulate_into_a_decision()
    {
        // Five hearings, five different forms — a shared phone, or a bad
        // transcriber. Five of ANYTHING must not be enough; five that AGREE must.
        var p = new PersonalRespellings();
        foreach (var f in new[] { "wayifayi", "wayifay", "wayifai", "weyifayi", "wayifayo" })
            p.Observe(Word, f, Ours);

        Assert.Null(p.Respell(Word));
    }

    [Fact]
    public void A_household_of_different_speakers_teaches_nothing()
    {
        // Three people saying it three different ways, twice each. Nobody reaches
        // five, so the shipped spelling stands — which is the right outcome. One
        // person's accent must not become everybody's.
        var p = new PersonalRespellings();
        for (var round = 0; round < 2; round++)
            foreach (var f in new[] { "wayifayi", "weyifeyi", "wayifayu" })
                p.Observe(Word, f, Ours);

        Assert.Null(p.Respell(Word));
    }

    [Fact]
    public void A_completely_different_word_is_not_evidence()
    {
        // The person was talking about something else and it landed in the same
        // transcript. Learning from it would teach nonsense.
        var p = new PersonalRespellings();
        for (var i = 0; i < 8; i++) p.Observe(Word, "ngiyabonga", Ours);

        Assert.Null(p.Respell(Word));
    }

    [Fact]
    public void Hearing_what_we_already_say_teaches_nothing_new()
    {
        var p = new PersonalRespellings();
        var changed = false;
        for (var i = 0; i < 8; i++) changed |= p.Observe(Word, Ours, Ours);

        Assert.False(changed);
        Assert.Null(p.Respell(Word));       // nothing to override; we already agree
    }

    [Fact]
    public void Observe_reports_the_moment_the_word_changes()
    {
        var p = new PersonalRespellings();
        var changedOn = new List<int>();
        for (var i = 1; i <= 7; i++)
            if (p.Observe(Word, Theirs, Ours)) changedOn.Add(i);

        // Exactly once, on the fifth. The sixth confirms rather than changes.
        Assert.Equal(new[] { 5 }, changedOn);
    }

    [Fact]
    public void A_person_can_be_relearned_after_confirmation()
    {
        // Someone's usage shifts, or the household changes. A confirmed word is not
        // frozen — five agreeing hearings of something else win again.
        var p = Heard(5, Theirs);
        p.Observe(Word, Theirs);                        // confirmed
        Assert.Equal(Theirs, p.Respell(Word));

        const string later = "wayifayo";
        for (var i = 0; i < 5; i++) p.Observe(Word, later);
        Assert.Equal(later, p.Respell(Word));
    }

    [Fact]
    public void Forgetting_a_word_returns_it_to_the_shipped_spelling()
    {
        var p = Heard(5, Theirs);
        p.Forget(Word);
        Assert.Null(p.Respell(Word));
        Assert.Empty(p.All());
    }

    // ── learning from what the person actually said ──────────────────────────

    private static readonly Dictionary<string, string> Shipped = new()
    {
        ["WiFi"] = "wayufayu",
        ["SMS"]  = "esemese",
    };

    [Fact]
    public void A_prefixed_mention_is_recognised_as_the_word()
    {
        // isiZulu glues prefixes on: "nge-wotsapha", "i-wayifayi". Comparing the
        // whole token would make every prefixed mention look like a different word
        // and nothing would ever be learned.
        var p = new PersonalRespellings();
        for (var i = 0; i < 5; i++)
            p.LearnFrom("ngizosebenzisa i-wayifayi ekhaya", Shipped);

        Assert.Equal("wayifayi", p.Respell("WiFi"));
    }

    [Fact]
    public void Five_ordinary_sentences_are_enough_and_four_are_not()
    {
        var p = new PersonalRespellings();
        for (var i = 0; i < 4; i++) p.LearnFrom("awunayo i-wayifayi ekhaya", Shipped);
        Assert.Null(p.Respell("WiFi"));

        var changed = p.LearnFrom("awunayo i-wayifayi ekhaya", Shipped);
        Assert.Equal(new[] { "WiFi" }, changed);
        Assert.Equal("wayifayi", p.Respell("WiFi"));
    }

    [Fact]
    public void A_sentence_without_the_word_teaches_nothing_about_it()
    {
        // The commonest case by far: most of what a person says has no borrowing in
        // it at all, and must leave the table untouched.
        var p = new PersonalRespellings();
        for (var i = 0; i < 10; i++)
            p.LearnFrom("sawubona ngicela usizo namuhla", Shipped);

        Assert.Null(p.Respell("WiFi"));
        Assert.Null(p.Respell("SMS"));
    }

    [Fact]
    public void Only_the_word_that_was_said_is_learned()
    {
        // Two known words in the table, one of them in the sentence. The other must
        // not drift toward whatever token happened to be nearest.
        var p = new PersonalRespellings();
        for (var i = 0; i < 6; i++)
            p.LearnFrom("ngithumele i-wayifayi manje", Shipped);

        Assert.Equal("wayifayi", p.Respell("WiFi"));
        Assert.Null(p.Respell("SMS"));
    }

    // ── surviving a restart ──────────────────────────────────────────────────

    [Fact]
    public void Learning_survives_a_restart()
    {
        // A year of listening that vanishes when the app closes is not learning.
        var path = Path.Combine(Path.GetTempPath(), $"respell-{Guid.NewGuid():N}.json");
        try
        {
            var p = Heard(5, Theirs);
            p.Observe(Word, Theirs);                       // confirmed
            p.Save(path);

            var reloaded = PersonalRespellings.Load(path);
            Assert.Equal(Theirs, reloaded.Respell(Word));
            Assert.Equal(LearningState.Confirmed, reloaded.All().Single().State);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Partial_progress_survives_too()
    {
        // Three hearings in is a real state worth keeping: losing it means the
        // person teaches the same word from scratch every time they reopen the app.
        var path = Path.Combine(Path.GetTempPath(), $"respell-{Guid.NewGuid():N}.json");
        try
        {
            Heard(3, Theirs).Save(path);

            var reloaded = PersonalRespellings.Load(path);
            Assert.Null(reloaded.Respell(Word));           // not adopted yet

            reloaded.Observe(Word, Theirs, Ours);
            Assert.Null(reloaded.Respell(Word));           // four
            reloaded.Observe(Word, Theirs, Ours);
            Assert.Equal(Theirs, reloaded.Respell(Word));  // five — adopted
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Confirmation_survives_a_restart_too()
    {
        // Found on the phone, not here: the sixth hearing confirmed in memory and
        // the file still said "adopted", because confirming changes no spelling and
        // the caller only saved when a spelling changed. A word could therefore
        // never reach a persisted Confirmed state — every restart put it back to
        // awaiting its check, leaving months of agreement one mishearing away from
        // being undone.
        var path = Path.Combine(Path.GetTempPath(), $"respell-{Guid.NewGuid():N}.json");
        try
        {
            var p = Heard(5, Theirs);
            p.Save(path);

            p.Observe(Word, Theirs);                        // the check
            Assert.Equal(LearningState.Confirmed, p.All().Single().State);
            Assert.True(p.HasUnsavedChanges);               // ...and it must be written

            p.Save(path);
            Assert.Equal(LearningState.Confirmed, PersonalRespellings.Load(path).All().Single().State);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Progress_short_of_adoption_is_worth_saving()
    {
        // Three hearings in is a real state. A caller that saved only on adoption
        // would make the person teach the same word from scratch every restart.
        var p = new PersonalRespellings();
        Assert.False(p.HasUnsavedChanges);

        p.Observe(Word, Theirs, Ours);
        Assert.True(p.HasUnsavedChanges);
    }

    [Fact]
    public void A_hearing_that_teaches_nothing_leaves_nothing_behind()
    {
        // A rejected hearing must not create an entry. Every unrelated word in
        // earshot would otherwise litter the table and show up in a "words your
        // CircleAI knows" view as words it had never actually learned.
        var p = new PersonalRespellings();
        p.Observe(Word, "ngiyabonga", Ours);

        Assert.Empty(p.All());
        Assert.False(p.HasUnsavedChanges);
    }

    [Fact]
    public void A_missing_or_broken_file_starts_empty_rather_than_throwing()
    {
        // Losing the learning is bad. Refusing to start because of it is worse —
        // and the person can teach it again just by talking.
        var missing = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.json");
        Assert.Empty(PersonalRespellings.Load(missing).All());

        var broken = Path.Combine(Path.GetTempPath(), $"broken-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(broken, "{ this is not json");
            Assert.Empty(PersonalRespellings.Load(broken).All());
        }
        finally { if (File.Exists(broken)) File.Delete(broken); }
    }

    [Fact]
    public void Nothing_is_learned_from_empty_input()
    {
        var p = new PersonalRespellings();
        Assert.False(p.Observe("", "wayifayi"));
        Assert.False(p.Observe(Word, ""));
        Assert.Empty(p.All());
    }
}
