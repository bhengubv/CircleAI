// voice_pipeline_test.go
//
// Verifies voice_pipeline.go and voice_audio_capture.go end-to-end: NullAudioCapture,
// ScriptedAudioCapture, and the VoicePipeline activation flow (wake -> capture ->
// [VAD] -> transcribe -> Transcribed event). Concurrency invariants (subscribe in
// constructor before start; fan-out snapshot outside the lock) are exercised.

package circleai_test

import (
	"context"
	"sync"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestNullAudioCapture(t *testing.T) {
	c := circleai.NullAudioCapture{}
	if c.Format() != circleai.AudioFormatPcm16Mono16k {
		t.Errorf("format %+v", c.Format())
	}
	if got := drain(c.CaptureAsync(context.Background())); len(got) != 0 {
		t.Errorf("null capture yielded %d chunks", len(got))
	}
	_ = c.Close(context.Background())
}

func TestScriptedAudioCapture_YieldsThenCloses(t *testing.T) {
	c := circleai.NewScriptedAudioCapture(circleai.AudioFormatPcm16Mono16k, [][]byte{le16(1), le16(2), le16(3)})
	got := drain(c.CaptureAsync(context.Background()))
	if len(got) != 3 {
		t.Fatalf("scripted capture yielded %d chunks, want 3", len(got))
	}
	if int16(got[1][0]) != 2 && got[1][0] != le16(2)[0] {
		t.Errorf("chunk order wrong")
	}
}

func TestScriptedAudioCapture_LoopCancellable(t *testing.T) {
	c := circleai.NewScriptedAudioCapture(circleai.AudioFormatPcm16Mono16k, [][]byte{le16(1)}).WithLoop(true)
	ctx, cancel := context.WithCancel(context.Background())
	ch := c.CaptureAsync(ctx)
	// Read a few looped chunks, then cancel.
	for i := 0; i < 3; i++ {
		select {
		case <-ch:
		case <-timeoutC(2):
			t.Fatal("looping capture stalled")
		}
	}
	cancel()
	// Channel must drain and close.
	deadline := time.After(2 * time.Second)
	for {
		select {
		case _, ok := <-ch:
			if !ok {
				return
			}
		case <-deadline:
			t.Fatal("looping capture did not close after cancel")
		}
	}
}

func TestVoicePipeline_ActivationRaisesTranscribed(t *testing.T) {
	// Wake detector: an InMemoryWakeWordDetector-style manual fire via the Voice
	// EnergyWakeWordDetector is stream-driven; here we use a controllable fake to
	// isolate the pipeline activation path.
	wake := newFakeWake("hey b")
	// Transcriber returns a fixed final transcript once it sees any audio.
	tr := circleai.NewKeywordVoiceTranscriber("en")
	tr.Register(le16(5), "hello butler")
	capture := circleai.NewScriptedAudioCapture(circleai.AudioFormatPcm16Mono16k, [][]byte{le16(5, 0, 0, 0)})

	p, err := circleai.NewVoicePipeline(wake, tr, capture, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer p.Close(context.Background())

	var mu sync.Mutex
	var results []circleai.VoiceTranscriptionResult
	done := make(chan struct{}, 1)
	p.OnTranscribed(func(e circleai.TranscribedEventArgs) {
		mu.Lock()
		results = append(results, e.Result)
		mu.Unlock()
		select {
		case done <- struct{}{}:
		default:
		}
	})

	if err := p.Start(context.Background()); err != nil {
		t.Fatal(err)
	}
	wake.fire() // trigger an activation

	select {
	case <-done:
	case <-timeoutC(3):
		t.Fatal("pipeline did not raise Transcribed after wake")
	}

	mu.Lock()
	defer mu.Unlock()
	if len(results) == 0 || results[0].Text != "hello butler" {
		t.Errorf("transcribed result %+v", results)
	}
	if results[0].LanguageCode != "und" {
		t.Errorf("pipeline language should be 'und', got %q", results[0].LanguageCode)
	}
}

func TestVoicePipeline_WithVadFiltersToSpeech(t *testing.T) {
	wake := newFakeWake("hey b")
	tr := circleai.NewKeywordVoiceTranscriber("en")
	// The VAD segments carry the concatenated speech; register the loud marker.
	tr.Register(frame640(6000)[:2], "speech heard")
	vad, _ := circleai.NewEnergyVadDetector(0.02, 2, 640)

	loud := frame640(6000)
	silence := frame640(0)
	capture := circleai.NewScriptedAudioCapture(circleai.AudioFormatPcm16Mono16k,
		[][]byte{loud, loud, silence, silence})

	p, err := circleai.NewVoicePipeline(wake, tr, capture, vad, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer p.Close(context.Background())
	if p.VoiceActivityDetector() == nil {
		t.Error("VAD accessor nil")
	}

	done := make(chan circleai.VoiceTranscriptionResult, 1)
	p.OnTranscribed(func(e circleai.TranscribedEventArgs) {
		select {
		case done <- e.Result:
		default:
		}
	})
	_ = p.Start(context.Background())
	wake.fire()

	select {
	case r := <-done:
		if r.Text != "speech heard" {
			t.Errorf("vad-filtered transcript %q", r.Text)
		}
	case <-timeoutC(3):
		t.Fatal("pipeline+VAD did not raise Transcribed")
	}
}

func TestVoicePipeline_NewWakeCancelsPriorActivation(t *testing.T) {
	// Two fires in quick succession: the pipeline must not crash and must
	// ultimately raise at least one Transcribed. This exercises cancelActivation.
	wake := newFakeWake("hey b")
	tr := circleai.NewKeywordVoiceTranscriber("en").Register(le16(7), "ok")
	capture := circleai.NewScriptedAudioCapture(circleai.AudioFormatPcm16Mono16k, [][]byte{le16(7)}).WithLoop(true)
	p, _ := circleai.NewVoicePipeline(wake, tr, capture, nil, nil)
	defer p.Close(context.Background())

	got := make(chan struct{}, 4)
	p.OnTranscribed(func(circleai.TranscribedEventArgs) { got <- struct{}{} })
	_ = p.Start(context.Background())
	wake.fire()
	wake.fire()

	select {
	case <-got:
	case <-timeoutC(3):
		t.Fatal("no Transcribed after rapid double-fire")
	}
}

func TestVoicePipeline_RequiresWakeAndTranscriber(t *testing.T) {
	tr := &circleai.NullVoiceTranscriber{}
	if _, err := circleai.NewVoicePipeline(nil, tr, nil, nil, nil); err == nil {
		t.Error("nil wake should error")
	}
	if _, err := circleai.NewVoicePipeline(circleai.NewNullWakeWordDetector(), nil, nil, nil, nil); err == nil {
		t.Error("nil transcriber should error")
	}
	// nil capture is allowed (NullAudioCapture used).
	p, err := circleai.NewVoicePipeline(circleai.NewNullWakeWordDetector(), tr, nil, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if _, ok := p.AudioCapture().(circleai.NullAudioCapture); !ok {
		t.Error("nil capture should default to NullAudioCapture")
	}
	_ = p.Close(context.Background())
}

func TestVoicePipeline_ClosesCollaborators(t *testing.T) {
	wake := newFakeWake("hey b")
	tr := &circleai.NullVoiceTranscriber{}
	p, _ := circleai.NewVoicePipeline(wake, tr, circleai.NullAudioCapture{}, nil, nil)
	if err := p.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if !wake.closed {
		t.Error("wake detector not closed by pipeline")
	}
	// Double close is a no-op.
	if err := p.Close(context.Background()); err != nil {
		t.Errorf("double close errored: %v", err)
	}
}

// ── fakeWake: a controllable IWakeWordDetector for pipeline tests ──────────

type fakeWake struct {
	word   string
	mu     sync.Mutex
	subs   []func(circleai.WakeWordDetectedEventArgs)
	listen bool
	closed bool
}

func newFakeWake(word string) *fakeWake { return &fakeWake{word: word} }

func (f *fakeWake) WakeWord() string { return f.word }
func (f *fakeWake) IsListening() bool {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.listen
}
func (f *fakeWake) Subscribe(h func(circleai.WakeWordDetectedEventArgs)) func() {
	f.mu.Lock()
	f.subs = append(f.subs, h)
	idx := len(f.subs) - 1
	f.mu.Unlock()
	return func() {
		f.mu.Lock()
		f.subs[idx] = nil
		f.mu.Unlock()
	}
}
func (f *fakeWake) Start(context.Context) error {
	f.mu.Lock()
	f.listen = true
	f.mu.Unlock()
	return nil
}
func (f *fakeWake) Stop(context.Context) error {
	f.mu.Lock()
	f.listen = false
	f.mu.Unlock()
	return nil
}
func (f *fakeWake) Close(context.Context) error {
	f.mu.Lock()
	f.closed = true
	f.mu.Unlock()
	return nil
}
func (f *fakeWake) fire() {
	f.mu.Lock()
	snap := make([]func(circleai.WakeWordDetectedEventArgs), len(f.subs))
	copy(snap, f.subs)
	f.mu.Unlock()
	evt := circleai.WakeWordDetectedEventArgs{WakeWord: f.word, DetectedAt: time.Now().UTC(), Confidence: 1}
	for _, h := range snap {
		if h != nil {
			h(evt)
		}
	}
}

var _ circleai.IWakeWordDetector = (*fakeWake)(nil)
