// voice_transcriber_keyword.go
//
// Deterministic in-memory IVoiceTranscriber for the Go port. The C# reference
// ships WhisperTranscriber (native whisper.cpp) and NullVoiceTranscriber; this
// provides the hermetic working transcriber the port requires so the pipeline,
// energy wake-word detector, and VAD path are all exercisable without a model.
//
// KeywordVoiceTranscriber decodes a caller-registered mapping of PCM-16 byte
// prefixes to phrases (identical rule model to KeywordSpeechRecognizer). It
// satisfies BOTH the single-shot Transcribe and the streaming StreamTranscribe
// contracts: streaming concatenates all chunks, transcribes once at end-of-stream,
// and emits a single final PartialTranscription.

package circleai

import (
	"context"
	"errors"
	"sync"
)

// KeywordVoiceTranscriber is a deterministic IVoiceTranscriber matching PCM-16
// byte-prefix rules to phrases. Hermetic — no model, no network.
type KeywordVoiceTranscriber struct {
	mu       sync.RWMutex
	rules    []keywordRule
	language string
	disposed bool
}

// NewKeywordVoiceTranscriber constructs an empty transcriber reporting language
// on every result (pass "" to report "und", mirroring the null transcriber's
// unknown-language convention).
func NewKeywordVoiceTranscriber(language string) *KeywordVoiceTranscriber {
	if language == "" {
		language = "und"
	}
	return &KeywordVoiceTranscriber{language: language}
}

// Register adds a rule: when decoded audio begins with marker, the transcript is
// phrase. Rules are tried in registration order. Returns the receiver for chaining.
func (t *KeywordVoiceTranscriber) Register(marker []byte, phrase string) *KeywordVoiceTranscriber {
	t.mu.Lock()
	t.rules = append(t.rules, keywordRule{marker: append([]byte(nil), marker...), phrase: phrase})
	t.mu.Unlock()
	return t
}

// Transcribe transcribes a complete PCM buffer against the registered rules,
// returning the first hit (or an empty "und" result when nothing matches).
func (t *KeywordVoiceTranscriber) Transcribe(ctx context.Context, pcmAudio []byte) (VoiceTranscriptionResult, error) {
	t.mu.RLock()
	disposed := t.disposed
	t.mu.RUnlock()
	if disposed {
		return VoiceTranscriptionResult{}, errors.New("transcriber disposed")
	}
	if err := ctx.Err(); err != nil {
		return VoiceTranscriptionResult{}, err
	}
	return t.match(pcmAudio), nil
}

// StreamTranscribe accumulates chunks and emits a single final PartialTranscription
// as soon as the accumulated buffer matches a registered rule, then closes the
// stream. If the input ends without ever matching, it emits nothing (mirroring a
// transcriber that yields no final for silence/no-match). Emitting on first match
// — rather than only at end-of-stream — matches real streaming transcriber
// behaviour and keeps the stream productive even for continuous (looping) capture.
func (t *KeywordVoiceTranscriber) StreamTranscribe(ctx context.Context, audioChunks <-chan []byte) <-chan PartialTranscription {
	out := make(chan PartialTranscription)
	go func() {
		defer close(out)
		var buf []byte
		for {
			select {
			case <-ctx.Done():
				return
			case chunk, ok := <-audioChunks:
				if !ok {
					// Input ended with no match -> no final result.
					return
				}
				buf = append(buf, chunk...)
				if res := t.match(buf); res.Text != "" {
					select {
					case out <- PartialTranscription{Text: res.Text, IsFinal: true, Confidence: res.Confidence}:
					case <-ctx.Done():
					}
					return
				}
			}
		}
	}()
	return out
}

// Close disposes the transcriber.
func (t *KeywordVoiceTranscriber) Close(context.Context) error {
	t.mu.Lock()
	t.disposed = true
	t.mu.Unlock()
	return nil
}

func (t *KeywordVoiceTranscriber) match(pcmAudio []byte) VoiceTranscriptionResult {
	t.mu.RLock()
	defer t.mu.RUnlock()
	for _, rule := range t.rules {
		if hasBytePrefix(pcmAudio, rule.marker) {
			return VoiceTranscriptionResult{Text: rule.phrase, Confidence: 1, LanguageCode: t.language}
		}
	}
	return VoiceTranscriptionResult{Text: "", Confidence: 0, LanguageCode: t.language}
}

// Interface guard.
var _ IVoiceTranscriber = (*KeywordVoiceTranscriber)(nil)
