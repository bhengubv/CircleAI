// Options.cs
//
// (3.2.0) OpenAI cloud-voice options. Defaults match Concierge's
// working config plus response_format=pcm so the contract returns
// real PCM-16 mono audio rather than MP3.

using System;

namespace CircleAI.Speech.Cloud;

/// <summary>(3.2.0) OpenAI Whisper + TTS options.</summary>
public sealed class OpenAiVoiceOptions
{
    public Uri    BaseAddress         { get; init; } = new("https://api.openai.com");
    public string? ApiKey              { get; init; }

    /// <summary>Whisper model. Default <c>whisper-1</c>.</summary>
    public string TranscriptionModel  { get; init; } = "whisper-1";

    /// <summary>TTS model. Default <c>tts-1</c>.</summary>
    public string SpeechModel         { get; init; } = "tts-1";

    /// <summary>Default voice id (alloy / echo / fable / onyx / nova / shimmer).</summary>
    public string DefaultVoice        { get; init; } = "alloy";

    /// <summary>
    /// PCM sample rate the TTS endpoint returns when
    /// <c>response_format=pcm</c>. OpenAI documents 24 kHz mono 16-bit
    /// for this format. Surfaced as <see cref="SynthesisResult.SampleRateHz"/>.
    /// </summary>
    public int PcmSampleRateHz        { get; init; } = 24_000;
}
