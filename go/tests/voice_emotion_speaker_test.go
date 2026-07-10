// voice_emotion_speaker_test.go
//
// Verifies voice_emotion.go and voice_speaker_identity.go: the softmax + Russell
// circumplex mapping (via an injected logits runner and the deterministic hash
// runner), and the enroll/identify centroid + cosine-similarity threshold logic
// (via an injected embedder and the deterministic hash embedder).

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── Emotion ──────────────────────────────────────────────────────────────

type fixedLogits struct{ v []float32 }

func (f fixedLogits) ScoreLogits([]float32) []float32 { return f.v }

func TestEmotionDetector_TopClassMapsToCircumplex(t *testing.T) {
	cfg := circleai.SpeechEmotionConfig{SampleRateHz: 16000}
	// Labels default to [neutral, happy, angry, sad]; force class 1 = "happy".
	d := circleai.NewInMemorySpeechEmotionDetector(cfg, fixedLogits{v: []float32{0.1, 5.0, 0.2, 0.3}})
	frame, ok, err := d.Sense(context.Background(), le16(1, 2, 3, 4), 16000)
	if err != nil {
		t.Fatal(err)
	}
	if !ok {
		t.Fatal("expected a reading")
	}
	if frame.Label != "happy" {
		t.Errorf("label %q, want happy", frame.Label)
	}
	// Circumplex for happy = (0.55, 0.81).
	if frame.Arousal != 0.55 || frame.Valence != 0.81 {
		t.Errorf("circumplex %v/%v, want 0.55/0.81", frame.Arousal, frame.Valence)
	}
	if frame.Probability <= 0.9 {
		t.Errorf("winning prob should dominate: %v", frame.Probability)
	}
	_ = d.Close(context.Background())
}

func TestEmotionDetector_EmptyAndMismatch(t *testing.T) {
	d := circleai.NewInMemorySpeechEmotionDetector(circleai.SpeechEmotionConfig{SampleRateHz: 16000}, nil)
	if _, ok, _ := d.Sense(context.Background(), nil, 16000); ok {
		t.Error("empty audio should give no reading")
	}
	if _, ok, _ := d.Sense(context.Background(), le16(1, 2), 8000); ok {
		t.Error("sample-rate mismatch should give no reading")
	}
}

func TestEmotionDetector_HashRunnerDeterministic(t *testing.T) {
	d := circleai.NewInMemorySpeechEmotionDetector(circleai.SpeechEmotionConfig{SampleRateHz: 16000}, nil)
	audio := le16(500, -400, 300, -200, 100, 900, -900, 250)
	a, okA, _ := d.Sense(context.Background(), audio, 16000)
	b, okB, _ := d.Sense(context.Background(), audio, 16000)
	if !okA || !okB {
		t.Fatal("expected readings")
	}
	if a != b {
		t.Errorf("hash-runner emotion not deterministic: %+v vs %+v", a, b)
	}
	// Label must be one of the default classes.
	switch a.Label {
	case "neutral", "happy", "angry", "sad":
	default:
		t.Errorf("unexpected label %q", a.Label)
	}
}

func TestEmotionDetector_CustomLabelsUnknownMapsToZero(t *testing.T) {
	cfg := circleai.SpeechEmotionConfig{SampleRateHz: 16000, Labels: []string{"xyzzy"}}
	d := circleai.NewInMemorySpeechEmotionDetector(cfg, fixedLogits{v: []float32{9}})
	frame, ok, _ := d.Sense(context.Background(), le16(1, 2), 16000)
	if !ok {
		t.Fatal("expected reading")
	}
	if frame.Label != "xyzzy" || frame.Arousal != 0 || frame.Valence != 0 {
		t.Errorf("unknown label should map to (0,0): %+v", frame)
	}
}

// ── Speaker identity ─────────────────────────────────────────────────────

// A short utterance (>= 1000ms @ 16k = 16000 samples = 32000 bytes) is required.
func utterance(seedByteFill byte, samples int) []byte {
	b := make([]byte, samples*2)
	for i := range b {
		b[i] = seedByteFill + byte(i%13)
	}
	return b
}

func TestSpeakerIdentity_EnrollThenIdentify(t *testing.T) {
	cfg := circleai.SpeakerIdentityConfig{SampleRateHz: 16000, MinUtteranceMs: 1000, MaxUtteranceMs: 8000, MatchThreshold: 0.5}
	s := circleai.NewInMemorySpeakerIdentity(cfg, nil) // deterministic hash embedder

	ctx := context.Background()
	// No enrollees -> no identification.
	if id, ok, _ := s.Identify(ctx, utterance(10, 16000), 16000); ok || id != "" {
		t.Errorf("identify with no enrollees returned %q/%v", id, ok)
	}

	alice := utterance(10, 16000)
	if err := s.Enroll(ctx, "alice", alice, 16000); err != nil {
		t.Fatal(err)
	}
	if s.EnrolledCount() != 1 {
		t.Errorf("enrolled count %d", s.EnrolledCount())
	}

	// The same utterance must identify as alice (self-similarity = 1 >= threshold).
	id, ok, err := s.Identify(ctx, alice, 16000)
	if err != nil {
		t.Fatal(err)
	}
	if !ok || id != "alice" {
		t.Errorf("self-identify got %q/%v, want alice/true", id, ok)
	}
	_ = s.Close(ctx)
}

func TestSpeakerIdentity_DistinguishesSpeakers(t *testing.T) {
	cfg := circleai.SpeakerIdentityConfig{SampleRateHz: 16000, MatchThreshold: 0.9}
	s := circleai.NewInMemorySpeakerIdentity(cfg, nil)
	ctx := context.Background()

	alice := utterance(10, 16000)
	bob := utterance(200, 16000) // very different byte pattern
	_ = s.Enroll(ctx, "alice", alice, 16000)
	_ = s.Enroll(ctx, "bob", bob, 16000)

	id, ok, _ := s.Identify(ctx, alice, 16000)
	if !ok || id != "alice" {
		t.Errorf("alice audio identified as %q/%v", id, ok)
	}
	id2, ok2, _ := s.Identify(ctx, bob, 16000)
	if !ok2 || id2 != "bob" {
		t.Errorf("bob audio identified as %q/%v", id2, ok2)
	}
}

func TestSpeakerIdentity_RunningCentroidAndSampleCount(t *testing.T) {
	cfg := circleai.SpeakerIdentityConfig{SampleRateHz: 16000, MatchThreshold: 0.5}
	s := circleai.NewInMemorySpeakerIdentity(cfg, nil)
	ctx := context.Background()
	a1 := utterance(10, 16000)
	a2 := utterance(11, 16000)
	if err := s.Enroll(ctx, "alice", a1, 16000); err != nil {
		t.Fatal(err)
	}
	if err := s.Enroll(ctx, "alice", a2, 16000); err != nil {
		t.Fatal(err)
	}
	// Still one enrolled user (centroid averaged, SampleCount incremented).
	if s.EnrolledCount() != 1 {
		t.Errorf("enrolled count after re-enroll = %d, want 1", s.EnrolledCount())
	}
}

func TestSpeakerIdentity_Validation(t *testing.T) {
	s := circleai.NewInMemorySpeakerIdentity(circleai.SpeakerIdentityConfig{SampleRateHz: 16000}, nil)
	ctx := context.Background()
	if err := s.Enroll(ctx, "  ", utterance(1, 16000), 16000); err == nil {
		t.Error("blank userId should error")
	}
	if err := s.Enroll(ctx, "alice", nil, 16000); err == nil {
		t.Error("empty audio should error")
	}
	// Too-short utterance (< MinUtteranceMs) -> embedding failure -> enroll errors.
	if err := s.Enroll(ctx, "alice", le16(1, 2), 16000); err == nil {
		t.Error("too-short utterance should error on enroll")
	}
	// Sample-rate mismatch on identify -> no ID (not an error).
	_ = s.Enroll(ctx, "alice", utterance(1, 16000), 16000)
	if id, ok, err := s.Identify(ctx, utterance(1, 16000), 8000); err != nil || ok || id != "" {
		t.Errorf("mismatch identify %q/%v/%v", id, ok, err)
	}
}

func TestSpeakerIdentity_InjectedEmbedder(t *testing.T) {
	// Injected embedder proves the ONNX seam works; identical vectors -> match.
	emb := constEmbedder{vec: []float32{1, 0, 0, 0}}
	cfg := circleai.SpeakerIdentityConfig{SampleRateHz: 16000, MinUtteranceMs: 1000, MatchThreshold: 0.5}
	s := circleai.NewInMemorySpeakerIdentity(cfg, emb)
	ctx := context.Background()
	_ = s.Enroll(ctx, "u", utterance(1, 16000), 16000)
	id, ok, _ := s.Identify(ctx, utterance(50, 16000), 16000)
	if !ok || id != "u" {
		t.Errorf("injected-embedder identify %q/%v", id, ok)
	}
}

type constEmbedder struct{ vec []float32 }

func (c constEmbedder) Embed([]float32) []float32 { return append([]float32(nil), c.vec...) }
