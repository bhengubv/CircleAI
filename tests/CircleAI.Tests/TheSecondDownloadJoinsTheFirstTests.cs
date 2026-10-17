// TheSecondDownloadJoinsTheFirstTests.cs
//
// TWO PATHS FETCHED THE SAME MODEL AT ONCE. On the P30 on 2026-10-16 the background
// fetch and the app's loading screen both started Qwen3.5-0.8B within 134 ms, each
// streamed the whole 548 MB into the same llm.mnn.weight.tmp, and three minutes in one
// of them removed that file from under the other:
//
//   Qwen3.5-0.8B-MNN failed: FileNotFoundException ... llm.mnn.weight.tmp
//
// These pin the fix with real concurrency, not a source check: the second caller joins
// the download in flight, hears its progress, and gets its outcome.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class TheSecondDownloadJoinsTheFirstTests
{
    // Unique per test: the registry of flights is process-wide, and tests run in
    // parallel.
    private static string Key() => "model-" + Guid.NewGuid().ToString("N");

    /// <summary>Records reports without a synchronization context, so they arrive at once.</summary>
    private sealed class Heard : IProgress<DownloadProgress>
    {
        public readonly List<long> Bytes = [];
        public void Report(DownloadProgress value) { lock (Bytes) Bytes.Add(value.BytesReceived); }
    }

    [Fact]
    public async Task A_second_caller_joins_rather_than_downloading_again()
    {
        var key = Key();
        var started = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? joinedKey = null;

        var first = InFlightDownloads.RunAsync(key, null, async _ =>
        {
            Interlocked.Increment(ref started);
            await release.Task;
            return "/models/Qwen3.5-0.8B-MNN";
        });
        var second = InFlightDownloads.RunAsync(key, null, _ =>
        {
            Interlocked.Increment(ref started);
            return Task.FromResult("/a/second/download");
        }, joined: k => joinedKey = k);

        release.SetResult();

        Assert.Equal("/models/Qwen3.5-0.8B-MNN", await first);
        Assert.Equal("/models/Qwen3.5-0.8B-MNN", await second);   // the same answer
        Assert.Equal(1, started);                                   // one download, not two
        Assert.Equal(key, joinedKey);
    }

    [Fact]
    public async Task Everyone_sharing_the_download_hears_its_progress()
    {
        var key = Key();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new Heard();
        var b = new Heard();

        var first = InFlightDownloads.RunAsync(key, a, async relay =>
        {
            await release.Task;   // the second caller has joined by now
            relay.Report(new DownloadProgress { BytesReceived = 100, TotalBytes = 200 });
            relay.Report(new DownloadProgress { BytesReceived = 200, TotalBytes = 200 });
            return "done";
        });
        var second = InFlightDownloads.RunAsync(key, b, _ => Task.FromResult("never"));

        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(new long[] { 100, 200 }, a.Bytes);
        Assert.Equal(new long[] { 100, 200 }, b.Bytes);
    }

    [Fact]
    public async Task A_failure_is_everyones_and_the_next_attempt_starts_fresh()
    {
        var key = Key();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = InFlightDownloads.RunAsync(key, null, async _ =>
        {
            await release.Task;
            throw new System.IO.IOException("the network went away");
        });
        var second = InFlightDownloads.RunAsync(key, null, _ => Task.FromResult("never"));

        release.SetResult();

        await Assert.ThrowsAsync<System.IO.IOException>(() => first);
        await Assert.ThrowsAsync<System.IO.IOException>(() => second);

        // Not a cached failure: a retry downloads again.
        Assert.Equal("retried", await InFlightDownloads.RunAsync(key, null, _ => Task.FromResult("retried")));
    }

    [Fact]
    public async Task Different_models_download_side_by_side()
    {
        var started = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string> Download(string name) => InFlightDownloads.RunAsync(Key(), null, async _ =>
        {
            Interlocked.Increment(ref started);
            await release.Task;
            return name;
        });

        var one = Download("ears");
        var two = Download("brain");
        release.SetResult();

        Assert.Equal("ears", await one);
        Assert.Equal("brain", await two);
        Assert.Equal(2, started);
    }

    [Fact]
    public async Task A_finished_download_is_not_held_on_to()
    {
        var key = Key();
        await InFlightDownloads.RunAsync(key, null, _ => Task.FromResult("first"));

        // A later request is a new download (which finds the model already there),
        // not a stale answer from the last one.
        Assert.Equal("second", await InFlightDownloads.RunAsync(key, null, _ => Task.FromResult("second")));
    }
}
