// Telemetry.cs
//
// (3.3.0) OpenTelemetry trace spans for the voice loop. Uses .NET's
// System.Diagnostics.ActivitySource — the host wires an OTel exporter
// to surface spans. ActivitySource name is stable so dashboards can
// pin to it.

using System;
using System.Diagnostics;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Public ActivitySource for the voice loop.</summary>
public static class VoiceLoopTelemetry
{
    /// <summary>(3.3.0) ActivitySource name CircleAI uses for voice-loop spans.</summary>
    public const string SourceName = "CircleAI.Telephony.VoiceLoop";

    /// <summary>(3.3.0) Source object — host registers an exporter against this.</summary>
    public static readonly ActivitySource Source = new(SourceName, "3.3.0");

    /// <summary>(3.3.0) Start a span for one voice loop turn.</summary>
    public static Activity? StartTurn(string callId)
        => Source.StartActivity("voice_loop.turn", ActivityKind.Internal,
            parentContext: default,
            tags: new[] { new KeyValuePair<string, object?>("call.id", callId) });

    /// <summary>(3.3.0) Start a span around the STT stage.</summary>
    public static Activity? StartAsr(string backend)
        => Source.StartActivity("voice_loop.asr", ActivityKind.Client,
            parentContext: default,
            tags: new[] { new KeyValuePair<string, object?>("backend", backend) });

    /// <summary>(3.3.0) Start a span around the LLM stage.</summary>
    public static Activity? StartLlm(string provider, string model)
        => Source.StartActivity("voice_loop.llm", ActivityKind.Client,
            parentContext: default,
            tags: new[]
            {
                new KeyValuePair<string, object?>("provider", provider),
                new KeyValuePair<string, object?>("model",    model),
            });

    /// <summary>(3.3.0) Start a span around the TTS stage.</summary>
    public static Activity? StartTts(string backend, string? voiceId = null)
        => Source.StartActivity("voice_loop.tts", ActivityKind.Client,
            parentContext: default,
            tags: new[]
            {
                new KeyValuePair<string, object?>("backend", backend),
                new KeyValuePair<string, object?>("voice",   voiceId),
            });

    /// <summary>(3.3.0) Tag a turn span with its outcome.</summary>
    public static void RecordOutcome(Activity? activity, bool success, string? errorReason = null)
    {
        if (activity is null) return;
        activity.SetTag("outcome", success ? "success" : "failure");
        if (!success && errorReason is not null)
        {
            activity.SetTag("error.message", errorReason);
            activity.SetStatus(ActivityStatusCode.Error, errorReason);
        }
        else if (success)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
    }
}
