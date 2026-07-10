// speech_cloud_intent_router_test.go
//
// Verifies speech_cloud_intent_router.go: the KeywordVoiceIntentRouter first-hit
// ordering, named-capture extraction (numeric/unnamed groups skipped, values
// trimmed, empties dropped), empty-transcript fallback, and NullVoiceIntentRouter.

package circleai_test

import (
	"context"
	"regexp"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func mustRE(s string) *regexp.Regexp { return regexp.MustCompile(s) }

func TestKeywordIntentRouter_FirstHitWithCaptures(t *testing.T) {
	intents := []circleai.VoiceIntent{
		{Name: "set-timer", Pattern: mustRE(`(?i)^set (?:a )?timer for (?P<duration>.+)$`)},
		{Name: "play-music", Pattern: mustRE(`(?i)^play (?P<track>.+)$`)},
	}
	r, err := circleai.NewKeywordVoiceIntentRouter(intents, "ask-ai")
	if err != nil {
		t.Fatal(err)
	}
	if r.BackendID() != "keyword" {
		t.Errorf("backend %q", r.BackendID())
	}

	m, err := r.Route(context.Background(), "  set a timer for 5 minutes  ")
	if err != nil {
		t.Fatal(err)
	}
	if m.IntentName != "set-timer" {
		t.Errorf("intent %q", m.IntentName)
	}
	if m.Transcript != "set a timer for 5 minutes" {
		t.Errorf("transcript %q", m.Transcript)
	}
	if m.Captures["duration"] != "5 minutes" {
		t.Errorf("capture duration = %q", m.Captures["duration"])
	}
	// Only the named group is surfaced (no "0"/whole-match key).
	if len(m.Captures) != 1 {
		t.Errorf("captures = %+v, want only 'duration'", m.Captures)
	}
}

func TestKeywordIntentRouter_FallbackOnNoMatch(t *testing.T) {
	intents := []circleai.VoiceIntent{
		{Name: "greet", Pattern: mustRE(`(?i)^hello$`)},
	}
	r, _ := circleai.NewKeywordVoiceIntentRouter(intents, "ask-ai")
	m, _ := r.Route(context.Background(), "what is the meaning of life")
	if m.IntentName != "ask-ai" {
		t.Errorf("fallback intent %q", m.IntentName)
	}
	if len(m.Captures) != 0 {
		t.Errorf("fallback captures %+v", m.Captures)
	}
	if m.Transcript != "what is the meaning of life" {
		t.Errorf("fallback transcript %q", m.Transcript)
	}
}

func TestKeywordIntentRouter_EmptyTranscript(t *testing.T) {
	r, _ := circleai.NewKeywordVoiceIntentRouter([]circleai.VoiceIntent{}, "ask-ai")
	m, _ := r.Route(context.Background(), "   ")
	if m.IntentName != "ask-ai" || m.Transcript != "" || len(m.Captures) != 0 {
		t.Errorf("empty transcript match %+v", m)
	}
}

func TestKeywordIntentRouter_EmptyNamedGroupDropped(t *testing.T) {
	// A named group that matches empty must NOT appear in Captures.
	intents := []circleai.VoiceIntent{
		{Name: "cmd", Pattern: mustRE(`(?i)^go(?P<rest>.*)$`)},
	}
	r, _ := circleai.NewKeywordVoiceIntentRouter(intents, "ask-ai")
	m, _ := r.Route(context.Background(), "go")
	if _, ok := m.Captures["rest"]; ok {
		t.Errorf("empty named group should be dropped, got %+v", m.Captures)
	}
}

func TestKeywordIntentRouter_DefaultFallbackName(t *testing.T) {
	// Empty fallbackIntentName defaults to "ask-ai".
	r, err := circleai.NewKeywordVoiceIntentRouter([]circleai.VoiceIntent{}, "")
	if err != nil {
		t.Fatal(err)
	}
	m, _ := r.Route(context.Background(), "anything")
	if m.IntentName != "ask-ai" {
		t.Errorf("default fallback %q", m.IntentName)
	}
}

func TestKeywordIntentRouter_NilIntentsRejected(t *testing.T) {
	if _, err := circleai.NewKeywordVoiceIntentRouter(nil, "ask-ai"); err == nil {
		t.Error("nil intents should be rejected")
	}
}

func TestNullIntentRouter(t *testing.T) {
	r := circleai.NullVoiceIntentRouterInstance
	if r.BackendID() != "null" {
		t.Errorf("backend %q", r.BackendID())
	}
	m, _ := r.Route(context.Background(), "hello there")
	if m.IntentName != "ask-ai" || m.Transcript != "hello there" || len(m.Captures) != 0 {
		t.Errorf("null router %+v", m)
	}
}
