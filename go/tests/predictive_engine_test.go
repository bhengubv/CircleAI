// predictive_engine_test.go
//
// Verifies IPredictiveEngine implementations against fixtures/predictive_engine.json,
// generated from the C# reference (HistogramPredictiveEngine) at a fixed clock of
// Monday 2026-01-05T08:00:00Z. HistogramPredictiveEngine is checked value-for-value
// (order, ExpectedByUtc, probability). SequencePredictiveEngine is checked for the
// invariants a Markov successor predictor must hold (conditional distribution,
// probability normalisation, cold-start fallback).

package circleai_test

import (
	"context"
	"encoding/json"
	"math"
	"os"
	"path/filepath"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

type peFixture struct {
	Epsilon float64  `json:"epsilon"`
	NowUTC  string   `json:"nowUtc"`
	Cases   []peCase `json:"cases"`
}

type peCase struct {
	Description    string       `json:"description"`
	HorizonMinutes int          `json:"horizonMinutes"`
	Expected       []peExpected `json:"expected"`
}

type peExpected struct {
	Description   string  `json:"description"`
	ExpectedByUTC string  `json:"expectedByUtc"`
	Probability   float64 `json:"probability"`
}

func mustTime(t *testing.T, s string) time.Time {
	t.Helper()
	// .NET "O" round-trip format, e.g. 2026-01-05T08:00:00.0000000+00:00.
	parsed, err := time.Parse("2006-01-02T15:04:05.9999999Z07:00", s)
	if err != nil {
		// Fall back to RFC3339Nano for the +00:00 offset form.
		parsed, err = time.Parse(time.RFC3339Nano, s)
		if err != nil {
			t.Fatalf("parse time %q: %v", s, err)
		}
	}
	return parsed.UTC()
}

func loadPEFixture(t *testing.T) peFixture {
	t.Helper()
	path := filepath.Join(fixturesDir(t), "predictive_engine.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read predictive_engine.json: %v", err)
	}
	var fix peFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("parse predictive_engine.json: %v", err)
	}
	return fix
}

func TestHistogramPredictiveEngine_Fixtures(t *testing.T) {
	fix := loadPEFixture(t)
	now := mustTime(t, fix.NowUTC)

	// Reproduce the exact observation set from the gold-fixture generator.
	e := circleai.NewHistogramPredictiveEngineAt(func() time.Time { return now })
	obs := []struct {
		desc string
		at   string
	}{
		{"coffee", "2026-01-05T08:00:00+00:00"},
		{"coffee", "2025-12-29T08:30:00+00:00"},
		{"coffee", "2026-01-05T09:15:00+00:00"},
		{"coffee", "2026-01-06T14:00:00+00:00"},
		{"standup", "2026-01-05T09:00:00+00:00"},
		{"standup", "2026-01-12T09:30:00+00:00"},
		{"lunch", "2026-01-05T12:00:00+00:00"},
	}
	for _, o := range obs {
		if err := e.Observe(o.desc, mustTime(t, o.at)); err != nil {
			t.Fatalf("Observe(%s): %v", o.desc, err)
		}
	}

	ctx := context.Background()
	for _, c := range fix.Cases {
		c := c
		t.Run(c.Description, func(t *testing.T) {
			got, err := e.Anticipate(ctx, c.HorizonMinutes)
			if err != nil {
				t.Fatalf("Anticipate: %v", err)
			}
			if len(got) != len(c.Expected) {
				t.Fatalf("count: got %d want %d (%+v)", len(got), len(c.Expected), got)
			}
			for i, exp := range c.Expected {
				if got[i].Description != exp.Description {
					t.Errorf("[%d] description: got %q want %q", i, got[i].Description, exp.Description)
				}
				if math.Abs(got[i].Probability-exp.Probability) > fix.Epsilon {
					t.Errorf("[%d] probability: got %v want %v", i, got[i].Probability, exp.Probability)
				}
				wantBy := mustTime(t, exp.ExpectedByUTC)
				if !got[i].ExpectedByUTC.Equal(wantBy) {
					t.Errorf("[%d] expectedBy: got %v want %v", i, got[i].ExpectedByUTC, wantBy)
				}
			}
		})
	}
}

func TestHistogramPredictiveEngine_Validation(t *testing.T) {
	e := circleai.NewHistogramPredictiveEngine()
	if err := e.Observe("   ", time.Now()); err == nil {
		t.Error("blank description should error")
	}
	if _, err := e.Anticipate(context.Background(), 0); err == nil {
		t.Error("horizon <= 0 should error")
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := e.Anticipate(ctx, 60); err == nil {
		t.Error("cancelled context should error")
	}
}

func TestSequencePredictiveEngine_Invariants(t *testing.T) {
	ctx := context.Background()
	fixedNow := mustTime(t, "2026-01-05T08:00:00+00:00")

	t.Run("empty engine yields no needs", func(t *testing.T) {
		e := circleai.NewSequencePredictiveEngineAt(func() time.Time { return fixedNow })
		got, err := e.Anticipate(ctx, 60)
		if err != nil {
			t.Fatalf("Anticipate: %v", err)
		}
		if len(got) != 0 {
			t.Errorf("expected no needs, got %v", got)
		}
	})

	t.Run("conditional successor distribution follows the last event", func(t *testing.T) {
		e := circleai.NewSequencePredictiveEngineAt(func() time.Time { return fixedNow })
		// wake -> coffee (x3), wake -> tea (x1); last event ends on "wake".
		seqs := [][]string{
			{"wake", "coffee"},
			{"wake", "coffee"},
			{"wake", "coffee"},
			{"wake", "tea"},
			{"wake"},
		}
		for _, s := range seqs {
			for _, ev := range s {
				if err := e.Observe(ev); err != nil {
					t.Fatalf("Observe: %v", err)
				}
			}
		}
		got, err := e.Anticipate(ctx, 60)
		if err != nil {
			t.Fatalf("Anticipate: %v", err)
		}
		if len(got) != 2 {
			t.Fatalf("expected 2 successors, got %d (%+v)", len(got), got)
		}
		// coffee (3/4) ranks above tea (1/4).
		if got[0].Description != "coffee" {
			t.Errorf("top successor: got %q want coffee", got[0].Description)
		}
		if math.Abs(got[0].Probability-0.75) > 1e-9 {
			t.Errorf("coffee probability: got %v want 0.75", got[0].Probability)
		}
		if got[1].Description != "tea" || math.Abs(got[1].Probability-0.25) > 1e-9 {
			t.Errorf("second successor: got (%q,%v) want (tea,0.25)", got[1].Description, got[1].Probability)
		}
		// Distribution sums to 1.
		var sum float64
		for _, n := range got {
			sum += n.Probability
			if !n.ExpectedByUTC.After(fixedNow) {
				t.Errorf("ExpectedByUtc must be in the future: %v", n.ExpectedByUTC)
			}
		}
		if math.Abs(sum-1.0) > 1e-9 {
			t.Errorf("probabilities should sum to 1, got %v", sum)
		}
	})

	t.Run("validation", func(t *testing.T) {
		e := circleai.NewSequencePredictiveEngine()
		if err := e.Observe(" "); err == nil {
			t.Error("blank description should error")
		}
		if _, err := e.Anticipate(ctx, -1); err == nil {
			t.Error("negative horizon should error")
		}
	})
}
