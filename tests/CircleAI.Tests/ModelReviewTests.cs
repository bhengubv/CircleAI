// ModelReviewTests.cs
//
// "CHECK ONCE EVERY HOUR WHEN DEVICE IS IDLE." (the owner, 2026-10-24)
//
// The question - "is this the best model given the resources we have?" - was answered
// once, at first launch, on the resources of that minute, and then believed for ever.
// The P30 ran Qwen3-0.6B for a week while Qwen3.5-0.8B was missing, and the only
// thing that would have fetched the better one was somebody reinstalling the app.
//
// The schedule is here because the two halves either side of it cannot be tested on
// this box: whether the screen is off needs a phone, and what to fetch needs a model
// store. Every rule in this area that turned out to be wrong was one that lived in a
// place no test could see.

using System;
using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class ModelReviewTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Fact]
    public void It_is_an_hour()
        => Assert.Equal(Hour, ModelReview.Every);

    [Fact]
    public void An_idle_phone_that_is_due_is_reviewed()
        => Assert.True(ModelReview.Due(deviceIdle: true, alreadyFetching: false, sinceLast: Hour));

    [Fact]
    public void A_phone_in_somebodys_hands_is_left_alone()
        // The review can spend data and fill storage. It waits for the moment that
        // costs nothing to notice.
        => Assert.False(ModelReview.Due(deviceIdle: false, alreadyFetching: false, sinceLast: TimeSpan.FromDays(1)));

    [Fact]
    public void A_fetch_already_running_is_not_reviewed_on_top_of()
        => Assert.False(ModelReview.Due(deviceIdle: true, alreadyFetching: true, sinceLast: TimeSpan.FromDays(1)));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(59)]
    public void Before_the_hour_is_up_nothing_happens(int minutes)
        => Assert.False(ModelReview.Due(true, false, TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void A_skipped_review_is_not_owed_twice_over()
    {
        // THE CLOCK DOES NOT RUN WHILE THE PHONE IS IN USE. Somebody on their phone
        // for six hours has skipped five reviews; putting it down earns them ONE, not
        // five downloads beginning at once.
        Assert.False(ModelReview.Due(deviceIdle: false, alreadyFetching: false, sinceLast: TimeSpan.FromHours(6)));
        Assert.True(ModelReview.Due(deviceIdle: true, alreadyFetching: false, sinceLast: TimeSpan.FromHours(6)));
    }

    [Fact]
    public void A_device_never_reviewed_is_due_the_moment_it_is_idle()
        => Assert.True(ModelReview.Due(deviceIdle: true, alreadyFetching: false, sinceLast: TimeSpan.MaxValue));

    // ── the Android half, guarded at the source ──────────────────────────────

    private static string Code(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "CircleAI.sln")))
            dir = System.IO.Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        var path = System.IO.Path.Combine(dir!, System.IO.Path.Combine(parts));
        Assert.True(System.IO.File.Exists(path), path + " has moved - this guard points at nothing");

        var text = System.IO.File.ReadAllText(path);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[ \t]*///?.*$", "",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        return text;
    }

    [Fact]
    public void The_review_runs_in_the_process_that_owns_the_models()
    {
        var app = Code("src", "CircleAIService", "ServiceApplication.cs");
        Assert.Contains("IdleModelReview.Start(this)", app, StringComparison.Ordinal);

        // AFTER the start-up fetch: the review's clock starts then, so the first one
        // is an hour away rather than seconds after the answer it would repeat.
        var fetch  = app.IndexOf("ModelFetchService.StartIfAnythingIsOwed(this, loader)", StringComparison.Ordinal);
        var review = app.IndexOf("IdleModelReview.Start(this)", StringComparison.Ordinal);
        Assert.True(fetch >= 0 && review > fetch, "the review is started before the fetch it would repeat");
    }

    [Fact]
    public void The_review_decides_nothing_itself()
    {
        var review = Code("src", "CircleAI", "Device", "IdleModelReview.cs");

        // The schedule is the tested one.
        Assert.Contains("ModelReview.Due(", review, StringComparison.Ordinal);

        // The plan and the fetching are the existing path's, computed fresh - not a
        // second opinion written here that could disagree with setup's.
        Assert.Contains("ModelFetchService.StartIfAnythingIsOwed(", review, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstRun.Plan(", review, StringComparison.Ordinal);

        // Idle is the screen being off. Doze is the stricter state and the wrong one:
        // a phone in doze does not run this timer at all.
        Assert.Contains("IsInteractive", review, StringComparison.Ordinal);
        Assert.DoesNotContain("IsDeviceIdleMode", review, StringComparison.Ordinal);

        // A timer in the service process that threw would take the process with it.
        Assert.Contains("catch (Exception ex)", review, StringComparison.Ordinal);
    }
}
