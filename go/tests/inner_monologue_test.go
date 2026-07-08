// inner_monologue_test.go
//
// Verifies IInnerMonologue implementations against fixtures/inner_monologue.json,
// generated from the C# reference (TemplateInnerMonologue). The reference's frame
// selector uses .NET's process-randomised String.GetHashCode, so the *specific*
// template chosen is not wire-stable even in C#; the deterministic sub-results
// Summarise() and InferDirection() ARE, and those are asserted value-for-value by
// checking that the produced thought contains the fixture summary and encodes the
// fixture direction. ReasoningLoopInnerMonologue is checked for its observe/
// interpret/decide structure and direction agreement.

package circleai_test

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

type imFixture struct {
	Cases []imCase `json:"cases"`
}

type imCase struct {
	ContextJSON       string `json:"contextJson"`
	ExpectedSummary   string `json:"expectedSummary"`
	ExpectedDirection string `json:"expectedDirection"`
}

func loadIMFixture(t *testing.T) imFixture {
	t.Helper()
	path := filepath.Join(fixturesDir(t), "inner_monologue.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read inner_monologue.json: %v", err)
	}
	var fix imFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("parse inner_monologue.json: %v", err)
	}
	return fix
}

func TestTemplateInnerMonologue_Fixtures(t *testing.T) {
	fix := loadIMFixture(t)
	m := &circleai.TemplateInnerMonologue{}
	ctx := context.Background()
	for _, c := range fix.Cases {
		c := c
		t.Run(c.ContextJSON, func(t *testing.T) {
			got, err := m.Reflect(ctx, c.ContextJSON)
			if err != nil {
				t.Fatalf("Reflect: %v", err)
			}
			// Whichever of the three frames was chosen, the deterministic
			// {summary} and {direction} substitutions must appear verbatim.
			if !strings.Contains(got.Thought, c.ExpectedSummary) {
				t.Errorf("thought %q does not contain summary %q", got.Thought, c.ExpectedSummary)
			}
			if !strings.Contains(got.Thought, c.ExpectedDirection) {
				t.Errorf("thought %q does not contain direction %q", got.Thought, c.ExpectedDirection)
			}
			// The chosen frame is one of the three known templates (structure).
			if !isOneOfKnownFrames(got.Thought, c.ExpectedSummary, c.ExpectedDirection) {
				t.Errorf("thought %q is not a recognised frame rendering", got.Thought)
			}
			if got.At.IsZero() {
				t.Error("reflection timestamp not set")
			}
		})
	}
}

func TestTemplateInnerMonologue_Deterministic(t *testing.T) {
	// Same input must always render the same thought (stable frame selection).
	m := &circleai.TemplateInnerMonologue{}
	ctx := context.Background()
	first, err := m.Reflect(ctx, `{"user":"hi"}`)
	if err != nil {
		t.Fatalf("Reflect: %v", err)
	}
	for i := 0; i < 5; i++ {
		again, err := m.Reflect(ctx, `{"user":"hi"}`)
		if err != nil {
			t.Fatalf("Reflect: %v", err)
		}
		if again.Thought != first.Thought {
			t.Errorf("non-deterministic frame: %q vs %q", again.Thought, first.Thought)
		}
	}
}

func isOneOfKnownFrames(thought, summary, direction string) bool {
	frames := []string{
		"Observation: " + summary + ". Implication: this likely means " + direction + ".",
		"Looking at " + summary + ", the salient pattern is " + direction + ".",
		"Given " + summary + ", my next step is to " + direction + ".",
	}
	for _, f := range frames {
		if thought == f {
			return true
		}
	}
	return false
}

func TestReasoningLoopInnerMonologue(t *testing.T) {
	fix := loadIMFixture(t)
	m := &circleai.ReasoningLoopInnerMonologue{}
	ctx := context.Background()
	for _, c := range fix.Cases {
		c := c
		t.Run(c.ContextJSON, func(t *testing.T) {
			got, err := m.Reflect(ctx, c.ContextJSON)
			if err != nil {
				t.Fatalf("Reflect: %v", err)
			}
			// Three-line observe/interpret/decide structure.
			for _, prefix := range []string{"Observe: ", "\nInterpret: ", "\nDecide: "} {
				if !strings.Contains(got.Thought, prefix) {
					t.Errorf("thought %q missing %q", got.Thought, strings.TrimSpace(prefix))
				}
			}
			// The Observe line carries the same summary the template engine uses.
			if !strings.Contains(got.Thought, c.ExpectedSummary) {
				t.Errorf("thought %q does not contain summary %q", got.Thought, c.ExpectedSummary)
			}
			// The Decide line carries the same direction.
			if !strings.Contains(got.Thought, c.ExpectedDirection) {
				t.Errorf("thought %q does not contain direction %q", got.Thought, c.ExpectedDirection)
			}
		})
	}
}
