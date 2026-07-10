// speech_end_of_turn.go
//
// Ports CircleAI.Speech.EndOfTurnDetectors.cs:
//   - NullEndOfTurnDetector: always "they finished" DI default.
//   - RuleBasedEndOfTurnDetector: punctuation + trailing-silence heuristics,
//     with "thinking" connectors (and, but, so, um, like...) extending the wait.
//   - ITurnModelRunner: host-supplied semantic turn-model seam.
//   - SmartTurnDetector: wraps the runner, falling back to the rule-based
//     detector when none is wired.
//
// Millisecond arithmetic reproduces the C# exactly: Math.Max/Math.Ceiling on the
// double TotalMilliseconds, then truncation to int.

package circleai

import (
	"math"
	"strings"
	"time"
)

// terminalPunctuation matches TerminalPunctuation (incl. CJK forms).
var terminalPunctuation = []string{".", "!", "?", "。", "！", "？"}

// hangingWords matches HangingWords — trailing connectors that extend the wait.
var hangingWords = map[string]struct{}{
	"and": {}, "but": {}, "so": {}, "or": {}, "because": {}, "if": {}, "when": {}, "while": {},
	"though": {}, "however": {}, "um": {}, "uh": {}, "like": {}, "you": {}, "the": {}, "a": {}, "an": {},
}

// NullEndOfTurnDetector always says "they finished" — DI default. Ports
// NullEndOfTurnDetector.
type NullEndOfTurnDetector struct{}

// NullEndOfTurnDetectorInstance mirrors NullEndOfTurnDetector.Instance.
var NullEndOfTurnDetectorInstance = NullEndOfTurnDetector{}

// BackendID returns "null".
func (NullEndOfTurnDetector) BackendID() string { return "null" }

// Predict always reports the turn complete.
func (NullEndOfTurnDetector) Predict(string, time.Duration) EndOfTurnResult {
	return EndOfTurnResult{IsComplete: true, Confidence: 1, WaitMoreMs: 0}
}

// Reset is a no-op.
func (NullEndOfTurnDetector) Reset() {}

// RuleBasedEndOfTurnDetector considers a turn complete when the transcript ends
// with terminal punctuation AND the user has been silent for at least the
// minimum hangover, OR when silence exceeds the max-wait ceiling regardless of
// text. Ports RuleBasedEndOfTurnDetector.
type RuleBasedEndOfTurnDetector struct {
	minSilence     time.Duration
	hangingSilence time.Duration
	maxSilence     time.Duration
}

// NewRuleBasedEndOfTurnDetector constructs a rule-based detector. Defaults:
// minSilence=400ms, hangingSilence=900ms, maxSilence=2500ms. Ports the
// constructor.
func NewRuleBasedEndOfTurnDetector(minSilence, hangingSilence, maxSilence time.Duration) *RuleBasedEndOfTurnDetector {
	return &RuleBasedEndOfTurnDetector{minSilence: minSilence, hangingSilence: hangingSilence, maxSilence: maxSilence}
}

// NewDefaultRuleBasedEndOfTurnDetector constructs a rule-based detector with the
// C# default hangovers (400 / 900 / 2500 ms).
func NewDefaultRuleBasedEndOfTurnDetector() *RuleBasedEndOfTurnDetector {
	return NewRuleBasedEndOfTurnDetector(400*time.Millisecond, 900*time.Millisecond, 2500*time.Millisecond)
}

// BackendID returns "rules".
func (d *RuleBasedEndOfTurnDetector) BackendID() string { return "rules" }

// Predict classifies the current state.
func (d *RuleBasedEndOfTurnDetector) Predict(partialTranscript string, trailingSilence time.Duration) EndOfTurnResult {
	text := strings.TrimSpace(partialTranscript)
	if trailingSilence >= d.maxSilence {
		return EndOfTurnResult{IsComplete: true, Confidence: 0.7, WaitMoreMs: 0}
	}

	if len(text) == 0 {
		wait := math.Max(150, durationMs(d.minSilence-trailingSilence))
		return EndOfTurnResult{IsComplete: false, Confidence: 0.2, WaitMoreMs: int(wait)}
	}

	endsTerminal := false
	for _, p := range terminalPunctuation {
		if strings.HasSuffix(text, p) {
			endsTerminal = true
			break
		}
	}

	lastWord := lastToken(text)
	_, endsHanging := hangingWords[strings.ToLower(strings.TrimRight(lastWord, ".,!?"))]

	if endsHanging {
		remaining := d.hangingSilence - trailingSilence
		if remaining <= 0 {
			return EndOfTurnResult{IsComplete: true, Confidence: 0.6, WaitMoreMs: 0}
		}
		return EndOfTurnResult{IsComplete: false, Confidence: 0.4, WaitMoreMs: int(math.Ceil(durationMs(remaining)))}
	}

	if endsTerminal && trailingSilence >= d.minSilence {
		return EndOfTurnResult{IsComplete: true, Confidence: 0.9, WaitMoreMs: 0}
	}

	if trailingSilence >= d.minSilence {
		return EndOfTurnResult{IsComplete: true, Confidence: 0.75, WaitMoreMs: 0}
	}

	ms := math.Max(50, durationMs(d.minSilence-trailingSilence))
	return EndOfTurnResult{IsComplete: false, Confidence: 0.6, WaitMoreMs: int(ms)}
}

// Reset is a no-op.
func (d *RuleBasedEndOfTurnDetector) Reset() {}

// ITurnModelRunner is a host-supplied semantic turn model. Ports
// ITurnModelRunner.
type ITurnModelRunner interface {
	// ScoreCompletion scores the current state; 0..1 = probability the turn is complete.
	ScoreCompletion(partialTranscript string, trailingSilence time.Duration) float32
}

// SmartTurnDetector uses the supplied semantic model when present; otherwise
// falls back to the rule-based detector. Ports SmartTurnDetector.
type SmartTurnDetector struct {
	runner    ITurnModelRunner
	fallback  *RuleBasedEndOfTurnDetector
	threshold float32
}

// NewSmartTurnDetector constructs a smart-turn wrapper. Pass nil runner to use
// the rule-based fallback. Default threshold=0.5. Ports the SmartTurnDetector
// constructor.
func NewSmartTurnDetector(runner ITurnModelRunner, threshold float32) *SmartTurnDetector {
	return &SmartTurnDetector{runner: runner, fallback: NewDefaultRuleBasedEndOfTurnDetector(), threshold: threshold}
}

// NewDefaultSmartTurnDetector constructs a smart-turn wrapper with the C#
// default threshold (0.5).
func NewDefaultSmartTurnDetector(runner ITurnModelRunner) *SmartTurnDetector {
	return NewSmartTurnDetector(runner, 0.5)
}

// BackendID returns "smart-turn-v2" or "smart-turn (fallback)".
func (d *SmartTurnDetector) BackendID() string {
	if d.runner == nil {
		return "smart-turn (fallback)"
	}
	return "smart-turn-v2"
}

// Predict classifies the current state — via the model, or the rule fallback.
func (d *SmartTurnDetector) Predict(partialTranscript string, trailingSilence time.Duration) EndOfTurnResult {
	if d.runner == nil {
		return d.fallback.Predict(partialTranscript, trailingSilence)
	}

	prob := clampFloat32(d.runner.ScoreCompletion(partialTranscript, trailingSilence), 0, 1)
	if prob >= d.threshold {
		return EndOfTurnResult{IsComplete: true, Confidence: prob, WaitMoreMs: 0}
	}
	waitMs := int(math.Round(float64((1 - prob) * 1000)))
	return EndOfTurnResult{IsComplete: false, Confidence: prob, WaitMoreMs: waitMs}
}

// Reset resets the fallback.
func (d *SmartTurnDetector) Reset() { d.fallback.Reset() }

// durationMs returns the total milliseconds of d as a float64 (mirrors
// TimeSpan.TotalMilliseconds, which is fractional and may be negative).
func durationMs(d time.Duration) float64 {
	return float64(d) / float64(time.Millisecond)
}

// lastToken returns the last whitespace-delimited token of text (mirrors
// Split(' ','\t','\n', RemoveEmptyEntries).LastOrDefault() ?? "").
func lastToken(text string) string {
	fields := strings.FieldsFunc(text, func(r rune) bool {
		return r == ' ' || r == '\t' || r == '\n'
	})
	if len(fields) == 0 {
		return ""
	}
	return fields[len(fields)-1]
}

// Interface guards.
var (
	_ IEndOfTurnDetector = NullEndOfTurnDetector{}
	_ IEndOfTurnDetector = (*RuleBasedEndOfTurnDetector)(nil)
	_ IEndOfTurnDetector = (*SmartTurnDetector)(nil)
)
