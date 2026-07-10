// speech_end_of_turn_test.go
//
// Verifies the CircleAI.Speech end-of-turn detectors: null (always complete),
// rule-based (terminal punctuation + hanging connectors + silence ceilings), and
// smart-turn (runner-vs-rule fallback). The millisecond arithmetic and confidence
// levels mirror the C# RuleBasedEndOfTurnDetector exactly.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestNullEndOfTurn_AlwaysComplete(t *testing.T) {
	d := circleai.NullEndOfTurnDetectorInstance
	if d.BackendID() != "null" {
		t.Errorf("backend %q", d.BackendID())
	}
	r := d.Predict("anything", 0)
	if !r.IsComplete || r.Confidence != 1 || r.WaitMoreMs != 0 {
		t.Errorf("null eot %+v", r)
	}
}

func TestRuleBased_MaxSilenceForcesComplete(t *testing.T) {
	d := circleai.NewDefaultRuleBasedEndOfTurnDetector()
	if d.BackendID() != "rules" {
		t.Errorf("backend %q", d.BackendID())
	}
	// Silence >= 2500ms -> complete at 0.7 regardless of text.
	r := d.Predict("still going and", 2500*time.Millisecond)
	if !r.IsComplete || r.Confidence != 0.7 {
		t.Errorf("max-silence %+v", r)
	}
}

func TestRuleBased_EmptyTextWaits(t *testing.T) {
	d := circleai.NewDefaultRuleBasedEndOfTurnDetector()
	r := d.Predict("   ", 100*time.Millisecond)
	if r.IsComplete || r.Confidence != 0.2 {
		t.Errorf("empty text %+v", r)
	}
	// wait = max(150, 400-100=300) = 300.
	if r.WaitMoreMs != 300 {
		t.Errorf("empty-text wait = %d, want 300", r.WaitMoreMs)
	}
}

func TestRuleBased_EmptyTextWaitFloor(t *testing.T) {
	d := circleai.NewDefaultRuleBasedEndOfTurnDetector()
	// silence 380 -> 400-380=20 -> floored to 150.
	r := d.Predict("", 380*time.Millisecond)
	if r.WaitMoreMs != 150 {
		t.Errorf("empty-text wait floor = %d, want 150", r.WaitMoreMs)
	}
}

func TestRuleBased_HangingWordExtendsWait(t *testing.T) {
	d := circleai.NewDefaultRuleBasedEndOfTurnDetector()
	// Ends with "and" (hanging); silence 500 < hanging 900 -> incomplete, wait
	// ceil(900-500=400)=400, confidence 0.4.
	r := d.Predict("I went to the store and", 500*time.Millisecond)
	if r.IsComplete || r.Confidence != 0.4 || r.WaitMoreMs != 400 {
		t.Errorf("hanging %+v", r)
	}
	// Once silence >= hanging 900, it completes at 0.6.
	r2 := d.Predict("... and", 900*time.Millisecond)
	if !r2.IsComplete || r2.Confidence != 0.6 {
		t.Errorf("hanging complete %+v", r2)
	}
}

func TestRuleBased_TerminalPunctuation(t *testing.T) {
	d := circleai.NewDefaultRuleBasedEndOfTurnDetector()
	// Ends with "." and silence 400 >= min 400 -> complete at 0.9.
	r := d.Predict("That is all.", 400*time.Millisecond)
	if !r.IsComplete || r.Confidence != 0.9 {
		t.Errorf("terminal %+v", r)
	}
	// CJK terminal punctuation counts too.
	r2 := d.Predict("好的。", 400*time.Millisecond)
	if !r2.IsComplete || r2.Confidence != 0.9 {
		t.Errorf("cjk terminal %+v", r2)
	}
}

func TestRuleBased_NonTerminalWithSilence(t *testing.T) {
	d := circleai.NewDefaultRuleBasedEndOfTurnDetector()
	// Non-terminal, non-hanging, silence >= min -> complete at 0.75.
	r := d.Predict("hello world", 500*time.Millisecond)
	if !r.IsComplete || r.Confidence != 0.75 {
		t.Errorf("non-terminal silence %+v", r)
	}
}

func TestRuleBased_NonTerminalStillWaiting(t *testing.T) {
	d := circleai.NewDefaultRuleBasedEndOfTurnDetector()
	// Non-terminal, silence 200 < min 400 -> incomplete, wait max(50, 200)=200, conf 0.6.
	r := d.Predict("hello world", 200*time.Millisecond)
	if r.IsComplete || r.Confidence != 0.6 || r.WaitMoreMs != 200 {
		t.Errorf("still waiting %+v", r)
	}
}

type fakeTurnRunner struct{ score float32 }

func (r fakeTurnRunner) ScoreCompletion(string, time.Duration) float32 { return r.score }

func TestSmartTurn_RunnerVsFallback(t *testing.T) {
	nilD := circleai.NewDefaultSmartTurnDetector(nil)
	if nilD.BackendID() != "smart-turn (fallback)" {
		t.Errorf("nil backend %q", nilD.BackendID())
	}
	// Falls back to rules: terminal + enough silence -> complete.
	if r := nilD.Predict("Done.", 500*time.Millisecond); !r.IsComplete {
		t.Errorf("fallback should complete: %+v", r)
	}

	d := circleai.NewDefaultSmartTurnDetector(fakeTurnRunner{score: 0.8})
	if d.BackendID() != "smart-turn-v2" {
		t.Errorf("backend %q", d.BackendID())
	}
	r := d.Predict("whatever", 0)
	if !r.IsComplete || r.Confidence != 0.8 {
		t.Errorf("runner complete %+v", r)
	}

	low := circleai.NewDefaultSmartTurnDetector(fakeTurnRunner{score: 0.25})
	r2 := low.Predict("whatever", 0)
	// wait = round((1-0.25)*1000) = 750.
	if r2.IsComplete || r2.WaitMoreMs != 750 {
		t.Errorf("runner incomplete %+v", r2)
	}
}
