// biometric_matcher_test.go
//
// Validates BiometricMatcher (CosineSimilarity + IsMatch) and FaceAffectMapper
// against all vectors in fixtures/facex_biometric_vectors.json.
// Float comparisons use the per-vector tolerance field (typically 1e-5 or 1e-4).

package circleai_test

import (
	"encoding/json"
	"errors"
	"math"
	"os"
	"path/filepath"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ---------------------------------------------------------------------------
// Fixture types
// ---------------------------------------------------------------------------

type biometricFixture struct {
	CosineSimilarityVectors []cosineVector       `json:"cosine_similarity_vectors"`
	AffectMapperVectors     []affectMapperVector `json:"affect_mapper_vectors"`
}

type cosineVector struct {
	ID                            string    `json:"id"`
	Description                   string    `json:"description"`
	A                             []float32 `json:"a"`
	B                             []float32 `json:"b"`
	ExpectedSimilarity            float64   `json:"expected_similarity"`
	Tolerance                     float64   `json:"tolerance"`
	ExpectedIsMatchAtThreshold085 *bool     `json:"expected_is_match_at_threshold_0_85"`
}

type affectMapperVector struct {
	ID             string     `json:"id"`
	Description    string     `json:"description"`
	InitialAffect  affectDims `json:"initial_affect"`
	Expression     string     `json:"expression"`
	Confidence     float32    `json:"confidence"`
	ExpectedAffect affectDims `json:"expected_affect"`
	Tolerance      float64    `json:"tolerance"`
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

func loadBiometricFixture(t *testing.T) biometricFixture {
	t.Helper()
	path := filepath.Join(fixturesDir(t), "facex_biometric_vectors.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("failed to read facex_biometric_vectors.json: %v", err)
	}
	var fix biometricFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("failed to parse facex_biometric_vectors.json: %v", err)
	}
	return fix
}

func expressionFromString(s string) circleai.FaceExpressionClassification {
	switch s {
	case "Happy":
		return circleai.FaceExpressionHappy
	case "Sad":
		return circleai.FaceExpressionSad
	case "Surprised":
		return circleai.FaceExpressionSurprised
	case "Confused":
		return circleai.FaceExpressionConfused
	case "Stressed":
		return circleai.FaceExpressionStressed
	case "Angry":
		return circleai.FaceExpressionAngry
	case "Neutral":
		return circleai.FaceExpressionNeutral
	default:
		return circleai.FaceExpressionUnknown
	}
}

// ---------------------------------------------------------------------------
// CosineSimilarity tests
// ---------------------------------------------------------------------------

func TestCosineSimilarity_Vectors(t *testing.T) {
	fix := loadBiometricFixture(t)

	if len(fix.CosineSimilarityVectors) == 0 {
		t.Fatal("no cosine_similarity_vectors in fixture")
	}

	for _, v := range fix.CosineSimilarityVectors {
		v := v
		t.Run(v.ID, func(t *testing.T) {
			got, err := circleai.CosineSimilarity(v.A, v.B)
			if err != nil {
				t.Fatalf("CosineSimilarity: unexpected error: %v", err)
			}
			if math.Abs(got-v.ExpectedSimilarity) > v.Tolerance {
				t.Errorf("CosineSimilarity: got %v, want %v (tolerance %v)",
					got, v.ExpectedSimilarity, v.Tolerance)
			}
		})
	}
}

func TestIsMatch_Vectors(t *testing.T) {
	fix := loadBiometricFixture(t)
	const defaultThreshold = float32(0.85)

	for _, v := range fix.CosineSimilarityVectors {
		v := v
		// IsMatch is deterministically similarity >= threshold (mirrors C#
		// BiometricMatcher.IsMatch). Derive the expectation from the golden
		// expected_similarity, honouring an explicit per-vector override when
		// the fixture provides the optional is_match field.
		t.Run(v.ID+"/IsMatch", func(t *testing.T) {
			profile := circleai.BiometricProfile{
				IdentityID:      "test",
				EmbeddingVector: v.B,
				MatchThreshold:  defaultThreshold,
				EnrolledAt:      time.Now(),
			}
			want := v.ExpectedSimilarity >= float64(defaultThreshold)
			if v.ExpectedIsMatchAtThreshold085 != nil {
				want = *v.ExpectedIsMatchAtThreshold085
			}
			got, err := circleai.IsMatch(v.A, profile)
			if err != nil {
				t.Fatalf("IsMatch: unexpected error: %v", err)
			}
			if got != want {
				sim, _ := circleai.CosineSimilarity(v.A, v.B)
				t.Errorf("IsMatch: got %v, want %v (sim=%.6f, threshold=%.2f)",
					got, want, sim, defaultThreshold)
			}
		})
	}
}

// ---------------------------------------------------------------------------
// CosineSimilarity edge cases
// ---------------------------------------------------------------------------

func TestCosineSimilarity_EmptySlices(t *testing.T) {
	// Empty but EQUAL length stays 0 with no error, matching C#, Python,
	// TypeScript and C. Only a LENGTH MISMATCH is refused.
	for _, pair := range [][2][]float32{{nil, nil}, {{}, {}}} {
		got, err := circleai.CosineSimilarity(pair[0], pair[1])
		if err != nil {
			t.Fatalf("empty slices: unexpected error: %v", err)
		}
		if got != 0 {
			t.Errorf("empty slices: got %v, want 0", got)
		}
	}
}

func TestCosineSimilarity_LengthMismatch(t *testing.T) {
	// Was: expected 0. Scoring embeddings of different dimensions is a
	// false-match path, and 0 reads as "not this person" when the honest
	// answer is "these came from different models".
	a := []float32{1, 0}
	b := []float32{1, 0, 0}
	got, err := circleai.CosineSimilarity(a, b)
	if !errors.Is(err, circleai.ErrEmbeddingDimensionMismatch) {
		t.Fatalf("length mismatch: got error %v, want ErrEmbeddingDimensionMismatch", err)
	}
	if got != 0 {
		t.Errorf("length mismatch: got %v alongside the error, want 0", got)
	}
}

func TestIsMatch_LengthMismatch(t *testing.T) {
	// A wrong-sized embedding is a model mismatch, not a failed match.
	profile := circleai.BiometricProfile{
		IdentityID:      "test",
		EmbeddingVector: []float32{1, 0, 0},
		MatchThreshold:  0.85,
		EnrolledAt:      time.Now(),
	}
	got, err := circleai.IsMatch([]float32{1, 0}, profile)
	if !errors.Is(err, circleai.ErrEmbeddingDimensionMismatch) {
		t.Fatalf("IsMatch: got error %v, want ErrEmbeddingDimensionMismatch", err)
	}
	if got {
		t.Error("IsMatch: a refused comparison must not report a match")
	}
}

func TestCosineSimilarity_ZeroVector(t *testing.T) {
	a := []float32{0, 0, 0}
	b := []float32{1, 0, 0}
	got, err := circleai.CosineSimilarity(a, b)
	if err != nil {
		t.Fatalf("zero vector: unexpected error: %v", err)
	}
	if got != 0 {
		t.Errorf("zero vector: got %v, want 0", got)
	}
}

// ---------------------------------------------------------------------------
// FaceAffectMapper tests
// ---------------------------------------------------------------------------

func TestFaceAffectMapper_Vectors(t *testing.T) {
	fix := loadBiometricFixture(t)

	if len(fix.AffectMapperVectors) == 0 {
		t.Fatal("no affect_mapper_vectors in fixture")
	}

	for _, v := range fix.AffectMapperVectors {
		v := v
		t.Run(v.ID, func(t *testing.T) {
			state := circleai.AffectState{
				UserID:         "test",
				LastUpdatedUTC: time.Now().UTC(),
				Curiosity:      float32(v.InitialAffect.Curiosity),
				Engagement:     float32(v.InitialAffect.Engagement),
				Uncertainty:    float32(v.InitialAffect.Uncertainty),
				Rapport:        float32(v.InitialAffect.Rapport),
				Energy:         float32(v.InitialAffect.Energy),
			}

			matrix := circleai.FacialMetricMatrix{
				Expression:      expressionFromString(v.Expression),
				ConfidenceScore: v.Confidence,
				CapturedAt:      time.Now().UTC(),
			}

			circleai.ApplyFaceAffect(&matrix, &state)

			eps := v.Tolerance
			assertDim(t, v.ID, "curiosity", state.Curiosity, v.ExpectedAffect.Curiosity, eps)
			assertDim(t, v.ID, "engagement", state.Engagement, v.ExpectedAffect.Engagement, eps)
			assertDim(t, v.ID, "uncertainty", state.Uncertainty, v.ExpectedAffect.Uncertainty, eps)
			assertDim(t, v.ID, "rapport", state.Rapport, v.ExpectedAffect.Rapport, eps)
			assertDim(t, v.ID, "energy", state.Energy, v.ExpectedAffect.Energy, eps)
		})
	}
}

// ---------------------------------------------------------------------------
// BiometricProfile
// ---------------------------------------------------------------------------

func TestBiometricProfile_EmbeddingDimension(t *testing.T) {
	p := circleai.BiometricProfile{
		IdentityID:      "id-1",
		EmbeddingVector: make([]float32, 128),
		MatchThreshold:  0.85,
		EnrolledAt:      time.Now(),
	}
	if p.EmbeddingDimension() != 128 {
		t.Errorf("EmbeddingDimension: got %d, want 128", p.EmbeddingDimension())
	}
}

// ---------------------------------------------------------------------------
// FaceCompanionBridge
// ---------------------------------------------------------------------------

func TestFaceCompanionBridge_DefaultThreshold(t *testing.T) {
	b := circleai.NewFaceCompanionBridge()
	const want = float32(0.70)
	if b.ConfusionThreshold != want {
		t.Errorf("ConfusionThreshold: got %v, want %v", b.ConfusionThreshold, want)
	}
}

func TestFaceCompanionBridge_Observe_ConfusedTriggersEvent(t *testing.T) {
	b := circleai.NewFaceCompanionBridge()

	state := circleai.AffectState{
		UserID:         "user-1",
		LastUpdatedUTC: time.Now().UTC(),
		Curiosity:      0.5,
		Engagement:     0.5,
		Uncertainty:    0.68, // just below threshold; confused will push it to 0.73
		Rapport:        0.0,
		Energy:         0.5,
	}

	matrix := circleai.FacialMetricMatrix{
		Expression:      circleai.FaceExpressionConfused,
		ConfidenceScore: 0.85,
		CapturedAt:      time.Now().UTC(),
	}

	evt := b.Observe(&matrix, &state, "sess-1", "id-1", circleai.InterfaceKindMobile)
	if evt == nil {
		t.Fatalf("expected a proactive event, got nil (uncertainty=%.2f)", state.Uncertainty)
	}
	if evt.TriggerName != "face.confusion_detected" {
		t.Errorf("TriggerName: got %q, want face.confusion_detected", evt.TriggerName)
	}
	if evt.SessionID != "sess-1" {
		t.Errorf("SessionID: got %q, want sess-1", evt.SessionID)
	}
}

func TestFaceCompanionBridge_Observe_HappyNoEvent(t *testing.T) {
	b := circleai.NewFaceCompanionBridge()

	state := circleai.AffectState{
		UserID:         "user-2",
		LastUpdatedUTC: time.Now().UTC(),
		Curiosity:      0.5,
		Engagement:     0.5,
		Uncertainty:    0.2,
		Rapport:        0.0,
		Energy:         0.5,
	}

	matrix := circleai.FacialMetricMatrix{
		Expression:      circleai.FaceExpressionHappy,
		ConfidenceScore: 0.92,
		CapturedAt:      time.Now().UTC(),
	}

	evt := b.Observe(&matrix, &state, "sess-2", "id-2", circleai.InterfaceKindMobile)
	if evt != nil {
		t.Errorf("expected nil event for happy expression, got %+v", evt)
	}
}
