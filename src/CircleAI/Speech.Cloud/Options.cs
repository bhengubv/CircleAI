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

/// <summary>(3.3.0) Deepgram STT options. Bearer-equivalent auth via "Token &lt;key&gt;".</summary>
public sealed class DeepgramOptions
{
    public Uri    BaseAddress { get; init; } = new("https://api.deepgram.com");
    public string? ApiKey      { get; init; }
    /// <summary>Model id — defaults to <c>nova-2-general</c>.</summary>
    public string Model       { get; init; } = "nova-2-general";
}

/// <summary>(3.3.0) AssemblyAI STT options.</summary>
public sealed class AssemblyAiOptions
{
    public Uri    BaseAddress { get; init; } = new("https://api.assemblyai.com");
    public string? ApiKey      { get; init; }
    /// <summary>Speech model — defaults to <c>universal</c>.</summary>
    public string SpeechModel { get; init; } = "universal";
}

/// <summary>(3.3.0) Google Cloud Speech-to-Text options (REST v1 + API-key auth).</summary>
public sealed class GoogleSpeechOptions
{
    public Uri    BaseAddress { get; init; } = new("https://speech.googleapis.com");
    public string? ApiKey      { get; init; }
    public string LanguageCode { get; init; } = "en-US";
}

/// <summary>(3.3.0) Microsoft Azure Speech-to-Text options.</summary>
public sealed class AzureSpeechOptions
{
    /// <summary>Region-specific endpoint, e.g. <c>https://eastus.stt.speech.microsoft.com</c>.</summary>
    public Uri?   BaseAddress { get; init; }
    public string? ApiKey      { get; init; }
    public string LanguageCode { get; init; } = "en-US";
}

/// <summary>(3.3.0) ElevenLabs TTS options.</summary>
public sealed class ElevenLabsOptions
{
    public Uri    BaseAddress { get; init; } = new("https://api.elevenlabs.io");
    public string? ApiKey      { get; init; }
    /// <summary>Default voice id (ElevenLabs UUID — varies per account).</summary>
    public string DefaultVoiceId { get; init; } = "21m00Tcm4TlvDq8ikWAM"; // Rachel
    /// <summary>Model id. Defaults to flash for low latency.</summary>
    public string Model       { get; init; } = "eleven_flash_v2_5";
    /// <summary>Output format. Returns PCM at 16/22/24/44 kHz.</summary>
    public string OutputFormat { get; init; } = "pcm_24000";
    public int PcmSampleRateHz { get; init; } = 24_000;
}

/// <summary>(3.3.0) Cartesia Sonic TTS options.</summary>
public sealed class CartesiaTtsOptions
{
    public Uri    BaseAddress { get; init; } = new("https://api.cartesia.ai");
    public string? ApiKey      { get; init; }
    public string Model       { get; init; } = "sonic-2";
    public string DefaultVoiceId { get; init; } = "a0e99841-438c-4a64-b679-ae501e7d6091"; // a sample
    public string OutputContainer { get; init; } = "raw";
    public string OutputEncoding  { get; init; } = "pcm_s16le";
    public int    PcmSampleRateHz { get; init; } = 24_000;
    public string CartesiaVersion { get; init; } = "2025-04-16";
}

/// <summary>(3.3.0) Deepgram Aura TTS options.</summary>
public sealed class DeepgramTtsOptions
{
    public Uri    BaseAddress { get; init; } = new("https://api.deepgram.com");
    public string? ApiKey      { get; init; }
    /// <summary>Aura voice model — defaults to <c>aura-asteria-en</c>.</summary>
    public string Voice       { get; init; } = "aura-asteria-en";
    public int    PcmSampleRateHz { get; init; } = 24_000;
}

/// <summary>(3.3.0) Microsoft Azure Speech TTS options.</summary>
public sealed class AzureTtsOptions
{
    /// <summary>Region-specific endpoint, e.g. <c>https://eastus.tts.speech.microsoft.com</c>.</summary>
    public Uri?   BaseAddress { get; init; }
    public string? ApiKey      { get; init; }
    public string LanguageCode { get; init; } = "en-US";
    public string DefaultVoiceName { get; init; } = "en-US-AvaMultilingualNeural";
    public int    PcmSampleRateHz  { get; init; } = 24_000;
}

/// <summary>(3.3.0) Google Cloud Text-to-Speech options.</summary>
public sealed class GoogleTtsOptions
{
    public Uri    BaseAddress { get; init; } = new("https://texttospeech.googleapis.com");
    public string? ApiKey      { get; init; }
    public string LanguageCode { get; init; } = "en-US";
    public string DefaultVoiceName { get; init; } = "en-US-Studio-O";
    public int    PcmSampleRateHz  { get; init; } = 24_000;
}

/// <summary>(3.3.0) PlayHT TTS options.</summary>
public sealed class PlayHtOptions
{
    public Uri    BaseAddress { get; init; } = new("https://api.play.ht");
    public string? ApiKey      { get; init; }
    public string? UserId      { get; init; }
    public string DefaultVoice { get; init; } = "s3://voice-cloning-zero-shot/d9ff78ba-d016-47f6-b0ef-dd630f59414e/female-cs/manifest.json";
    public string Model       { get; init; } = "PlayDialog";
    public int    PcmSampleRateHz { get; init; } = 24_000;
}

/// <summary>(3.3.0) Cartesia STT options (Bearer auth).</summary>
public sealed class CartesiaSttOptions
{
    public Uri    BaseAddress { get; init; } = new("https://api.cartesia.ai");
    public string? ApiKey      { get; init; }
    /// <summary>Model id — defaults to Cartesia's default English STT model.</summary>
    public string Model       { get; init; } = "ink-whisper";
    /// <summary>API version header value. Defaults to current stable.</summary>
    public string CartesiaVersion { get; init; } = "2025-04-16";
}
