// HousekeepingBeforePullTests.cs
//
// HOUSEKEEPING BEFORE PULLING MODELS - the owner's rule, and on Android it was not the
// order. On the P30's fresh install on 2026-10-23:
//
//   13:54:24.780  CircleAI.Housekeeping: no abandoned downloads to sweep
//   13:54:25.337  CircleAI.Fetch: setup: starting 6 download(s), smallest first
//   13:54:25.550  CIRCLEAI-HOUSEKEEPING planned=0 reclaimed=0
//   13:54:25.552  CircleAI.Setup: catalogue: 90 assessed, 5 run on this device
//
// The sweep and the catalogue's bootstrap ran as two tasks and the downloads waited for
// the sweep alone. The wait itself is portable and tested here on every leg; the places
// that must call it are Android-only, so they are guarded at the source.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class HousekeepingBeforePullTests
{
    [Fact]
    public async Task A_pull_waits_until_housekeeping_has_finished()
    {
        var housekeeping = new TaskCompletionSource();
        var said = new List<string>();

        var wait = ModelSetup.WaitForHousekeepingAsync(housekeeping.Task, said.Add);
        await Task.Delay(200);
        Assert.False(wait.IsCompleted, "the pull went ahead while housekeeping was still running");

        housekeeping.SetResult();
        await wait.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(said, s => s.Contains("housekeeping finished", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_housekeeping_to_wait_for_is_no_wait()
    {
        var said = new List<string>();
        await ModelSetup.WaitForHousekeepingAsync(null, said.Add).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(said);
    }

    [Fact]
    public async Task A_housekeeping_that_failed_does_not_cost_every_download()
    {
        var said = new List<string>();
        await ModelSetup.WaitForHousekeepingAsync(Task.FromException(new IOException("disk")), said.Add)
                        .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(said, s => s.Contains("failed", StringComparison.Ordinal) && s.Contains("go ahead", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_person_waiting_on_speech_is_not_held_for_ever_by_a_housekeeping_that_hangs()
    {
        var said = new List<string>();
        await ModelSetup.WaitForHousekeepingAsync(new TaskCompletionSource().Task, said.Add,
                                                  atMost: TimeSpan.FromMilliseconds(200))
                        .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(said, s => s.Contains("going ahead", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelled_while_waiting_pulls_nothing()
    {
        using var cts = new CancellationTokenSource();
        var wait = ModelSetup.WaitForHousekeepingAsync(new TaskCompletionSource().Task, _ => { }, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    // ── the Android heads, guarded at the source ─────────────────────────────

    private static string Repo()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CircleAI.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Source(params string[] parts)
    {
        var path = Path.Combine(Repo(), Path.Combine(parts));
        Assert.True(File.Exists(path), path + " has moved - this guard is now pointing at nothing");
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_service_pulls_nothing_before_housekeeping_has_finished()
    {
        var app = Source("src", "CircleAIService", "ServiceApplication.cs");

        var storage = app.IndexOf("CircleAI.Device.ModelFetchService.StorageDirectory = ModelStore.Path;", StringComparison.Ordinal);
        var ready   = app.IndexOf("CircleAI.Device.CircleNeuronService.CatalogueReady = System.Threading.Tasks.Task.Run(", StringComparison.Ordinal);
        var sweep   = app.IndexOf(".SweepAbandoned(ModelStore.Path", StringComparison.Ordinal);
        var done    = app.IndexOf("CircleAI.Device.CircleNeuronService.HousekeepingDone = System.Threading.Tasks.Task.WhenAll(", StringComparison.Ordinal);
        var wait    = app.IndexOf("ModelSetup.WaitForHousekeepingAsync(", done, StringComparison.Ordinal);
        var pull    = app.IndexOf("ModelFetchService.StartIfAnythingIsOwed(", StringComparison.Ordinal);
        var warm    = app.IndexOf("WakeTurn.WarmAsync()", StringComparison.Ordinal);

        Assert.True(storage >= 0 && storage < ready, "the bootstrap's dead-weight check reads the store before it is set");
        Assert.True(done > ready && done > sweep, "housekeeping must cover both the sweep and the catalogue's bootstrap");
        Assert.True(wait > done && pull > wait, "the downloads start before housekeeping has finished");
        Assert.True(warm > done, "the voice warm-up can pull a voice before housekeeping has finished");

        // CatalogueReady is the brain's wait: the sweep must not slip into it.
        var readyBody = app[ready..app.IndexOf("});", ready, StringComparison.Ordinal)];
        Assert.DoesNotContain("SweepAbandoned", readyBody, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fetch_service_waits_for_housekeeping_even_when_android_restarts_it()
    {
        var fetch  = Source("src", "CircleAI", "Device", "ModelFetchService.cs");
        var start  = fetch.IndexOf("public override StartCommandResult OnStartCommand(", StringComparison.Ordinal);
        var wait   = fetch.IndexOf("ModelSetup.WaitForHousekeepingAsync(CircleNeuronService.HousekeepingDone", start, StringComparison.Ordinal);
        var resume = fetch.IndexOf("ModelSetup.ResumeUnfinishedAsync(", start, StringComparison.Ordinal);
        var plan   = fetch.IndexOf("ModelSetup.FetchNotStartedAsync(", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && wait > start, "the fetch service never waits for housekeeping");
        Assert.True(resume > wait && plan > wait, "the fetch service pulls before housekeeping has finished");
    }

    [Fact]
    public void A_voice_or_the_ears_fetched_on_demand_wait_for_housekeeping_too()
    {
        var speech = Source("src", "CircleAI", "Assistant.Device", "ServiceSpeech.cs");

        var speak  = speech.IndexOf("public async Task<byte[]> SpeakAsync(", StringComparison.Ordinal);
        var wait1  = speech.IndexOf("WaitForHousekeepingAsync(", speak, StringComparison.Ordinal);
        var render = speech.IndexOf("_voice.RenderAsync(", speak, StringComparison.Ordinal);
        Assert.True(speak >= 0 && wait1 > speak && render > wait1, "a voice can be pulled before housekeeping");

        var listen = speech.IndexOf("private async Task<CircleAIListener?> ListenerAsync(", StringComparison.Ordinal);
        var wait2  = speech.IndexOf("WaitForHousekeepingAsync(", listen, StringComparison.Ordinal);
        var create = speech.IndexOf(".TryCreateAsync(", listen, StringComparison.Ordinal);
        Assert.True(listen >= 0 && wait2 > listen && create > wait2, "the ears can be pulled before housekeeping");

        // And a person waiting on it is never held for ever.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(speech, @"atMost: TimeSpan\.FromSeconds\(30\)").Count);
    }

    [Fact]
    public void Setup_started_from_the_app_waits_for_housekeeping()
    {
        var setup = Source("src", "CircleAI", "Assistant.Device", "DeviceSetup.cs");
        var run   = setup.IndexOf("public Task RunAsync(IProgress<SetupProgressReport> progress", StringComparison.Ordinal);
        var wait  = setup.IndexOf("WaitForHousekeepingAsync(", run, StringComparison.Ordinal);
        var plan  = setup.IndexOf("FirstRun.Plan(", run, StringComparison.Ordinal);

        Assert.True(run >= 0 && wait > run, "setup started from the app never waits for housekeeping");
        Assert.True(plan > wait, "setup plans its downloads before housekeeping has finished");
    }
}
