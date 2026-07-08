// world_model_test.go
//
// Verifies IWorldModel implementations against fixtures/world_model.json (and the
// observation-extraction vectors in fixtures/world_model_extract.json), both
// generated from the C# reference (FrequencyWorldModel). FrequencyWorldModel is
// checked value-for-value; BayesianWorldModel is checked for the invariants a
// Bayesian variant must hold (argmax agreement on decisive evidence, cold-start
// fallback, probability normalisation).

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

type wmFixture struct {
	Epsilon float64  `json:"epsilon"`
	Cases   []wmCase `json:"cases"`
}

type wmCase struct {
	Description         string   `json:"description"`
	ScenarioJSON        string   `json:"scenarioJson"`
	ExpectedOutcome     string   `json:"expectedOutcome"`
	ExpectedProbability float64  `json:"expectedProbability"`
	ExpectedSupporters  []string `json:"expectedSupporters"`
}

type wmExtractFixture struct {
	Cases []wmExtractCase `json:"cases"`
}

type wmExtractCase struct {
	Input    string   `json:"input"`
	Expected []string `json:"expected"`
}

func loadWMFixture(t *testing.T) wmFixture {
	t.Helper()
	path := filepath.Join(fixturesDir(t), "world_model.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read world_model.json: %v", err)
	}
	var fix wmFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("parse world_model.json: %v", err)
	}
	return fix
}

// buildTrainedFrequencyModel reproduces the exact training set the gold-fixture
// generator used, so Predict outputs are directly comparable.
func buildTrainedFrequencyModel(t *testing.T) *circleai.FrequencyWorldModel {
	t.Helper()
	m := circleai.NewFrequencyWorldModel()
	must := func(err error) {
		t.Helper()
		if err != nil {
			t.Fatalf("Observe: %v", err)
		}
	}
	must(m.Observe([]string{"sky=grey", "humidity=high"}, "rain"))
	must(m.Observe([]string{"sky=grey", "humidity=high"}, "rain"))
	must(m.Observe([]string{"sky=grey", "humidity=low"}, "cloudy"))
	must(m.Observe([]string{"sky=blue"}, "sunny"))
	must(m.Observe([]string{"sky=blue"}, "sunny"))
	must(m.Observe([]string{"sky=blue"}, "sunny"))
	must(m.Observe([]string{"traffic=heavy"}, "late"))
	return m
}

func TestFrequencyWorldModel_Fixtures(t *testing.T) {
	fix := loadWMFixture(t)
	m := buildTrainedFrequencyModel(t)
	ctx := context.Background()
	for _, c := range fix.Cases {
		c := c
		t.Run(c.Description, func(t *testing.T) {
			got, err := m.Predict(ctx, c.ScenarioJSON)
			if err != nil {
				t.Fatalf("Predict: %v", err)
			}
			if got.Outcome != c.ExpectedOutcome {
				t.Errorf("outcome: got %q want %q", got.Outcome, c.ExpectedOutcome)
			}
			if math.Abs(got.Probability-c.ExpectedProbability) > fix.Epsilon {
				t.Errorf("probability: got %v want %v", got.Probability, c.ExpectedProbability)
			}
			assertTexts(t, got.SupportingFactors, c.ExpectedSupporters)
		})
	}
}

func TestFrequencyWorldModel_Validation(t *testing.T) {
	m := circleai.NewFrequencyWorldModel()
	if err := m.Observe([]string{"a=b"}, "   "); err == nil {
		t.Error("blank outcome should error")
	}
	// ctx cancellation is honoured.
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := m.Predict(ctx, "{}"); err == nil {
		t.Error("cancelled context should error")
	}
}

func TestWorldModel_ObservationExtraction(t *testing.T) {
	// Exercises the observation extractor indirectly: a fresh model trained so
	// that each expected "name=value" token maps to a unique outcome lets us
	// confirm the extractor produced exactly those tokens (JsonElement.ToString
	// parity for string/number/bool/null).
	path := filepath.Join(fixturesDir(t), "world_model_extract.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read world_model_extract.json: %v", err)
	}
	var fix wmExtractFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("parse world_model_extract.json: %v", err)
	}
	ctx := context.Background()
	for _, c := range fix.Cases {
		c := c
		t.Run(c.Input, func(t *testing.T) {
			m := circleai.NewFrequencyWorldModel()
			// Train: each expected token co-occurs with a token-specific outcome.
			for _, tok := range c.Expected {
				if err := m.Observe([]string{tok}, "out::"+tok); err != nil {
					t.Fatalf("Observe: %v", err)
				}
			}
			got, err := m.Predict(ctx, c.Input)
			if err != nil {
				t.Fatalf("Predict: %v", err)
			}
			// Supporters are exactly the extracted tokens that matched training,
			// i.e. the full expected set (order-preserved).
			assertTexts(t, got.SupportingFactors, c.Expected)
			if len(c.Expected) == 0 {
				if got.Outcome != "unknown" {
					t.Errorf("empty extraction should yield unknown, got %q", got.Outcome)
				}
			}
		})
	}
}

func TestBayesianWorldModel_Invariants(t *testing.T) {
	ctx := context.Background()

	t.Run("cold start returns unknown 0.5", func(t *testing.T) {
		m := circleai.NewBayesianWorldModel()
		got, err := m.Predict(ctx, `{"sky":"grey"}`)
		if err != nil {
			t.Fatalf("Predict: %v", err)
		}
		if got.Outcome != "unknown" || math.Abs(got.Probability-0.5) > 1e-12 {
			t.Errorf("cold start: got (%q, %v) want (unknown, 0.5)", got.Outcome, got.Probability)
		}
	})

	t.Run("argmax agrees with frequency model on decisive evidence", func(t *testing.T) {
		m := circleai.NewBayesianWorldModel()
		for i := 0; i < 5; i++ {
			if err := m.Observe([]string{"sky=grey", "humidity=high"}, "rain"); err != nil {
				t.Fatal(err)
			}
		}
		for i := 0; i < 5; i++ {
			if err := m.Observe([]string{"sky=blue"}, "sunny"); err != nil {
				t.Fatal(err)
			}
		}
		got, err := m.Predict(ctx, `{"sky":"grey","humidity":"high"}`)
		if err != nil {
			t.Fatalf("Predict: %v", err)
		}
		if got.Outcome != "rain" {
			t.Errorf("outcome: got %q want rain", got.Outcome)
		}
		if got.Probability <= 0 || got.Probability > 1 {
			t.Errorf("probability out of (0,1]: %v", got.Probability)
		}
		if got.Probability <= 0.5 {
			t.Errorf("decisive evidence should push probability above 0.5, got %v", got.Probability)
		}
		assertTexts(t, got.SupportingFactors, []string{"sky=grey", "humidity=high"})
	})

	t.Run("unseen observation still returns a valid distribution", func(t *testing.T) {
		m := circleai.NewBayesianWorldModel()
		if err := m.Observe([]string{"a=1"}, "x"); err != nil {
			t.Fatal(err)
		}
		if err := m.Observe([]string{"b=2"}, "y"); err != nil {
			t.Fatal(err)
		}
		got, err := m.Predict(ctx, `{"c":"3"}`)
		if err != nil {
			t.Fatalf("Predict: %v", err)
		}
		// No evidence for c=3 anywhere, but the model still returns a normalised
		// prediction over known outcomes (Laplace smoothing) — never "unknown"
		// once trained.
		if got.Outcome == "unknown" {
			t.Errorf("trained model should not fall back to unknown")
		}
		if got.Probability <= 0 || got.Probability > 1 {
			t.Errorf("probability out of (0,1]: %v", got.Probability)
		}
		if len(got.SupportingFactors) != 0 {
			t.Errorf("no observation should be a supporter, got %v", got.SupportingFactors)
		}
	})
}
