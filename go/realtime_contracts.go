// realtime_contracts.go
//
// Ports the CircleAI.Realtime carrier-agnostic streaming contracts plus the
// built-in loopback and null implementations:
//   Contracts.cs:
//     RealtimeAudioFormat        -> RealtimeAudioFormat (Pcm16k=0/Pcm24k=1/Mulaw8k=2)
//     RealtimeDirection          -> RealtimeDirection (Inbound=0/Outbound=1)
//     RealtimeSessionConfig      -> RealtimeSessionConfig
//     RealtimeTool               -> RealtimeTool
//     RealtimeAudioFrame         -> RealtimeAudioFrame
//     RealtimeEvent (union)      -> RealtimeEvent interface + 8 event structs
//     IRealtimeSession           -> IRealtimeSession interface
//     IRealtimeService           -> IRealtimeService interface
//   LoopbackRealtimeService.cs   -> LoopbackTextToAudio, LoopbackRealtimeService,
//                                    LoopbackRealtimeSession, SilenceTextToAudio
//   NullImplementations.cs       -> NullRealtimeService, NullRealtimeSession
//
// STREAMS: C# IAsyncEnumerable<T> -> a <-chan T returned from a method taking a
// context; the channel closes when the source completes (session disposed) or
// ctx cancels. IAsyncDisposable -> Close(ctx). The C# discriminated union
// (abstract record RealtimeEvent + sealed subtypes) becomes the RealtimeEvent
// interface (unexported marker + At() accessor) with one struct per subtype.
//
// CONCURRENCY (this wave is stream/transport-heavy): LoopbackRealtimeSession is
// fed by Send* and drained by Receive*. The C# reference uses UNBOUNDED
// Channels, whose TryWrite never blocks and buffers writes until read — so an
// event emitted before ReceiveEvents is first called is retained, not lost.
// This port backs both audio and event streams with the package's
// unboundedChannel[T] (see security_unbounded_channel.go) to preserve exactly
// that: a caller may Send before it starts receiving and still observe every
// frame/event. ReceiveAudio/ReceiveEvents subscribe synchronously (the reader
// goroutine is spawned inside ReadAll before this method returns) so there is no
// subscribe-after-start race. Close completes both channels; in-flight readers
// drain the buffer, then observe completion and their channels close.

package circleai

import (
	"context"
	"errors"
	"fmt"
	"math"
	"strings"
	"time"

	"github.com/google/uuid"
)

// ---------------------------------------------------------------------------
// Enums (Contracts.cs)
// ---------------------------------------------------------------------------

// RealtimeAudioFormat is the wire audio format used in a realtime session.
// Ordinals match the C# enum (Pcm16k=0, Pcm24k=1, Mulaw8k=2). Ports
// RealtimeAudioFormat.
type RealtimeAudioFormat int

const (
	// RealtimeAudioFormatPcm16k — 16-bit linear PCM, mono, 16 kHz.
	RealtimeAudioFormatPcm16k RealtimeAudioFormat = iota
	// RealtimeAudioFormatPcm24k — 16-bit linear PCM, mono, 24 kHz.
	RealtimeAudioFormatPcm24k
	// RealtimeAudioFormatMulaw8k — G.711 μ-law, mono, 8 kHz (carrier-native).
	RealtimeAudioFormatMulaw8k
)

// String renders the C# enum member name for a RealtimeAudioFormat.
func (f RealtimeAudioFormat) String() string {
	switch f {
	case RealtimeAudioFormatPcm16k:
		return "Pcm16k"
	case RealtimeAudioFormatPcm24k:
		return "Pcm24k"
	case RealtimeAudioFormatMulaw8k:
		return "Mulaw8k"
	default:
		return "Unknown"
	}
}

// RealtimeDirection is the direction of audio in a realtime session. Ordinals
// match the C# enum (Inbound=0, Outbound=1). Ports RealtimeDirection.
type RealtimeDirection int

const (
	// RealtimeDirectionInbound — audio from the caller to us.
	RealtimeDirectionInbound RealtimeDirection = iota
	// RealtimeDirectionOutbound — audio from us to the caller.
	RealtimeDirectionOutbound
)

// String renders the C# enum member name for a RealtimeDirection.
func (d RealtimeDirection) String() string {
	switch d {
	case RealtimeDirectionInbound:
		return "Inbound"
	case RealtimeDirectionOutbound:
		return "Outbound"
	default:
		return "Unknown"
	}
}

// ---------------------------------------------------------------------------
// Records (Contracts.cs)
// ---------------------------------------------------------------------------

// RealtimeTool is one tool the model can call. Ports the RealtimeTool record.
type RealtimeTool struct {
	// Name is the tool name as the model sees it.
	Name string
	// Description tells the model when to call this.
	Description string
	// JsonSchema is the JSON schema for the tool's input arguments.
	JsonSchema string
}

// RealtimeSessionConfig configures a realtime session. Ports the
// RealtimeSessionConfig record; the C# optional parameters map to zero-value
// fields (VoiceId/SystemPrompt/LanguageHint == "" for null, Tools == nil).
// AudioFormat defaults to Pcm24k — use NewRealtimeSessionConfig to apply that
// default, since a zero-valued RealtimeAudioFormat is Pcm16k (ordinal 0).
type RealtimeSessionConfig struct {
	// Model is the vendor-specific model id (e.g. "gpt-4o-realtime-preview-2024-12-17").
	Model string
	// VoiceId is the vendor voice id (e.g. "alloy", "Aoede"); "" = default.
	VoiceId string
	// SystemPrompt shapes the assistant's responses; "" = none.
	SystemPrompt string
	// AudioFormat is the wire audio format (C# default: Pcm24k).
	AudioFormat RealtimeAudioFormat
	// LanguageHint is an ISO hint (e.g. "en-US"); "" = auto-detect.
	LanguageHint string
	// Tools are optional tool definitions exposed to the model; nil = none.
	Tools []RealtimeTool
}

// NewRealtimeSessionConfig builds a config for model with the C# defaults
// applied (AudioFormat = Pcm24k, all optionals empty/nil). Mirrors constructing
// the record with only its required Model argument.
func NewRealtimeSessionConfig(model string) RealtimeSessionConfig {
	return RealtimeSessionConfig{Model: model, AudioFormat: RealtimeAudioFormatPcm24k}
}

// RealtimeAudioFrame is one audio frame in a realtime session. Ports the
// RealtimeAudioFrame record (ReadOnlyMemory<byte> Pcm -> []byte Pcm).
type RealtimeAudioFrame struct {
	// Pcm is the raw audio payload for this frame.
	Pcm []byte
	// Format is the wire format of Pcm.
	Format RealtimeAudioFormat
	// Offset is the frame's offset from the start of the stream.
	Offset time.Duration
}

// ---------------------------------------------------------------------------
// RealtimeEvent discriminated union (Contracts.cs)
// ---------------------------------------------------------------------------

// RealtimeEvent is the closed set of events a realtime session can emit. Ports
// the abstract record RealtimeEvent(DateTimeOffset At) and its sealed subtypes.
// Implemented by exactly the eight event structs in this file; the unexported
// marker method keeps the union closed to this package.
type RealtimeEvent interface {
	// At is the event timestamp (UTC).
	At() time.Time
	isRealtimeEvent()
}

// SpeechStartedEvent signals caller speech started. Ports SpeechStartedEvent.
type SpeechStartedEvent struct{ AtUtc time.Time }

// SpeechEndedEvent signals caller speech ended (model now processing). Ports
// SpeechEndedEvent.
type SpeechEndedEvent struct{ AtUtc time.Time }

// TranscriptDeltaEvent is a partial transcript delta. Ports TranscriptDeltaEvent.
type TranscriptDeltaEvent struct {
	AtUtc     time.Time
	Delta     string
	Direction RealtimeDirection
}

// TranscriptFinalEvent is a final transcript for an utterance. Ports
// TranscriptFinalEvent.
type TranscriptFinalEvent struct {
	AtUtc     time.Time
	Text      string
	Direction RealtimeDirection
}

// ToolCallEvent signals the model wants to call a tool. Ports ToolCallEvent.
type ToolCallEvent struct {
	AtUtc         time.Time
	CallId        string
	ToolName      string
	ArgumentsJson string
}

// TurnCompleteEvent signals the assistant turn is complete. Ports
// TurnCompleteEvent.
type TurnCompleteEvent struct{ AtUtc time.Time }

// SessionErrorEvent reports a vendor error mid-session. Ports SessionErrorEvent.
type SessionErrorEvent struct {
	AtUtc   time.Time
	Message string
}

// At / marker implementations.
func (e SpeechStartedEvent) At() time.Time   { return e.AtUtc }
func (e SpeechEndedEvent) At() time.Time     { return e.AtUtc }
func (e TranscriptDeltaEvent) At() time.Time { return e.AtUtc }
func (e TranscriptFinalEvent) At() time.Time { return e.AtUtc }
func (e ToolCallEvent) At() time.Time        { return e.AtUtc }
func (e TurnCompleteEvent) At() time.Time    { return e.AtUtc }
func (e SessionErrorEvent) At() time.Time    { return e.AtUtc }

func (SpeechStartedEvent) isRealtimeEvent()   {}
func (SpeechEndedEvent) isRealtimeEvent()     {}
func (TranscriptDeltaEvent) isRealtimeEvent() {}
func (TranscriptFinalEvent) isRealtimeEvent() {}
func (ToolCallEvent) isRealtimeEvent()        {}
func (TurnCompleteEvent) isRealtimeEvent()    {}
func (SessionErrorEvent) isRealtimeEvent()    {}

// ---------------------------------------------------------------------------
// Session + service contracts (Contracts.cs)
// ---------------------------------------------------------------------------

// IRealtimeSession is one open conversation with a realtime vendor. Audio flows
// both ways concurrently; control + transcripts surface as RealtimeEvents. Ports
// IRealtimeSession (IAsyncDisposable -> Close). ReceiveAudio/ReceiveEvents each
// return a channel that closes when the session is closed or ctx cancels.
type IRealtimeSession interface {
	// SessionId is the vendor session identifier.
	SessionId() string
	// ReceiveAudio streams inbound audio (caller -> us).
	ReceiveAudio(ctx context.Context) <-chan RealtimeAudioFrame
	// SendAudio sends one audio frame to the model.
	SendAudio(ctx context.Context, frame RealtimeAudioFrame) error
	// SendText sends a text turn to the model (no audio, e.g. TTS-only).
	SendText(ctx context.Context, text string) error
	// SendToolResult replies to a tool call with its result.
	SendToolResult(ctx context.Context, callId, resultJson string) error
	// CancelResponse cancels the current model response (barge-in).
	CancelResponse(ctx context.Context) error
	// ReceiveEvents streams control + transcript events from the vendor.
	ReceiveEvents(ctx context.Context) <-chan RealtimeEvent
	// Close disposes the session (ports IAsyncDisposable.DisposeAsync).
	Close(ctx context.Context) error
}

// IRealtimeService is a vendor connector that opens realtime sessions. Ports
// IRealtimeService.
type IRealtimeService interface {
	// ProviderId is the vendor self-id (e.g. "openai-realtime").
	ProviderId() string
	// IsConfigured is true when credentials are present.
	IsConfigured() bool
	// StartSession opens one realtime session per config.
	StartSession(ctx context.Context, config RealtimeSessionConfig) (IRealtimeSession, error)
}

// ---------------------------------------------------------------------------
// Loopback service (LoopbackRealtimeService.cs)
// ---------------------------------------------------------------------------

// LoopbackTextToAudio synthesises outbound audio for text. Ports the
// LoopbackTextToAudio delegate. The default (SilenceTextToAudio) emits real
// silence frames sized to the text's expected speech duration.
type LoopbackTextToAudio func(ctx context.Context, text string, format RealtimeAudioFormat) ([]byte, error)

// sampleRateOf returns the sample rate for a RealtimeAudioFormat. Ports
// LoopbackRealtimeSession.SampleRateOf.
func sampleRateOf(f RealtimeAudioFormat) int {
	switch f {
	case RealtimeAudioFormatPcm16k:
		return 16000
	case RealtimeAudioFormatPcm24k:
		return 24000
	case RealtimeAudioFormatMulaw8k:
		return 8000
	default:
		return 16000
	}
}

// SilenceTextToAudio emits real silence frames sized to ~80 ms per word (min
// 50 ms), as 16-bit PCM zero-amplitude samples. Ports
// LoopbackRealtimeService.SilenceTextToAudio: sampleCount = sr*durationMs/1000,
// byte length = sampleCount*2.
func SilenceTextToAudio(_ context.Context, text string, format RealtimeAudioFormat) ([]byte, error) {
	sr := sampleRateOf(format)
	wordCount := 0
	if strings.TrimSpace(text) != "" {
		wordCount = len(strings.FieldsFunc(text, func(r rune) bool {
			return r == ' ' || r == '\t' || r == '\n'
		}))
	}
	durationMs := wordCount * 80
	if durationMs < 50 {
		durationMs = 50
	}
	sampleCount := sr * durationMs / 1000
	return make([]byte, sampleCount*2), nil
}

// LoopbackRealtimeService is the built-in in-process IRealtimeService: sessions
// echo inbound audio to outbound and reply to SendText with synthesised audio.
// Ports LoopbackRealtimeService. ProviderId="loopback", IsConfigured=true.
type LoopbackRealtimeService struct {
	textToAudio LoopbackTextToAudio
}

// NewLoopbackRealtimeService constructs the service with the default silence
// synthesiser. Ports the parameterless C# constructor.
func NewLoopbackRealtimeService() *LoopbackRealtimeService {
	return &LoopbackRealtimeService{textToAudio: SilenceTextToAudio}
}

// NewLoopbackRealtimeServiceWith constructs the service with a custom
// text-to-audio synthesiser (must not be nil). Ports the
// LoopbackRealtimeService(LoopbackTextToAudio) constructor.
func NewLoopbackRealtimeServiceWith(textToAudio LoopbackTextToAudio) (*LoopbackRealtimeService, error) {
	if textToAudio == nil {
		return nil, errors.New("textToAudio required")
	}
	return &LoopbackRealtimeService{textToAudio: textToAudio}, nil
}

// ProviderId returns "loopback".
func (s *LoopbackRealtimeService) ProviderId() string { return "loopback" }

// IsConfigured returns true.
func (s *LoopbackRealtimeService) IsConfigured() bool { return true }

// StartSession opens a new loopback session for config. Ports StartSessionAsync.
func (s *LoopbackRealtimeService) StartSession(_ context.Context, config RealtimeSessionConfig) (IRealtimeSession, error) {
	return newLoopbackRealtimeSession(config, s.textToAudio), nil
}

// ---------------------------------------------------------------------------
// Loopback session (LoopbackRealtimeService.cs)
// ---------------------------------------------------------------------------

// LoopbackRealtimeSession is the in-process loopback IRealtimeSession. Ports
// LoopbackRealtimeSession. Audio and events are backed by unbounded channels so
// Send* before Receive* still delivers every frame/event. Not safe for
// concurrent Send* from multiple goroutines against the same session (the C#
// reference likewise mutates _offset/_speaking without a lock); serialise sends.
type LoopbackRealtimeSession struct {
	config      RealtimeSessionConfig
	textToAudio LoopbackTextToAudio
	audio       *unboundedChannel[RealtimeAudioFrame]
	events      *unboundedChannel[RealtimeEvent]
	offset      time.Duration
	speaking    bool
	sessionId   string
}

// NewLoopbackRealtimeSession constructs a loopback session for config using the
// default silence synthesiser. Ports the LoopbackRealtimeSession(config)
// constructor.
func NewLoopbackRealtimeSession(config RealtimeSessionConfig) *LoopbackRealtimeSession {
	return newLoopbackRealtimeSession(config, SilenceTextToAudio)
}

// NewLoopbackRealtimeSessionWith constructs a loopback session for config with a
// custom synthesiser (must not be nil). Ports the
// LoopbackRealtimeSession(config, textToAudio) constructor.
func NewLoopbackRealtimeSessionWith(config RealtimeSessionConfig, textToAudio LoopbackTextToAudio) (*LoopbackRealtimeSession, error) {
	if textToAudio == nil {
		return nil, errors.New("textToAudio required")
	}
	return newLoopbackRealtimeSession(config, textToAudio), nil
}

func newLoopbackRealtimeSession(config RealtimeSessionConfig, textToAudio LoopbackTextToAudio) *LoopbackRealtimeSession {
	return &LoopbackRealtimeSession{
		config:      config,
		textToAudio: textToAudio,
		audio:       newUnboundedChannel[RealtimeAudioFrame](),
		events:      newUnboundedChannel[RealtimeEvent](),
		sessionId:   "loop-" + strings.ReplaceAll(uuid.NewString(), "-", ""),
	}
}

// SessionId returns the "loop-<uuidN>" identifier.
func (s *LoopbackRealtimeSession) SessionId() string { return s.sessionId }

// ReceiveAudio streams inbound (echoed) audio frames until the session is closed
// or ctx cancels. Ports ReceiveAudioAsync (WaitToRead/TryRead drain loop over an
// unbounded channel — here the unboundedChannel bridge yields buffered then
// future frames).
func (s *LoopbackRealtimeSession) ReceiveAudio(ctx context.Context) <-chan RealtimeAudioFrame {
	return s.audio.ReadAll(ctx)
}

// SendAudio echoes frame back as outbound audio and emits a
// SpeechStarted/SpeechEnded event when the frame's silence state flips. Ports
// SendAudioAsync (RMS silence detection + loopback echo).
func (s *LoopbackRealtimeSession) SendAudio(_ context.Context, frame RealtimeAudioFrame) error {
	nowSpeaking := !isSilentPcm16(frame.Pcm)
	if nowSpeaking != s.speaking {
		if nowSpeaking {
			s.events.Write(SpeechStartedEvent{AtUtc: time.Now().UTC()})
		} else {
			s.events.Write(SpeechEndedEvent{AtUtc: time.Now().UTC()})
		}
		s.speaking = nowSpeaking
	}
	s.audio.Write(frame)
	return nil
}

// SendText emits a Delta/Final transcript pair (Outbound), synthesises audio for
// text and pushes it as an outbound frame advancing the stream offset, then
// emits TurnComplete. Ports SendTextAsync.
func (s *LoopbackRealtimeSession) SendText(ctx context.Context, text string) error {
	s.events.Write(TranscriptDeltaEvent{AtUtc: time.Now().UTC(), Delta: text, Direction: RealtimeDirectionOutbound})
	pcm, err := s.textToAudio(ctx, text, s.config.AudioFormat)
	if err != nil {
		return err
	}
	if len(pcm) > 0 {
		s.audio.Write(RealtimeAudioFrame{Pcm: pcm, Format: s.config.AudioFormat, Offset: s.offset})
		// offset += ms for (len/2) samples at the format's sample rate.
		ms := float64(len(pcm)) / 2.0 / float64(sampleRateOf(s.config.AudioFormat)) * 1000.0
		s.offset += time.Duration(ms * float64(time.Millisecond))
	}
	s.events.Write(TranscriptFinalEvent{AtUtc: time.Now().UTC(), Text: text, Direction: RealtimeDirectionOutbound})
	s.events.Write(TurnCompleteEvent{AtUtc: time.Now().UTC()})
	return nil
}

// SendToolResult emits an Outbound transcript delta summarising the tool result
// (truncated to 60 runes). Ports SendToolResultAsync. Blank callId is rejected.
func (s *LoopbackRealtimeSession) SendToolResult(_ context.Context, callId, resultJson string) error {
	if strings.TrimSpace(callId) == "" {
		return errors.New("callId required")
	}
	delta := fmt.Sprintf("[tool %s: %s]", callId, truncateRunes(resultJson, 60))
	s.events.Write(TranscriptDeltaEvent{AtUtc: time.Now().UTC(), Delta: delta, Direction: RealtimeDirectionOutbound})
	return nil
}

// CancelResponse emits a TurnComplete event. Ports CancelResponseAsync.
func (s *LoopbackRealtimeSession) CancelResponse(_ context.Context) error {
	s.events.Write(TurnCompleteEvent{AtUtc: time.Now().UTC()})
	return nil
}

// ReceiveEvents streams control + transcript events until the session is closed
// or ctx cancels. Ports ReceiveEventsAsync.
func (s *LoopbackRealtimeSession) ReceiveEvents(ctx context.Context) <-chan RealtimeEvent {
	return s.events.ReadAll(ctx)
}

// Close completes both streams; in-flight readers drain buffered items then
// their channels close. Idempotent. Ports DisposeAsync (TryComplete on both).
func (s *LoopbackRealtimeSession) Close(_ context.Context) error {
	s.audio.Complete()
	s.events.Complete()
	return nil
}

// isSilentPcm16 is an RMS-based silence detector over 16-bit little-endian PCM,
// returning true below ~-42 dBFS (rms < 250). Buffers under 64 bytes are treated
// as silent. Ports LoopbackRealtimeSession.IsSilent.
func isSilentPcm16(pcm []byte) bool {
	if len(pcm) < 64 {
		return true
	}
	var sumSq int64
	samples := len(pcm) / 2
	for i := 0; i+1 < len(pcm); i += 2 {
		s := int16(uint16(pcm[i]) | uint16(pcm[i+1])<<8)
		sumSq += int64(s) * int64(s)
	}
	rms := math.Sqrt(float64(sumSq) / float64(samples))
	return rms < 250.0
}

// truncateRunes returns s if it has <= max runes, else its first max runes plus
// an ellipsis. Ports LoopbackRealtimeSession.Truncate (rune-aware to match C#
// substring-by-char semantics).
func truncateRunes(s string, max int) string {
	r := []rune(s)
	if len(r) <= max {
		return s
	}
	return string(r[:max]) + "…"
}

// ---------------------------------------------------------------------------
// Null implementations (NullImplementations.cs)
// ---------------------------------------------------------------------------

// NullRealtimeService throws on StartSession and reports IsConfigured=false.
// Ports NullRealtimeService. Use NullRealtimeServiceInstance for the singleton.
type NullRealtimeService struct{}

// NullRealtimeServiceInstance is the shared singleton (ports the C# static
// readonly Instance).
var NullRealtimeServiceInstance = NullRealtimeService{}

// ProviderId returns "null".
func (NullRealtimeService) ProviderId() string { return "null" }

// IsConfigured returns false.
func (NullRealtimeService) IsConfigured() bool { return false }

// StartSession always errors "no vendor wired". Ports the C# InvalidOperationException.
func (NullRealtimeService) StartSession(_ context.Context, _ RealtimeSessionConfig) (IRealtimeSession, error) {
	return nil, errors.New("No realtime vendor is registered. Add CircleAI.Realtime.Cloud connectors (OpenAI, Gemini, Nova, ElevenLabs, Ultravox).")
}

// NullRealtimeSession is a fully muted session that yields nothing. Ports
// NullRealtimeSession. All Send*/Cancel/Close are no-ops; Receive* channels
// close immediately.
type NullRealtimeSession struct{}

// SessionId returns "null".
func (NullRealtimeSession) SessionId() string { return "null" }

// ReceiveAudio returns an already-closed channel (yields nothing).
func (NullRealtimeSession) ReceiveAudio(_ context.Context) <-chan RealtimeAudioFrame {
	ch := make(chan RealtimeAudioFrame)
	close(ch)
	return ch
}

// SendAudio is a no-op.
func (NullRealtimeSession) SendAudio(_ context.Context, _ RealtimeAudioFrame) error { return nil }

// SendText is a no-op.
func (NullRealtimeSession) SendText(_ context.Context, _ string) error { return nil }

// SendToolResult is a no-op.
func (NullRealtimeSession) SendToolResult(_ context.Context, _, _ string) error { return nil }

// CancelResponse is a no-op.
func (NullRealtimeSession) CancelResponse(_ context.Context) error { return nil }

// ReceiveEvents returns an already-closed channel (yields nothing).
func (NullRealtimeSession) ReceiveEvents(_ context.Context) <-chan RealtimeEvent {
	ch := make(chan RealtimeEvent)
	close(ch)
	return ch
}

// Close is a no-op.
func (NullRealtimeSession) Close(_ context.Context) error { return nil }

// Interface guards.
var (
	_ IRealtimeService = (*LoopbackRealtimeService)(nil)
	_ IRealtimeSession = (*LoopbackRealtimeSession)(nil)
	_ IRealtimeService = NullRealtimeService{}
	_ IRealtimeSession = NullRealtimeSession{}
)
