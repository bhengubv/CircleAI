// TheAppDoesNotClaimAnEvictionTests.cs
//
// THE APP REPORTED A BROWNOUT THAT NEVER HAPPENED. On every memory-pressure signal it
// logged "evicting the specialist, keeping the generalist" - five times in eight
// minutes on the P30 on 2026-10-16 - and raised an event nobody in the process was
// listening to: the only subscriber, DeviceBrain, had left the app with the models.
// The models live in the service, which answers its own pressure.
//
// So the raise now returns how many listeners it told, and the app logs that number
// instead of a claim. These pin both halves.

using System;
using System.IO;
using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class TheAppDoesNotClaimAnEvictionTests
{
    [Fact]
    public void The_raise_counts_every_listener_it_told_even_one_that_throws()
    {
        // Relative, not absolute: AppLifecycle is process-wide, and another test may
        // hold a subscription of its own while this one runs.
        var heard = 0;
        Action quiet = () => heard++;
        Action loud  = () => throw new InvalidOperationException("a page that broke");

        var before = AppLifecycle.RaiseMemoryIsShort();
        AppLifecycle.MemoryIsShort += quiet;
        AppLifecycle.MemoryIsShort += loud;
        try
        {
            var told = AppLifecycle.RaiseMemoryIsShort();

            Assert.True(told >= before + 2, $"told {told}, expected at least {before + 2}");
            Assert.Equal(1, heard);   // the throwing one cost the other nothing
        }
        finally
        {
            AppLifecycle.MemoryIsShort -= quiet;
            AppLifecycle.MemoryIsShort -= loud;
        }
    }

    [Fact]
    public void The_app_logs_the_count_and_no_longer_claims_an_eviction()
    {
        var source = File.ReadAllText(Path.Combine(Root(),
            "samples", "CircleAI.Samples.Hybrid", "CircleAI.Samples.App",
            "Platforms", "Android", "MainActivity.cs"));

        // The old statement itself; the remarks above it still quote the words.
        Assert.DoesNotContain("- evicting the specialist, keeping the generalist\");", source);
        Assert.Contains("var told = CircleAI.Assistant.AppLifecycle.RaiseMemoryIsShort();", source);
        Assert.Contains("in-app listener(s) told", source);
    }

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        return dir!;
    }
}
