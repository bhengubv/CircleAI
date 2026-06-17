// Contracts.cs
//
// (2.3.0) The CircleAI.Speech contract surface. ASR / TTS / wake-word /
// OCR — every primitive needed for B! Butler's voice loop. Real
// backends ship in 2.3.1.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Speech;

/// <summary>One transcribed segment.</summary>
public sealed record TranscribedSegment(
    string         Text,
    TimeSpan       Offset,
    TimeSpan       Duration,
    string?        Language = null,
    float          Confidence = 0f);

/// <summary>Outcome of one ASR call.</summary>
public sealed record TranscriptionResult(
    string                          Text,
    string?                         Language,
    IReadOnlyList<TranscribedSegment> Segments,
    TimeSpan                        TotalDuration);

/// <summary>Outcome of one TTS call.</summary>
public sealed record SynthesisResult(
    ReadOnlyMemory<byte> AudioPcm16Mono,
    int                  SampleRateHz,
    TimeSpan             Duration);

/// <summary>One OCR result.</summary>
public sealed record OcrResult(
    string                    Text,
    IReadOnlyList<OcrTextBlock> Blocks);

/// <summary>One detected text block in an OCR result.</summary>
public sealed record OcrTextBlock(
    string  Text,
    int     X,
    int     Y,
    int     Width,
    int     Height,
    float   Confidence,
    string? Language = null);

/// <summary>(2.3.0) Convert audio to text.</summary>
public interface ISpeechRecognizer
{
    /// <summary>Backend self-identification — "funasr-1.x" / "yapsnap" / "null".</summary>
    string BackendId { get; }

    /// <summary>Recognise one buffer of PCM-16 mono audio.</summary>
    ValueTask<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> audioPcm16Mono,
        int                  sampleRateHz,
        string?              languageHint = null,
        CancellationToken    ct           = default);
}

/// <summary>(2.3.0) Convert text to spoken audio.</summary>
public interface ISpeechSynthesizer
{
    /// <summary>Backend self-identification — "chattts" / "null".</summary>
    string BackendId { get; }

    /// <summary>Synthesise one utterance. Returns PCM-16 mono.</summary>
    ValueTask<SynthesisResult> SynthesizeAsync(
        string            text,
        string?           voiceId      = null,
        string?           languageHint = null,
        CancellationToken ct           = default);
}

/// <summary>
/// (2.3.0) Spot a wake word ("Hey B") in a continuous audio stream.
/// Implementations are long-running (`StartAsync`/`StopAsync`).
/// </summary>
public interface IWakeWordDetector : IAsyncDisposable
{
    /// <summary>Backend self-identification — "hey-snips" / "null".</summary>
    string BackendId { get; }

    /// <summary>Subscribe to wake-word fire events.</summary>
    IDisposable Subscribe(Func<WakeWordEvent, ValueTask> handler);

    /// <summary>Begin listening on the system mic. Idempotent.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stop listening. Idempotent.</summary>
    Task StopAsync(CancellationToken ct = default);
}

/// <summary>One wake-word fire.</summary>
public sealed record WakeWordEvent(
    string         Keyword,
    float          Confidence,
    DateTimeOffset DetectedAtUtc);

/// <summary>(2.3.0) Read text out of an image.</summary>
public interface IOpticalCharacterRecognizer
{
    /// <summary>Backend self-identification — "paddleocr-2.x" / "null".</summary>
    string BackendId { get; }

    /// <summary>Recognise text in an image. <paramref name="languageHint"/> e.g. "eng" / "chi" / "auto".</summary>
    ValueTask<OcrResult> RecognizeAsync(
        ReadOnlyMemory<byte> imageBytes,
        string?              languageHint = "auto",
        CancellationToken    ct           = default);
}
