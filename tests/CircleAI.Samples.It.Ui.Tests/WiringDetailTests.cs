// WiringDetailTests.cs
//
// The five questions a startup report has to answer per component:
// WHAT it is, HOW FAR it got, WHERE it lives, WHO holds it, HOW LONG it took.
//
// The report answered the first two. The other three existed only inside the
// Detail prose - "unpacked at /data/.../espeak-ng-data" reads fine to a person
// and cannot be grouped, sorted, compared between two phones, or diffed against
// yesterday. A startup report whose facts are only in sentences is a log.
//
// These pin the derived answers, which is where a field nobody fills becomes
// visible: Took summing to zero across a report that shows timings would read as
// "everything was instant" rather than "nothing was measured".

using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Ui.Tests;

public class WiringDetailTests
{
    private static WiringRow Row(
        string title, WiringStage stage, string? where = null,
        string? who = null, double ms = 0) =>
        new(title, "hook", stage, "detail", where, who,
            ms > 0 ? TimeSpan.FromMilliseconds(ms) : null);

    [Fact]
    public void A_report_adds_up_what_its_checks_cost()
    {
        // The loading screen holds the door until these finish, so "which one is
        // costing the wait" is the first question anybody asks of a slow start.
        var report = new WiringReport(
            [Row("a", WiringStage.Wired, ms: 12), Row("b", WiringStage.Wired, ms: 30)], 2, 2);

        Assert.Equal(42, report.Took.TotalMilliseconds, 3);
    }

    [Fact]
    public void It_names_the_slowest_thing_rather_than_only_the_total()
    {
        // "The app takes a while to start" is not a finding. "espeak G2P took 2.1
        // of the 2.4 seconds" is one.
        var report = new WiringReport(
            [
                Row("fast", WiringStage.Wired, ms: 3),
                Row("slow", WiringStage.Wired, ms: 2100),
                Row("middling", WiringStage.Wired, ms: 40),
            ], 3, 3);

        Assert.Equal("slow", report.Slowest?.Title);
    }

    [Fact]
    public void An_untimed_report_says_nothing_rather_than_zero()
    {
        // A row with no timing must not be reported as instant. Slowest returns
        // null when nothing was measured, so a screen can tell "not measured"
        // from "measured and fast".
        var report = new WiringReport([Row("a", WiringStage.Wired)], 1, 1);

        Assert.Null(report.Slowest);
        Assert.Equal(TimeSpan.Zero, report.Took);
    }

    [Fact]
    public void A_mixed_report_only_counts_what_was_timed()
    {
        var report = new WiringReport(
            [Row("timed", WiringStage.Wired, ms: 50), Row("not", WiringStage.Broken)], 1, 2);

        Assert.Equal(50, report.Took.TotalMilliseconds, 3);
        Assert.Equal("timed", report.Slowest?.Title);
    }

    [Fact]
    public void Where_is_allowed_to_be_absent_and_that_is_information()
    {
        // A HOOK THAT WAS NEVER SET HAS NO PLACE TO POINT AT, and that is the
        // whole finding: ItSpeaker.MobilePhonemizerFactory being null is not a
        // file in the wrong folder, it is a line of code nobody ran. Forcing a
        // path here would mean inventing one.
        var unset = Row("Phonemizer", WiringStage.Absent,
            who: "ItSpeaker.MobilePhonemizerFactory, set by VoiceWiring.Install");

        Assert.Null(unset.Where);
        Assert.NotNull(unset.Who);
        Assert.False(unset.Working);
    }

    [Fact]
    public void Who_survives_the_thing_being_missing()
    {
        // The most useful field when something is Absent: not where it isn't,
        // but what was supposed to have put it there. That sentence is the fix.
        var report = new WiringReport(
            [
                Row("Phonemizer", WiringStage.Absent,
                    who: "ItSpeaker.MobilePhonemizerFactory, set by VoiceWiring.Install"),
                Row("Japanese", WiringStage.Wired, where: "/data/openjtalk",
                    who: "OpenJTalkPhonemizer.ModelStoreFolder, set in MainApplication.OnCreate"),
            ], 1, 2);

        Assert.All(report.Failing, r => Assert.False(string.IsNullOrWhiteSpace(r.Who)));
    }

    [Fact]
    public void The_old_shape_still_builds()
    {
        // Where, Who and Took are optional on purpose. Every existing caller -
        // including the browser probe, which genuinely knows none of the three -
        // keeps compiling, so adding them could not break a head by omission.
        var row = new WiringRow("English voice", "voice", WiringStage.Wired, "the browser speaks it");

        Assert.True(row.Working);
        Assert.Null(row.Where);
        Assert.Null(row.Took);
    }
}
