// voice_null_impls.go
//
// Ports the CircleAI.Voice fail-safe defaults:
//   NullVoiceTranscriber.cs        -> NullVoiceTranscriber
//   NullWakeWordDetector.cs        -> NullWakeWordDetector
//   NullVoiceActivityDetector.cs   -> NullVoiceActivityDetector (stream pass-through)
//   NullTtsEngine.cs               -> NullTtsEngine (+ NullTtsEngineEmptyResult)
//
// These re-emit / drain streams as their C# counterparts do:
//   - NullVoiceTranscriber.StreamTranscribe drains the input (so producers are
//     not blocked) and emits nothing.
//   - NullVoiceActivityDetector.Detect re-emits every chunk as speech.
//   - NullTtsEngine.StreamSynthesise yields nothing.

package circleai

import (
	"context"
	"errors"
	"sync"
)

// ---------------------------------------------------------------------------
// NullVoiceTranscriber
// ---------------------------------------------------------------------------

// NullVoiceTranscriber returns empty results without consuming audio. Ports
// NullVoiceTranscriber.
type NullVoiceTranscriber struct {
	mu       sync.Mutex
	disposed bool
}

// Transcribe returns an empty ("und") result.
func (t *NullVoiceTranscriber) Transcribe(ctx context.Context, _ []byte) (VoiceTranscriptionResult, error) {
	t.mu.Lock()
	disposed := t.disposed
	t.mu.Unlock()
	if disposed {
		return VoiceTranscriptionResult{}, errors.New("transcriber disposed")
	}
	if err := ctx.Err(); err != nil {
		return VoiceTranscriptionResult{}, err
	}
	return VoiceTranscriptionResult{Text: "", Confidence: 0, LanguageCode: "und"}, nil
}

// StreamTranscribe drains audioChunks (so callers' producers are not blocked) and
// emits nothing; the returned channel closes once the input is drained or ctx
// cancels.
func (t *NullVoiceTranscriber) StreamTranscribe(ctx context.Context, audioChunks <-chan []byte) <-chan PartialTranscription {
	out := make(chan PartialTranscription)
	go func() {
		defer close(out)
		for {
			select {
			case <-ctx.Done():
				return
			case _, ok := <-audioChunks:
				if !ok {
					return
				}
				// discard
			}
		}
	}()
	return out
}

// Close disposes the transcriber.
func (t *NullVoiceTranscriber) Close(context.Context) error {
	t.mu.Lock()
	t.disposed = true
	t.mu.Unlock()
	return nil
}

// ---------------------------------------------------------------------------
// NullWakeWordDetector (CircleAI.Voice)
// ---------------------------------------------------------------------------

// NullWakeWordDetector tracks listening state but never fires. Ports the
// CircleAI.Voice NullWakeWordDetector.
type NullWakeWordDetector struct {
	wakeWord  string
	mu        sync.Mutex
	listening bool
	disposed  bool
}

// NewNullWakeWordDetector constructs a null detector with the default Butler / B!
// wake word "Hey B".
func NewNullWakeWordDetector() *NullWakeWordDetector {
	return &NullWakeWordDetector{wakeWord: "Hey B"}
}

// NewNullWakeWordDetectorWith constructs a null detector with a custom wake word.
// Returns an error if wakeWord is blank (mirrors the C# ArgumentException).
func NewNullWakeWordDetectorWith(wakeWord string) (*NullWakeWordDetector, error) {
	if len(wakeWord) == 0 || isBlank(wakeWord) {
		return nil, errors.New("wakeWord required")
	}
	return &NullWakeWordDetector{wakeWord: wakeWord}, nil
}

// WakeWord returns the configured wake word.
func (d *NullWakeWordDetector) WakeWord() string { return d.wakeWord }

// IsListening reports whether the detector is started.
func (d *NullWakeWordDetector) IsListening() bool {
	d.mu.Lock()
	defer d.mu.Unlock()
	return d.listening
}

// Subscribe returns a no-op unsubscribe func (this detector never fires).
func (d *NullWakeWordDetector) Subscribe(_ func(WakeWordDetectedEventArgs)) (unsubscribe func()) {
	return func() {}
}

// Start marks the detector listening. Idempotent.
func (d *NullWakeWordDetector) Start(ctx context.Context) error {
	d.mu.Lock()
	defer d.mu.Unlock()
	if d.disposed {
		return errors.New("detector disposed")
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	d.listening = true
	return nil
}

// Stop marks the detector not listening. Idempotent.
func (d *NullWakeWordDetector) Stop(ctx context.Context) error {
	d.mu.Lock()
	defer d.mu.Unlock()
	if d.disposed {
		return errors.New("detector disposed")
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	d.listening = false
	return nil
}

// Close disposes the detector.
func (d *NullWakeWordDetector) Close(context.Context) error {
	d.mu.Lock()
	defer d.mu.Unlock()
	d.disposed = true
	d.listening = false
	return nil
}

// ---------------------------------------------------------------------------
// NullVoiceActivityDetector (stream pass-through)
// ---------------------------------------------------------------------------

// NullVoiceActivityDetector passes every chunk through as a speech segment
// without analysis. Ports the CircleAI.Voice NullVoiceActivityDetector (the
// per-frame Speech null is NullSpeechVoiceActivityDetector).
type NullVoiceActivityDetector struct{}

// Detect re-emits every chunk as VadSegment{chunk, true}. The returned channel
// closes when audioStream completes or ctx cancels.
func (NullVoiceActivityDetector) Detect(ctx context.Context, audioStream <-chan []byte) <-chan VadSegment {
	out := make(chan VadSegment)
	go func() {
		defer close(out)
		for {
			select {
			case <-ctx.Done():
				return
			case chunk, ok := <-audioStream:
				if !ok {
					return
				}
				select {
				case out <- VadSegment{Audio: chunk, IsSpeech: true}:
				case <-ctx.Done():
					return
				}
			}
		}
	}()
	return out
}

// ---------------------------------------------------------------------------
// NullTtsEngine
// ---------------------------------------------------------------------------

// NullTtsEngineEmptyResult is the empty synthesis result a real engine would use:
// 24 kHz, mono, 16-bit. Ports NullTtsEngine.EmptyResult.
var NullTtsEngineEmptyResult = TtsSynthesisResult{AudioData: []byte{}, SampleRate: 24000, Channels: 1, BitsPerSample: 16}

// NullTtsEngine returns empty audio and yields nothing. Ports NullTtsEngine.
type NullTtsEngine struct{}

// Synthesise always returns the empty 24 kHz mono 16-bit result.
func (NullTtsEngine) Synthesise(ctx context.Context, _ string) (TtsSynthesisResult, error) {
	if err := ctx.Err(); err != nil {
		return TtsSynthesisResult{}, err
	}
	return NullTtsEngineEmptyResult, nil
}

// StreamSynthesise yields nothing; the returned channel closes immediately.
func (NullTtsEngine) StreamSynthesise(ctx context.Context, _ string) <-chan []byte {
	out := make(chan []byte)
	close(out)
	return out
}

// (isBlank — empty-or-all-whitespace check mirroring string.IsNullOrWhiteSpace —
// is defined once in sync_channel.go and reused across the flat package.)

// Interface guards.
var (
	_ IVoiceTranscriber      = (*NullVoiceTranscriber)(nil)
	_ IWakeWordDetector      = (*NullWakeWordDetector)(nil)
	_ IVoiceActivityDetector = NullVoiceActivityDetector{}
	_ ITtsEngine             = NullTtsEngine{}
)
