// CallCostCalculator.cs
//
// (3.3.0) Track per-call cost across the four spend axes: carrier
// telephony minutes, STT seconds, TTS characters, LLM tokens (input +
// output). Caller pipeline emits usage events; the calculator turns
// them into a running cost figure that the orchestrator can compare
// against a budget ceiling.

using System;
using System.Threading;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Per-unit prices (USD or any consistent currency).</summary>
/// <param name="CarrierPerMinute">Cost per minute of carrier telephony.</param>
/// <param name="SttPerSecond">Cost per second of STT.</param>
/// <param name="TtsPerThousandChars">Cost per 1000 characters of TTS.</param>
/// <param name="LlmInputPerKToken">Cost per 1000 input tokens.</param>
/// <param name="LlmOutputPerKToken">Cost per 1000 output tokens.</param>
public sealed record CallPricing(
    decimal CarrierPerMinute,
    decimal SttPerSecond,
    decimal TtsPerThousandChars,
    decimal LlmInputPerKToken,
    decimal LlmOutputPerKToken);

/// <summary>(3.3.0) Breakdown of where the money went.</summary>
public sealed record CallCostBreakdown(
    decimal Carrier,
    decimal Stt,
    decimal Tts,
    decimal LlmInput,
    decimal LlmOutput,
    decimal Total);

/// <summary>(3.3.0) Tracks cost for one call.</summary>
public sealed class CallCostCalculator
{
    private readonly CallPricing _pricing;
    private long _carrierMs;
    private long _sttMs;
    private long _ttsChars;
    private long _llmInputTokens;
    private long _llmOutputTokens;

    public CallCostCalculator(CallPricing pricing)
    {
        _pricing = pricing ?? throw new ArgumentNullException(nameof(pricing));
    }

    /// <summary>Add carrier telephony usage.</summary>
    public void AddCarrierTime(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) return;
        Interlocked.Add(ref _carrierMs, (long)duration.TotalMilliseconds);
    }

    /// <summary>Add STT usage.</summary>
    public void AddSttTime(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) return;
        Interlocked.Add(ref _sttMs, (long)duration.TotalMilliseconds);
    }

    /// <summary>Add TTS usage in characters.</summary>
    public void AddTtsCharacters(int chars)
    {
        if (chars <= 0) return;
        Interlocked.Add(ref _ttsChars, chars);
    }

    /// <summary>Add LLM tokens.</summary>
    public void AddLlmTokens(int inputTokens, int outputTokens)
    {
        if (inputTokens > 0)  Interlocked.Add(ref _llmInputTokens,  inputTokens);
        if (outputTokens > 0) Interlocked.Add(ref _llmOutputTokens, outputTokens);
    }

    /// <summary>Snapshot the current total cost breakdown.</summary>
    public CallCostBreakdown CurrentBreakdown()
    {
        var carrierMin     = (decimal)Interlocked.Read(ref _carrierMs)        / 60_000m;
        var sttSec         = (decimal)Interlocked.Read(ref _sttMs)            / 1000m;
        var ttsK           = (decimal)Interlocked.Read(ref _ttsChars)         / 1000m;
        var llmInputK      = (decimal)Interlocked.Read(ref _llmInputTokens)   / 1000m;
        var llmOutputK     = (decimal)Interlocked.Read(ref _llmOutputTokens)  / 1000m;

        var carrier   = carrierMin * _pricing.CarrierPerMinute;
        var stt       = sttSec     * _pricing.SttPerSecond;
        var tts       = ttsK       * _pricing.TtsPerThousandChars;
        var llmIn     = llmInputK  * _pricing.LlmInputPerKToken;
        var llmOut    = llmOutputK * _pricing.LlmOutputPerKToken;
        var total     = carrier + stt + tts + llmIn + llmOut;

        return new CallCostBreakdown(carrier, stt, tts, llmIn, llmOut, total);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _carrierMs,       0);
        Interlocked.Exchange(ref _sttMs,           0);
        Interlocked.Exchange(ref _ttsChars,        0);
        Interlocked.Exchange(ref _llmInputTokens,  0);
        Interlocked.Exchange(ref _llmOutputTokens, 0);
    }
}
