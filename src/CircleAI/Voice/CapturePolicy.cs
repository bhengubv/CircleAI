// CapturePolicy.cs
//
// HOW THE MICROPHONE IS OPENED, MADE SWITCHABLE SO IT CAN BE MEASURED.
//
// THE DEFECT THIS EXISTS TO SETTLE, MEASURED ON A P30 ON 2026-10-12. The same
// person said "Hey B!" into the same phone in the same room twice, four minutes
// apart - once into the phone's own recorder app, once into our wake loop,
// which wrote down the exact samples it handed the spotter:
//
//     the recorder app   peak 0,449   median per-second RMS 0,0446   50 of 55 s above the noise floor
//     our wake loop      peak 0,187   median per-second RMS 0,0014    0 of 59 s above the noise floor
//
// Our LOUDEST second (0,0080) is below the 0,0083 an empty room measures. The
// spotter was not being shown quiet speech; it was being shown something
// indistinguishable from silence, and no phrase, threshold, encoder or gain was
// ever going to rescue it. The recorder's audio fires the shipped keyword EIGHT
// times through the identical decoder.
//
// WHY THIS IS A POLICY AND NOT A PATCH. The capture is currently
// AudioSource.VoiceRecognition with AGC and acoustic echo cancellation
// attached, and that was a deliberate choice with its own measurement behind it
// - AndroidAudio's comment records "Hey B" heard 1 time in 10 on raw Mic and
// calls VoiceRecognition "the single highest-value line in this file". That
// measurement was taken before any of today's instruments existed, and it may
// still be right. An echo canceller with no playback reference to cancel
// against is the obvious suspect, but obvious suspects have been wrong four
// times today.
//
// So this does not decide. It makes the combinations SELECTABLE, by marker
// files a person or an adb command creates, so the question is answered by
// four captures and a number rather than by another opinion.

using System;
using System.IO;

namespace CircleAI.Voice;

/// <summary>Which microphone source and effects the capture should use.</summary>
/// <param name="RawMic">
/// True to open the plain microphone instead of the voice-recognition source.
/// </param>
/// <param name="NoEffects">True to attach no AGC, noise suppression or echo cancellation.</param>
/// <param name="NoEchoCanceller">
/// True to attach everything EXCEPT echo cancellation - the narrow test, since
/// an AEC with nothing to cancel against is the standing suspect.
/// </param>
public readonly record struct CapturePolicy(bool RawMic, bool NoEffects, bool NoEchoCanceller)
{
    /// <summary>The shipping configuration: voice-recognition source, all effects.</summary>
    public static CapturePolicy Default => new(false, false, false);

    /// <summary>Marker that selects the plain microphone source.</summary>
    public const string RawMicMarker = "capture-raw-mic.on";

    /// <summary>Marker that detaches every audio effect.</summary>
    public const string NoEffectsMarker = "capture-no-effects.on";

    /// <summary>Marker that detaches only the echo canceller.</summary>
    public const string NoEchoMarker = "capture-no-echo.on";

    /// <summary>Reads the policy from marker files in a directory.</summary>
    /// <remarks>
    /// NEVER THROWS AND ALWAYS RETURNS SOMETHING SHIPPABLE. This runs while the
    /// microphone is being opened; a directory that cannot be read must leave
    /// the product on its normal configuration, not stop it listening.
    /// </remarks>
    public static CapturePolicy From(string? directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return Default;

            bool Set(string marker) => File.Exists(Path.Combine(directory, marker));

            return new CapturePolicy(
                RawMic: Set(RawMicMarker),
                NoEffects: Set(NoEffectsMarker),
                NoEchoCanceller: Set(NoEchoMarker));
        }
        catch
        {
            return Default;
        }
    }

    /// <summary>True when nothing has been overridden.</summary>
    public bool IsDefault => !RawMic && !NoEffects && !NoEchoCanceller;

    /// <summary>What to put in the log, so a capture is never ambiguous.</summary>
    /// <remarks>
    /// A measurement whose configuration is not written beside it is not a
    /// measurement. Four captures that look alike and differ in one detached
    /// effect are exactly how a day gets wasted.
    /// </remarks>
    public string Describe() => IsDefault
        ? "VoiceRecognition + all effects (shipping default)"
        : (RawMic ? "Mic" : "VoiceRecognition")
          + (NoEffects ? " + NO effects" : NoEchoCanceller ? " + AGC/NS, NO echo canceller" : " + all effects");
}
