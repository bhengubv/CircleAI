// voice_pipeline.go
//
// Ports CircleAI.Voice.VoicePipeline.cs — the convenience composition of
// IWakeWordDetector + IAudioCapture + IVoiceTranscriber (+ optional
// IVoiceActivityDetector + ITtsEngine) — plus IAudioCapture / NullAudioCapture
// and TranscribedEventArgs.
//
// On wake-word detection the pipeline captures audio, optionally filters it
// through VAD, feeds the speech chunks to the transcriber, and raises Transcribed
// with the final VoiceTranscriptionResult. It does not own the wake-word
// lifecycle: callers Start/Stop it; disposing it disposes all collaborators.
//
// CONCURRENCY SAFETY (this wave is stream/transport-heavy):
//   - The pipeline subscribes to the wake detector in the CONSTRUCTOR (before any
//     Start), so no fire is missed.
//   - Each activation runs on its own goroutine with its own cancellation; a new
//     wake event cancels the prior activation first.
//   - Transcribed/ActivationFailed handlers are snapshotted under the gate and
//     invoked outside it (a handler may safely mutate subscriptions).
//   - The activation pipes capture -> (VAD) -> transcriber over channels;
//     StreamTranscribe is subscribed to synchronously before the drain loop.

package circleai

import (
	"context"
	"errors"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// IAudioCapture / NullAudioCapture (VoicePipeline.cs)
// ---------------------------------------------------------------------------

// IAudioCapture captures raw audio from a platform input (microphone) and
// exposes it as a stream of PCM byte chunks in the Format it reports. Ports
// IAudioCapture (IAsyncDisposable -> Close).
type IAudioCapture interface {
	// Format is the PCM format produced by CaptureAsync.
	Format() AudioFormat
	// CaptureAsync begins capturing; the returned channel yields PCM chunks until
	// ctx is cancelled or the underlying capture stops, then closes.
	CaptureAsync(ctx context.Context) <-chan []byte
	// Close disposes the capture (ports IAsyncDisposable.DisposeAsync).
	Close(ctx context.Context) error
}

// NullAudioCapture yields no audio — a safe default when no platform microphone
// backend is available. Ports NullAudioCapture.
type NullAudioCapture struct{}

// Format returns the canonical PCM-16 mono 16 kHz format.
func (NullAudioCapture) Format() AudioFormat { return AudioFormatPcm16Mono16k }

// CaptureAsync returns an already-closed channel (no audio).
func (NullAudioCapture) CaptureAsync(context.Context) <-chan []byte {
	out := make(chan []byte)
	close(out)
	return out
}

// Close is a no-op.
func (NullAudioCapture) Close(context.Context) error { return nil }

var _ IAudioCapture = NullAudioCapture{}

// ---------------------------------------------------------------------------
// TranscribedEventArgs
// ---------------------------------------------------------------------------

// TranscribedEventArgs describes a completed transcription produced by
// VoicePipeline after a wake-word activation. Ports the TranscribedEventArgs
// class.
type TranscribedEventArgs struct {
	// Result is the final transcription result for the activation.
	Result VoiceTranscriptionResult
	// CompletedAt is the UTC timestamp when the transcription completed.
	CompletedAt time.Time
}

// ---------------------------------------------------------------------------
// VoicePipeline
// ---------------------------------------------------------------------------

// VoicePipeline composes a wake detector, transcriber, capture source, and
// optional VAD/TTS. Ports VoicePipeline. Construct with NewVoicePipeline.
type VoicePipeline struct {
	wake        IWakeWordDetector
	transcriber IVoiceTranscriber
	capture     IAudioCapture
	vad         IVoiceActivityDetector
	tts         ITtsEngine

	gate           sync.Mutex
	activationStop context.CancelFunc
	disposed       bool
	unsubWake      func()

	transcribedSubs []*transcribedSub
	failedSubs      []*failedSub
}

type transcribedSub struct {
	handler func(TranscribedEventArgs)
}

type failedSub struct {
	handler func(error)
}

// NewVoicePipeline constructs a pipeline. wake and transcriber are required.
// capture may be nil (a NullAudioCapture is used). vad may be nil (all captured
// audio is forwarded directly). tts may be nil. The pipeline subscribes to the
// wake detector immediately. Ports the VoicePipeline constructor.
func NewVoicePipeline(wake IWakeWordDetector, transcriber IVoiceTranscriber, capture IAudioCapture, vad IVoiceActivityDetector, tts ITtsEngine) (*VoicePipeline, error) {
	if wake == nil {
		return nil, errors.New("wake required")
	}
	if transcriber == nil {
		return nil, errors.New("transcriber required")
	}
	if capture == nil {
		capture = NullAudioCapture{}
	}
	p := &VoicePipeline{
		wake:        wake,
		transcriber: transcriber,
		capture:     capture,
		vad:         vad,
		tts:         tts,
	}
	// Subscribe BEFORE any Start so no wake event is missed.
	p.unsubWake = wake.Subscribe(p.onWakeWordDetected)
	return p, nil
}

// WakeDetector returns the wake-word detector this pipeline observes.
func (p *VoicePipeline) WakeDetector() IWakeWordDetector { return p.wake }

// Transcriber returns the transcriber this pipeline drives.
func (p *VoicePipeline) Transcriber() IVoiceTranscriber { return p.transcriber }

// AudioCapture returns the audio capture source this pipeline reads from.
func (p *VoicePipeline) AudioCapture() IAudioCapture { return p.capture }

// TtsEngine returns the optional TTS engine supplied at construction (nil when
// none). The host is responsible for calling it after a transcription event.
func (p *VoicePipeline) TtsEngine() ITtsEngine { return p.tts }

// VoiceActivityDetector returns the optional VAD supplied at construction (nil
// when all audio is forwarded directly).
func (p *VoicePipeline) VoiceActivityDetector() IVoiceActivityDetector { return p.vad }

// OnTranscribed registers a handler for completed activations and returns an
// idempotent unsubscribe func (ports the Transcribed event).
func (p *VoicePipeline) OnTranscribed(handler func(TranscribedEventArgs)) (unsubscribe func()) {
	if handler == nil {
		panic("handler must not be nil")
	}
	sub := &transcribedSub{handler: handler}
	p.gate.Lock()
	p.transcribedSubs = append(p.transcribedSubs, sub)
	p.gate.Unlock()
	var once sync.Once
	return func() {
		once.Do(func() {
			p.gate.Lock()
			for i, s := range p.transcribedSubs {
				if s == sub {
					p.transcribedSubs = append(p.transcribedSubs[:i], p.transcribedSubs[i+1:]...)
					break
				}
			}
			p.gate.Unlock()
		})
	}
}

// OnActivationFailed registers a handler for activation failures and returns an
// idempotent unsubscribe func (ports the ActivationFailed event).
func (p *VoicePipeline) OnActivationFailed(handler func(error)) (unsubscribe func()) {
	if handler == nil {
		panic("handler must not be nil")
	}
	sub := &failedSub{handler: handler}
	p.gate.Lock()
	p.failedSubs = append(p.failedSubs, sub)
	p.gate.Unlock()
	var once sync.Once
	return func() {
		once.Do(func() {
			p.gate.Lock()
			for i, s := range p.failedSubs {
				if s == sub {
					p.failedSubs = append(p.failedSubs[:i], p.failedSubs[i+1:]...)
					break
				}
			}
			p.gate.Unlock()
		})
	}
}

// Start begins listening for the wake word (delegates to the detector).
func (p *VoicePipeline) Start(ctx context.Context) error {
	p.gate.Lock()
	disposed := p.disposed
	p.gate.Unlock()
	if disposed {
		return errors.New("pipeline disposed")
	}
	return p.wake.Start(ctx)
}

// Stop stops listening for the wake word and cancels any in-flight activation.
func (p *VoicePipeline) Stop(ctx context.Context) error {
	p.gate.Lock()
	disposed := p.disposed
	p.gate.Unlock()
	if disposed {
		return errors.New("pipeline disposed")
	}
	p.cancelActivation()
	return p.wake.Stop(ctx)
}

func (p *VoicePipeline) onWakeWordDetected(WakeWordDetectedEventArgs) {
	p.gate.Lock()
	if p.disposed {
		p.gate.Unlock()
		return
	}
	p.gate.Unlock()

	// Cancel any previous activation still running, then start a new one.
	p.cancelActivation()

	ctx, cancel := context.WithCancel(context.Background())
	p.gate.Lock()
	p.activationStop = cancel
	p.gate.Unlock()

	go p.runActivation(ctx)
}

func (p *VoicePipeline) runActivation(ctx context.Context) {
	// When VAD is configured, pipe raw audio through it and pass only speech
	// segments to the transcriber. Otherwise forward the raw capture stream.
	var audioInput <-chan []byte
	if p.vad == nil {
		audioInput = p.capture.CaptureAsync(ctx)
	} else {
		audioInput = extractSpeechSegments(ctx, p.vad, p.capture.CaptureAsync(ctx))
	}

	// StreamTranscribe is subscribed to synchronously here (before the drain).
	partials := p.transcriber.StreamTranscribe(ctx, audioInput)
	result, ok := drainToFinal(ctx, partials)

	if ctx.Err() != nil {
		// Activation was cancelled (stop requested or new wake event). Swallow.
		return
	}

	if ok {
		p.fireTranscribed(TranscribedEventArgs{Result: result, CompletedAt: time.Now().UTC()})
	}
	// else: transcriber yielded no final result (silence/noise/premature cancel).
	// This is normal; no event is raised.
}

// extractSpeechSegments filters rawAudio through vad and yields only the audio
// bytes of speech segments. Ports VoicePipeline.ExtractSpeechSegmentsAsync.
func extractSpeechSegments(ctx context.Context, vad IVoiceActivityDetector, rawAudio <-chan []byte) <-chan []byte {
	out := make(chan []byte)
	go func() {
		defer close(out)
		segments := vad.Detect(ctx, rawAudio)
		for {
			select {
			case <-ctx.Done():
				return
			case seg, ok := <-segments:
				if !ok {
					return
				}
				if seg.IsSpeech {
					select {
					case out <- seg.Audio:
					case <-ctx.Done():
						return
					}
				}
			}
		}
	}()
	return out
}

// drainToFinal drains the partial-transcription stream and returns the final
// result. Returns (zero, false) if the stream produces no items. Ports
// ToFinalAsync (language is unknown at this layer -> "und").
func drainToFinal(ctx context.Context, source <-chan PartialTranscription) (VoiceTranscriptionResult, bool) {
	var last PartialTranscription
	var have bool
	for {
		select {
		case <-ctx.Done():
			if !have {
				return VoiceTranscriptionResult{}, false
			}
			return VoiceTranscriptionResult{Text: last.Text, Confidence: last.Confidence, LanguageCode: "und"}, true
		case partial, ok := <-source:
			if !ok {
				if !have {
					return VoiceTranscriptionResult{}, false
				}
				return VoiceTranscriptionResult{Text: last.Text, Confidence: last.Confidence, LanguageCode: "und"}, true
			}
			last = partial
			have = true
			if partial.IsFinal {
				return VoiceTranscriptionResult{Text: last.Text, Confidence: last.Confidence, LanguageCode: "und"}, true
			}
		}
	}
}

func (p *VoicePipeline) cancelActivation() {
	p.gate.Lock()
	cancel := p.activationStop
	p.activationStop = nil
	p.gate.Unlock()
	if cancel != nil {
		cancel()
	}
}

func (p *VoicePipeline) fireTranscribed(e TranscribedEventArgs) {
	p.gate.Lock()
	snapshot := make([]*transcribedSub, len(p.transcribedSubs))
	copy(snapshot, p.transcribedSubs)
	p.gate.Unlock()
	for _, s := range snapshot {
		s.handler(e)
	}
}

// fireActivationFailed is retained for parity with the C# ActivationFailed path;
// the deterministic transcribers used in-package do not surface errors mid-stream,
// but a host transcriber whose StreamTranscribe closure fails can be adapted to
// call this via a wrapper. It snapshots handlers under the gate and fires outside.
func (p *VoicePipeline) fireActivationFailed(err error) {
	p.gate.Lock()
	snapshot := make([]*failedSub, len(p.failedSubs))
	copy(snapshot, p.failedSubs)
	p.gate.Unlock()
	for _, s := range snapshot {
		s.handler(err)
	}
}

// Close disposes the pipeline and all collaborators. Ports DisposeAsync.
func (p *VoicePipeline) Close(ctx context.Context) error {
	p.gate.Lock()
	if p.disposed {
		p.gate.Unlock()
		return nil
	}
	p.disposed = true
	unsub := p.unsubWake
	p.gate.Unlock()

	if unsub != nil {
		unsub()
	}
	p.cancelActivation()

	var firstErr error
	if err := p.wake.Close(ctx); err != nil && firstErr == nil {
		firstErr = err
	}
	if err := p.transcriber.Close(ctx); err != nil && firstErr == nil {
		firstErr = err
	}
	if err := p.capture.Close(ctx); err != nil && firstErr == nil {
		firstErr = err
	}
	return firstErr
}
