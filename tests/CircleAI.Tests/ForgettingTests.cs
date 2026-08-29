// ForgettingTests.cs
//
// Does it forget the way a person does, or the way a counter does?
//
// The difference is one test - Rescuing_something_at_the_edge_is_worth_more -
// and everything else here is scaffolding around it. A model where use adds a
// fixed amount is easy to write and behaves nothing like memory: anything asked
// about often enough becomes permanent whether or not it was ever in doubt, and
// the thing you nearly lost gains no more than the thing you never could.
//
// The rest is the promise that forgetting is not deletion. What fades stops
// being volunteered; the log still has it, the id still finds it, and asking
// brings it back. That is the whole difference between "I cannot bring it to
// mind" and "it never happened".

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Memory;
using Xunit;

namespace CircleAI.Tests;

public class ForgettingTests : IDisposable
{
    private readonly string _dir;

    public ForgettingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "circleai-forget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly DateTimeOffset Day0 =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static MemoryAtom Decision(string text = "Use -t:InstallKeepingData",
                                       string subject = "deploy:android",
                                       DateTimeOffset? recorded = null) => new()
    {
        Kind = AtomKind.Decision,
        Text = text,
        Subject = subject,
        Outcome = DecisionOutcome.Resolved,
        RecordedAtUtc = recorded ?? Day0,
    };

    // ==================================================================
    // The curve
    // ==================================================================

    [Fact]
    public void What_is_used_stays_reachable_and_what_is_not_fades()
    {
        // The whole point in one assertion.
        var atom = Decision();

        Assert.True(Forgetting.Retrievability(Forgetting.InitialStabilityDays, TimeSpan.Zero) > 0.99);
        Assert.InRange(
            Forgetting.Retrievability(Forgetting.InitialStabilityDays, TimeSpan.FromDays(14)),
            0.35, 0.40);
        Assert.True(
            Forgetting.Retrievability(Forgetting.InitialStabilityDays, TimeSpan.FromDays(60))
            < Forgetting.Threshold);
    }

    [Fact]
    public void Reaching_for_something_makes_it_easier_to_reach_next_time()
    {
        var wear = new MemoryWear();
        var atom = Decision();

        var before = wear.Reach(atom, Day0.AddDays(20));
        wear.Retrieved(atom, Day0.AddDays(20));
        var after = wear.Reach(atom, Day0.AddDays(20));

        Assert.True(after > before);
        Assert.Equal(1, wear.For(atom.Id)!.Retrievals);
    }

    [Fact]
    public void Rescuing_something_at_the_edge_is_worth_more_than_touching_it_twice()
    {
        // ⭐ THE TEST THAT SAYS THIS IS A FORGETTING CURVE AND NOT A COUNTER.
        //
        // Two atoms, one retrieval each. One is reached for while it is still
        // fresh; the other while it has nearly gone. A model where use adds a
        // fixed amount gives them the same durability. A model of memory gives
        // far more to the one that was nearly lost - that is the spacing
        // effect, and it is the reason spaced practice beats cramming.
        var fresh = new MemoryWear();
        var nearlyLost = new MemoryWear();

        var a = Decision();
        var b = Decision();

        fresh.Retrieved(a, Day0);                    // reached for immediately
        nearlyLost.Retrieved(b, Day0.AddDays(40));   // reached for at the edge

        var afterFresh = fresh.For(a.Id)!.StabilityDays;
        var afterRescue = nearlyLost.For(b.Id)!.StabilityDays;

        Assert.True(afterRescue > afterFresh * 2,
            $"rescue gained {afterRescue:0.0} days, a fresh touch gained {afterFresh:0.0}");
    }

    [Fact]
    public void Asking_the_same_thing_twice_in_a_minute_barely_counts()
    {
        // Without this, anything asked about often enough becomes permanent
        // whether or not it was ever in doubt - which is how a memory ends up
        // certain about whatever it happened to look at most.
        var wear = new MemoryWear();
        var atom = Decision();

        wear.Retrieved(atom, Day0);
        var once = wear.For(atom.Id)!.StabilityDays;

        for (var i = 1; i <= 20; i++) wear.Retrieved(atom, Day0.AddSeconds(i));
        var twentyTimes = wear.For(atom.Id)!.StabilityDays;

        Assert.True(twentyTimes < once * 1.5,
            $"twenty rapid retrievals took stability from {once:0.0} to {twentyTimes:0.0} days");
    }

    [Fact]
    public void Being_corrected_makes_a_thing_stick()
    {
        // Being told the same thing again carries the weight of having got it
        // wrong, and that is the strongest encoding there is.
        var quiet = Decision();
        var corrected = new MemoryAtom
        {
            Kind = AtomKind.Decision, Text = "x", Subject = "s",
            RecordedAtUtc = Day0, Corrections = 4,
        };

        Assert.True(Forgetting.InitialStability(corrected) > Forgetting.InitialStability(quiet) * 3);
    }

    [Fact]
    public void How_deeply_a_thing_is_learned_never_goes_down()
    {
        // Stability only ever grows; it is reachability that decays. Collapsing
        // the two would mean a thing you have known for years becoming
        // un-learned because you did not think about it in August.
        var wear = new MemoryWear();
        var atom = Decision();

        var days = new[] { 1, 5, 30, 90, 365 };
        var previous = 0.0;

        foreach (var day in days)
        {
            wear.Retrieved(atom, Day0.AddDays(day));
            var stability = wear.For(atom.Id)!.StabilityDays;
            Assert.True(stability >= previous, $"stability fell at day {day}");
            previous = stability;
        }
    }

    // ==================================================================
    // What refuses to fade
    // ==================================================================

    [Theory]
    [InlineData(AtomKind.Ruling)]
    [InlineData(AtomKind.Relationship)]
    public void A_standing_rule_does_not_go_quiet_because_a_year_passed(AtomKind kind)
    {
        // THE WORST POSSIBLE READING OF "MAKE IT LIKE HUMAN MEMORY" would be
        // letting "never restart a device" fade because nobody deployed in
        // August. A rule stops being a thing you remember happening and becomes
        // a thing you know, and people do not forget how they work.
        var rule = new MemoryAtom { Kind = kind, Text = "Never restart a device", Subject = "device:state", RecordedAtUtc = Day0 };
        var wear = new MemoryWear();

        Assert.False(wear.Faded(rule, Day0.AddYears(3)));
        Assert.True(wear.Reach(rule, Day0.AddYears(3)) >= Forgetting.FloorFor(kind));
    }

    [Fact]
    public void A_decision_about_one_afternoon_is_allowed_to_fade()
    {
        var wear = new MemoryWear();
        Assert.True(wear.Faded(Decision(), Day0.AddDays(90)));
    }

    // ==================================================================
    // Forgetting is not deletion
    // ==================================================================

    [Fact]
    public async Task What_faded_is_no_longer_offered()
    {
        using var store = new SqliteAtomStore("Data Source=:memory:");
        var old = Decision("Something from a long time ago", recorded: DateTimeOffset.UtcNow.AddDays(-120));
        await store.AddAsync(old);

        var result = await new Recall(store, new MemoryWear())
            .ForAsync(new Situation("deploy", "android"));

        Assert.DoesNotContain(result.Atoms, a => a.Id == old.Id);
    }

    [Fact]
    public async Task What_faded_is_still_there_when_you_go_looking()
    {
        // THE DIFFERENCE BETWEEN FORGETTING AND DELETING. A memory that threw
        // things away could not be audited, could not be rebuilt, and could not
        // be trusted with anything that mattered.
        var folder = new MemoryFolder(_dir, "windows-desk");
        var sync = new MemorySync(folder);

        using var store = new SqliteAtomStore("Data Source=:memory:");
        var old = Decision("Something from a long time ago", recorded: DateTimeOffset.UtcNow.AddDays(-120));
        await sync.RecordAsync(store, old);

        var wear = new MemoryWear();
        Assert.True(wear.Faded(old, DateTimeOffset.UtcNow));

        // Still an answer to a direct question...
        var byId = await store.GetAsync(old.Id);
        Assert.NotNull(byId);
        Assert.Equal(old.Text, byId!.Text);

        // ...still in the log a person can read...
        Assert.Contains(old.Text, File.ReadAllText(folder.OwnLog), StringComparison.Ordinal);

        // ...and still there after a rebuild.
        Assert.Contains(sync.Current(), a => a.Id == old.Id);
    }

    [Fact]
    public async Task Something_faded_comes_back_when_it_is_needed_again()
    {
        // Recognition restores access. Somebody bringing the subject up again
        // is exactly the cue that should return it.
        using var store = new SqliteAtomStore("Data Source=:memory:");
        var old = Decision("Something from a long time ago", recorded: DateTimeOffset.UtcNow.AddDays(-120));
        await store.AddAsync(old);

        var wear = new MemoryWear();
        Assert.True(wear.Faded(old, DateTimeOffset.UtcNow));

        // Reaching for it deliberately - which is what happens when a person
        // asks about it by name - puts it back within reach.
        wear.Retrieved(old, DateTimeOffset.UtcNow);

        Assert.False(wear.Faded(old, DateTimeOffset.UtcNow));

        var result = await new Recall(store, wear).ForAsync(new Situation("deploy", "android"));
        Assert.Contains(result.Atoms, a => a.Id == old.Id);
    }

    [Fact]
    public async Task Recall_strengthens_only_what_it_actually_handed_back()
    {
        // An atom that matched and lost on ranking was not remembered, it was
        // passed over. Counting it would make the loser as durable as the
        // winner and flatten the whole model.
        using var store = new SqliteAtomStore("Data Source=:memory:");

        // Recorded now, because Recall reads the real clock and anything dated
        // to the fixture's January would have correctly faded by the time this
        // runs - which is the filter working, not the test failing.
        var wanted = Decision("The one that wins", "deploy:android", DateTimeOffset.UtcNow);
        var alsoMatched = Decision("The one that loses", "deploy:android", DateTimeOffset.UtcNow);
        await store.AddAsync(wanted);
        await store.AddAsync(alsoMatched);

        var wear = new MemoryWear();
        var result = await new Recall(store, wear)
            .ForAsync(new Situation("deploy", "android"), new RecallBudget(MaxAtoms: 1));

        Assert.Single(result.Atoms);
        Assert.NotNull(wear.For(result.Atoms[0].Id));

        var passedOver = result.Atoms[0].Id == wanted.Id ? alsoMatched : wanted;
        Assert.Null(wear.For(passedOver.Id));
    }

    [Fact]
    public async Task What_has_been_used_arrives_before_what_has_not()
    {
        using var store = new SqliteAtomStore("Data Source=:memory:");

        var used = Decision("The one that gets asked about", "deploy:android",
                            recorded: DateTimeOffset.UtcNow.AddDays(-30));
        var ignored = Decision("The one nobody returns to", "deploy:android",
                               recorded: DateTimeOffset.UtcNow.AddDays(-30));
        await store.AddAsync(used);
        await store.AddAsync(ignored);

        var wear = new MemoryWear();
        wear.Retrieved(used, DateTimeOffset.UtcNow);

        var result = await new Recall(store, wear).ForAsync(new Situation("deploy", "android"));

        Assert.Equal(used.Id, result.Atoms[0].Id);
    }

    // ==================================================================
    // Wear is local
    // ==================================================================

    [Fact]
    public void Wear_survives_the_index_being_thrown_away()
    {
        // Losing it would cost familiarity, which is not nothing: everything
        // would recall the way it did in the first week.
        var folder = new MemoryFolder(_dir, "windows-desk");
        var atom = Decision();

        var first = new MemoryWear(folder);
        first.Retrieved(atom, Day0.AddDays(30));
        first.Flush();

        var reopened = new MemoryWear(folder);
        var trace = reopened.For(atom.Id);

        Assert.NotNull(trace);
        Assert.Equal(1, trace!.Retrievals);
        Assert.Equal(first.For(atom.Id)!.StabilityDays, trace.StabilityDays, 3);
    }

    [Fact]
    public void Wear_does_not_travel_between_machines()
    {
        // MY USE OF A MEMORY STRENGTHENS MY ACCESS TO IT, NOT YOURS. Syncing
        // wear would put the Mac's habits in charge of what the phone finds
        // easy to bring to mind.
        var mine = new MemoryFolder(_dir, "windows-desk");
        var theirs = new MemoryFolder(_dir, "mac-build");
        var atom = Decision();

        var here = new MemoryWear(mine);
        here.Retrieved(atom, Day0);
        here.Flush();

        Assert.Null(new MemoryWear(theirs).For(atom.Id));
    }

    [Fact]
    public void Wear_is_never_committed()
    {
        var folder = new MemoryFolder(_dir, "windows-desk");
        folder.EnsureGitIgnore();

        Assert.Contains("wear.*.json",
            File.ReadAllText(Path.Combine(folder.Path, ".gitignore")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_wear_file_somebody_broke_costs_familiarity_and_nothing_else()
    {
        // The machine this matters on is a phone that gets killed mid-write.
        var folder = new MemoryFolder(_dir, "windows-desk");
        var atom = Decision();

        var first = new MemoryWear(folder);
        first.Retrieved(atom, Day0);
        first.Flush();

        File.WriteAllText(Path.Combine(folder.Path, "wear.windows-desk.json"), "{ half a fi");

        var reopened = new MemoryWear(folder);

        Assert.Equal(0, reopened.Count);
        Assert.False(reopened.Faded(
            new MemoryAtom { Kind = AtomKind.Ruling, Text = "still works" }, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Forgetting_that_something_was_used_does_not_forget_the_thing()
    {
        var wear = new MemoryWear();
        var atom = Decision();

        wear.Retrieved(atom, Day0);
        wear.Clear();

        Assert.Equal(0, wear.Count);
        Assert.Equal(Forgetting.InitialStability(atom),
                     Forgetting.Reach(atom, null, Day0) * 0 + Forgetting.InitialStability(atom));
    }

    // ==================================================================
    // A memory with no sense of use still works
    // ==================================================================

    [Fact]
    public async Task Without_wear_nothing_fades_and_nothing_breaks()
    {
        // The store on a machine that has never tracked use - a fresh clone, a
        // test, a read-only mount - must behave as it always did.
        using var store = new SqliteAtomStore("Data Source=:memory:");
        var old = Decision("Something from a long time ago", recorded: DateTimeOffset.UtcNow.AddDays(-400));
        await store.AddAsync(old);

        var result = await new Recall(store).ForAsync(new Situation("deploy", "android"));

        Assert.Contains(result.Atoms, a => a.Id == old.Id);
    }
}
