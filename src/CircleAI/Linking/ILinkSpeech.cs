// ILinkSpeech.cs
//
// What the link needs from speech, and nothing else.
//
// A NARROW PORT RATHER THAN THE REAL INTERFACES. CircleAI.Speech already has
// ISpeechRecognizer and ISpeechSynthesizer, and they are richer than this — options,
// confidences, streaming, diagnostics. Injecting them here would make CircleAI.Device
// depend on CircleAI.Speech for the sake of two method calls, and would tie the wire
// to a contract that will keep growing for reasons the wire does not care about.
//
// This is shaped like the transaction instead: bytes in, text out, text in, bytes
// out. The host adapts whatever it actually runs to this, which is the same shape the
// service already uses for Memory, Skills and Catalog — an interface it names, a
// host that supplies one.

using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Linking;

/// <summary>Turning sound into words and words into sound, for the cross-app link.</summary>
/// <remarks>
/// Audio is <see cref="LinkAudioFormat"/> on both sides — 16 kHz mono 16-bit
/// little-endian PCM, raw, no container. An implementation that works in anything
/// else converts at its own edge rather than making the wire carry a format field.
/// </remarks>
public interface ILinkSpeech
{
    /// <summary>Words for this audio.</summary>
    /// <param name="pcm16">Audio in <see cref="LinkAudioFormat"/>.</param>
    /// <param name="language">BCP-47 tag, or null to let the implementation choose.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The transcript; empty when nothing was recognised.</returns>
    /// <remarks>
    /// EMPTY IS A RESULT, NOT A FAILURE. Silence, a cough, a room with nobody in it —
    /// all transcribe to nothing, and a caller that treats that as an error will tell
    /// somebody the service is broken when it merely heard no words.
    /// </remarks>
    Task<string> TranscribeAsync(byte[] pcm16, string? language, CancellationToken ct = default);

    /// <summary>Audio for these words.</summary>
    /// <param name="text">What to say.</param>
    /// <param name="language">BCP-47 tag, or null to let the implementation choose.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Audio in <see cref="LinkAudioFormat"/>.</returns>
    /// <remarks>
    /// LENGTH IS THE CALLER'S PROBLEM AND THE WIRE'S LIMIT. A paragraph synthesises to
    /// more audio than one transaction carries, so an implementation may return more
    /// than <see cref="LinkAudio.MaxAudioBytes"/> and the codec will refuse it with a
    /// readable reason rather than throwing inside the reply.
    /// </remarks>
    Task<byte[]> SpeakAsync(string text, string? language, CancellationToken ct = default);
}
