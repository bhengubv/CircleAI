// realtime_cloud_services.go
//
// Ports the 5 vendor IRealtimeService connectors from CircleAI.Realtime.Cloud/:
//   OpenAiRealtimeService, GeminiLiveService, NovaSonicService,
//   ElevenLabsConvService, UltravoxService.
//
// TRANSPORT SEAM: the C# constructors take an optional IRealtimeTransportFactory
// (defaulting to NullRealtimeTransportFactory.Instance); so do these. Each
// StartSession composes the vendor endpoint URL + auth headers exactly as the C#
// does, calls IRealtimeTransportFactory.Connect, and wraps the returned transport
// in a RealtimeWebSocketSession. Nothing here dials a socket — the injected
// factory does (real ClientWebSocket in a host; InMemory/Null in tests).
//
// HTTP SEAM (Ultravox only): Ultravox first POSTs /api/calls to obtain a joinUrl,
// then opens a WS to it. That single HTTP call goes through the package's injected
// ToolHTTPDoer (the same seam Speech.Cloud uses), not an HttpClient.
//
// CONFIG GATE: StartSession on an unconfigured connector returns the C#'s
// InvalidOperationException message as a Go error (fail-loud, matching
// EnsureConfigured) — unlike Speech.Cloud's fail-soft adapters, the C# realtime
// connectors throw.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/url"
)

// ---------------------------------------------------------------------------
// OpenAI Realtime
// ---------------------------------------------------------------------------

// OpenAiRealtimeService is an IRealtimeService backed by OpenAI's gpt-4o-realtime
// WSS API (Bearer + OpenAI-Beta header). Ports OpenAiRealtimeService.
type OpenAiRealtimeService struct {
	options    OpenAiRealtimeOptions
	transports IRealtimeTransportFactory
}

// NewOpenAiRealtimeService builds the connector. A nil transports factory defaults
// to NullRealtimeTransportFactory (StartSession then errors on Connect until a host
// wires a real factory) — mirroring the C# default argument.
func NewOpenAiRealtimeService(options OpenAiRealtimeOptions, transports IRealtimeTransportFactory) *OpenAiRealtimeService {
	if transports == nil {
		transports = NullRealtimeTransportFactoryInstance
	}
	return &OpenAiRealtimeService{options: options, transports: transports}
}

// ProviderId returns "openai-realtime".
func (s *OpenAiRealtimeService) ProviderId() string { return "openai-realtime" }

// IsConfigured is true when the API key is present.
func (s *OpenAiRealtimeService) IsConfigured() bool { return !isBlank(s.options.ApiKey) }

// StartSession opens an OpenAI Realtime WS session. Ports StartSessionAsync.
func (s *OpenAiRealtimeService) StartSession(ctx context.Context, config RealtimeSessionConfig) (IRealtimeSession, error) {
	if !s.IsConfigured() {
		return nil, errors.New("OpenAI Realtime is not configured. Set OpenAiRealtimeOptions.ApiKey before calling StartSession.")
	}
	model := config.Model
	if isBlank(model) {
		model = s.options.DefaultModel
	}
	endpoint, err := url.Parse(s.options.WebSocketEndpoint + "?model=" + url.QueryEscape(model))
	if err != nil {
		return nil, err
	}
	headers := map[string]string{
		"Authorization": "Bearer " + s.options.ApiKey,
		"OpenAI-Beta":   s.options.BetaHeader,
	}
	transport, err := s.transports.Connect(ctx, endpoint, headers)
	if err != nil {
		return nil, err
	}
	return NewRealtimeWebSocketSession(transport, config, s.ProviderId()), nil
}

// ---------------------------------------------------------------------------
// Gemini Live
// ---------------------------------------------------------------------------

// GeminiLiveService is an IRealtimeService backed by Google Gemini Live
// (BidiGenerateContent; API key on the query string). Ports GeminiLiveService.
type GeminiLiveService struct {
	options    GeminiLiveOptions
	transports IRealtimeTransportFactory
}

// NewGeminiLiveService builds the connector (nil factory => null default).
func NewGeminiLiveService(options GeminiLiveOptions, transports IRealtimeTransportFactory) *GeminiLiveService {
	if transports == nil {
		transports = NullRealtimeTransportFactoryInstance
	}
	return &GeminiLiveService{options: options, transports: transports}
}

// ProviderId returns "gemini-live".
func (s *GeminiLiveService) ProviderId() string { return "gemini-live" }

// IsConfigured is true when the API key is present.
func (s *GeminiLiveService) IsConfigured() bool { return !isBlank(s.options.ApiKey) }

// StartSession opens a Gemini Live WS session (no headers; key on query). Ports
// StartSessionAsync.
func (s *GeminiLiveService) StartSession(ctx context.Context, config RealtimeSessionConfig) (IRealtimeSession, error) {
	if !s.IsConfigured() {
		return nil, errors.New("Gemini Live is not configured. Set GeminiLiveOptions.ApiKey before calling StartSession.")
	}
	endpoint, err := url.Parse(s.options.WebSocketEndpoint + "?key=" + url.QueryEscape(s.options.ApiKey))
	if err != nil {
		return nil, err
	}
	transport, err := s.transports.Connect(ctx, endpoint, nil)
	if err != nil {
		return nil, err
	}
	return NewRealtimeWebSocketSession(transport, config, s.ProviderId()), nil
}

// ---------------------------------------------------------------------------
// AWS Nova Sonic
// ---------------------------------------------------------------------------

// NovaSonicService is an IRealtimeService backed by AWS Nova Sonic. Credentials
// are surfaced via headers; the host's transport factory performs SigV4 signing.
// Ports NovaSonicService.
type NovaSonicService struct {
	options    NovaSonicOptions
	transports IRealtimeTransportFactory
}

// NewNovaSonicService builds the connector (nil factory => null default).
func NewNovaSonicService(options NovaSonicOptions, transports IRealtimeTransportFactory) *NovaSonicService {
	if transports == nil {
		transports = NullRealtimeTransportFactoryInstance
	}
	return &NovaSonicService{options: options, transports: transports}
}

// ProviderId returns "aws-nova-sonic".
func (s *NovaSonicService) ProviderId() string { return "aws-nova-sonic" }

// IsConfigured is true when BOTH the access key id and secret key are present.
func (s *NovaSonicService) IsConfigured() bool {
	return !isBlank(s.options.AccessKeyId) && !isBlank(s.options.SecretAccessKey)
}

// StartSession opens a Nova Sonic bidirectional-stream WS session, surfacing AWS
// creds via X-Amz-* headers for the factory to SigV4-sign. Ports StartSessionAsync.
func (s *NovaSonicService) StartSession(ctx context.Context, config RealtimeSessionConfig) (IRealtimeSession, error) {
	if !s.IsConfigured() {
		return nil, errors.New("AWS Nova Sonic is not configured. Set NovaSonicOptions.AccessKeyId and SecretAccessKey before calling StartSession.")
	}
	endpoint, err := url.Parse(fmt.Sprintf(
		"wss://bedrock-runtime.%s.amazonaws.com/model/%s/invoke-with-bidirectional-stream",
		s.options.Region, url.PathEscape(config.Model)))
	if err != nil {
		return nil, err
	}
	headers := map[string]string{
		"X-Amz-Access-Key": s.options.AccessKeyId,
		"X-Amz-Secret-Key": s.options.SecretAccessKey,
		"X-Amz-Region":     s.options.Region,
	}
	if !isBlank(s.options.SessionToken) {
		headers["X-Amz-Security-Token"] = s.options.SessionToken
	}
	transport, err := s.transports.Connect(ctx, endpoint, headers)
	if err != nil {
		return nil, err
	}
	return NewRealtimeWebSocketSession(transport, config, s.ProviderId()), nil
}

// ---------------------------------------------------------------------------
// ElevenLabs Conversational AI
// ---------------------------------------------------------------------------

// ElevenLabsConvService is an IRealtimeService backed by ElevenLabs Conversational
// AI (?agent_id=, xi-api-key header). Ports ElevenLabsConvService.
type ElevenLabsConvService struct {
	options    ElevenLabsConvOptions
	transports IRealtimeTransportFactory
}

// NewElevenLabsConvService builds the connector (nil factory => null default).
func NewElevenLabsConvService(options ElevenLabsConvOptions, transports IRealtimeTransportFactory) *ElevenLabsConvService {
	if transports == nil {
		transports = NullRealtimeTransportFactoryInstance
	}
	return &ElevenLabsConvService{options: options, transports: transports}
}

// ProviderId returns "elevenlabs-conv".
func (s *ElevenLabsConvService) ProviderId() string { return "elevenlabs-conv" }

// IsConfigured is true when BOTH the API key and the agent id are present.
func (s *ElevenLabsConvService) IsConfigured() bool {
	return !isBlank(s.options.ApiKey) && !isBlank(s.options.AgentId)
}

// StartSession opens an ElevenLabs Conv WS session. Ports StartSessionAsync.
func (s *ElevenLabsConvService) StartSession(ctx context.Context, config RealtimeSessionConfig) (IRealtimeSession, error) {
	if !s.IsConfigured() {
		return nil, errors.New("ElevenLabs Conversational AI is not configured. Set ElevenLabsConvOptions.ApiKey AND AgentId before calling StartSession.")
	}
	endpoint, err := url.Parse(s.options.WebSocketEndpoint + "?agent_id=" + url.QueryEscape(s.options.AgentId))
	if err != nil {
		return nil, err
	}
	headers := map[string]string{"xi-api-key": s.options.ApiKey}
	transport, err := s.transports.Connect(ctx, endpoint, headers)
	if err != nil {
		return nil, err
	}
	return NewRealtimeWebSocketSession(transport, config, s.ProviderId()), nil
}

// ---------------------------------------------------------------------------
// Ultravox (HTTP session-create then WS join)
// ---------------------------------------------------------------------------

// UltravoxService is an IRealtimeService backed by Ultravox. It POSTs /api/calls
// (through the injected ToolHTTPDoer) to obtain a joinUrl, then opens a WS to it
// (through the injected IRealtimeTransportFactory). Ports UltravoxService.
type UltravoxService struct {
	doer       ToolHTTPDoer
	options    UltravoxOptions
	transports IRealtimeTransportFactory
}

// NewUltravoxService builds the connector against an injected HTTP doer (for the
// session-create POST) and transport factory (for the WS). A nil factory defaults
// to null.
func NewUltravoxService(options UltravoxOptions, doer ToolHTTPDoer, transports IRealtimeTransportFactory) *UltravoxService {
	if transports == nil {
		transports = NullRealtimeTransportFactoryInstance
	}
	return &UltravoxService{doer: doer, options: options, transports: transports}
}

// ProviderId returns "ultravox".
func (s *UltravoxService) ProviderId() string { return "ultravox" }

// IsConfigured is true when the API key is present.
func (s *UltravoxService) IsConfigured() bool { return !isBlank(s.options.ApiKey) }

// StartSession creates an Ultravox call then joins its WS. Unlike the fail-soft
// Speech adapters, a create failure surfaces as an error (mirrors the C#
// EnsureSuccessStatusCode + "did not return a joinUrl" throws). Ports
// StartSessionAsync.
func (s *UltravoxService) StartSession(ctx context.Context, config RealtimeSessionConfig) (IRealtimeSession, error) {
	if !s.IsConfigured() {
		return nil, errors.New("Ultravox is not configured. Set UltravoxOptions.ApiKey before calling StartSession.")
	}
	if s.doer == nil {
		return nil, errors.New("Ultravox requires an HTTP doer for session creation.")
	}
	model := config.Model
	if isBlank(model) {
		model = s.options.DefaultModel
	}
	voice := config.VoiceId
	if isBlank(voice) {
		voice = s.options.DefaultVoice
	}
	body, _ := json.Marshal(map[string]any{
		"model":        model,
		"voice":        voice,
		"systemPrompt": config.SystemPrompt,
		"medium":       map[string]any{"serverWebSocket": map[string]any{"inputSampleRate": 16000, "outputSampleRate": 24000}},
	})
	headers := map[string]string{"X-API-Key": s.options.ApiKey, "Content-Type": "application/json"}

	resp, err := s.doer(ctx, "POST", joinBaseAndPath(s.options.ApiEndpoint, "/api/calls"), headers, body)
	if err != nil {
		return nil, err
	}
	if !is2xx(resp.StatusCode) {
		return nil, fmt.Errorf("ultravox /api/calls returned status %d", resp.StatusCode)
	}
	root, ok := jsonObj(resp.Body)
	if !ok {
		return nil, errors.New("Ultravox API did not return a joinUrl.")
	}
	joinURL := strField(root, "joinUrl")
	if isBlank(joinURL) {
		return nil, errors.New("Ultravox API did not return a joinUrl.")
	}
	endpoint, err := url.Parse(joinURL)
	if err != nil {
		return nil, err
	}
	transport, err := s.transports.Connect(ctx, endpoint, nil)
	if err != nil {
		return nil, err
	}
	return NewRealtimeWebSocketSession(transport, config, s.ProviderId()), nil
}

// Interface guards.
var (
	_ IRealtimeService = (*OpenAiRealtimeService)(nil)
	_ IRealtimeService = (*GeminiLiveService)(nil)
	_ IRealtimeService = (*NovaSonicService)(nil)
	_ IRealtimeService = (*ElevenLabsConvService)(nil)
	_ IRealtimeService = (*UltravoxService)(nil)
)
