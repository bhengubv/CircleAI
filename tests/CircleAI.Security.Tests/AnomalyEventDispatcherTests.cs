// AnomalyEventDispatcherTests.cs
//
// Verifies the safe-by-default verify -> dedup -> dispatch composer wrapping
// ISecurityWatchdog. Mirrors Bhengu.Finance.Payments.Tests.Webhooks.
// WebhookEventDispatcherTests for shape and intent.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Security;
using Xunit;

namespace CircleAI.Security.Tests;

public sealed class AnomalyEventDispatcherTests
{
    private sealed class CountingWatchdog : ISecurityWatchdog
    {
        public int CallCount { get; private set; }

        public Task<SecurityResponse> OnAnomalyDetectedAsync(
            AnomalySignal signal,
            SecurityCheckpoint? checkpoint = null,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(SecurityResponse.NoAction(signal.Id, "stub"));
        }

        public IAsyncEnumerable<AnomalySignal> StreamSignalsAsync(CancellationToken ct = default) =>
            EmptyAsync(ct);

        private static async IAsyncEnumerable<AnomalySignal> EmptyAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    [Fact]
    public void Ctor_Rejects_Null_Watchdog()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DefaultAnomalyEventDispatcher(null!));
    }

    [Fact]
    public async Task BelowThreshold_Signal_Is_Dropped_Without_Calling_Watchdog()
    {
        var inner = new CountingWatchdog();
        var dispatcher = new DefaultAnomalyEventDispatcher(inner, minimumConfidence: 0.5);

        var weak = AnomalySignal.Create(
            ThreatVector.MemoryAnomaly, 0.1, "M", "weak signal");
        var result = await dispatcher.VerifyAndDispatchAsync(weak);

        Assert.Equal(AnomalyDispatchOutcome.BelowThreshold, result.Outcome);
        Assert.Null(result.Response);
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public async Task At_Or_Above_Threshold_Signal_Is_Dispatched_Once()
    {
        var inner = new CountingWatchdog();
        var dispatcher = new DefaultAnomalyEventDispatcher(inner, minimumConfidence: 0.5);

        var strong = AnomalySignal.Create(
            ThreatVector.MemoryAnomaly, 0.5, "M", "strong signal");
        var result = await dispatcher.VerifyAndDispatchAsync(strong);

        Assert.Equal(AnomalyDispatchOutcome.Dispatched, result.Outcome);
        Assert.NotNull(result.Response);
        Assert.Equal(strong.Id, result.Response!.SignalId);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task Repeated_Signal_Id_Is_Deduped_Silently()
    {
        var inner = new CountingWatchdog();
        var dispatcher = new DefaultAnomalyEventDispatcher(inner, minimumConfidence: 0.3);

        var signal = AnomalySignal.Create(
            ThreatVector.MemoryAnomaly, 0.9, "M", "first time");

        var first  = await dispatcher.VerifyAndDispatchAsync(signal);
        var second = await dispatcher.VerifyAndDispatchAsync(signal);
        var third  = await dispatcher.VerifyAndDispatchAsync(signal);

        Assert.Equal(AnomalyDispatchOutcome.Dispatched, first.Outcome);
        Assert.Equal(AnomalyDispatchOutcome.Duplicate, second.Outcome);
        Assert.Equal(AnomalyDispatchOutcome.Duplicate, third.Outcome);
        Assert.Null(second.Response);
        Assert.Null(third.Response);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task Cancelled_Token_Returns_Cancelled_Without_Calling_Watchdog()
    {
        var inner = new CountingWatchdog();
        var dispatcher = new DefaultAnomalyEventDispatcher(inner);

        var signal = AnomalySignal.Create(
            ThreatVector.MemoryAnomaly, 0.9, "M", "blocked");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await dispatcher.VerifyAndDispatchAsync(signal, ct: cts.Token);

        Assert.Equal(AnomalyDispatchOutcome.Cancelled, result.Outcome);
        Assert.Null(result.Response);
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public async Task Null_Signal_Throws_ArgumentNullException()
    {
        var inner = new CountingWatchdog();
        var dispatcher = new DefaultAnomalyEventDispatcher(inner);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            dispatcher.VerifyAndDispatchAsync(null!));
    }

    [Fact]
    public async Task Minimum_Confidence_Out_Of_Range_Is_Clamped_To_Unit_Interval()
    {
        // Construct with an absurd minimum — clamped to 1.0, so a max-confidence
        // signal still dispatches.
        var inner = new CountingWatchdog();
        var dispatcher = new DefaultAnomalyEventDispatcher(inner, minimumConfidence: 5.0);

        var max = AnomalySignal.Create(
            ThreatVector.MemoryAnomaly, 1.0, "M", "max");
        var result = await dispatcher.VerifyAndDispatchAsync(max);

        Assert.Equal(AnomalyDispatchOutcome.Dispatched, result.Outcome);
    }
}
