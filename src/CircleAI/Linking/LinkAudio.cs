// LinkAudio.cs
//
// Carrying sound across the cross-app link.
//
// WHY THE SPLIT IS HERE AND NOT ELSEWHERE. The microphone belongs to the FOREGROUND
// app: it holds the permission, it knows when the screen is up, it starts and stops
// on a tap. The MODELS belong to the service, because a recogniser and a voice are
// hundreds of megabytes and fifteen apps on a 3 GB phone cannot each hold a copy.
// So the client records and plays; the service transcribes and synthesises. Shipping
// the microphone itself to a background service would be more moving parts for less.
//
// THE CONSTRAINT THAT SHAPES ALL OF THIS: a binder transaction has a hard limit of
// about ONE MEGABYTE, shared across everything in flight in the process — exceed it
// and the kernel throws TransactionTooLargeException, which surfaces as an unrelated
// crash in whichever call happens to be unlucky. 16 kHz 16-bit mono PCM is 32 kB per
// second, so a megabyte is roughly THIRTY SECONDS of speech. That is enough for a
// spoken question and nowhere near enough for a recording, which is why the limit is
// declared here, checked before the transact, and reported as a refusal naming the
// number rather than left to become a crash somewhere else.
//
// Longer audio needs a ParcelFileDescriptor rather than a bigger buffer: Android's
// answer to "too large for a transaction" is to pass a handle and stream. That is a
// second transaction shape and it is deliberately NOT built yet — a spoken turn fits,
// and building streaming before anything needs it would be guessing at the shape.

using System;

namespace CircleAI.Linking;

/// <summary>What the service should do with a piece of audio, or produce as one.</summary>
public enum LinkAudioVerb
{
    /// <summary>Audio in, text out. The service runs the recogniser.</summary>
    Transcribe = 0,

    /// <summary>Text in, audio out. The service runs the voice.</summary>
    Speak = 1,
}

/// <summary>
/// The audio format both sides agree on, stated rather than negotiated.
/// </summary>
/// <remarks>
/// ONE FORMAT, because a negotiation is a thing that can disagree. 16 kHz mono
/// 16-bit little-endian PCM is what the recognisers this product catalogues are
/// trained on (Whisper and the Zipformer wake word both resample to it), so anything
/// else would be converted at one end or the other anyway. Raw PCM rather than a
/// container: a header would be bytes spent describing what is already fixed.
/// </remarks>
public static class LinkAudioFormat
{
    /// <summary>Samples per second: 16 000.</summary>
    public const int SampleRate = 16_000;

    /// <summary>Channels: 1. Speech is mono; a second channel is double the bytes for nothing.</summary>
    public const int Channels = 1;

    /// <summary>Bits per sample: 16, signed little-endian.</summary>
    public const int BitsPerSample = 16;

    /// <summary>Bytes one second of this format occupies: 32 000.</summary>
    public const int BytesPerSecond = SampleRate * Channels * BitsPerSample / 8;
}

/// <summary>A request to transcribe audio or synthesise it.</summary>
/// <param name="Verb">Which direction.</param>
/// <param name="Audio">PCM for <see cref="LinkAudioVerb.Transcribe"/>; empty for Speak.</param>
/// <param name="Text">The words for <see cref="LinkAudioVerb.Speak"/>; empty for Transcribe.</param>
/// <param name="Language">BCP-47 tag, or null to let the service choose.</param>
public sealed record LinkAudioRequest(
    LinkAudioVerb Verb,
    byte[] Audio,
    string Text = "",
    string? Language = null);

/// <summary>What came back.</summary>
/// <param name="Ok">False when the service refused or failed; <paramref name="Error"/> says why.</param>
/// <param name="Text">The transcript, for Transcribe.</param>
/// <param name="Audio">PCM, for Speak.</param>
/// <param name="Error">Why not, when not ok.</param>
public sealed record LinkAudioReply(bool Ok, string Text, byte[] Audio, string? Error)
{
    /// <summary>A transcript came back.</summary>
    public static LinkAudioReply Transcribed(string text) => new(true, text, Array.Empty<byte>(), null);

    /// <summary>Spoken audio came back.</summary>
    public static LinkAudioReply Spoke(byte[] audio) => new(true, string.Empty, audio, null);

    /// <summary>It did not work, and this is why in words safe to show a person.</summary>
    public static LinkAudioReply Failure(string error) => new(false, string.Empty, Array.Empty<byte>(), error);
}

/// <summary>Limits and checks for audio on the link.</summary>
public static class LinkAudio
{
    /// <summary>
    /// The most audio one transaction may carry: <b>800 kB</b>, about 25 seconds.
    /// </summary>
    /// <remarks>
    /// UNDER the kernel's ~1 MB binder budget on purpose, and by a wide margin. That
    /// budget is shared across every transaction in flight in the process, not owned
    /// by this one, so a call sized to the documented maximum fails whenever
    /// something else is talking — intermittently, and blamed on whatever else was
    /// unlucky. Leaving a quarter of it spare is what makes the failure mode
    /// deterministic.
    /// </remarks>
    public const int MaxAudioBytes = 800 * 1024;

    /// <summary>Seconds of speech <see cref="MaxAudioBytes"/> holds, for a message to a person.</summary>
    public static double MaxSeconds => (double)MaxAudioBytes / LinkAudioFormat.BytesPerSecond;

    /// <summary>
    /// Whether this much audio can cross, and a sentence saying why not when it cannot.
    /// </summary>
    /// <remarks>
    /// CHECKED BEFORE THE TRANSACT, on both sides. A client that skips it gets
    /// TransactionTooLargeException — which names neither the size nor the caller and
    /// can land on an unrelated call — and a service that trusts the client to have
    /// checked is a service any app can crash.
    /// </remarks>
    public static bool Fits(int byteCount, out string? refusal)
    {
        if (byteCount < 0)
        {
            refusal = "Audio length cannot be negative.";
            return false;
        }
        if (byteCount > MaxAudioBytes)
        {
            refusal = $"That is {byteCount / (double)LinkAudioFormat.BytesPerSecond:0.#} seconds of audio; "
                    + $"the link carries at most {MaxSeconds:0.#}. Send it in shorter pieces.";
            return false;
        }
        refusal = null;
        return true;
    }
}
