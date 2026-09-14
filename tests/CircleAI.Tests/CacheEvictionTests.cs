// CacheEvictionTests.cs
//
// The pure policy behind the self-managing cache: what ages out, what the cap
// evicts, and - the load-bearing one - that it only ever drops what it is handed.
// The device side only ever hands it AppPaths.Cache, so the person's memory is
// out of reach by construction; here we prove the arithmetic that reach relies on.
//
// No phone, no disk, no clock of its own: (bytes, time) in, a plan out.

using System;
using System.Collections.Generic;
using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class CacheEvictionTests
{
    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Keep30 = TimeSpan.FromDays(30);
    private static readonly TimeSpan Forever = TimeSpan.MaxValue;
    private const long NoCap = 0;

    /// <summary>A cache file <paramref name="ageDays"/> old, by last-use.</summary>
    private static CacheFile F(string path, long bytes, double ageDays)
        => new(path, bytes, Now - TimeSpan.FromDays(ageDays));

    [Fact]
    public void Empty_cache_is_a_no_op()
    {
        var plan = CacheEviction.Plan([], NoCap, Keep30, Now);
        Assert.Empty(plan.Delete);
        Assert.Equal(0, plan.FreedBytes);
        Assert.Equal("", plan.Reason);
    }

    [Fact]
    public void Fresh_files_with_no_cap_are_all_kept()
    {
        var files = new[] { F("a", 10, 1), F("b", 20, 5) };
        var plan = CacheEviction.Plan(files, NoCap, Keep30, Now);
        Assert.Empty(plan.Delete);
        Assert.Equal("", plan.Reason);
    }

    [Fact]
    public void Age_drops_only_the_stale_files()
    {
        var files = new[] { F("fresh", 10, 5), F("stale", 20, 40) };
        var plan = CacheEviction.Plan(files, NoCap, Keep30, Now);
        Assert.Equal(new[] { "stale" }, plan.Delete);
        Assert.Equal(20, plan.FreedBytes);
        Assert.Equal("aged out", plan.Reason);
    }

    [Fact]
    public void A_file_exactly_at_the_window_is_kept()
    {
        // > keep, not >=, so a file precisely at the window survives.
        var plan = CacheEviction.Plan(new[] { F("edge", 10, 30) }, NoCap, Keep30, Now);
        Assert.Empty(plan.Delete);
    }

    [Fact]
    public void One_second_past_the_window_ages_out()
    {
        var f = new CacheFile("edge", 10, Now - Keep30 - TimeSpan.FromSeconds(1));
        var plan = CacheEviction.Plan(new[] { f }, NoCap, Keep30, Now);
        Assert.Equal(new[] { "edge" }, plan.Delete);
    }

    [Fact]
    public void Forever_never_ages_anything_out()
    {
        var plan = CacheEviction.Plan(new[] { F("ancient", 10, 3650) }, NoCap, Forever, Now);
        Assert.Empty(plan.Delete);
    }

    [Fact]
    public void Cap_evicts_oldest_first_until_under()
    {
        var files = new[]
        {
            F("new", 30, 1),
            F("mid", 30, 10),
            F("old", 20, 20),
            F("oldest", 20, 40),
        };
        // total 100, cap 60: drop oldest(20)->80, old(20)->60, stop.
        var plan = CacheEviction.Plan(files, 60, Forever, Now);
        Assert.Equal(new[] { "oldest", "old" }, plan.Delete);
        Assert.Equal(40, plan.FreedBytes);
        Assert.Equal("over the cap", plan.Reason);
    }

    [Fact]
    public void Cap_already_satisfied_drops_nothing()
    {
        var files = new[] { F("a", 10, 1), F("b", 20, 2) };
        var plan = CacheEviction.Plan(files, 1000, Forever, Now);
        Assert.Empty(plan.Delete);
    }

    [Fact]
    public void No_cap_never_evicts_for_size()
    {
        var files = new[] { F("big", 10_000_000_000, 1) };
        var plan = CacheEviction.Plan(files, NoCap, Forever, Now);
        Assert.Empty(plan.Delete);
    }

    [Fact]
    public void Age_and_cap_together_report_both()
    {
        var files = new[]
        {
            F("stale", 50, 40),      // aged out
            F("newishBig", 80, 2),
            F("newBig", 80, 1),
        };
        // after age: survivors newishBig(80)+newBig(80)=160, cap 100 ->
        // drop oldest survivor newishBig(80)->80, stop.
        var plan = CacheEviction.Plan(files, 100, Keep30, Now);
        Assert.Contains("stale", plan.Delete);
        Assert.Contains("newishBig", plan.Delete);
        Assert.DoesNotContain("newBig", plan.Delete);
        Assert.Equal("aged out and over the cap", plan.Reason);
    }

    [Fact]
    public void Delete_list_is_oldest_first()
    {
        var files = new[]
        {
            F("mid", 10, 40),
            F("oldest", 10, 100),
            F("newer", 10, 35),
        };
        var plan = CacheEviction.Plan(files, NoCap, Keep30, Now);
        Assert.Equal(new[] { "oldest", "mid", "newer" }, plan.Delete);
    }

    [Fact]
    public void Equal_timestamps_break_ties_by_path()
    {
        var t = Now - TimeSpan.FromDays(40);
        var files = new[]
        {
            new CacheFile("b", 10, t),
            new CacheFile("a", 10, t),
        };
        var plan = CacheEviction.Plan(files, NoCap, Keep30, Now);
        Assert.Equal(new[] { "a", "b" }, plan.Delete);
    }

    [Fact]
    public void A_future_timestamp_is_treated_as_fresh()
    {
        // Clock skew: a file stamped in the future reads as age zero and is kept -
        // the safe way to be wrong.
        var f = new CacheFile("future", 10, Now + TimeSpan.FromDays(5));
        var plan = CacheEviction.Plan(new[] { f }, NoCap, Keep30, Now);
        Assert.Empty(plan.Delete);
    }

    [Fact]
    public void Memory_sized_precious_bytes_are_irrelevant_because_they_are_never_passed()
    {
        // The device side hands this ONLY cache files. To make the invariant
        // explicit: even a huge, ancient entry is dropped without ceremony because
        // it IS cache - nothing here knows or cares about models or memory, which
        // never arrive in this list.
        var files = new[] { F("scratch", 5_000_000_000, 400) };
        var plan = CacheEviction.Plan(files, CacheEviction.MaxCacheDefaultBytes, Keep30, Now);
        Assert.Equal(new[] { "scratch" }, plan.Delete);
    }
}
