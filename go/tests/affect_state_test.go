// affect_state_test.go
//
// Validates AffectState math against all 12 vectors in fixtures/affect_state.json.
// All float comparisons use epsilon = 1e-5 (float32 precision).

package circleai_test

import (
	"encoding/json"
	"math"
	"os"
	"path/filepath"
	"runtime"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ---------------------------------------------------------------------------
// Fixture types
// ---------------------------------------------------------------------------

type affectFixture struct {
	Epsilon float64        `json:"epsilon"`
	Vectors []affectVector `json:"vectors"`
}

type affectVector struct {
	ID             string                 `json:"id"`
	Description    string                 `json:"description"`
	Input          affectDims             `json:"input"`
	Operation      string                 `json:"operation"`
	OperationParam map[string]interface{} `json:"operationParam"`
	Expected       affectDims             `json:"expected"`
}

type affectDims struct {
	Curiosity   float64 `json:"curiosity"`
	Engagement  float64 `json:"engagement"`
	Uncertainty float64 `json:"uncertainty"`
	Rapport     float64 `json:"rapport"`
	Energy      float64 `json:"energy"`
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

func fixturesDir(t *testing.T) string {
	t.Helper()
	_, file, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatal("runtime.Caller failed")
	}
	// tests/ -> go/ -> CircleAI/ -> fixtures/
	return filepath.Join(filepath.Dir(file), "..", "..", "fixtures")
}

func loadAffectFixture(t *testing.T) affectFixture {
	t.Helper()
	path := filepath.Join(fixturesDir(t), "affect_state.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("failed to read affect_state.json: %v", err)
	}
	var fix affectFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("failed to parse affect_state.json: %v", err)
	}
	return fix
}

func makeState(d affectDims) circleai.AffectState {
	return circleai.AffectState{
		UserID:         "test",
		LastUpdatedUTC: time.Now().UTC(),
		Curiosity:      float32(d.Curiosity),
		Engagement:     float32(d.Engagement),
		Uncertainty:    float32(d.Uncertainty),
		Rapport:        float32(d.Rapport),
		Energy:         float32(d.Energy),
	}
}

func assertDim(t *testing.T, id, name string, got float32, want float64, epsilon float64) {
	t.Helper()
	if math.Abs(float64(got)-want) > epsilon {
		t.Errorf("[%s] %s: got %v, want %v (epsilon %v)", id, name, got, want, epsilon)
	}
}

// ---------------------------------------------------------------------------
// Table-driven test
// ---------------------------------------------------------------------------

func TestAffectStateVectors(t *testing.T) {
	const epsilon = 1e-5 // float32 precision

	fix := loadAffectFixture(t)

	if len(fix.Vectors) == 0 {
		t.Fatal("no vectors found in fixture")
	}

	for _, v := range fix.Vectors {
		v := v // capture
		t.Run(v.ID, func(t *testing.T) {
			state := makeState(v.Input)

			count := 1
			if v.OperationParam != nil {
				if c, ok := v.OperationParam["count"]; ok {
					switch cv := c.(type) {
					case float64:
						count = int(cv)
					}
				}
			}

			switch v.Operation {
			case "positive_signal":
				for i := 0; i < count; i++ {
					state.ApplyPositiveSignal()
				}

			case "negative_signal":
				for i := 0; i < count; i++ {
					state.ApplyNegativeSignal()
				}

			case "positive_then_negative":
				state.ApplyPositiveSignal()
				state.ApplyNegativeSignal()

			case "negative_then_positive":
				state.ApplyNegativeSignal()
				state.ApplyPositiveSignal()

			case "idle_decay":
				hours := 0.0
				if v.OperationParam != nil {
					if h, ok := v.OperationParam["hours"]; ok {
						hours = h.(float64)
					}
				}
				state.ApplyIdleDecay(time.Duration(hours * float64(time.Hour)))

			default:
				t.Fatalf("unknown operation: %q", v.Operation)
			}

			assertDim(t, v.ID, "curiosity", state.Curiosity, v.Expected.Curiosity, epsilon)
			assertDim(t, v.ID, "engagement", state.Engagement, v.Expected.Engagement, epsilon)
			assertDim(t, v.ID, "uncertainty", state.Uncertainty, v.Expected.Uncertainty, epsilon)
			assertDim(t, v.ID, "rapport", state.Rapport, v.Expected.Rapport, epsilon)
			assertDim(t, v.ID, "energy", state.Energy, v.Expected.Energy, epsilon)
		})
	}
}

// ---------------------------------------------------------------------------
// ToSystemPromptHint
// ---------------------------------------------------------------------------

func TestAffectStateToSystemPromptHint_Empty(t *testing.T) {
	state := circleai.NewAffectState("user1")
	hint := state.ToSystemPromptHint()
	if hint != "" {
		t.Errorf("expected empty hint for neutral state, got %q", hint)
	}
}

func TestAffectStateToSystemPromptHint_HighEngagement(t *testing.T) {
	state := circleai.NewAffectState("user1")
	state.Engagement = 0.8
	hint := state.ToSystemPromptHint()
	if hint == "" {
		t.Error("expected non-empty hint for high engagement")
	}
	if hint[:len("[Affect state]")] != "[Affect state]" {
		t.Errorf("hint does not start with [Affect state]: %q", hint)
	}
}

// ---------------------------------------------------------------------------
// NewAffectState defaults
// ---------------------------------------------------------------------------

func TestNewAffectState_Defaults(t *testing.T) {
	state := circleai.NewAffectState("u1")
	if state.UserID != "u1" {
		t.Errorf("UserID: got %q, want %q", state.UserID, "u1")
	}
	assertDim(t, "defaults", "curiosity", state.Curiosity, 0.5, 1e-7)
	assertDim(t, "defaults", "engagement", state.Engagement, 0.5, 1e-7)
	assertDim(t, "defaults", "uncertainty", state.Uncertainty, 0.2, 1e-7)
	assertDim(t, "defaults", "rapport", state.Rapport, 0.0, 1e-7)
	assertDim(t, "defaults", "energy", state.Energy, 0.5, 1e-7)
}
