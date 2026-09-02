// affect_vad_test.go
//
// Validates AffectState.ToVad() — projection from the five engagement
// dimensions onto the Valence / Arousal / Dominance space.
//
// Derivation must be byte-identical across language ports; vectors live in
// fixtures/affect_vad_derivation.json. Float comparisons use epsilon = 1e-5
// (float32 precision; matches the fixture's declared epsilon).

package circleai_test

import (
	"encoding/json"
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

type affectVadFixture struct {
	Epsilon float64         `json:"epsilon"`
	Vectors []affectVadCase `json:"vectors"`
}

type affectVadCase struct {
	ID          string     `json:"id"`
	Description string     `json:"description"`
	Input       affectDims `json:"input"`
	Expected    vadDims    `json:"expected"`
}

type vadDims struct {
	Valence   float64 `json:"valence"`
	Arousal   float64 `json:"arousal"`
	Dominance float64 `json:"dominance"`
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

func loadAffectVadFixture(t *testing.T) affectVadFixture {
	t.Helper()
	path := filepath.Join(fixturesDir(t), "affect_vad_derivation.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("failed to read affect_vad_derivation.json: %v", err)
	}
	var fix affectVadFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("failed to parse affect_vad_derivation.json: %v", err)
	}
	return fix
}

func assertVadDim(t *testing.T, id, name string, got float32, want float64, epsilon float64) {
	t.Helper()
	if math.Abs(float64(got)-want) > epsilon {
		t.Errorf("[%s] %s: got %v, want %v (epsilon %v)", id, name, got, want, epsilon)
	}
}

// ---------------------------------------------------------------------------
// Table-driven derivation tests
// ---------------------------------------------------------------------------

func TestAffectStateToVad_Vectors(t *testing.T) {
	const epsilon = 1e-5 // float32 precision; matches fixture epsilon

	fix := loadAffectVadFixture(t)

	if len(fix.Vectors) == 0 {
		t.Fatal("no vectors found in affect_vad_derivation.json")
	}

	for _, v := range fix.Vectors {
		v := v // capture
		t.Run(v.ID, func(t *testing.T) {
			state := circleai.AffectState{
				UserID:         "test",
				LastUpdatedUTC: time.Now().UTC(),
				Curiosity:      float32(v.Input.Curiosity),
				Engagement:     float32(v.Input.Engagement),
				Uncertainty:    float32(v.Input.Uncertainty),
				Rapport:        float32(v.Input.Rapport),
				Energy:         float32(v.Input.Energy),
			}

			vad := state.ToVad()

			assertVadDim(t, v.ID, "valence", vad.Valence, v.Expected.Valence, epsilon)
			assertVadDim(t, v.ID, "arousal", vad.Arousal, v.Expected.Arousal, epsilon)
			assertVadDim(t, v.ID, "dominance", vad.Dominance, v.Expected.Dominance, epsilon)
		})
	}
}

// ---------------------------------------------------------------------------
// Output-clamp guarantee — derived axes are bounded to [0.0, 1.0]
// ---------------------------------------------------------------------------

func TestAffectStateToVad_OutputClampedTo01(t *testing.T) {
	// Construct a state with values outside the documented [0,1] input range
	// so the unclamped formulas would overshoot. ToVad must still produce
	// outputs inside [0,1].
	state := circleai.AffectState{
		UserID:         "test",
		LastUpdatedUTC: time.Now().UTC(),
		Curiosity:      2.0,  // out of range — drives arousal above 1
		Engagement:     2.0,  // out of range — drives valence + dominance above 1
		Uncertainty:    -1.0, // out of range — drives all three above 1
		Rapport:        2.0,
		Energy:         2.0,
	}

	vad := state.ToVad()

	if vad.Valence < 0 || vad.Valence > 1 {
		t.Errorf("Valence not clamped to [0,1]: got %v", vad.Valence)
	}
	if vad.Arousal < 0 || vad.Arousal > 1 {
		t.Errorf("Arousal not clamped to [0,1]: got %v", vad.Arousal)
	}
	if vad.Dominance < 0 || vad.Dominance > 1 {
		t.Errorf("Dominance not clamped to [0,1]: got %v", vad.Dominance)
	}
}

func TestAffectStateToVad_OutputClampedToZero(t *testing.T) {
	// Mirror case — drive all formulas negative.
	state := circleai.AffectState{
		UserID:         "test",
		LastUpdatedUTC: time.Now().UTC(),
		Curiosity:      -2.0,
		Engagement:     -2.0,
		Uncertainty:    2.0, // (1 - 2.0) = -1.0 in valence + dominance
		Rapport:        -2.0,
		Energy:         -2.0,
	}

	vad := state.ToVad()

	if vad.Valence != 0 {
		t.Errorf("Valence: got %v, want 0 (lower clamp)", vad.Valence)
	}
	if vad.Arousal != 0 {
		t.Errorf("Arousal: got %v, want 0 (lower clamp)", vad.Arousal)
	}
	if vad.Dominance != 0 {
		t.Errorf("Dominance: got %v, want 0 (lower clamp)", vad.Dominance)
	}
}

// ---------------------------------------------------------------------------
// Default-state sanity check — uses NewAffectState constructor
// ---------------------------------------------------------------------------

func TestAffectStateToVad_DefaultStateMatchesFixture(t *testing.T) {
	const epsilon = 1e-5

	state := circleai.NewAffectState("default-user")
	vad := state.ToVad()

	// NewAffectState produces curiosity=0.5, engagement=0.5, uncertainty=0.2,
	// rapport=0, energy=0.5 — matches the "default_state" fixture vector.
	assertVadDim(t, "default_state", "valence", vad.Valence, 0.43333333, epsilon)
	assertVadDim(t, "default_state", "arousal", vad.Arousal, 0.425, epsilon)
	assertVadDim(t, "default_state", "dominance", vad.Dominance, 0.65, epsilon)
}
