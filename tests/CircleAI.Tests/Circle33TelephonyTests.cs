// Circle33TelephonyTests.cs
//
// (3.3.0) Tests for CircleAI.Telephony — null carrier + null inbound
// dispatcher + carrier-fallback composite. The real Twilio / Telnyx /
// Plivo adapters get their own test classes when their packages land.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Tests;

public sealed class Circle33TelephonyTests
{
    // ── NullTelephonyCarrier ──────────────────────────────────────────

    [Fact]
    public void NullCarrier_CarrierId_IsStable()
    {
        Assert.Equal("null", NullTelephonyCarrier.Instance.CarrierId);
    }

    [Fact]
    public void NullCarrier_IsConfigured_False()
    {
        Assert.False(NullTelephonyCarrier.Instance.IsConfigured);
    }

    [Fact]
    public async Task NullCarrier_ProvisionNumber_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await NullTelephonyCarrier.Instance.ProvisionNumberAsync("ZA"));
    }

    [Fact]
    public async Task NullCarrier_Dial_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await NullTelephonyCarrier.Instance.DialAsync(
                "+27821234567",
                "+27829876543",
                new Uri("wss://example.com/stream")));
    }

    [Fact]
    public async Task NullCarrier_ConfigureInboundWebhook_Noop()
    {
        // Should not throw — null carrier silently accepts config calls
        // so test hosts don't crash on bootstrap.
        await NullTelephonyCarrier.Instance.ConfigureInboundWebhookAsync(
            "+27821234567",
            new Uri("https://example.com/webhook"));
    }

    [Fact]
    public async Task NullCarrier_ListNumbers_Empty()
    {
        var numbers = await NullTelephonyCarrier.Instance.ListNumbersAsync();
        Assert.Empty(numbers);
    }

    // ── NullInboundCallDispatcher ─────────────────────────────────────

    [Fact]
    public void NullInboundDispatcher_CarrierId_IsStable()
    {
        Assert.Equal("null", NullInboundCallDispatcher.Instance.CarrierId);
    }

    [Fact]
    public void NullInboundDispatcher_Subscribe_NeverFires()
    {
        var fired = false;
        using var sub = NullInboundCallDispatcher.Instance.Subscribe(_ =>
        {
            fired = true;
            return ValueTask.CompletedTask;
        });

        // No call ever arrives. Disposal is a no-op.
        Assert.False(fired);
    }

    [Fact]
    public void NullInboundDispatcher_Dispose_DoesNotThrow()
    {
        var sub = NullInboundCallDispatcher.Instance.Subscribe(_ => ValueTask.CompletedTask);
        sub.Dispose();
        sub.Dispose(); // double-dispose idempotent
    }

    // ── DI registration ───────────────────────────────────────────────

    [Fact]
    public void AddCircleAiTelephony_RegistersNullDefaults()
    {
        var sp = new ServiceCollection()
            .AddCircleAiTelephony()
            .BuildServiceProvider();

        Assert.IsType<NullTelephonyCarrier>(sp.GetRequiredService<ITelephonyCarrier>());
        Assert.IsType<NullInboundCallDispatcher>(sp.GetRequiredService<IInboundCallDispatcher>());
    }

    [Fact]
    public void AddCircleAiTelephony_HostOverrideWins()
    {
        var custom = new FakeCarrier("custom", isConfigured: true);
        var sp = new ServiceCollection()
            .AddSingleton<ITelephonyCarrier>(custom)
            .AddCircleAiTelephony() // TryAdd path — should NOT override
            .BuildServiceProvider();

        var resolved = sp.GetRequiredService<ITelephonyCarrier>();
        Assert.Same(custom, resolved);
    }

    // ── CarrierFallback composite ─────────────────────────────────────

    [Fact]
    public void Fallback_NoCarriers_IsNotConfigured()
    {
        var fallback = BuildFallback();
        Assert.False(fallback.IsConfigured);
        Assert.Contains("fallback", fallback.CarrierId);
    }

    [Fact]
    public void Fallback_AllUnconfigured_IsNotConfigured()
    {
        var fallback = BuildFallback(
            new FakeCarrier("twilio", isConfigured: false),
            new FakeCarrier("telnyx", isConfigured: false));
        Assert.False(fallback.IsConfigured);
    }

    [Fact]
    public void Fallback_OneConfigured_IsConfigured()
    {
        var fallback = BuildFallback(
            new FakeCarrier("twilio", isConfigured: false),
            new FakeCarrier("telnyx", isConfigured: true));
        Assert.True(fallback.IsConfigured);
    }

    [Fact]
    public async Task Fallback_PicksFirstConfigured()
    {
        var twilio = new FakeCarrier("twilio", isConfigured: false);
        var telnyx = new FakeCarrier("telnyx", isConfigured: true);
        var plivo  = new FakeCarrier("plivo",  isConfigured: true);
        var fallback = BuildFallback(twilio, telnyx, plivo);

        var numbers = await fallback.ListNumbersAsync();

        Assert.Equal(0, twilio.ListNumbersCallCount);
        Assert.Equal(1, telnyx.ListNumbersCallCount); // first configured wins
        Assert.Equal(0, plivo.ListNumbersCallCount);
        Assert.Empty(numbers);
    }

    [Fact]
    public async Task Fallback_AllUnconfigured_FallsThroughToNullCarrier()
    {
        var fallback = BuildFallback(
            new FakeCarrier("twilio", isConfigured: false),
            new FakeCarrier("telnyx", isConfigured: false));

        // Null carrier returns empty list, doesn't throw.
        var numbers = await fallback.ListNumbersAsync();
        Assert.Empty(numbers);
    }

    private static ITelephonyCarrier BuildFallback(params FakeCarrier[] carriers)
    {
        var services = new ServiceCollection();
        var factories = new Func<IServiceProvider, ITelephonyCarrier>[carriers.Length];
        for (int i = 0; i < carriers.Length; i++)
        {
            var carrier = carriers[i];
            factories[i] = _ => carrier;
        }
        services.AddCarrierFallback(factories);
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<ITelephonyCarrier>();
    }

    // ── Primitives shape ──────────────────────────────────────────────

    [Fact]
    public void CallInfo_RoundTrip()
    {
        var startedAt = DateTimeOffset.Parse("2026-06-23T12:00:00Z");
        var info = new CallInfo(
            CallId:       "CAxxxxx",
            Direction:    CallDirection.Inbound,
            From:         "+27821234567",
            To:           "+27829876543",
            CarrierId:    "twilio",
            MediaFormat:  CallMediaFormat.Mulaw8000,
            StartedAtUtc: startedAt);

        Assert.Equal("CAxxxxx", info.CallId);
        Assert.Equal(CallDirection.Inbound, info.Direction);
        Assert.Equal(CallMediaFormat.Mulaw8000, info.MediaFormat);
    }

    [Fact]
    public void OutboundDialOptions_Defaults()
    {
        var opts = new OutboundDialOptions();
        Assert.False(opts.DetectAnsweringMachine);
        Assert.Equal(30, opts.RingTimeoutSeconds);
        Assert.Null(opts.CallerIdOverride);
        Assert.Null(opts.FollowMeNumbers);
    }

    [Fact]
    public void AudioFrame_RoundTrip()
    {
        var pcm = new byte[1600];
        var frame = new AudioFrame(pcm, CallMediaFormat.Pcm16000, TimeSpan.FromMilliseconds(20));
        Assert.Equal(1600, frame.Pcm.Length);
        Assert.Equal(CallMediaFormat.Pcm16000, frame.Format);
    }

    [Fact]
    public void DtmfEvent_RoundTrip()
    {
        var evt = new DtmfEvent('5', TimeSpan.FromMilliseconds(120), TimeSpan.FromSeconds(3));
        Assert.Equal('5', evt.Digit);
        Assert.Equal(TimeSpan.FromMilliseconds(120), evt.Duration);
    }

    // ── Fake carrier for tests ────────────────────────────────────────

    private sealed class FakeCarrier : ITelephonyCarrier
    {
        public FakeCarrier(string id, bool isConfigured)
        {
            CarrierId    = id;
            IsConfigured = isConfigured;
        }

        public string CarrierId   { get; }
        public bool   IsConfigured { get; }
        public int    ListNumbersCallCount { get; private set; }

        public ValueTask<ProvisionedNumber> ProvisionNumberAsync(
            string countryCode, string? areaCode = null, CancellationToken ct = default)
            => ValueTask.FromResult(new ProvisionedNumber("+0", CarrierId, DateTimeOffset.UtcNow, 0m));

        public ValueTask ConfigureInboundWebhookAsync(
            string phoneNumber, Uri inboundWebhook, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<ICallSession> DialAsync(
            string fromNumber, string toNumber, Uri streamUrl,
            OutboundDialOptions? options = default, CancellationToken ct = default)
            => throw new NotImplementedException("FakeCarrier doesn't return a session.");

        public ValueTask<IReadOnlyList<ProvisionedNumber>> ListNumbersAsync(CancellationToken ct = default)
        {
            ListNumbersCallCount++;
            return ValueTask.FromResult<IReadOnlyList<ProvisionedNumber>>(Array.Empty<ProvisionedNumber>());
        }
    }
}
