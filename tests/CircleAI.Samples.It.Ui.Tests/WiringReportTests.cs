// WiringReportTests.cs
//
// The report that would have caught the mute build, tested.
//
// It carries CLAIMED and WORKING side by side, and the gap between them is the
// entire point: the home screen said "78 languages, spoken out loud" from a
// catalogue count while the true number was one. A report that only carried the
// working figure would be as unfalsifiable as the claim it replaced.

using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Ui.Tests;

public class WiringReportTests
{
    private static WiringRow Row(string title, WiringStage stage)
        => new(title, "hook", stage, "because");

    [Fact]
    public void Only_wired_counts_as_working()
    {
        // Present is not ready and Loads is not ready. A file that exists and a
        // model that opens are both things nobody is using yet - and "downloaded
        // therefore fine" is exactly the assumption that hid a null phonemizer
        // for weeks.
        foreach (var stage in new[]
                 { WiringStage.Absent, WiringStage.Present, WiringStage.Loads, WiringStage.Broken })
            Assert.False(Row("x", stage).Working, $"{stage} must not count as working");

        Assert.True(Row("x", WiringStage.Wired).Working);
    }

    [Fact]
    public void Reports_the_claim_next_to_the_truth()
    {
        var report = new WiringReport([Row("a", WiringStage.Wired)], Working: 1, Claimed: 78);

        Assert.Equal("1 of 78 working", report.Summary);
    }

    [Fact]
    public void Failing_lists_everything_not_doing_its_job()
    {
        var report = new WiringReport(
            [Row("ok", WiringStage.Wired),
             Row("missing", WiringStage.Absent),
             Row("bust", WiringStage.Broken)],
            Working: 1, Claimed: 3);

        Assert.Equal(2, report.Failing.Count);
        Assert.DoesNotContain(report.Failing, r => r.Title == "ok");
    }

    [Fact]
    public void A_report_with_nothing_failing_is_the_healthy_one()
    {
        var report = new WiringReport([Row("a", WiringStage.Wired)], 1, 1);

        Assert.Empty(report.Failing);
        Assert.Equal("1 of 1 working", report.Summary);
    }

    [Fact]
    public void Claimed_is_the_whole_catalogue_even_when_few_rows_are_shown()
    {
        // A diagnostics screen asking about three languages must not be able to
        // print "3 of 3" and have it read as everything being fine.
        var report = new WiringReport(
            [Row("a", WiringStage.Wired), Row("b", WiringStage.Wired), Row("c", WiringStage.Wired)],
            Working: 3, Claimed: 78);

        Assert.Equal("3 of 78 working", report.Summary);
    }
}
