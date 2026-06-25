// Circle33CallCostTests.cs
//
// (3.3.0) Tests for per-call cost calculator.

using System;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33CallCostTests
{
    private static readonly CallPricing TestPricing = new(
        CarrierPerMinute:    0.020m,
        SttPerSecond:        0.001m,
        TtsPerThousandChars: 0.015m,
        LlmInputPerKToken:   0.005m,
        LlmOutputPerKToken:  0.015m);

    [Fact]
    public void EmptyCalculator_ReturnsZeros()
    {
        var c = new CallCostCalculator(TestPricing);
        var b = c.CurrentBreakdown();
        Assert.Equal(0m, b.Total);
    }

    [Fact]
    public void Carrier_OneMinute_CostsExpected()
    {
        var c = new CallCostCalculator(TestPricing);
        c.AddCarrierTime(TimeSpan.FromMinutes(1));
        Assert.Equal(0.020m, c.CurrentBreakdown().Carrier);
    }

    [Fact]
    public void Stt_60Seconds_CostsExpected()
    {
        var c = new CallCostCalculator(TestPricing);
        c.AddSttTime(TimeSpan.FromSeconds(60));
        Assert.Equal(0.060m, c.CurrentBreakdown().Stt);
    }

    [Fact]
    public void Tts_2000Chars_CostsExpected()
    {
        var c = new CallCostCalculator(TestPricing);
        c.AddTtsCharacters(2000);
        Assert.Equal(0.030m, c.CurrentBreakdown().Tts);
    }

    [Fact]
    public void Llm_TokensCharged_PerSide()
    {
        var c = new CallCostCalculator(TestPricing);
        c.AddLlmTokens(inputTokens: 1000, outputTokens: 500);
        var b = c.CurrentBreakdown();
        Assert.Equal(0.005m, b.LlmInput);
        Assert.Equal(0.0075m, b.LlmOutput);
    }

    [Fact]
    public void Total_SumsAllAxes()
    {
        var c = new CallCostCalculator(TestPricing);
        c.AddCarrierTime(TimeSpan.FromMinutes(1));
        c.AddSttTime(TimeSpan.FromSeconds(30));
        c.AddTtsCharacters(1000);
        c.AddLlmTokens(2000, 500);

        var b = c.CurrentBreakdown();
        var expected = 0.020m + 0.030m + 0.015m + 0.010m + 0.0075m;
        Assert.Equal(expected, b.Total);
    }

    [Fact]
    public void NegativeUsage_IsIgnored()
    {
        var c = new CallCostCalculator(TestPricing);
        c.AddCarrierTime(TimeSpan.FromMinutes(-1));
        c.AddSttTime(TimeSpan.FromSeconds(-30));
        c.AddTtsCharacters(-500);
        c.AddLlmTokens(-1, -1);
        Assert.Equal(0m, c.CurrentBreakdown().Total);
    }

    [Fact]
    public void Reset_ClearsAccumulators()
    {
        var c = new CallCostCalculator(TestPricing);
        c.AddCarrierTime(TimeSpan.FromMinutes(5));
        c.AddSttTime(TimeSpan.FromSeconds(60));
        Assert.True(c.CurrentBreakdown().Total > 0);

        c.Reset();
        Assert.Equal(0m, c.CurrentBreakdown().Total);
    }

    [Fact]
    public void Pricing_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new CallCostCalculator(null!));
    }
}
