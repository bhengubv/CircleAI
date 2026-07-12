// realtime_cloud_session.go
//
// Ports CircleAI.Realtime.Cloud/RealtimeWebSocketSession.cs — the concrete
// IRealtimeSession that rides an IRealtimeTransport (the WebSocket seam ported in
// realtime_cloud_transport.go) and translates vendor JSON text frames to
// RealtimeEvents / binary frames to RealtimeAudioFrames.
//
// TRANSPORT SEAM: the C# constructor takes an IRealtimeTransport; so does this.
// The 5 vendor services (realtime_cloud_services.go) obtain a transport from an
// IRealtimeTransportFactory (real ClientWebSocket in a host; InMemory/Null here)
// and hand it to NewRealtimeWebSocketSession — nothing in this file dials a socket.
//
// STREAM BRIDGING: C# `await foreach (var frame in transport.ReceiveBinaryAsync)`
// becomes forwarding the transport's ReceiveBinary channel, wrapping each []byte
// as a RealtimeAudioFrame in the config's AudioFormat (Offset = 0, as in the C#).
// For events, a goroutine drains ReceiveText, runs the lenient ParseRealtimeEvent
// on each frame, and forwards non-nil results — the parse-failure branch is
// swallowed exactly like the C# try/catch-and-skip. The output channel closes when
// the transport's text stream closes or ctx cancels.
//
// SEND ENVELOPES: SendText/SendToolResult/CancelResponse serialise the same
// vendor-neutral JSON envelopes as the C# ({type:"user.text"|"tool.result"|
// "response.cancel", provider, ...}); SendAudio forwards the frame's PCM as a
// binary frame. Close mirrors DisposeAsync (CloseConn then Close, both best-effort).

package circleai

import (
	"context"
	"encoding/json"
	"time"
)

// RealtimeWebSocketSession is an IRealtimeSession backed by an IRealtimeTransport.
// Vendor-specific JSON envelope translation lives here (lenient cross-vendor
// parser). Ports RealtimeWebSocketSession.
type RealtimeWebSocketSession struct {
	transport  IRealtimeTransport
	config     RealtimeSessionConfig
	providerID string
	sessionID  string
}

// NewRealtimeWebSocketSession builds a session over transport for providerID
// (e.g. "openai-realtime"). Ports the RealtimeWebSocketSession constructor;
// sessionId is a fresh dashless uuid via the package's uuidN(), matching
// Guid.NewGuid().ToString("n").
func NewRealtimeWebSocketSession(transport IRealtimeTransport, config RealtimeSessionConfig, providerID string) *RealtimeWebSocketSession {
	return &RealtimeWebSocketSession{
		transport:  transport,
		config:     config,
		providerID: providerID,
		sessionID:  uuidN(),
	}
}

// SessionId returns the session identifier.
func (s *RealtimeWebSocketSession) SessionId() string { return s.sessionID }

// ReceiveAudio forwards the transport's inbound binary frames as RealtimeAudioFrames
// in the config's AudioFormat (Offset = 0). The channel closes when the transport
// binary stream closes or ctx cancels. Ports ReceiveAudioAsync.
func (s *RealtimeWebSocketSession) ReceiveAudio(ctx context.Context) <-chan RealtimeAudioFrame {
	out := make(chan RealtimeAudioFrame)
	src := s.transport.ReceiveBinary(ctx)
	go func() {
		defer close(out)
		for {
			select {
			case <-ctx.Done():
				return
			case b, ok := <-src:
				if !ok {
					return
				}
				frame := RealtimeAudioFrame{Pcm: b, Format: s.config.AudioFormat, Offset: 0}
				select {
				case <-ctx.Done():
					return
				case out <- frame:
				}
			}
		}
	}()
	return out
}

// SendAudio forwards the frame's PCM as one binary transport frame. Ports
// SendAudioAsync.
func (s *RealtimeWebSocketSession) SendAudio(ctx context.Context, frame RealtimeAudioFrame) error {
	return s.transport.SendBinary(ctx, frame.Pcm)
}

// SendText sends the vendor-neutral {type:"user.text"} envelope. Ports SendTextAsync.
func (s *RealtimeWebSocketSession) SendText(ctx context.Context, text string) error {
	b, _ := json.Marshal(map[string]any{"type": "user.text", "provider": s.providerID, "text": text})
	return s.transport.SendText(ctx, string(b))
}

// SendToolResult sends the {type:"tool.result"} envelope. Ports SendToolResultAsync.
func (s *RealtimeWebSocketSession) SendToolResult(ctx context.Context, callID, resultJSON string) error {
	b, _ := json.Marshal(map[string]any{
		"type":        "tool.result",
		"provider":    s.providerID,
		"call_id":     callID,
		"result_json": resultJSON,
	})
	return s.transport.SendText(ctx, string(b))
}

// CancelResponse sends the {type:"response.cancel"} envelope. Ports CancelResponseAsync.
func (s *RealtimeWebSocketSession) CancelResponse(ctx context.Context) error {
	b, _ := json.Marshal(map[string]any{"type": "response.cancel", "provider": s.providerID})
	return s.transport.SendText(ctx, string(b))
}

// ReceiveEvents drains the transport's inbound text frames, parses each with the
// lenient cross-vendor ParseRealtimeEvent, and forwards non-nil events. Unparseable
// frames are skipped (mirrors the C# try/catch that logs-and-continues). The
// channel closes when the transport text stream closes or ctx cancels. Ports
// ReceiveEventsAsync.
func (s *RealtimeWebSocketSession) ReceiveEvents(ctx context.Context) <-chan RealtimeEvent {
	out := make(chan RealtimeEvent)
	src := s.transport.ReceiveText(ctx)
	go func() {
		defer close(out)
		for {
			select {
			case <-ctx.Done():
				return
			case text, ok := <-src:
				if !ok {
					return
				}
				ev, ok := ParseRealtimeEvent(text, time.Now().UTC())
				if !ok {
					continue
				}
				select {
				case <-ctx.Done():
					return
				case out <- ev:
				}
			}
		}
	}()
	return out
}

// Close closes then disposes the transport, both best-effort. Ports DisposeAsync.
func (s *RealtimeWebSocketSession) Close(ctx context.Context) error {
	_ = s.transport.CloseConn(ctx)
	return s.transport.Close(ctx)
}

// ---------------------------------------------------------------------------
// Lenient cross-vendor JSON event parser (RealtimeWebSocketSession.ParseEvent)
// ---------------------------------------------------------------------------

// ParseRealtimeEvent parses one vendor JSON text frame into a RealtimeEvent,
// stamping it with at. Returns (nil,false) for blank/unrecognised frames. It
// recognises OpenAI Realtime `type` discriminators, their short aliases, and the
// Gemini Live serverContent shape — a faithful port of
// RealtimeWebSocketSession.ParseEvent (which returns null for unknown frames).
func ParseRealtimeEvent(jsonText string, at time.Time) (RealtimeEvent, bool) {
	if isBlank(jsonText) {
		return nil, false
	}
	root, ok := jsonObj([]byte(jsonText))
	if !ok {
		return nil, false
	}

	// OpenAI Realtime uses "type" = "input_audio_buffer.speech_started" etc.
	if _, has := root["type"]; has {
		typ := strField(root, "type")
		if typ == "" {
			// present but not a string — fall through to the Gemini branch.
		} else {
			switch typ {
			case "input_audio_buffer.speech_started", "speech_started":
				return SpeechStartedEvent{AtUtc: at}, true
			case "input_audio_buffer.speech_stopped", "speech_stopped":
				return SpeechEndedEvent{AtUtc: at}, true

			case "conversation.item.input_audio_transcription.delta", "transcript.delta":
				return TranscriptDeltaEvent{AtUtc: at, Delta: strField(root, "delta"), Direction: RealtimeDirectionInbound}, true

			case "conversation.item.input_audio_transcription.completed", "transcript.final":
				txt := strField(root, "transcript")
				if txt == "" {
					txt = strField(root, "text")
				}
				return TranscriptFinalEvent{AtUtc: at, Text: txt, Direction: RealtimeDirectionInbound}, true

			case "response.audio_transcript.delta":
				return TranscriptDeltaEvent{AtUtc: at, Delta: strField(root, "delta"), Direction: RealtimeDirectionOutbound}, true

			case "response.audio_transcript.done":
				return TranscriptFinalEvent{AtUtc: at, Text: strField(root, "transcript"), Direction: RealtimeDirectionOutbound}, true

			case "response.function_call_arguments.done", "tool.call":
				args := "{}"
				if raw, ok := root["arguments"]; ok {
					args = string(raw) // GetRawText() — preserve the raw JSON.
				}
				return ToolCallEvent{
					AtUtc:         at,
					CallId:        strField(root, "call_id"),
					ToolName:      strField(root, "name"),
					ArgumentsJson: args,
				}, true

			case "response.done", "turn.complete":
				return TurnCompleteEvent{AtUtc: at}, true

			case "error":
				msg := jsonText
				if errObj, ok := objField(root, "error"); ok {
					if _, has := errObj["message"]; has {
						msg = strField(errObj, "message")
					}
				}
				return SessionErrorEvent{AtUtc: at, Message: msg}, true

			default:
				return nil, false
			}
		}
	}

	// Gemini Live emits { serverContent: { modelTurn: { parts: [{ text: "..." }] } } }.
	if sc, ok := objField(root, "serverContent"); ok {
		if boolField(sc, "turnComplete") {
			return TurnCompleteEvent{AtUtc: at}, true
		}
		if mt, ok := objField(sc, "modelTurn"); ok {
			if parts, ok := arrField(mt, "parts"); ok {
				for _, pRaw := range parts {
					p, ok := arrObj(pRaw)
					if !ok {
						continue
					}
					if _, has := p["text"]; has {
						return TranscriptDeltaEvent{AtUtc: at, Delta: strField(p, "text"), Direction: RealtimeDirectionOutbound}, true
					}
				}
			}
		}
	}

	return nil, false
}

// Interface guard.
var _ IRealtimeSession = (*RealtimeWebSocketSession)(nil)
