// translation.go
//
// Ports CircleAI.Languages.Translation:
//   TranslationTypes.cs      -> TranslationMode, TranslationRequest, TranslationResult, ConversationTurn
//   ITranslationEngine.cs    -> ITranslationEngine
//   ILiveTranslator.cs       -> ILiveTranslator
//   LlmTranslationEngine.cs  -> LlmTranslationEngine
//
// On-device translation. No network call, no data leaving the device. The
// engine translates meaning — not just words — using the on-device LLM via
// IChatGenerator (inference.go). Deterministic given a deterministic generator.

package circleai

import (
	"context"
	"fmt"
	"strings"
	"time"
)

// ---------------------------------------------------------------------------
// TranslationMode
// ---------------------------------------------------------------------------

// TranslationMode selects the register/domain the translation should target.
// Ports the TranslationMode enum (stable ordinals).
type TranslationMode int

const (
	// TranslationModeStandard is a general-purpose translation.
	TranslationModeStandard TranslationMode = iota
	// TranslationModeConversational is tuned for live conversation.
	TranslationModeConversational
	// TranslationModeDocument is tuned for long-form document text.
	TranslationModeDocument
	// TranslationModeTechnical is tuned for technical material.
	TranslationModeTechnical
	// TranslationModeLegal is tuned for legal text.
	TranslationModeLegal
	// TranslationModeMedical is tuned for medical text.
	TranslationModeMedical
)

// String renders the mode the way the C# enum's ToString() does, so prompts
// built from it match the C# original byte-for-byte.
func (m TranslationMode) String() string {
	switch m {
	case TranslationModeStandard:
		return "Standard"
	case TranslationModeConversational:
		return "Conversational"
	case TranslationModeDocument:
		return "Document"
	case TranslationModeTechnical:
		return "Technical"
	case TranslationModeLegal:
		return "Legal"
	case TranslationModeMedical:
		return "Medical"
	default:
		return fmt.Sprintf("TranslationMode(%d)", int(m))
	}
}

// ---------------------------------------------------------------------------
// TranslationRequest
// ---------------------------------------------------------------------------

// TranslationRequest is a request to translate a piece of text between two
// languages. Ports the TranslationRequest record.
type TranslationRequest struct {
	// Text is the source text to translate.
	Text string

	// SourceBcpTag is the BCP-47 tag of the source language.
	SourceBcpTag string

	// TargetBcpTag is the BCP-47 tag of the target language.
	TargetBcpTag string

	// Mode selects the translation register. Defaults to TranslationModeStandard.
	Mode TranslationMode

	// ContextHint is an optional hint about the surrounding context. nil when absent.
	ContextHint *string
}

// ---------------------------------------------------------------------------
// TranslationResult
// ---------------------------------------------------------------------------

// TranslationResult is the result of a completed translation.
// Ports the TranslationResult record.
type TranslationResult struct {
	// OriginalText is the text that was translated.
	OriginalText string

	// TranslatedText is the translation.
	TranslatedText string

	// SourceBcpTag is the source language tag.
	SourceBcpTag string

	// TargetBcpTag is the target language tag.
	TargetBcpTag string

	// Confidence is the engine's confidence in [0, 1].
	Confidence float32

	// TranslatedAt is the UTC time the translation completed.
	TranslatedAt time.Time
}

// ---------------------------------------------------------------------------
// ConversationTurn
// ---------------------------------------------------------------------------

// ConversationTurn is one turn in a live bidirectional conversation.
// Ports the ConversationTurn record.
type ConversationTurn struct {
	// SpeakerBcpTag is the BCP-47 tag of the speaker's language.
	SpeakerBcpTag string

	// OriginalText is what the speaker said, in their language.
	OriginalText string

	// TranslatedText is the translation for the listener, or nil before translation.
	TranslatedText *string

	// Timestamp is the UTC time of the turn.
	Timestamp time.Time
}

// ---------------------------------------------------------------------------
// ITranslationEngine
// ---------------------------------------------------------------------------

// ITranslationEngine is an on-device translation engine. No network call, no
// data leaving the device. Ports the ITranslationEngine interface.
type ITranslationEngine interface {
	// Translate translates the request and returns a completed result.
	Translate(ctx context.Context, request TranslationRequest) (TranslationResult, error)

	// StreamTranslate streams the translation token-by-token. The returned
	// channel is closed when the stream ends; the error channel receives at
	// most one error and is then closed.
	StreamTranslate(ctx context.Context, request TranslationRequest) (<-chan string, <-chan error)

	// IsLanguagePairSupported reports whether the source→target pair is
	// supported.
	IsLanguagePairSupported(ctx context.Context, sourceBcpTag, targetBcpTag string) (bool, error)
}

// ---------------------------------------------------------------------------
// ILiveTranslator
// ---------------------------------------------------------------------------

// ILiveTranslator is a bidirectional live conversation translator. Party A
// speaks partyABcpTag; party B speaks partyBBcpTag. Each turn is translated in
// real-time so both parties hear each other. Runs entirely on-device.
// Ports the ILiveTranslator interface (extends ITranslationEngine).
type ILiveTranslator interface {
	ITranslationEngine

	// StreamConversation reads turns from inputStream, translates each into the
	// other party's language, and emits the translated turns on the returned
	// channel. The output channel is closed when inputStream is closed or ctx
	// is cancelled; the error channel carries at most one error.
	StreamConversation(
		ctx context.Context,
		inputStream <-chan ConversationTurn,
		partyABcpTag, partyBBcpTag string,
	) (<-chan ConversationTurn, <-chan error)
}

// ---------------------------------------------------------------------------
// LlmTranslationEngine
// ---------------------------------------------------------------------------

// LlmTranslationEngine is an ITranslationEngine/ILiveTranslator backed by the
// on-device LLM via IChatGenerator. All processing is on-device — no API calls,
// no data leaving the device. Ports LlmTranslationEngine.
type LlmTranslationEngine struct {
	generator IChatGenerator
}

// NewLlmTranslationEngine constructs the engine around a chat generator.
// generator must not be nil.
func NewLlmTranslationEngine(generator IChatGenerator) *LlmTranslationEngine {
	if generator == nil {
		panic("generator is required")
	}
	return &LlmTranslationEngine{generator: generator}
}

// Translate translates the request in a single generation call. Confidence is
// fixed at 0.9, matching the C# constant.
func (e *LlmTranslationEngine) Translate(ctx context.Context, request TranslationRequest) (TranslationResult, error) {
	messages := []ChatMessage{{Role: "user", Content: buildTranslationPrompt(request)}}
	translated, err := e.generator.Generate(ctx, messages, nil)
	if err != nil {
		return TranslationResult{}, err
	}
	return TranslationResult{
		OriginalText:   request.Text,
		TranslatedText: strings.TrimSpace(translated),
		SourceBcpTag:   request.SourceBcpTag,
		TargetBcpTag:   request.TargetBcpTag,
		Confidence:     0.9,
		TranslatedAt:   time.Now().UTC(),
	}, nil
}

// StreamTranslate streams the translation token-by-token straight from the
// generator's Stream. The generator's channels are forwarded verbatim.
func (e *LlmTranslationEngine) StreamTranslate(ctx context.Context, request TranslationRequest) (<-chan string, <-chan error) {
	messages := []ChatMessage{{Role: "user", Content: buildTranslationPrompt(request)}}
	return e.generator.Stream(ctx, messages, nil)
}

// IsLanguagePairSupported always returns true — the on-device LLM handles any
// pair it was trained on. Matches the C# Task.FromResult(true).
func (e *LlmTranslationEngine) IsLanguagePairSupported(ctx context.Context, sourceBcpTag, targetBcpTag string) (bool, error) {
	return true, nil
}

// StreamConversation translates each inbound turn into the other party's
// language and emits the enriched turn. The consumer is subscribed to
// inputStream synchronously inside the spawned goroutine before any blocking
// work, and the output/error channels are always closed on exit so callers
// never leak.
func (e *LlmTranslationEngine) StreamConversation(
	ctx context.Context,
	inputStream <-chan ConversationTurn,
	partyABcpTag, partyBBcpTag string,
) (<-chan ConversationTurn, <-chan error) {
	out := make(chan ConversationTurn)
	errs := make(chan error, 1)

	go func() {
		defer close(out)
		defer close(errs)
		for {
			select {
			case <-ctx.Done():
				return
			case turn, ok := <-inputStream:
				if !ok {
					return
				}
				targetTag := partyBBcpTag
				if turn.SpeakerBcpTag != partyABcpTag {
					targetTag = partyABcpTag
				}
				req := TranslationRequest{
					Text:         turn.OriginalText,
					SourceBcpTag: turn.SpeakerBcpTag,
					TargetBcpTag: targetTag,
					Mode:         TranslationModeConversational,
				}
				result, err := e.Translate(ctx, req)
				if err != nil {
					errs <- err
					return
				}
				translated := result.TranslatedText
				enriched := turn
				enriched.TranslatedText = &translated
				select {
				case out <- enriched:
				case <-ctx.Done():
					return
				}
			}
		}
	}()

	return out, errs
}

var _ ILiveTranslator = (*LlmTranslationEngine)(nil)

// buildTranslationPrompt renders the LLM prompt for a request. Ports
// LlmTranslationEngine.BuildPrompt exactly (mode rendered via TranslationMode.String).
func buildTranslationPrompt(r TranslationRequest) string {
	var b strings.Builder
	b.WriteString("Translate the following text from ")
	b.WriteString(r.SourceBcpTag)
	b.WriteString(" to ")
	b.WriteString(r.TargetBcpTag)
	b.WriteString(". Mode: ")
	b.WriteString(r.Mode.String())
	b.WriteString(". Preserve meaning and cultural context, not just literal words. ")
	if r.ContextHint != nil {
		b.WriteString("Context: ")
		b.WriteString(*r.ContextHint)
		b.WriteString(". ")
	}
	b.WriteString("Return only the translation with no explanation.\n\n")
	b.WriteString(r.Text)
	return b.String()
}
