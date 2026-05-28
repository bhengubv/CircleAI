// anomaly_signal_test.go
//
// Validates NewAnomalySignal — confidence is clamped to [0.0, 1.0], evidence
// defaults to an empty map when nil, and stamped fields (ID, DetectedAt) are
// populated. Clamp vectors come from fixtures/anomaly_signal_schema.json.
// Float comparisons use epsilon = 1e-6 (clamp is a no-loss operation).

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

type anomalyFixture struct {
	ThreatVector  threatVectorBlock `json:"threatVector"`
	ClampVectors  []clampVector     `json:"clamp_vectors"`
}

type threatVectorBlock struct {
	Values []threatVectorEntry `json:"values"`
}

type threatVectorEntry struct {
	Name    string `json:"name"`
	Ordinal int    `json:"ordinal"`
}

type clampVector struct {
	ID                  string  `json:"id"`
	InputConfidence     float64 `json:"input_confidence"`
	ExpectedConfidence  float64 `json:"expected_confidence"`
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

func loadAnomalyFixture(t *testing.T) anomalyFixture {
	t.Helper()
	path := filepath.Join(fixturesDir(t), "anomaly_signal_schema.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("failed to read anomaly_signal_schema.json: %v", err)
	}
	var fix anomalyFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("failed to parse anomaly_signal_schema.json: %v", err)
	}
	return fix
}

// ---------------------------------------------------------------------------
// ThreatVector ordinals — must stay stable across language ports
// ---------------------------------------------------------------------------

func TestThreatVector_OrdinalsMatchFixture(t *testing.T) {
	fix := loadAnomalyFixture(t)

	if len(fix.ThreatVector.Values) == 0 {
		t.Fatal("no threatVector values in fixture")
	}

	want := map[string]circleai.ThreatVector{
		"MemoryAnomaly":         circleai.ThreatVectorMemoryAnomaly,
		"ControlFlowDrift":      circleai.ThreatVectorControlFlowDrift,
		"PrivilegeEscalation":   circleai.ThreatVectorPrivilegeEscalation,
		"BiometricSpoofAttempt": circleai.ThreatVectorBiometricSpoofAttempt,
		"NetworkPivot":          circleai.ThreatVectorNetworkPivot,
		"StateCorruption":       circleai.ThreatVectorStateCorruption,
		"AgentPatchRejected":    circleai.ThreatVectorAgentPatchRejected,
		"Unknown":               circleai.ThreatVectorUnknown,
	}

	for _, entry := range fix.ThreatVector.Values {
		got, ok := want[entry.Name]
		if !ok {
			t.Errorf("fixture has unknown ThreatVector name %q", entry.Name)
			continue
		}
		if int(got) != entry.Ordinal {
			t.Errorf("ThreatVector%s: got ordinal %d, want %d", entry.Name, int(got), entry.Ordinal)
		}
	}

	if len(fix.ThreatVector.Values) != len(want) {
		t.Errorf("fixture has %d ThreatVector entries, Go port has %d",
			len(fix.ThreatVector.Values), len(want))
	}
}

// ---------------------------------------------------------------------------
// Confidence clamp — table-driven against fixture
// ---------------------------------------------------------------------------

func TestAnomalySignal_ConfidenceClamp(t *testing.T) {
	const epsilon = 1e-6

	fix := loadAnomalyFixture(t)

	if len(fix.ClampVectors) == 0 {
		t.Fatal("no clamp_vectors in fixture")
	}

	for _, v := range fix.ClampVectors {
		v := v
		t.Run(v.ID, func(t *testing.T) {
			sig := circleai.NewAnomalySignal(
				circleai.ThreatVectorMemoryAnomaly,
				float32(v.InputConfidence),
				"Circle.AI.Test",
				"clamp vector "+v.ID,
				nil,
			)
			if sig == nil {
				t.Fatal("NewAnomalySignal returned nil")
			}
			got := float64(sig.Confidence)
			if math.Abs(got-v.ExpectedConfidence) > epsilon {
				t.Errorf("[%s] confidence: input=%v got=%v want=%v",
					v.ID, v.InputConfidence, got, v.ExpectedConfidence)
			}
		})
	}
}

// ---------------------------------------------------------------------------
// Factory behaviour — ID, DetectedAt, evidence default
// ---------------------------------------------------------------------------

func TestNewAnomalySignal_StampsIDAndTimestamp(t *testing.T) {
	before := time.Now().UTC()
	sig := circleai.NewAnomalySignal(
		circleai.ThreatVectorBiometricSpoofAttempt,
		0.42,
		"Circle.AI.Identity",
		"liveness check failed",
		map[string]string{"reason": "no_blink"},
	)
	after := time.Now().UTC()

	if sig == nil {
		t.Fatal("NewAnomalySignal returned nil")
	}
	if sig.ID == "" {
		t.Error("ID is empty — expected a UUID v4 string")
	}
	if sig.DetectedAt.Before(before) || sig.DetectedAt.After(after) {
		t.Errorf("DetectedAt %v is outside [%v, %v]", sig.DetectedAt, before, after)
	}
	if sig.DetectedAt.Location() != time.UTC {
		t.Errorf("DetectedAt location: got %v, want UTC", sig.DetectedAt.Location())
	}
	if sig.Vector != circleai.ThreatVectorBiometricSpoofAttempt {
		t.Errorf("Vector: got %v, want BiometricSpoofAttempt", sig.Vector)
	}
	if sig.AffectedModule != "Circle.AI.Identity" {
		t.Errorf("AffectedModule: got %q, want Circle.AI.Identity", sig.AffectedModule)
	}
	if sig.Description != "liveness check failed" {
		t.Errorf("Description: got %q", sig.Description)
	}
	if v, ok := sig.Evidence["reason"]; !ok || v != "no_blink" {
		t.Errorf("Evidence[reason]: got %q ok=%v, want no_blink", v, ok)
	}
}

func TestNewAnomalySignal_NilEvidenceBecomesEmptyMap(t *testing.T) {
	sig := circleai.NewAnomalySignal(
		circleai.ThreatVectorUnknown,
		0.5,
		"Circle.AI.Companion",
		"unknown anomaly",
		nil,
	)
	if sig == nil {
		t.Fatal("NewAnomalySignal returned nil")
	}
	if sig.Evidence == nil {
		t.Error("Evidence is nil — expected empty map")
	}
	if len(sig.Evidence) != 0 {
		t.Errorf("Evidence len: got %d, want 0", len(sig.Evidence))
	}
}

func TestNewAnomalySignal_UniqueIDs(t *testing.T) {
	const n = 32
	seen := make(map[string]struct{}, n)
	for i := 0; i < n; i++ {
		sig := circleai.NewAnomalySignal(
			circleai.ThreatVectorMemoryAnomaly,
			0.5,
			"Circle.AI.Memory",
			"dup id check",
			nil,
		)
		if sig == nil {
			t.Fatal("NewAnomalySignal returned nil")
		}
		if _, dup := seen[sig.ID]; dup {
			t.Fatalf("duplicate ID generated at iteration %d: %s", i, sig.ID)
		}
		seen[sig.ID] = struct{}{}
	}
}
