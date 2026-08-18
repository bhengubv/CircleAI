#nullable enable

// VoiceTrace.cs
//
// Where the voice layer says how long it took.
//
// THE MEASUREMENTS WERE BEING TAKEN OUTSIDE THE THING BEING MEASURED. A caller
// timed TranscribeAsync with a stopwatch and logged "transcribe=6834 ms", which
// is true and useless: it cannot say whether that was the encoder, the decoder,
// building a processor that should have been kept, or an audio window six times
// longer than the audio. Six point eight seconds with no way to attribute it is
// a number to quote, not a number to act on.
//
// The component reports its own internals instead. Everything the caller could
// not see from outside — how much audio there was, how wide a window it was
// decoded in, how many threads, and where the milliseconds went — is written
// here, by the code that actually knows.
//
// A SINK RATHER THAN A LOGGER, because this assembly has no project references
// at all (checked: CircleAI.Voice.csproj lists none) and adding a logging
// dependency to reach one line of diagnostics would be the wrong trade. The
// host points this at whatever it already uses — Android.Util.Log on the phone,
// Console on the desktop — and nothing here needs to know which.
//
// Null by default, so a host that wires nothing pays a null check.

using System;

namespace CircleAI.Voice;

/// <summary>Diagnostic timing output from the voice layer.</summary>
public static class VoiceTrace
{
    /// <summary>Where trace lines go. Null — the default — discards them.</summary>
    public static Action<string>? Sink { get; set; }

    /// <summary>True when anything is listening, so callers can skip formatting.</summary>
    public static bool Enabled => Sink is not null;

    /// <summary>Writes one line, if anything is listening.</summary>
    /// <remarks>
    /// Swallows sink failures. A diagnostic that can break the thing it is
    /// diagnosing is worse than no diagnostic — this must never be the reason a
    /// transcription throws.
    /// </remarks>
    public static void Write(string line)
    {
        var sink = Sink;
        if (sink is null) return;
        try { sink(line); } catch { /* never break the caller for a log line */ }
    }
}
