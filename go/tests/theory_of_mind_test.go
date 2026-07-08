// theory_of_mind_test.go
//
// Verifies BeliefTrackerTheoryOfMind against fixtures/theory_of_mind.json,
// generated from the C# reference. The headline assertion is byte-exact: the
// LikelyBeliefJson field must equal the C# JsonSerializer.Serialize(
// Dictionary<string,double>) output, including insertion order, .NET shortest
// round-trip double formatting, and System.Text.Json's default \uXXXX escaping of
// the quote, HTML-sensitive characters, and non-ASCII. Confidence is compared
// with an epsilon.

package circleai_test

import (
	"context"
	"encoding/json"
	"math"
	"os"
	"path/filepath"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

type tomFixture struct {
	Epsilon float64   `json:"epsilon"`
	Cases   []tomCase `json:"cases"`
}

type tomCase struct {
	Target                   string  `json:"target"`
	InteractionHistoryJSON   string  `json:"interactionHistoryJson"`
	ExpectedLikelyBeliefJSON string  `json:"expectedLikelyBeliefJson"`
	ExpectedConfidence       float64 `json:"expectedConfidence"`
}

func loadTOMFixture(t *testing.T) tomFixture {
	t.Helper()
	path := filepath.Join(fixturesDir(t), "theory_of_mind.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read theory_of_mind.json: %v", err)
	}
	var fix tomFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("parse theory_of_mind.json: %v", err)
	}
	return fix
}

func TestBeliefTrackerTheoryOfMind_Fixtures(t *testing.T) {
	fix := loadTOMFixture(t)
	m := &circleai.BeliefTrackerTheoryOfMind{}
	ctx := context.Background()
	for _, c := range fix.Cases {
		c := c
		t.Run(c.Target, func(t *testing.T) {
			got, err := m.Estimate(ctx, c.Target, c.InteractionHistoryJSON)
			if err != nil {
				t.Fatalf("Estimate: %v", err)
			}
			if got.TargetIdentifier != c.Target {
				t.Errorf("target: got %q want %q", got.TargetIdentifier, c.Target)
			}
			if got.LikelyBeliefJSON != c.ExpectedLikelyBeliefJSON {
				t.Errorf("belief JSON mismatch:\n got %s\nwant %s", got.LikelyBeliefJSON, c.ExpectedLikelyBeliefJSON)
			}
			if math.Abs(got.Confidence-c.ExpectedConfidence) > fix.Epsilon {
				t.Errorf("confidence: got %v want %v", got.Confidence, c.ExpectedConfidence)
			}
			// The belief JSON must itself be valid JSON that round-trips to a
			// map (guards against escaping regressions producing invalid output).
			var back map[string]float64
			if err := json.Unmarshal([]byte(got.LikelyBeliefJSON), &back); err != nil {
				t.Errorf("belief JSON is not valid JSON: %v (%s)", err, got.LikelyBeliefJSON)
			}
		})
	}
}

func TestBeliefTrackerTheoryOfMind_Validation(t *testing.T) {
	m := &circleai.BeliefTrackerTheoryOfMind{}
	ctx := context.Background()
	if _, err := m.Estimate(ctx, "   ", "history"); err == nil {
		t.Error("blank target should error")
	}
	// Cancelled context is honoured.
	cctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := m.Estimate(cctx, "x", "y"); err == nil {
		t.Error("cancelled context should error")
	}
	// Empty history yields an empty bag and zero confidence.
	got, err := m.Estimate(ctx, "x", "")
	if err != nil {
		t.Fatalf("Estimate: %v", err)
	}
	if got.LikelyBeliefJSON != "{}" || got.Confidence != 0 {
		t.Errorf("empty history: got (%q, %v) want ({}, 0)", got.LikelyBeliefJSON, got.Confidence)
	}
}
