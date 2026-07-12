// speech_cloud_options.go
//
// Ports CircleAI.Speech.Cloud/Options.cs — the per-vendor options records for
// the cloud STT / TTS adapters. Each C# `sealed class Xxx { get; init; }` becomes
// a Go struct; the C# member defaults become a New<Name>Options constructor (a
// zero-valued Go struct would otherwise lose every default). BaseAddress is a
// string here (the transport is the injected ToolHTTPDoer, not an HttpClient, so
// there is no Uri type to satisfy). Nullable `string? ApiKey` maps to a plain
// string ("" == unset), matching the IsConfigured !IsNullOrWhiteSpace checks.

package circleai

// OpenAiVoiceOptions holds OpenAI Whisper (STT) + TTS options. Ports
// OpenAiVoiceOptions.
type OpenAiVoiceOptions struct {
	// BaseAddress is the OpenAI API root (default https://api.openai.com).
	BaseAddress string
	// ApiKey authenticates as Bearer; "" == unconfigured.
	ApiKey string
	// TranscriptionModel is the Whisper model (default whisper-1).
	TranscriptionModel string
	// SpeechModel is the TTS model (default tts-1).
	SpeechModel string
	// DefaultVoice is the fallback voice id (default alloy).
	DefaultVoice string
	// PcmSampleRateHz is the sample rate of TTS PCM output (default 24000).
	PcmSampleRateHz int
}

// NewOpenAiVoiceOptions returns OpenAiVoiceOptions with the C# defaults applied.
func NewOpenAiVoiceOptions() OpenAiVoiceOptions {
	return OpenAiVoiceOptions{
		BaseAddress:        "https://api.openai.com",
		TranscriptionModel: "whisper-1",
		SpeechModel:        "tts-1",
		DefaultVoice:       "alloy",
		PcmSampleRateHz:    24000,
	}
}

// DeepgramOptions holds Deepgram STT options (Token auth). Ports DeepgramOptions.
type DeepgramOptions struct {
	// BaseAddress is the Deepgram API root (default https://api.deepgram.com).
	BaseAddress string
	// ApiKey authenticates as "Token <key>"; "" == unconfigured.
	ApiKey string
	// Model is the STT model id (default nova-2-general).
	Model string
}

// NewDeepgramOptions returns DeepgramOptions with the C# defaults applied.
func NewDeepgramOptions() DeepgramOptions {
	return DeepgramOptions{BaseAddress: "https://api.deepgram.com", Model: "nova-2-general"}
}

// AssemblyAiOptions holds AssemblyAI STT options. Ports AssemblyAiOptions.
type AssemblyAiOptions struct {
	// BaseAddress is the AssemblyAI API root (default https://api.assemblyai.com).
	BaseAddress string
	// ApiKey authenticates via the raw Authorization header; "" == unconfigured.
	ApiKey string
	// SpeechModel is the speech model (default universal).
	SpeechModel string
}

// NewAssemblyAiOptions returns AssemblyAiOptions with the C# defaults applied.
func NewAssemblyAiOptions() AssemblyAiOptions {
	return AssemblyAiOptions{BaseAddress: "https://api.assemblyai.com", SpeechModel: "universal"}
}

// GoogleSpeechOptions holds Google Cloud STT options (API-key auth). Ports
// GoogleSpeechOptions.
type GoogleSpeechOptions struct {
	// BaseAddress is the API root (default https://speech.googleapis.com).
	BaseAddress string
	// ApiKey is the ?key= API key; "" == unconfigured.
	ApiKey string
	// LanguageCode is the default BCP-47 language (default en-US).
	LanguageCode string
}

// NewGoogleSpeechOptions returns GoogleSpeechOptions with the C# defaults applied.
func NewGoogleSpeechOptions() GoogleSpeechOptions {
	return GoogleSpeechOptions{BaseAddress: "https://speech.googleapis.com", LanguageCode: "en-US"}
}

// AzureSpeechOptions holds Microsoft Azure STT options. BaseAddress is
// region-specific and has no default (empty == unconfigured). Ports
// AzureSpeechOptions.
type AzureSpeechOptions struct {
	// BaseAddress is the region endpoint, e.g. https://eastus.stt.speech.microsoft.com.
	BaseAddress string
	// ApiKey is the Ocp-Apim-Subscription-Key; "" == unconfigured.
	ApiKey string
	// LanguageCode is the default BCP-47 language (default en-US).
	LanguageCode string
}

// NewAzureSpeechOptions returns AzureSpeechOptions with the C# defaults applied
// (BaseAddress intentionally left empty — it is region-specific).
func NewAzureSpeechOptions() AzureSpeechOptions {
	return AzureSpeechOptions{LanguageCode: "en-US"}
}

// ElevenLabsOptions holds ElevenLabs TTS options. Ports ElevenLabsOptions.
type ElevenLabsOptions struct {
	// BaseAddress is the API root (default https://api.elevenlabs.io).
	BaseAddress string
	// ApiKey is the xi-api-key; "" == unconfigured.
	ApiKey string
	// DefaultVoiceId is the fallback voice id (default Rachel).
	DefaultVoiceId string
	// Model is the model id (default eleven_flash_v2_5).
	Model string
	// OutputFormat is the PCM output format (default pcm_24000).
	OutputFormat string
	// PcmSampleRateHz is the fallback sample rate when OutputFormat lacks one.
	PcmSampleRateHz int
}

// NewElevenLabsOptions returns ElevenLabsOptions with the C# defaults applied.
func NewElevenLabsOptions() ElevenLabsOptions {
	return ElevenLabsOptions{
		BaseAddress:     "https://api.elevenlabs.io",
		DefaultVoiceId:  "21m00Tcm4TlvDq8ikWAM",
		Model:           "eleven_flash_v2_5",
		OutputFormat:    "pcm_24000",
		PcmSampleRateHz: 24000,
	}
}

// CartesiaTtsOptions holds Cartesia Sonic TTS options. Ports CartesiaTtsOptions.
type CartesiaTtsOptions struct {
	// BaseAddress is the API root (default https://api.cartesia.ai).
	BaseAddress string
	// ApiKey authenticates as Bearer; "" == unconfigured.
	ApiKey string
	// Model is the TTS model (default sonic-2).
	Model string
	// DefaultVoiceId is the fallback voice id.
	DefaultVoiceId string
	// OutputContainer is the audio container (default raw).
	OutputContainer string
	// OutputEncoding is the audio encoding (default pcm_s16le).
	OutputEncoding string
	// PcmSampleRateHz is the sample rate of PCM output (default 24000).
	PcmSampleRateHz int
	// CartesiaVersion is the Cartesia-Version header value.
	CartesiaVersion string
}

// NewCartesiaTtsOptions returns CartesiaTtsOptions with the C# defaults applied.
func NewCartesiaTtsOptions() CartesiaTtsOptions {
	return CartesiaTtsOptions{
		BaseAddress:     "https://api.cartesia.ai",
		Model:           "sonic-2",
		DefaultVoiceId:  "a0e99841-438c-4a64-b679-ae501e7d6091",
		OutputContainer: "raw",
		OutputEncoding:  "pcm_s16le",
		PcmSampleRateHz: 24000,
		CartesiaVersion: "2025-04-16",
	}
}

// DeepgramTtsOptions holds Deepgram Aura TTS options. Ports DeepgramTtsOptions.
type DeepgramTtsOptions struct {
	// BaseAddress is the API root (default https://api.deepgram.com).
	BaseAddress string
	// ApiKey authenticates as "Token <key>"; "" == unconfigured.
	ApiKey string
	// Voice is the Aura voice model (default aura-asteria-en).
	Voice string
	// PcmSampleRateHz is the sample rate of PCM output (default 24000).
	PcmSampleRateHz int
}

// NewDeepgramTtsOptions returns DeepgramTtsOptions with the C# defaults applied.
func NewDeepgramTtsOptions() DeepgramTtsOptions {
	return DeepgramTtsOptions{BaseAddress: "https://api.deepgram.com", Voice: "aura-asteria-en", PcmSampleRateHz: 24000}
}

// AzureTtsOptions holds Microsoft Azure TTS options. BaseAddress is
// region-specific and has no default. Ports AzureTtsOptions.
type AzureTtsOptions struct {
	// BaseAddress is the region endpoint, e.g. https://eastus.tts.speech.microsoft.com.
	BaseAddress string
	// ApiKey is the Ocp-Apim-Subscription-Key; "" == unconfigured.
	ApiKey string
	// LanguageCode is the default BCP-47 language (default en-US).
	LanguageCode string
	// DefaultVoiceName is the fallback voice (default en-US-AvaMultilingualNeural).
	DefaultVoiceName string
	// PcmSampleRateHz is the sample rate of PCM output (default 24000).
	PcmSampleRateHz int
}

// NewAzureTtsOptions returns AzureTtsOptions with the C# defaults applied
// (BaseAddress intentionally left empty — it is region-specific).
func NewAzureTtsOptions() AzureTtsOptions {
	return AzureTtsOptions{LanguageCode: "en-US", DefaultVoiceName: "en-US-AvaMultilingualNeural", PcmSampleRateHz: 24000}
}

// GoogleTtsOptions holds Google Cloud TTS options. Ports GoogleTtsOptions.
type GoogleTtsOptions struct {
	// BaseAddress is the API root (default https://texttospeech.googleapis.com).
	BaseAddress string
	// ApiKey is the ?key= API key; "" == unconfigured.
	ApiKey string
	// LanguageCode is the default BCP-47 language (default en-US).
	LanguageCode string
	// DefaultVoiceName is the fallback voice (default en-US-Studio-O).
	DefaultVoiceName string
	// PcmSampleRateHz is the sample rate of PCM output (default 24000).
	PcmSampleRateHz int
}

// NewGoogleTtsOptions returns GoogleTtsOptions with the C# defaults applied.
func NewGoogleTtsOptions() GoogleTtsOptions {
	return GoogleTtsOptions{
		BaseAddress:      "https://texttospeech.googleapis.com",
		LanguageCode:     "en-US",
		DefaultVoiceName: "en-US-Studio-O",
		PcmSampleRateHz:  24000,
	}
}

// PlayHtOptions holds PlayHT TTS options. Ports PlayHtOptions.
type PlayHtOptions struct {
	// BaseAddress is the API root (default https://api.play.ht).
	BaseAddress string
	// ApiKey authenticates as Bearer; "" == unconfigured.
	ApiKey string
	// UserId is the X-USER-ID header; "" == unconfigured.
	UserId string
	// DefaultVoice is the fallback voice manifest URL.
	DefaultVoice string
	// Model is the voice engine (default PlayDialog).
	Model string
	// PcmSampleRateHz is the sample rate of PCM output (default 24000).
	PcmSampleRateHz int
}

// NewPlayHtOptions returns PlayHtOptions with the C# defaults applied.
func NewPlayHtOptions() PlayHtOptions {
	return PlayHtOptions{
		BaseAddress:     "https://api.play.ht",
		DefaultVoice:    "s3://voice-cloning-zero-shot/d9ff78ba-d016-47f6-b0ef-dd630f59414e/female-cs/manifest.json",
		Model:           "PlayDialog",
		PcmSampleRateHz: 24000,
	}
}

// CartesiaSttOptions holds Cartesia STT options (Bearer auth). Ports
// CartesiaSttOptions.
type CartesiaSttOptions struct {
	// BaseAddress is the API root (default https://api.cartesia.ai).
	BaseAddress string
	// ApiKey authenticates as Bearer; "" == unconfigured.
	ApiKey string
	// Model is the STT model (default ink-whisper).
	Model string
	// CartesiaVersion is the Cartesia-Version header value.
	CartesiaVersion string
}

// NewCartesiaSttOptions returns CartesiaSttOptions with the C# defaults applied.
func NewCartesiaSttOptions() CartesiaSttOptions {
	return CartesiaSttOptions{BaseAddress: "https://api.cartesia.ai", Model: "ink-whisper", CartesiaVersion: "2025-04-16"}
}
