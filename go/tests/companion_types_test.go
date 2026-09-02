// companion_types_test.go
//
// Validates:
//   - InterfaceKind has exactly 7 values.
//   - CompanionContext and CompanionTurn can be constructed and carry correct values.
//   - CompanionProactiveEvent fields are populated correctly.
//   - PersonaState.ToSystemPromptHint() matches all fixture vectors.

package circleai_test

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ---------------------------------------------------------------------------
// InterfaceKind
// ---------------------------------------------------------------------------

func TestInterfaceKind_HasSevenValues(t *testing.T) {
	// Enumerate all 7 expected values and ensure none share the same underlying int.
	kinds := []circleai.InterfaceKind{
		circleai.InterfaceKindMobile,
		circleai.InterfaceKindWearable,
		circleai.InterfaceKindDesktop,
		circleai.InterfaceKindWeb,
		circleai.InterfaceKindIoT,
		circleai.InterfaceKindAmbient,
		circleai.InterfaceKindHeadless,
	}
	if len(kinds) != 7 {
		t.Errorf("expected 7 InterfaceKind values, got %d", len(kinds))
	}
	seen := make(map[circleai.InterfaceKind]bool)
	for _, k := range kinds {
		if seen[k] {
			t.Errorf("duplicate InterfaceKind value: %v", k)
		}
		seen[k] = true
	}
}

// ---------------------------------------------------------------------------
// CompanionContext
// ---------------------------------------------------------------------------

func TestCompanionContext_Fields(t *testing.T) {
	lang := "zu"
	now := time.Now().UTC()
	ctx := circleai.CompanionContext{
		IdentityID:           "id-001",
		DisplayName:          "Sipho",
		PreferredLanguage:    &lang,
		Interface:            circleai.InterfaceKindMobile,
		PersonaHints:         "[User preferences]\nKeep responses brief.\n",
		AffectSummary:        "",
		RecentMemorySnippets: []string{"Hello!", "How are you?"},
		ActiveGoals:          []string{"Learn Go"},
		ContextBuiltAt:       now,
	}

	if ctx.IdentityID != "id-001" {
		t.Errorf("IdentityID: got %q", ctx.IdentityID)
	}
	if ctx.Interface != circleai.InterfaceKindMobile {
		t.Errorf("Interface: got %v", ctx.Interface)
	}
	if ctx.PreferredLanguage == nil || *ctx.PreferredLanguage != "zu" {
		t.Errorf("PreferredLanguage: got %v", ctx.PreferredLanguage)
	}
	if len(ctx.RecentMemorySnippets) != 2 {
		t.Errorf("RecentMemorySnippets count: got %d", len(ctx.RecentMemorySnippets))
	}
	if len(ctx.ActiveGoals) != 1 {
		t.Errorf("ActiveGoals count: got %d", len(ctx.ActiveGoals))
	}
}

// ---------------------------------------------------------------------------
// CompanionTurn
// ---------------------------------------------------------------------------

func TestCompanionTurn_Fields(t *testing.T) {
	now := time.Now().UTC()
	turn := circleai.CompanionTurn{
		Role:      "user",
		Content:   "Hello, B!",
		Timestamp: now,
	}
	if turn.Role != "user" {
		t.Errorf("Role: got %q", turn.Role)
	}
	if turn.Content != "Hello, B!" {
		t.Errorf("Content: got %q", turn.Content)
	}
	if !turn.Timestamp.Equal(now) {
		t.Errorf("Timestamp mismatch")
	}

	assistant := circleai.CompanionTurn{
		Role:      "assistant",
		Content:   "Hey! How can I help?",
		Timestamp: now,
	}
	if assistant.Role != "assistant" {
		t.Errorf("assistant Role: got %q", assistant.Role)
	}
}

// ---------------------------------------------------------------------------
// CompanionProactiveEvent
// ---------------------------------------------------------------------------

func TestCompanionProactiveEvent_Fields(t *testing.T) {
	now := time.Now().UTC()
	evt := circleai.CompanionProactiveEvent{
		SessionID:   "sess-42",
		IdentityID:  "id-001",
		Interface:   circleai.InterfaceKindMobile,
		Message:     "Just checking in — how's that Go project going?",
		TriggerName: "goal_checkin",
		GeneratedAt: now,
	}
	if evt.SessionID != "sess-42" {
		t.Errorf("SessionID: got %q", evt.SessionID)
	}
	if evt.TriggerName != "goal_checkin" {
		t.Errorf("TriggerName: got %q", evt.TriggerName)
	}
	if !evt.GeneratedAt.Equal(now) {
		t.Errorf("GeneratedAt mismatch")
	}
}

// ---------------------------------------------------------------------------
// PersonaState.ToSystemPromptHint — fixture-driven
// ---------------------------------------------------------------------------

type personaFixture struct {
	Vectors []personaVector `json:"vectors"`
}

type personaVector struct {
	ID           string       `json:"id"`
	Description  string       `json:"description"`
	Input        personaInput `json:"input"`
	ExpectedHint string       `json:"expectedHint"`
}

type personaInput struct {
	Verbosity       string  `json:"verbosity"`
	Formality       string  `json:"formality"`
	PreferredLocale *string `json:"preferredLocale"`
}

func loadPersonaFixture(t *testing.T) personaFixture {
	t.Helper()
	path := filepath.Join(fixturesDir(t), "persona_state.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("failed to read persona_state.json: %v", err)
	}
	var fix personaFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("failed to parse persona_state.json: %v", err)
	}
	return fix
}

func TestPersonaStateToSystemPromptHint_Fixture(t *testing.T) {
	fix := loadPersonaFixture(t)

	if len(fix.Vectors) == 0 {
		t.Fatal("no persona vectors found")
	}

	for _, v := range fix.Vectors {
		v := v
		t.Run(v.ID, func(t *testing.T) {
			ps := circleai.NewPersonaState("test-user")
			ps.Verbosity = v.Input.Verbosity
			ps.Formality = v.Input.Formality
			ps.PreferredLocale = v.Input.PreferredLocale

			got := ps.ToSystemPromptHint()
			if got != v.ExpectedHint {
				t.Errorf("ToSystemPromptHint:\n  got  %q\n  want %q", got, v.ExpectedHint)
			}
		})
	}
}
