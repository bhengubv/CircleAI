// PredictiveWarmupTests.cs
//
// (RT-07) Tests for the histogram predictor + controller. Uses a fake
// clock and fake IAIService so no real generator is loaded.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting;
using CircleAI.Hosting.Warmup;
using CircleAI.Inference;
using CircleAI.Memory;
using CircleAI.Tools;
using Xunit;

namespace CircleAI.Tests;

public sealed class HistogramRequestPredictorTests
{
    [Fact]
    public void Predict_NoData_ReturnsZeroConfidence()
    {
        var p = new HistogramRequestPredictor();
        var f = p.Predict(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        Assert.Equal(0.0, f.ProbabilityOfArrival);
        Assert.Equal(0.0, f.Confidence);
        Assert.Equal(0, p.ObservedArrivals);
    }

    [Fact]
    public void Predict_AfterArrivals_ReportsNonZeroProbability()
    {
        var p = new HistogramRequestPredictor();
        var slot = new DateTimeOffset(2026, 6, 17, 14, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 30; i++) p.RecordArrival(slot.AddMinutes(i));

        // Forecast a 5-minute window at the same hour — should show
        // non-zero probability and increasing confidence.
        var fwd = p.Predict(slot, TimeSpan.FromMinutes(5));
        Assert.True(fwd.ProbabilityOfArrival > 0);
        Assert.True(fwd.Confidence > 0);
        Assert.Equal(30, p.ObservedArrivals);
    }

    [Fact]
    public void Predict_QuietPeriod_ReportsLowProbability()
    {
        var p = new HistogramRequestPredictor();
        var busy  = new DateTimeOffset(2026, 6, 17, 9,  0, 0, TimeSpan.Zero);
        var quiet = new DateTimeOffset(2026, 6, 17, 23, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 30; i++) p.RecordArrival(busy.AddMinutes(i));

        var fwd = p.Predict(quiet, TimeSpan.FromMinutes(5));
        Assert.True(fwd.ProbabilityOfArrival < 0.1);
    }
}

public sealed class PredictiveWarmupControllerTests
{
    private sealed class FakeService : IAIService
    {
        public int PrewarmCalls;
        public bool IsReady => true;

        public Task PrewarmAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref PrewarmCalls);
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> AskAsync(string q, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> ChatAsync(IReadOnlyList<ChatMessage> m, GenerationOptions? o = null, CancellationToken ct = default) => Task.FromResult("");
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> m, GenerationOptions? o = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<ToolResult> InvokeToolAsync(ToolInvocation i, CancellationToken ct = default) => Task.FromResult(ToolResult.Failure("n/a", "no tools wired"));
        public Task<string> AgenticChatAsync(string p, GenerationOptions? o = null, CancellationToken ct = default) => Task.FromResult("");
        public Task SubmitFeedbackAsync(FeedbackSignal s, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TickAsync_BelowThreshold_DoesNothing()
    {
        var svc = new FakeService();
        var p   = new HistogramRequestPredictor(); // no arrivals -> low score
        var opts = new PredictiveWarmupOptions { Enabled = true, WarmupThreshold = 0.5 };
        await using var c = new PredictiveWarmupController(svc, p, opts);

        var fired = await c.TickAsync();
        Assert.False(fired);
        Assert.Equal(0, svc.PrewarmCalls);
    }

    [Fact]
    public async Task TickAsync_AboveThreshold_FiresPrewarm()
    {
        var svc = new FakeService();
        var p   = new HistogramRequestPredictor();
        var when = new DateTimeOffset(2026, 6, 17, 14, 0, 0, TimeSpan.Zero);
        // Hammer the predictor so probability×confidence at this hour clears 0.5
        for (var d = 0; d < 8; d++)
            for (var i = 0; i < 60; i++)
                p.RecordArrival(when.AddDays(d).AddMinutes(i));

        var opts = new PredictiveWarmupOptions { Enabled = true, WarmupThreshold = 0.15 };
        await using var c = new PredictiveWarmupController(svc, p, opts, clock: () => when);

        var fired = await c.TickAsync();
        Assert.True(fired);
        Assert.Equal(1, svc.PrewarmCalls);
    }

    [Fact]
    public async Task TickAsync_RespectsMinTimeBetweenWarmups()
    {
        var svc = new FakeService();
        var p   = new HistogramRequestPredictor();
        var when = new DateTimeOffset(2026, 6, 17, 14, 0, 0, TimeSpan.Zero);
        for (var d = 0; d < 8; d++)
            for (var i = 0; i < 60; i++)
                p.RecordArrival(when.AddDays(d).AddMinutes(i));

        var opts = new PredictiveWarmupOptions
        {
            Enabled               = true,
            WarmupThreshold       = 0.15,
            MinTimeBetweenWarmups = TimeSpan.FromMinutes(10),
        };
        await using var c = new PredictiveWarmupController(svc, p, opts, clock: () => when);

        Assert.True(await c.TickAsync());
        Assert.False(await c.TickAsync()); // throttled — within 10 minutes
        Assert.Equal(1, svc.PrewarmCalls);
    }

    [Fact]
    public void NotifyArrival_FeedsPredictor()
    {
        var svc  = new FakeService();
        var p    = new HistogramRequestPredictor();
        var opts = new PredictiveWarmupOptions();
        var c    = new PredictiveWarmupController(svc, p, opts);

        Assert.Equal(0, p.ObservedArrivals);
        c.NotifyArrival();
        c.NotifyArrival();
        Assert.Equal(2, p.ObservedArrivals);
    }
}
