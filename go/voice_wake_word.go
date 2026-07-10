// voice_wake_word.go
//
// Ports CircleAI.Voice.EnergyWakeWordDetector.cs — a wake-word detector that
// combines energy-based VAD with speech-to-text transcription. Audio is captured
// continuously via IAudioCapture, short speech segments are transcribed, and when
// a transcript contains the wake word (case-insensitive Contains) the
// WakeWordDetected event fires.
//
// CONCURRENCY SAFETY (this wave is stream/transport-heavy):
//   - The background listen loop is started synchronously from Start under the
//     gate; the loop consumes the capture stream through EnergyVadDetector.
//   - Fire snapshots subscribers UNDER the gate and invokes handlers OUTSIDE it,
//     so a handler that (un)subscribes cannot self-deadlock the detector.
//   - Subscriptions are an unbounded slice: a subscriber attached before Start
//     receives every fire produced after Start (no lost-before-subscribe race,
//     because callers subscribe first, then Start).

package circleai

import (
	"context"
	"errors"
	"strings"
	"sync"
	"time"
)

// EnergyWakeWordDetector detects a wake word by transcribing energy-gated speech
// segments captured from an IAudioCapture. Ports EnergyWakeWordDetector.
type EnergyWakeWordDetector struct {
	capture     IAudioCapture
	transcriber IVoiceTranscriber
	vad         *EnergyVadDetector
	wakeWord    string

	gate      sync.Mutex
	listening bool
	disposed  bool
	cancel    context.CancelFunc
	done      chan struct{}
	subs      []*voiceWakeSub
}

type voiceWakeSub struct {
	handler func(WakeWordDetectedEventArgs)
}

// NewEnergyWakeWordDetector constructs an energy wake-word detector. wakeWord
// defaults to "hey b" when empty; matching is case-insensitive Contains.
// energyThreshold tunes the VAD (see EnergyVadDetector). capture and transcriber
// must not be nil. Ports the EnergyWakeWordDetector constructor.
func NewEnergyWakeWordDetector(capture IAudioCapture, transcriber IVoiceTranscriber, wakeWord string, energyThreshold float32) (*EnergyWakeWordDetector, error) {
	if capture == nil {
		return nil, errors.New("capture required")
	}
	if transcriber == nil {
		return nil, errors.New("transcriber required")
	}
	if wakeWord == "" {
		wakeWord = "hey b"
	}
	if isBlank(wakeWord) {
		return nil, errors.New("wakeWord must not be whitespace")
	}
	vad, err := NewEnergyVadDetector(energyThreshold, 10, 640)
	if err != nil {
		return nil, err
	}
	return &EnergyWakeWordDetector{
		capture:     capture,
		transcriber: transcriber,
		vad:         vad,
		wakeWord:    strings.TrimSpace(wakeWord),
	}, nil
}

// WakeWord returns the configured wake word.
func (d *EnergyWakeWordDetector) WakeWord() string { return d.wakeWord }

// IsListening reports whether the background loop is running.
func (d *EnergyWakeWordDetector) IsListening() bool {
	d.gate.Lock()
	defer d.gate.Unlock()
	return d.listening
}

// Subscribe registers handler and returns an idempotent unsubscribe func. Call
// before Start so a fire produced right after Start is delivered.
func (d *EnergyWakeWordDetector) Subscribe(handler func(WakeWordDetectedEventArgs)) (unsubscribe func()) {
	if handler == nil {
		panic("handler must not be nil")
	}
	sub := &voiceWakeSub{handler: handler}
	d.gate.Lock()
	d.subs = append(d.subs, sub)
	d.gate.Unlock()

	var once sync.Once
	return func() {
		once.Do(func() {
			d.gate.Lock()
			for i, s := range d.subs {
				if s == sub {
					d.subs = append(d.subs[:i], d.subs[i+1:]...)
					break
				}
			}
			d.gate.Unlock()
		})
	}
}

// Start begins the background listening loop. Idempotent: calling when already
// listening has no effect.
func (d *EnergyWakeWordDetector) Start(ctx context.Context) error {
	d.gate.Lock()
	defer d.gate.Unlock()
	if d.disposed {
		return errors.New("detector disposed")
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	if d.listening {
		return nil
	}
	loopCtx, cancel := context.WithCancel(context.Background())
	d.cancel = cancel
	d.done = make(chan struct{})
	d.listening = true
	go d.listenLoop(loopCtx, d.done)
	return nil
}

// Stop cancels the background loop and waits for it to complete. Idempotent.
func (d *EnergyWakeWordDetector) Stop(ctx context.Context) error {
	d.gate.Lock()
	if d.disposed {
		d.gate.Unlock()
		return errors.New("detector disposed")
	}
	if !d.listening {
		d.gate.Unlock()
		return nil
	}
	cancel := d.cancel
	done := d.done
	d.listening = false
	d.cancel = nil
	d.done = nil
	d.gate.Unlock()

	if cancel != nil {
		cancel()
	}
	if done != nil {
		select {
		case <-done:
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	return nil
}

// Close disposes the detector, stopping the loop first.
func (d *EnergyWakeWordDetector) Close(ctx context.Context) error {
	_ = d.Stop(ctx)
	d.gate.Lock()
	d.disposed = true
	d.gate.Unlock()
	return nil
}

// listenLoop captures audio, runs VAD, transcribes speech segments, and fires
// WakeWordDetected when the phrase is found. It exits when loopCtx is cancelled
// or the capture stream ends, closing done on the way out.
func (d *EnergyWakeWordDetector) listenLoop(loopCtx context.Context, done chan struct{}) {
	defer close(done)
	defer func() {
		d.gate.Lock()
		d.listening = false
		d.gate.Unlock()
	}()

	audioStream := d.capture.CaptureAsync(loopCtx)
	segments := d.vad.Detect(loopCtx, audioStream)

	for {
		select {
		case <-loopCtx.Done():
			return
		case seg, ok := <-segments:
			if !ok {
				return
			}
			if !seg.IsSpeech || len(seg.Audio) == 0 {
				continue
			}

			res, err := d.transcriber.Transcribe(loopCtx, seg.Audio)
			if err != nil {
				if loopCtx.Err() != nil {
					return
				}
				// Transcription failed for this segment — skip and keep listening.
				continue
			}

			if isBlank(res.Text) {
				continue
			}

			if strings.Contains(strings.ToLower(res.Text), strings.ToLower(d.wakeWord)) {
				d.fire(WakeWordDetectedEventArgs{
					WakeWord:   d.wakeWord,
					DetectedAt: time.Now().UTC(),
					Confidence: res.Confidence,
				})
			}
		}
	}
}

// fire snapshots subscribers under the gate and invokes handlers outside it.
func (d *EnergyWakeWordDetector) fire(evt WakeWordDetectedEventArgs) {
	d.gate.Lock()
	snapshot := make([]*voiceWakeSub, len(d.subs))
	copy(snapshot, d.subs)
	d.gate.Unlock()

	for _, s := range snapshot {
		s.handler(evt)
	}
}

// Interface guard.
var _ IWakeWordDetector = (*EnergyWakeWordDetector)(nil)
