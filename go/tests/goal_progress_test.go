// goal_progress_test.go
//
// Validates Goal.AdvanceProgress against all 7 vectors in
// fixtures/goal_progress.json.
// Progress is clamped to [0.0, 1.0]; comparisons use epsilon 1e-5.

package circleai_test

import (
	"encoding/json"
	"math"
	"os"
	"path/filepath"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ---------------------------------------------------------------------------
// Fixture types
// ---------------------------------------------------------------------------

type goalFixture struct {
	Vectors []goalVector `json:"vectors"`
}

type goalVector struct {
	ID               string  `json:"id"`
	Description      string  `json:"description"`
	InitialProgress  float32 `json:"initial_progress"`
	Delta            float32 `json:"delta"`
	ExpectedProgress float32 `json:"expected_progress"`
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

func loadGoalFixture(t *testing.T) goalFixture {
	t.Helper()
	path := filepath.Join(fixturesDir(t), "goal_progress.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("failed to read goal_progress.json: %v", err)
	}
	var fix goalFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("failed to parse goal_progress.json: %v", err)
	}
	return fix
}

// ---------------------------------------------------------------------------
// Table-driven test
// ---------------------------------------------------------------------------

func TestGoalAdvanceProgress_Vectors(t *testing.T) {
	const epsilon = float64(1e-5)

	fix := loadGoalFixture(t)
	if len(fix.Vectors) == 0 {
		t.Fatal("no vectors in goal_progress.json")
	}

	for _, v := range fix.Vectors {
		v := v
		t.Run(v.ID, func(t *testing.T) {
			g := circleai.Goal{
				ID:       "test-goal",
				UserID:   "user-1",
				Title:    "Test",
				Progress: v.InitialProgress,
			}

			updated := g.AdvanceProgress(v.Delta)

			got := float64(updated.Progress)
			want := float64(v.ExpectedProgress)
			if math.Abs(got-want) > epsilon {
				t.Errorf("[%s] Progress: got %.7f, want %.7f (epsilon %v)",
					v.ID, got, want, epsilon)
			}

			// Verify immutability: original goal must not be mutated.
			if g.Progress != v.InitialProgress {
				t.Errorf("[%s] original Goal.Progress was mutated: got %.7f, want %.7f",
					v.ID, g.Progress, v.InitialProgress)
			}
		})
	}
}

// ---------------------------------------------------------------------------
// Boundary and property tests
// ---------------------------------------------------------------------------

func TestGoalAdvanceProgress_NeverExceedsOne(t *testing.T) {
	g := circleai.Goal{Progress: 1.0}
	updated := g.AdvanceProgress(9999)
	if updated.Progress != 1.0 {
		t.Errorf("clamped max: got %v, want 1.0", updated.Progress)
	}
}

func TestGoalAdvanceProgress_NeverBelowZero(t *testing.T) {
	g := circleai.Goal{Progress: 0.0}
	updated := g.AdvanceProgress(-9999)
	if updated.Progress != 0.0 {
		t.Errorf("clamped min: got %v, want 0.0", updated.Progress)
	}
}

func TestGoalAdvanceProgress_Immutability(t *testing.T) {
	g := circleai.Goal{Progress: 0.5}
	_ = g.AdvanceProgress(0.3)
	if g.Progress != 0.5 {
		t.Errorf("original should not be mutated: got %v, want 0.5", g.Progress)
	}
}

func TestGoalAdvanceProgress_ZeroIsIdempotent(t *testing.T) {
	const eps = float64(1e-7)
	g := circleai.Goal{Progress: 0.42}
	updated := g.AdvanceProgress(0)
	if math.Abs(float64(updated.Progress)-0.42) > eps {
		t.Errorf("zero delta changed progress: got %v, want 0.42", updated.Progress)
	}
}
