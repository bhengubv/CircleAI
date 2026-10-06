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

/// <summary>(3.3.0) Acoustic echo canceller — subtracts the far-end reference from the near-end mic input.</summary>
public interface IEchoCanceller
{
    /// <summary>Backend self-identification — "nlms" / "webrtc-aec3" / "null".</summary>
    string BackendId { get; }

    /// <summary>
    /// Cancel echo of <paramref name="farEndReference"/> out of
    /// <paramref name="nearEndMicrophone"/>. Writes the result into
    /// <paramref name="destination"/>. Both inputs must be the same
    /// sample rate and length (PCM-16 mono).
    /// </summary>
    int Cancel(
        ReadOnlySpan<byte> nearEndMicrophone,
        ReadOnlySpan<byte> farEndReference,
        int                sampleRateHz,
        Span<byte>         destination);

    /// <summary>Reset adaptive-filter state at the start of a new call.</summary>
    void Reset();
}

/// <summary>(3.3.0) Audio noise reducer — cleans a frame of PCM-16 mono audio.</summary>
public interface INoiseReducer
{
    /// <summary>Backend self-identification — "krisp" / "deepfilternet" / "passthrough" / "null".</summary>
    string BackendId { get; }

    /// <summary>True when the underlying model / runtime is available.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Reduce noise in <paramref name="audioPcm16Mono"/> and write into
    /// <paramref name="destination"/>. The destination buffer must be at
    /// least as long as the input. Returns the number of bytes written.
    /// </summary>
    int Reduce(ReadOnlySpan<byte> audioPcm16Mono, int sampleRateHz, Span<byte> destination);
}

/// <summary>(3.3.0) Verdict on whether a partial transcript represents a finished thought.</summary>
/// <param name="IsComplete">True if the speaker likely finished their turn.</param>
/// <param name="Confidence">0..1 confidence.</param>
/// <param name="WaitMoreMs">If <c>IsComplete=false</c>, how many extra ms to wait before re-asking.</param>
public sealed record EndOfTurnResult(bool IsComplete, float Confidence, int WaitMoreMs);

/// <summary>
/// (3.3.0) Decide whether the caller has finished their turn given the
/// latest partial transcript + the trailing-silence duration. VAD says
/// "they're silent now"; this says "they're DONE."
/// </summary>
public interface IEndOfTurnDetector
{
    /// <summary>Backend self-identification — "rules" / "smart-turn-v2" / "null".</summary>
    string BackendId { get; }

    /// <summary>Classify the current state.</summary>
    EndOfTurnResult Predict(string partialTranscript, TimeSpan trailingSilence);

    /// <summary>Reset internal state at the start of a fresh turn.</summary>
    void Reset();
}

/// <summary>(3.3.0) One verdict from a voice-activity detector.</summary>
/// <param name="IsSpeech">True if this frame contains speech.</param>
/// <param name="SpeechProbability">0..1 confidence the frame is speech.</param>
/// <param name="Offset">Frame start offset relative to the stream start.</param>
public sealed record VadFrameResult(bool IsSpeech, float SpeechProbability, TimeSpan Offset);

/// <summary>
/// (3.3.0) Voice-activity detector. Implementations classify each
/// 10-30 ms audio frame as speech or silence so a voice loop knows
/// when the caller has started/stopped talking.
/// </summary>
public interface IVoiceActivityDetector
{
    /// <summary>Backend self-identification — "energy" / "silero" / "null".</summary>
    string BackendId { get; }

    /// <summary>Speech probability threshold for <see cref="VadFrameResult.IsSpeech"/>.</summary>
    float SpeechThreshold { get; }

    /// <summary>Classify one frame of PCM-16 mono audio.</summary>
    VadFrameResult Classify(
        ReadOnlySpan<byte> audioPcm16Mono,
        int                sampleRateHz,
        TimeSpan           offset);

    /// <summary>Reset any internal hangover state at the start of a fresh utterance.</summary>
    void Reset();
}

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
