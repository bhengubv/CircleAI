// accessibility_board.go
//
// Ports the CircleAI.Accessibility primitive vertical
// (AccessibilityPrimitives.cs):
//   AccessibilityNeed (enum) -> AccessibilityNeed (int consts, stable ordinals)
//   UserAccessibilityProfile / AdaptationHint (records) -> value structs
//   IAccessibilityBoard      -> AccessibilityBoard interface (I-prefix dropped)
//   InMemoryAccessibilityBoard -> InMemoryAccessibilityBoard
//
// The AccessibilityDomainContext / AccessibilityCompanionAdapter (LLM glue) are
// out of scope.
//
// DETERMINISM: HintsFor emits hints in the fixed C# order — contrast, motion,
// aria, text-scale (formatted "F2"), then one "need" hint per need in the
// profile's declared order. AccessibilityNeed.String reproduces the C# enum
// ToString() used for the need hint value.

package circleai

import (
	"strconv"
	"sync"
)

// AccessibilityNeed enumerates accessibility need categories. Ports the
// AccessibilityNeed enum; ordinals are stable (Visual=0..Speech=4).
type AccessibilityNeed int

const (
	AccessibilityNeedVisual AccessibilityNeed = iota
	AccessibilityNeedHearing
	AccessibilityNeedMotor
	AccessibilityNeedCognitive
	AccessibilityNeedSpeech
)

// String returns the C# enum member name for this need (used as the "need" hint
// value). Ports AccessibilityNeed.ToString().
func (n AccessibilityNeed) String() string {
	switch n {
	case AccessibilityNeedVisual:
		return "Visual"
	case AccessibilityNeedHearing:
		return "Hearing"
	case AccessibilityNeedMotor:
		return "Motor"
	case AccessibilityNeedCognitive:
		return "Cognitive"
	case AccessibilityNeedSpeech:
		return "Speech"
	default:
		return strconv.Itoa(int(n))
	}
}

// UserAccessibilityProfile is a user's accessibility profile. Ports the
// UserAccessibilityProfile record. Needs mirrors the C# IReadOnlyList.
type UserAccessibilityProfile struct {
	UserId       string
	Needs        []AccessibilityNeed
	TextScale    float64
	HighContrast bool
	ReducedMotion bool
	ScreenReader bool
}

// AdaptationHint is a UI adaptation hint. Ports the AdaptationHint record.
type AdaptationHint struct {
	Kind  string
	Value string
}

// AccessibilityBoard is the accessibility-profiles board. Ports
// IAccessibilityBoard.
type AccessibilityBoard interface {
	SetProfile(p UserAccessibilityProfile)
	GetProfile(userId string) (UserAccessibilityProfile, bool)
	// HintsFor derives adaptation hints from a user's profile (empty if none).
	HintsFor(userId string) []AdaptationHint
}

// InMemoryAccessibilityBoard is a concurrency-safe in-memory AccessibilityBoard.
// Ports InMemoryAccessibilityBoard.
type InMemoryAccessibilityBoard struct {
	mu       sync.Mutex
	profiles map[string]UserAccessibilityProfile
}

// NewInMemoryAccessibilityBoard constructs an empty board.
func NewInMemoryAccessibilityBoard() *InMemoryAccessibilityBoard {
	return &InMemoryAccessibilityBoard{profiles: make(map[string]UserAccessibilityProfile)}
}

// SetProfile stores (or replaces by UserId) a profile. Ports SetProfile.
func (b *InMemoryAccessibilityBoard) SetProfile(p UserAccessibilityProfile) {
	b.mu.Lock()
	b.profiles[p.UserId] = p
	b.mu.Unlock()
}

// GetProfile returns the user's profile, or (zero,false). Ports GetProfile.
func (b *InMemoryAccessibilityBoard) GetProfile(userId string) (UserAccessibilityProfile, bool) {
	b.mu.Lock()
	p, ok := b.profiles[userId]
	b.mu.Unlock()
	return p, ok
}

// HintsFor derives adaptation hints from a user's profile. Ports HintsFor (no
// profile -> empty slice). Hint order and the text-scale "F2" format match C#.
func (b *InMemoryAccessibilityBoard) HintsFor(userId string) []AdaptationHint {
	b.mu.Lock()
	p, ok := b.profiles[userId]
	b.mu.Unlock()
	if !ok {
		return []AdaptationHint{}
	}
	hints := make([]AdaptationHint, 0)
	if p.HighContrast {
		hints = append(hints, AdaptationHint{Kind: "contrast", Value: "high"})
	}
	if p.ReducedMotion {
		hints = append(hints, AdaptationHint{Kind: "motion", Value: "reduced"})
	}
	if p.ScreenReader {
		hints = append(hints, AdaptationHint{Kind: "aria", Value: "verbose"})
	}
	if p.TextScale > 1 {
		hints = append(hints, AdaptationHint{Kind: "text-scale", Value: strconv.FormatFloat(p.TextScale, 'f', 2, 64)})
	}
	for _, n := range p.Needs {
		hints = append(hints, AdaptationHint{Kind: "need", Value: n.String()})
	}
	return hints
}

// Interface guard.
var _ AccessibilityBoard = (*InMemoryAccessibilityBoard)(nil)
