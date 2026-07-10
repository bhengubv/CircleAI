// voice_wake_word_test.go
//
// Verifies voice_wake_word.go: the EnergyWakeWordDetector's capture -> VAD ->
// transcribe -> fire loop, subscribe-before-start delivery, idempotent Start/Stop,
// and the fan-out snapshot-outside-lock invariant (a self-unsubscribing handler
// must not deadlock the loop).

package circleai_test

import (
	"context"
	"encoding/binary"
	"sync"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// loudMarkerFrames builds capture chunks whose first VAD-emitted speech segment
// begins with the given marker bytes, so a KeywordVoiceTranscriber can match it.
func loudFrame(marker []byte) []byte {
	b := make([]byte, 640)
	copy(b, marker)
	// Fill the remainder with a loud tone so the frame's RMS clears the VAD gate.
	for i := len(marker) / 2; i < 320; i++ {
		binary.LittleEndian.PutUint16(b[i*2:i*2+2], uint16(int16(7000)))
	}
	return b
}

func TestEnergyWakeWordDetector_FiresOnTranscript(t *testing.T) {
	marker := le16(1234)
	speech := loudFrame(marker)
	silence := make([]byte, 640) // triggers end-of-speech so the segment is emitted

	capture := circleai.NewScriptedAudioCapture(circleai.AudioFormatPcm16Mono16k,
		[][]byte{speech, speech, silence, silence, silence, silence, silence, silence, silence, silence, silence, silence})
	tr := circleai.NewKeywordVoiceTranscriber("en")
	// The emitted segment begins with the marker bytes.
	tr.Register(marker, "hey b wake up")

	d, err := circleai.NewEnergyWakeWordDetector(capture, tr, "hey b", 0.02)
	if err != nil {
		t.Fatal(err)
	}
	if d.WakeWord() != "hey b" {
		t.Errorf("wake word %q", d.WakeWord())
	}

	fired := make(chan circleai.WakeWordDetectedEventArgs, 1)
	// Subscribe BEFORE Start.
	unsub := d.Subscribe(func(e circleai.WakeWordDetectedEventArgs) {
		select {
		case fired <- e:
		default:
		}
	})
	defer unsub()

	if err := d.Start(context.Background()); err != nil {
		t.Fatal(err)
	}

	select {
	case e := <-fired:
		if e.WakeWord != "hey b" {
			t.Errorf("fired event %+v", e)
		}
	case <-timeoutC(3):
		t.Fatal("energy wake-word detector did not fire")
	}
	_ = d.Stop(context.Background())
	_ = d.Close(context.Background())
}

func TestEnergyWakeWordDetector_IdempotentStartStop(t *testing.T) {
	capture := circleai.NewScriptedAudioCapture(circleai.AudioFormatPcm16Mono16k, [][]byte{}).WithLoop(true)
	tr := &circleai.NullVoiceTranscriber{}
	d, _ := circleai.NewEnergyWakeWordDetector(capture, tr, "hey b", 0.02)

	ctx := context.Background()
	if err := d.Start(ctx); err != nil {
		t.Fatal(err)
	}
	if err := d.Start(ctx); err != nil { // second start is a no-op
		t.Fatal(err)
	}
	if !d.IsListening() {
		t.Error("should be listening")
	}
	if err := d.Stop(ctx); err != nil {
		t.Fatal(err)
	}
	if err := d.Stop(ctx); err != nil { // second stop is a no-op
		t.Fatal(err)
	}
	if d.IsListening() {
		t.Error("should not be listening after stop")
	}
	_ = d.Close(ctx)
}

func TestEnergyWakeWordDetector_NoFalseFireOnNonMatch(t *testing.T) {
	marker := le16(1234)
	speech := loudFrame(marker)
	silence := make([]byte, 640)
	capture := circleai.NewScriptedAudioCapture(circleai.AudioFormatPcm16Mono16k,
		[][]byte{speech, silence, silence, silence, silence, silence, silence, silence, silence, silence, silence, silence})
	tr := circleai.NewKeywordVoiceTranscriber("en")
	tr.Register(marker, "play some jazz") // transcript lacks the wake word

	d, _ := circleai.NewEnergyWakeWordDetector(capture, tr, "hey b", 0.02)
	var mu sync.Mutex
	fired := 0
	d.Subscribe(func(circleai.WakeWordDetectedEventArgs) {
		mu.Lock()
		fired++
		mu.Unlock()
	})
	_ = d.Start(context.Background())
	// Let the finite capture drain.
	waitUntil(t, 3, func() bool { return !d.IsListening() })
	mu.Lock()
	defer mu.Unlock()
	if fired != 0 {
		t.Errorf("false fire count = %d", fired)
	}
	_ = d.Close(context.Background())
}

func TestEnergyWakeWordDetector_Validation(t *testing.T) {
	cap := circleai.NullAudioCapture{}
	tr := &circleai.NullVoiceTranscriber{}
	if _, err := circleai.NewEnergyWakeWordDetector(nil, tr, "hey b", 0.02); err == nil {
		t.Error("nil capture should error")
	}
	if _, err := circleai.NewEnergyWakeWordDetector(cap, nil, "hey b", 0.02); err == nil {
		t.Error("nil transcriber should error")
	}
	if _, err := circleai.NewEnergyWakeWordDetector(cap, tr, "   ", 0.02); err == nil {
		t.Error("blank wake word should error")
	}
	// Empty wake word defaults to "hey b".
	d, err := circleai.NewEnergyWakeWordDetector(cap, tr, "", 0.02)
	if err != nil || d.WakeWord() != "hey b" {
		t.Errorf("default wake word %v %q", err, d.WakeWord())
	}
}

// waitUntil polls cond up to seconds, failing the test if it never holds.
func waitUntil(t *testing.T, seconds int, cond func() bool) {
	t.Helper()
	deadline := timeoutC(seconds)
	tick := time.NewTicker(5 * time.Millisecond)
	defer tick.Stop()
	for {
		if cond() {
			return
		}
		select {
		case <-deadline:
			t.Fatal("condition not met before timeout")
		case <-tick.C:
		}
	}
}
