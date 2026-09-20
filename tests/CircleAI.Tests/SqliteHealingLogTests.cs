// SqliteHealingLogTests.cs
//
// The durable healing store, over an in-memory SQLite database — round-trips every
// field, orders newest-first, marks handled, and counts by time.

using System;
using System.Threading.Tasks;
using CircleAI.Hosting.SelfHealing;
using Xunit;

namespace CircleAI.Tests;

public sealed class SqliteHealingLogTests
{
    private static HealingRecord Rec(string id, DateTimeOffset at, HealingOutcome outcome,
        bool needsHuman, string? recommended = "retry")
        => new(
            Id: id, At: at, Signature: "test|boom", Source: "Test", Message: "boom",
            Kind: "quick-fix", Category: "cache", Summary: "safe to retry",
            RecommendedAction: recommended, ActionTaken: "auto-fixed — cache",
            Outcome: outcome, NeedsHuman: needsHuman, Handled: false);

    [Fact]
    public async Task Round_trips_a_record_through_every_field()
    {
        using var log = new SqliteHealingLog("Data Source=:memory:");
        var at = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        await log.AppendAsync(Rec("a", at, HealingOutcome.Escalated, needsHuman: true, recommended: null));

        var back = Assert.Single(await log.RecentAsync());
        Assert.Equal("a", back.Id);
        Assert.Equal(at, back.At);
        Assert.Equal("test|boom", back.Signature);
        Assert.Equal(HealingOutcome.Escalated, back.Outcome);
        Assert.True(back.NeedsHuman);
        Assert.Null(back.RecommendedAction);
        Assert.False(back.Handled);
    }

    [Fact]
    public async Task Recent_is_newest_first_and_limited()
    {
        using var log = new SqliteHealingLog("Data Source=:memory:");
        var t0 = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        await log.AppendAsync(Rec("old", t0, HealingOutcome.Healed, false));
        await log.AppendAsync(Rec("mid", t0.AddHours(1), HealingOutcome.Healed, false));
        await log.AppendAsync(Rec("new", t0.AddHours(2), HealingOutcome.Healed, false));

        var all = await log.RecentAsync();
        Assert.Equal(new[] { "new", "mid", "old" }, new[] { all[0].Id, all[1].Id, all[2].Id });

        var one = await log.RecentAsync(limit: 1);
        Assert.Single(one);
        Assert.Equal("new", one[0].Id);
    }

    [Fact]
    public async Task Mark_handled_sets_handled_and_clears_needs_human()
    {
        using var log = new SqliteHealingLog("Data Source=:memory:");
        var at = DateTimeOffset.UtcNow;
        await log.AppendAsync(Rec("x", at, HealingOutcome.Escalated, needsHuman: true));

        await log.MarkHandledAsync("x");

        var back = Assert.Single(await log.RecentAsync());
        Assert.True(back.Handled);
        Assert.False(back.NeedsHuman);
    }

    [Fact]
    public async Task Count_since_counts_only_recent_rows()
    {
        using var log = new SqliteHealingLog("Data Source=:memory:");
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        await log.AppendAsync(Rec("old", now.AddHours(-5), HealingOutcome.Healed, false));
        await log.AppendAsync(Rec("recent", now.AddMinutes(-5), HealingOutcome.Healed, false));

        Assert.Equal(1, await log.CountSinceAsync(now.AddHours(-1)));
        Assert.Equal(2, await log.CountSinceAsync(now.AddHours(-6)));
    }

    [Fact]
    public async Task Survives_a_reopen_of_the_same_file()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"healing-{Guid.NewGuid():N}.db");
        // Pooling=False so each Dispose releases the file handle deterministically — the
        // test can then delete the file. The app keeps one long-lived store and never
        // deletes, so it uses the default (pooled) connection string.
        var cs = $"Data Source={path};Pooling=False";
        try
        {
            using (var log = new SqliteHealingLog(cs))
                await log.AppendAsync(Rec("persist", DateTimeOffset.UtcNow, HealingOutcome.Healed, false));

            using (var reopened = new SqliteHealingLog(cs))
                Assert.Single(await reopened.RecentAsync());
        }
        finally { try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { /* best-effort cleanup */ } }
    }
}
