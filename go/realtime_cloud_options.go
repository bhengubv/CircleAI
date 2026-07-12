// realtime_cloud_options.go
//
// Ports CircleAI.Realtime.Cloud/Options.cs — the per-vendor options records for
// the 5 realtime connectors. Each C# `sealed class` becomes a Go struct plus a
// New<Name>Options constructor that applies the C# member defaults (a zero-valued
// struct would lose them). C# Uri fields map to plain string endpoints (the
// transport is the injected IRealtimeTransportFactory, which takes a *url.URL at
// Connect time — the connectors parse these strings there). Nullable `string?`
// maps to string ("" == unset).

package circleai

// OpenAiRealtimeOptions holds OpenAI Realtime (WSS) options. Ports
// OpenAiRealtimeOptions.
type OpenAiRealtimeOptions struct {
	// WebSocketEndpoint is the WSS root (default wss://api.openai.com/v1/realtime).
	WebSocketEndpoint string
	// ApiKey authenticates as Bearer; "" == unconfigured.
	ApiKey string
	// DefaultModel is used when RealtimeSessionConfig.Model is blank.
	DefaultModel string
	// BetaHeader is the OpenAI-Beta header value (default realtime=v1).
	BetaHeader string
}

// NewOpenAiRealtimeOptions returns OpenAiRealtimeOptions with the C# defaults.
func NewOpenAiRealtimeOptions() OpenAiRealtimeOptions {
	return OpenAiRealtimeOptions{
		WebSocketEndpoint: "wss://api.openai.com/v1/realtime",
		DefaultModel:      "gpt-4o-realtime-preview-2024-12-17",
		BetaHeader:        "realtime=v1",
	}
}

// GeminiLiveOptions holds Google Gemini Live options. Ports GeminiLiveOptions.
type GeminiLiveOptions struct {
	// WebSocketEndpoint is the BidiGenerateContent WSS endpoint.
	WebSocketEndpoint string
	// ApiKey is placed on the query string; "" == unconfigured.
	ApiKey string
	// DefaultModel is the model id (default models/gemini-2.0-flash-exp).
	DefaultModel string
}

// NewGeminiLiveOptions returns GeminiLiveOptions with the C# defaults.
func NewGeminiLiveOptions() GeminiLiveOptions {
	return GeminiLiveOptions{
		WebSocketEndpoint: "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent",
		DefaultModel:      "models/gemini-2.0-flash-exp",
	}
}

// NovaSonicOptions holds AWS Nova Sonic options (SigV4 on the handshake, done by
// the host transport factory). Ports NovaSonicOptions.
type NovaSonicOptions struct {
	// Region is the AWS region (default us-east-1).
	Region string
	// AccessKeyId is the AWS access key id; "" == unconfigured.
	AccessKeyId string
	// SecretAccessKey is the AWS secret key; "" == unconfigured.
	SecretAccessKey string
	// SessionToken is an optional STS session token.
	SessionToken string
	// DefaultModel is the model id (default amazon.nova-sonic-v1:0).
	DefaultModel string
}

// NewNovaSonicOptions returns NovaSonicOptions with the C# defaults.
func NewNovaSonicOptions() NovaSonicOptions {
	return NovaSonicOptions{Region: "us-east-1", DefaultModel: "amazon.nova-sonic-v1:0"}
}

// ElevenLabsConvOptions holds ElevenLabs Conversational AI options. Ports
// ElevenLabsConvOptions.
type ElevenLabsConvOptions struct {
	// WebSocketEndpoint is the convai conversation WSS endpoint.
	WebSocketEndpoint string
	// ApiKey is the xi-api-key header; "" == unconfigured.
	ApiKey string
	// AgentId is the ElevenLabs agent id; "" == unconfigured.
	AgentId string
}

// NewElevenLabsConvOptions returns ElevenLabsConvOptions with the C# defaults.
func NewElevenLabsConvOptions() ElevenLabsConvOptions {
	return ElevenLabsConvOptions{WebSocketEndpoint: "wss://api.elevenlabs.io/v1/convai/conversation"}
}

// UltravoxOptions holds Ultravox options (HTTP session-create then WS join).
// Ports UltravoxOptions.
type UltravoxOptions struct {
	// ApiEndpoint is the HTTP API root for session creation (default https://api.ultravox.ai).
	ApiEndpoint string
	// ApiKey is the X-API-Key header; "" == unconfigured.
	ApiKey string
	// DefaultModel is used when RealtimeSessionConfig.Model is blank.
	DefaultModel string
	// DefaultVoice is used when RealtimeSessionConfig.VoiceId is blank.
	DefaultVoice string
}

// NewUltravoxOptions returns UltravoxOptions with the C# defaults.
func NewUltravoxOptions() UltravoxOptions {
	return UltravoxOptions{ApiEndpoint: "https://api.ultravox.ai", DefaultModel: "fixie-ai/ultravox-70B", DefaultVoice: "Mark"}
}
