// accessibility_board_test.go
//
// Verifies the CircleAI.Accessibility port (accessibility_board.go): profile
// set/get and the ordered adaptation-hint derivation (contrast, motion, aria,
// text-scale "F2", then one hint per need).

package circleai_test

import (
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestAccessibility_HintsFor(t *testing.T) {
	b := circleai.NewInMemoryAccessibilityBoard()
	b.SetProfile(circleai.UserAccessibilityProfile{
		UserId:        "u1",
		Needs:         []circleai.AccessibilityNeed{circleai.AccessibilityNeedVisual, circleai.AccessibilityNeedMotor},
		TextScale:     1.5,
		HighContrast:  true,
		ReducedMotion: true,
		ScreenReader:  true,
	})
	if got, ok := b.GetProfile("u1"); !ok || got.TextScale != 1.5 {
		t.Fatalf("get profile = %+v ok=%v", got, ok)
	}

	hints := b.HintsFor("u1")
	// Expected fixed order: contrast, motion, aria, text-scale, need(Visual), need(Motor).
	want := []circleai.AdaptationHint{
		{Kind: "contrast", Value: "high"},
		{Kind: "motion", Value: "reduced"},
		{Kind: "aria", Value: "verbose"},
		{Kind: "text-scale", Value: "1.50"},
		{Kind: "need", Value: "Visual"},
		{Kind: "need", Value: "Motor"},
	}
	if len(hints) != len(want) {
		t.Fatalf("hint count = %d, want %d: %+v", len(hints), len(want), hints)
	}
	for i := range want {
		if hints[i] != want[i] {
			t.Fatalf("hint[%d] = %+v, want %+v", i, hints[i], want[i])
		}
	}
}

func TestAccessibility_NoProfileAndMinimal(t *testing.T) {
	b := circleai.NewInMemoryAccessibilityBoard()
	if h := b.HintsFor("nobody"); len(h) != 0 {
		t.Fatalf("hints for unknown user must be empty: %+v", h)
	}
	// TextScale == 1 emits no text-scale hint; only ScreenReader set.
	b.SetProfile(circleai.UserAccessibilityProfile{UserId: "u2", TextScale: 1.0, ScreenReader: true})
	h := b.HintsFor("u2")
	if len(h) != 1 || h[0].Kind != "aria" {
		t.Fatalf("minimal hints failed: %+v", h)
	}
}
