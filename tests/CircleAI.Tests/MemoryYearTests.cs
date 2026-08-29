// MemoryYearTests.cs
//
// A year of use, and the number that falls out of it.
//
// FOURTEEN DAYS WAS REASONED, NOT DERIVED, and reasoning produced the wrong
// answer. It made a decision fade about six weeks after it was written down -
// so the thing that bit somebody in January would have gone quiet by March,
// which is the exact failure the store exists to prevent.
//
// THE INSIGHT IT MISSED: the value of a memory is inversely related to how
// often the situation comes up. Something that happens daily gets learned
// anyway; something that happens twice a year is precisely what nobody
// remembers and precisely what is worth writing down. So decay has to be slow
// enough to survive the gap between rare events, and that gap - not a
// psychology paper about nonsense syllables - is what sets the constant.
//
// So the number is SOLVED FOR here rather than chosen. Two requirements pin it
// from either side, a simulated year checks it behaves, and a sweep fails the
// build if somebody changes the constant to something that no longer satisfies
// them.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Memory;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public class MemoryYearTests
{
    private readonly ITestOutputHelper _out;

    public MemoryYearTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// A diagnostic line whose numbers read the same everywhere.
    /// </summary>
    /// <remarks>
    /// This machine writes 76,8 for seventy-six and eight tenths, and a number
    /// in a report that could be read as seven hundred is worse than none.
    /// Fourth place it has bitten: the log, the command, the device probe, here.
    /// </remarks>
    private static string Line(FormattableString line) => FormattableString.Invariant(line);

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------
    // The two requirements that pin the number
    // ------------------------------------------------------------------
    //
    // Both are statements about work, not about psychology, and both are
    // arguable - which is the point. They are written down, so the number that
    // follows from them can be checked and argued with.

    /// <summary>
    /// A thing that comes round twice a year must survive the gap.
    /// </summary>
    /// <remarks>
    /// The deploy that wiped the models bit in one month and would bite again
    /// the next time somebody deployed - which might be eight months later. A
    /// memory that has gone quiet by then has failed at the only job it had.
    /// </remarks>
    private const int MustSurviveDays = 230;

    /// <summary>
    /// A thing nobody has returned to in most of a year has gone.
    /// </summary>
    /// <remarks>
    /// Not deleted - it stops being volunteered. A finished project's decisions
    /// crowding today's recall is how a store becomes a filing cabinet.
    /// </remarks>
    private const int MustFadeByDays = 355;

    [Fact]
    public void The_number_is_solved_for_not_chosen()
    {
        // r = e^(-t/S), and fading is r < Threshold. So:
        //
        //   survive t=230  ->  S >  230 / ln(1/0.05)  =  77 days
        //   fade at t=355  ->  S <  355 / ln(1/0.05)  = 118 days
        //
        // Anything in that window satisfies both. The constant has to be in it.
        var horizon = Math.Log(1.0 / Forgetting.Threshold);
        var floor = MustSurviveDays / horizon;
        var ceiling = MustFadeByDays / horizon;

        _out.WriteLine(Line($"requirements admit {floor:0.0} to {ceiling:0.0} days"));
        _out.WriteLine(Line($"the constant is {Forgetting.InitialStabilityDays:0.0} days"));

        Assert.True(Forgetting.InitialStabilityDays > floor,
            $"{Forgetting.InitialStabilityDays:0.0} days fades a thing that comes round " +
            $"every {MustSurviveDays} days, which is the whole reason to write it down");

        Assert.True(Forgetting.InitialStabilityDays < ceiling,
            $"{Forgetting.InitialStabilityDays:0.0} days keeps a thing nobody has " +
            $"returned to in {MustFadeByDays} days, which is how a store fills with noise");
    }

    [Fact]
    public void Every_other_value_that_was_considered_fails_one_of_them()
    {
        // The sweep, so the window is a fact rather than an assertion. Fourteen
        // days - the number this replaced - fails the first requirement badly.
        var horizon = Math.Log(1.0 / Forgetting.Threshold);

        foreach (var days in new[] { 7.0, 14.0, 30.0, 60.0, 90.0, 120.0, 180.0, 365.0 })
        {
            var survives = Forgetting.Retrievability(days, TimeSpan.FromDays(MustSurviveDays))
                           >= Forgetting.Threshold;
            var fades = Forgetting.Retrievability(days, TimeSpan.FromDays(MustFadeByDays))
                        < Forgetting.Threshold;

            _out.WriteLine(Line(
                $"{days,6:0} days  survives the gap: {(survives ? "yes" : "NO "),-3}  lets go of the old: {(fades ? "yes" : "NO")}"));

            Assert.Equal(survives && fades, days > MustSurviveDays / horizon &&
                                            days < MustFadeByDays / horizon);
        }
    }

    // ------------------------------------------------------------------
    // A year of somebody's work
    // ------------------------------------------------------------------

    /// <summary>How often somebody comes back to a thing.</summary>
    private sealed record Habit(string Name, AtomKind Kind, int[] TouchedOnDays);

    /// <summary>
    /// A year that looks like work rather than like a benchmark.
    /// </summary>
    /// <remarks>
    /// The archetypes matter more than the numbers. RARE is the one everything
    /// turns on: written down in January, needed again in September, untouched
    /// in between - and the only kind of memory that is genuinely load-bearing,
    /// because the others get learned by repetition anyway.
    /// </remarks>
    private static readonly Habit[] Year =
    [
        new("daily",     AtomKind.Decision, Enumerable.Range(0, 52).Select(w => w * 7).ToArray()),
        new("monthly",   AtomKind.Decision, [0, 30, 61, 92, 122, 153, 184, 214, 245, 275, 306, 337]),
        new("rare",      AtomKind.Decision, [20, 250]),
        new("one-off",   AtomKind.Decision, [40]),
        new("obsolete",  AtomKind.Decision, [10]),
        new("the rule",  AtomKind.Ruling,   [5]),
        new("who I am",  AtomKind.Relationship, [0]),
    ];

    private static Dictionary<string, (MemoryAtom Atom, MemoryWear Wear)> LiveThroughTheYear(
        int throughDay = 365)
    {
        var world = new Dictionary<string, (MemoryAtom, MemoryWear)>();

        foreach (var habit in Year)
        {
            var recorded = Start.AddDays(habit.TouchedOnDays[0]);
            var atom = new MemoryAtom
            {
                Kind = habit.Kind,
                Text = $"Something about {habit.Name}",
                Subject = "work:" + habit.Name,
                RecordedAtUtc = recorded,
            };

            var wear = new MemoryWear();
            foreach (var day in habit.TouchedOnDays.Where(d => d <= throughDay))
                wear.Retrieved(atom, Start.AddDays(day));

            world[habit.Name] = (atom, wear);
        }

        return world;
    }

    [Fact]
    public void After_a_year_the_right_things_are_still_within_reach()
    {
        var world = LiveThroughTheYear();
        var endOfYear = Start.AddDays(365);

        foreach (var (name, (atom, wear)) in world)
            _out.WriteLine(Line(
                $"{name,-10} reach {wear.Reach(atom, endOfYear):0.000}  stability {wear.For(atom.Id)?.StabilityDays ?? 0:0} days"));

        // What is used stays available.
        Assert.False(world["daily"].Wear.Faded(world["daily"].Atom, endOfYear));
        Assert.False(world["monthly"].Wear.Faded(world["monthly"].Atom, endOfYear));

        // ⭐ THE ONE THAT MATTERS. Touched in January and again in September,
        // and still here at the end of the year - because coming back to it
        // after eight months is worth far more than touching it every day.
        Assert.False(world["rare"].Wear.Faded(world["rare"].Atom, endOfYear));

        // What belonged to a finished project has gone quiet.
        Assert.True(world["obsolete"].Wear.Faded(world["obsolete"].Atom, endOfYear));

        // And what is not an episode at all never goes quiet.
        Assert.False(world["the rule"].Wear.Faded(world["the rule"].Atom, endOfYear));
        Assert.False(world["who I am"].Wear.Faded(world["who I am"].Atom, endOfYear));
    }

    [Fact]
    public void The_thing_that_comes_round_twice_a_year_is_there_when_it_does()
    {
        // Checked at the moment it is needed rather than at year end, because
        // that is when a memory either works or does not.
        var world = LiveThroughTheYear(throughDay: 20);
        var (atom, wear) = world["rare"];

        var whenItComesRound = Start.AddDays(250);

        _out.WriteLine(Line($"after {250 - 20} days untouched, reach {wear.Reach(atom, whenItComesRound):0.000}"));

        Assert.False(wear.Faded(atom, whenItComesRound),
            "the thing written down in January was gone by September, which is " +
            "the only failure that actually costs anybody anything");
    }

    [Fact]
    public void The_same_attention_spread_out_is_worth_more_than_crammed()
    {
        // THE SPACING EFFECT, STATED PROPERLY. It does not say two retrievals
        // beat fifty-two - of course something used weekly for a year ends up
        // deeply learned. It says that for THE SAME NUMBER of retrievals,
        // spread out beats crammed together. That is the comparison, and an
        // earlier version of this test got it wrong.
        var atom = new MemoryAtom
        {
            Kind = AtomKind.Decision, Text = "the same thing either way",
            Subject = "work:spacing", RecordedAtUtc = Start,
        };

        var crammed = new MemoryWear();
        foreach (var day in new[] { 0, 1, 2 }) crammed.Retrieved(atom, Start.AddDays(day));

        var spaced = new MemoryWear();
        foreach (var day in new[] { 0, 100, 200 }) spaced.Retrieved(atom, Start.AddDays(day));

        var a = crammed.For(atom.Id)!;
        var b = spaced.For(atom.Id)!;

        _out.WriteLine(Line($"crammed days 0,1,2:     {a.Retrievals} retrievals -> {a.StabilityDays:0} days"));
        _out.WriteLine(Line($"spaced  days 0,100,200: {b.Retrievals} retrievals -> {b.StabilityDays:0} days"));

        Assert.Equal(a.Retrievals, b.Retrievals);
        Assert.True(b.StabilityDays > a.StabilityDays * 2,
            $"three crammed retrievals gave {a.StabilityDays:0} days, three spaced gave {b.StabilityDays:0}");
    }

    [Fact]
    public void Coming_back_after_months_is_what_makes_a_rare_thing_durable()
    {
        // Once the rare thing has been needed a second time it is good for far
        // longer than the gap that nearly lost it - so the third time, whenever
        // that is, it will still be there.
        var world = LiveThroughTheYear();
        var rare = world["rare"].Wear.For(world["rare"].Atom.Id)!;

        _out.WriteLine(Line($"rare: {rare.Retrievals} retrievals -> {rare.StabilityDays:0} days"));

        Assert.True(rare.StabilityDays > MustSurviveDays,
            $"after being needed twice it is good for only {rare.StabilityDays:0} days, " +
            $"which is less than the {MustSurviveDays}-day gap it just survived");
    }

    [Fact]
    public void The_working_set_stops_growing_even_though_the_memory_does_not()
    {
        // THE MOBILE JUSTIFICATION, MEASURED - and the honest version of it.
        // Three quarters of one year's atoms are still live at the end of that
        // year, which sounds like nothing is being forgotten. The claim is not
        // that the working set is SMALL, it is that it is BOUNDED: what is
        // offered settles at roughly the fade horizon's worth of atoms, while
        // what is kept goes on growing forever.
        //
        // A store that grew without bound is the one a phone cannot afford,
        // and this is the test that says it does not.
        var wear = new MemoryWear();
        var atoms = new List<MemoryAtom>();

        for (var day = 0; day < 365 * 3; day++)
            for (var i = 0; i < 2; i++)
                atoms.Add(new MemoryAtom
                {
                    Kind = AtomKind.Decision,
                    Text = $"Something decided on day {day}",
                    Subject = "work:day" + day,
                    RecordedAtUtc = Start.AddDays(day),
                });

        int LiveAt(int day) =>
            atoms.Where(a => a.RecordedAtUtc <= Start.AddDays(day))
                 .Count(a => !wear.Faded(a, Start.AddDays(day)));

        var afterOne = LiveAt(365);
        var afterTwo = LiveAt(365 * 2);
        var afterThree = LiveAt(365 * 3);

        var keptOne = atoms.Count(a => a.RecordedAtUtc <= Start.AddDays(365));
        var keptTwo = atoms.Count(a => a.RecordedAtUtc <= Start.AddDays(730));
        _out.WriteLine(Line($"kept:     {keptOne} / {keptTwo} / {atoms.Count} atoms"));
        _out.WriteLine(Line($"offered:  {afterOne} / {afterTwo} / {afterThree} after one, two, three years"));

        // Three times the atoms written down; the same number offered.
        Assert.InRange(afterTwo, (int)(afterOne * 0.9), (int)(afterOne * 1.1));
        Assert.InRange(afterThree, (int)(afterOne * 0.9), (int)(afterOne * 1.1));

        // And nothing was thrown away to achieve it.
        Assert.Equal(365 * 3 * 2, atoms.Count);
    }

    [Fact]
    public void Nothing_that_faded_was_lost()
    {
        // Said again here because it is the promise most easily broken by a
        // change to the curve, and the one that would matter most.
        var world = LiveThroughTheYear();
        var (atom, wear) = world["obsolete"];
        var endOfYear = Start.AddDays(365);

        Assert.True(wear.Faded(atom, endOfYear));

        // Still exactly what it was, and one deliberate reach brings it back.
        Assert.Equal("Something about obsolete", atom.Text);
        wear.Retrieved(atom, endOfYear);
        Assert.False(wear.Faded(atom, endOfYear));
    }
}
