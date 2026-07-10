// speech_dsp_test.go
//
// Verifies the CircleAI.Speech DSP primitives: echo cancellers (null / NLMS /
// WebRTC-with-runner), noise reducers (null / spectral-subtraction gate / Krisp /
// DeepFilterNet), and the per-frame VAD (null / energy / silero-with-runner).
// Backend ids, fallback behaviour, and the deterministic arithmetic are checked.

package circleai_test

import (
	"encoding/binary"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func le16(samples ...int16) []byte {
	b := make([]byte, len(samples)*2)
	for i, s := range samples {
		binary.LittleEndian.PutUint16(b[i*2:i*2+2], uint16(s))
	}
	return b
}

// ── Echo cancellers ──────────────────────────────────────────────────────

func TestNullEchoCanceller_PassThrough(t *testing.T) {
	c := circleai.NullEchoCancellerInstance
	if c.BackendID() != "null" {
		t.Errorf("backend %q", c.BackendID())
	}
	near := le16(100, 200, 300)
	far := le16(9, 9, 9)
	dst := make([]byte, len(near))
	n := c.Cancel(near, far, 16000, dst)
	if n != len(near) {
		t.Errorf("wrote %d want %d", n, len(near))
	}
	for i := range near {
		if dst[i] != near[i] {
			t.Errorf("byte %d changed", i)
		}
	}
	c.Reset() // no-op, must not panic
}

func TestNlmsEchoCanceller_ConvergesTowardCancellation(t *testing.T) {
	c := circleai.NewDefaultNlmsEchoCanceller()
	if c.BackendID() != "nlms" {
		t.Errorf("backend %q", c.BackendID())
	}
	// Mic == far (pure echo). After adapting over many samples the residual
	// energy should shrink below the input energy.
	const n = 2048
	near := make([]byte, n*2)
	far := make([]byte, n*2)
	for i := 0; i < n; i++ {
		v := int16(8000 * sinApprox(float64(i)*0.2))
		binary.LittleEndian.PutUint16(near[i*2:i*2+2], uint16(v))
		binary.LittleEndian.PutUint16(far[i*2:i*2+2], uint16(v))
	}
	dst := make([]byte, n*2)
	c.Cancel(near, far, 16000, dst)

	// Compare residual energy of the SECOND HALF (after the filter has adapted)
	// against the input energy over the same region.
	half := n // byte offset of the midpoint (n samples * 2 bytes / 2)
	inE := energy(near[half:])
	outE := energy(dst[half:])
	if outE >= inE {
		t.Errorf("NLMS did not attenuate echo: inE=%.1f outE=%.1f", inE, outE)
	}
}

func TestNlmsEchoCanceller_ResetClearsState(t *testing.T) {
	c := circleai.NewDefaultNlmsEchoCanceller()
	near := le16(5000, -5000, 5000, -5000)
	far := le16(5000, -5000, 5000, -5000)
	dst := make([]byte, len(near))
	c.Cancel(near, far, 16000, dst)
	c.Reset()
	// After reset, first-sample output for a fresh identical call must equal the
	// very first output of a brand-new canceller (deterministic cold start).
	fresh := circleai.NewDefaultNlmsEchoCanceller()
	dst2a := make([]byte, len(near))
	dst2b := make([]byte, len(near))
	c.Cancel(near, far, 16000, dst2a)
	fresh.Cancel(near, far, 16000, dst2b)
	if !bytesEqual(dst2a, dst2b) {
		t.Errorf("post-reset output differs from cold-start output")
	}
}

func TestNlmsEchoCanceller_LengthMismatchPanics(t *testing.T) {
	defer func() {
		if recover() == nil {
			t.Error("expected panic on length mismatch")
		}
	}()
	c := circleai.NewDefaultNlmsEchoCanceller()
	c.Cancel(le16(1, 2), le16(1), 16000, make([]byte, 4))
}

type fakeAecRunner struct{ reset int }

func (r *fakeAecRunner) Process(near, _ []byte, _ int, dst []byte) int {
	// Invert the near-end to prove the runner (not the fallback) ran.
	for i := 0; i+1 < len(near); i += 2 {
		s := int16(binary.LittleEndian.Uint16(near[i : i+2]))
		binary.LittleEndian.PutUint16(dst[i:i+2], uint16(-s))
	}
	return len(near)
}
func (r *fakeAecRunner) Reset() { r.reset++ }

func TestWebRtcEchoCanceller_UsesRunnerWhenPresent(t *testing.T) {
	nilC := circleai.NewWebRtcEchoCanceller(nil)
	if nilC.BackendID() != "webrtc-aec3 (fallback)" {
		t.Errorf("nil-runner backend %q", nilC.BackendID())
	}
	r := &fakeAecRunner{}
	c := circleai.NewWebRtcEchoCanceller(r)
	if c.BackendID() != "webrtc-aec3" {
		t.Errorf("runner backend %q", c.BackendID())
	}
	near := le16(100, 200)
	dst := make([]byte, len(near))
	c.Cancel(near, le16(0, 0), 16000, dst)
	got := int16(binary.LittleEndian.Uint16(dst[0:2]))
	if got != -100 {
		t.Errorf("runner not used, got %d want -100", got)
	}
	c.Reset()
	if r.reset != 1 {
		t.Errorf("runner Reset called %d times", r.reset)
	}
}

// ── Noise reducers ───────────────────────────────────────────────────────

func TestNullNoiseReducer_PassThrough(t *testing.T) {
	r := circleai.NullNoiseReducerInstance
	if r.BackendID() != "null" || !r.IsAvailable() {
		t.Fatalf("backend/avail: %q %v", r.BackendID(), r.IsAvailable())
	}
	in := le16(1, 2, 3)
	dst := make([]byte, len(in))
	if n := r.Reduce(in, 16000, dst); n != len(in) || !bytesEqual(in, dst) {
		t.Errorf("null reducer changed data")
	}
}

func TestSpectralSubtraction_GatesBelowFloor(t *testing.T) {
	r := circleai.NewDefaultSpectralSubtractionNoiseReducer()
	if r.BackendID() != "passthrough" {
		t.Errorf("backend %q", r.BackendID())
	}
	// floor = 0.008 * 32767 ~= 262. A sample of magnitude 100 is below floor and
	// gets * 0.25 -> 25. A sample of 10000 passes unchanged.
	in := le16(100, 10000, -100)
	dst := make([]byte, len(in))
	r.Reduce(in, 16000, dst)
	got := readPcm16(dst)
	if got[0] != int16(float32(100)*0.25) {
		t.Errorf("below-floor not attenuated: %d", got[0])
	}
	if got[1] != 10000 {
		t.Errorf("above-floor changed: %d", got[1])
	}
	if got[2] != int16(float32(-100)*0.25) {
		t.Errorf("below-floor negative not attenuated: %d", got[2])
	}
}

type fakeNrRunner struct{}

func (fakeNrRunner) Process(in []byte, _ int, dst []byte) int {
	for i := range in {
		dst[i] = 0 // zero everything, proving the runner ran
	}
	return len(in)
}

func TestKrispAndDeepFilterNet_RunnerVsFallback(t *testing.T) {
	kNil := circleai.NewKrispNoiseReducer(nil)
	if kNil.BackendID() != "krisp (fallback)" {
		t.Errorf("krisp nil backend %q", kNil.BackendID())
	}
	k := circleai.NewKrispNoiseReducer(fakeNrRunner{})
	if k.BackendID() != "krisp" {
		t.Errorf("krisp backend %q", k.BackendID())
	}
	in := le16(9, 9)
	dst := make([]byte, len(in))
	k.Reduce(in, 16000, dst)
	if dst[0] != 0 || dst[1] != 0 {
		t.Errorf("krisp runner not used")
	}

	dNil := circleai.NewDeepFilterNetNoiseReducer(nil)
	if dNil.BackendID() != "deepfilternet (fallback)" {
		t.Errorf("dfn nil backend %q", dNil.BackendID())
	}
	d := circleai.NewDeepFilterNetNoiseReducer(fakeNrRunner{})
	if d.BackendID() != "deepfilternet" {
		t.Errorf("dfn backend %q", d.BackendID())
	}
}

// ── Per-frame VAD ────────────────────────────────────────────────────────

func TestNullSpeechVad_AlwaysSpeech(t *testing.T) {
	d := circleai.NullSpeechVoiceActivityDetectorInstance
	if d.BackendID() != "null" || d.SpeechThreshold() != 0.5 {
		t.Fatalf("null vad backend/thr: %q %v", d.BackendID(), d.SpeechThreshold())
	}
	r := d.Classify(le16(0, 0), 16000, 5*time.Millisecond)
	if !r.IsSpeech || r.SpeechProbability != 1 || r.Offset != 5*time.Millisecond {
		t.Errorf("null vad result %+v", r)
	}
}

func TestEnergyVad_SilenceVsSpeechAndHangover(t *testing.T) {
	d := circleai.NewDefaultEnergyVoiceActivityDetector()
	if d.BackendID() != "energy" {
		t.Errorf("backend %q", d.BackendID())
	}
	// Silence frame: all zeros -> low RMS -> not speech.
	silence := make([]byte, 320*2)
	if r := d.Classify(silence, 16000, 0); r.IsSpeech {
		t.Errorf("silence classified as speech: %+v", r)
	}
	// Loud alternating frame: high RMS + moderate ZCR -> speech.
	loud := make([]byte, 320*2)
	for i := 0; i < 320; i++ {
		v := int16(6000)
		if i%4 < 2 {
			v = -6000
		}
		binary.LittleEndian.PutUint16(loud[i*2:i*2+2], uint16(v))
	}
	r := d.Classify(loud, 16000, 0)
	if !r.IsSpeech {
		t.Fatalf("loud frame not speech: %+v", r)
	}
	// Immediately after, a silence frame should still be speech due to hangover.
	if r2 := d.Classify(silence, 16000, 0); !r2.IsSpeech {
		t.Errorf("hangover did not hold speech: %+v", r2)
	}
	d.Reset()
	if r3 := d.Classify(silence, 16000, 0); r3.IsSpeech {
		t.Errorf("after reset silence should not be speech: %+v", r3)
	}
}

func TestEnergyVad_ShortFrame(t *testing.T) {
	d := circleai.NewDefaultEnergyVoiceActivityDetector()
	if r := d.Classify([]byte{0x01}, 16000, 0); r.IsSpeech || r.SpeechProbability != 0 {
		t.Errorf("sub-2-byte frame should be non-speech zero: %+v", r)
	}
}

type fakeVadRunner struct{ score float32 }

func (r fakeVadRunner) ScoreFrame([]byte, int) float32 { return r.score }

func TestSileroVad_RunnerVsFallback(t *testing.T) {
	nilD := circleai.NewDefaultSileroVoiceActivityDetector(nil)
	if nilD.BackendID() != "silero (fallback)" {
		t.Errorf("nil backend %q", nilD.BackendID())
	}
	d := circleai.NewDefaultSileroVoiceActivityDetector(fakeVadRunner{score: 0.9})
	if d.BackendID() != "silero" {
		t.Errorf("backend %q", d.BackendID())
	}
	r := d.Classify(le16(0, 0), 16000, 0)
	if !r.IsSpeech || r.SpeechProbability != 0.9 {
		t.Errorf("silero runner result %+v", r)
	}
	// Below threshold with prior speech -> hangover holds it speech.
	low := circleai.NewSileroVoiceActivityDetector(fakeVadRunner{score: 0.1}, 0.5, 2)
	_ = low // fresh detector: first low frame is not speech
	if r0 := low.Classify(le16(0, 0), 16000, 0); r0.IsSpeech {
		t.Errorf("cold low-score frame should not be speech: %+v", r0)
	}
}

// ── helpers ──────────────────────────────────────────────────────────────

func bytesEqual(a, b []byte) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

func energy(b []byte) float64 {
	var e float64
	for i := 0; i+1 < len(b); i += 2 {
		s := float64(int16(binary.LittleEndian.Uint16(b[i : i+2])))
		e += s * s
	}
	return e
}

// sinApprox is a tiny sine for generating test tones without importing math into
// the test's hot path (kept dependency-light; accuracy is irrelevant here).
func sinApprox(x float64) float64 {
	// Reduce to [-pi, pi] crudely then use a 5th-order Taylor approximation.
	for x > 3.14159265 {
		x -= 2 * 3.14159265
	}
	for x < -3.14159265 {
		x += 2 * 3.14159265
	}
	x2 := x * x
	return x * (1 - x2/6 + x2*x2/120)
}
