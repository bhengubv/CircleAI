// feedback_analyser_test.go
//
// Exercises FeedbackAnalyser (persona-adaptation deltas from a window of
// signals) and InMemoryFeedbackStore. Mirrors the TS pilot suite
// tests/feedback_analyser.test.ts 1:1 and the C# FeedbackAnalyser rules.

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// FP32 deltas — must equal the C# `float` literals exactly.
const (
	verbosityDown float32 = -0.1
	verbosityUp   float32 = 0.05
)

var fbSeq int64

// mkSignal builds a FeedbackSignal with a monotonic default timestamp so window
// ordering is deterministic per call (mirrors the TS `make` helper).
func mkSignal(polarity circleai.FeedbackPolarity, at time.Time, userText string) circleai.FeedbackSignal {
	if at.IsZero() {
		at = time.UnixMilli(1_700_000_000_000 + fbSeq*1000).UTC()
		fbSeq++
	}
	if userText == "" {
		userText = "user"
	}
	s := circleai.NewFeedbackSignal(userText, "response", polarity)
	s.RecordedAtUTC = at
	return s
}

// ── FeedbackAnalyser ─────────────────────────────────────────────────────────

func TestFeedbackAnalyser_WindowSizeGuard(t *testing.T) {
	if _, err := circleai.NewFeedbackAnalyser(0); err == nil {
		t.Errorf("expected error for windowSize=0")
	}
	if _, err := circleai.NewFeedbackAnalyser(1); err != nil {
		t.Errorf("windowSize=1 should be valid: %v", err)
	}
}

func TestFeedbackAnalyser_EmptySignalSet(t *testing.T) {
	a := circleai.NewFeedbackAnalyserDefault().Analyse(nil)
	if a.VerbosityDelta != 0 {
		t.Errorf("verbosityDelta: got %v want 0", a.VerbosityDelta)
	}
	if a.FormalityDelta != 0 {
		t.Errorf("formalityDelta: got %v want 0", a.FormalityDelta)
	}
	if len(a.PreferredTopics) != 0 {
		t.Errorf("preferredTopics: got %v want empty", a.PreferredTopics)
	}
}

func TestFeedbackAnalyser_NegativeMajorityDropsVerbosity(t *testing.T) {
	analyser := circleai.NewFeedbackAnalyserDefault()
	// 8 negative + 2 positive = 80% negative.
	var signals []circleai.FeedbackSignal
	for i := 0; i < 8; i++ {
		signals = append(signals, mkSignal(circleai.FeedbackNegative, time.Time{}, ""))
	}
	for i := 0; i < 2; i++ {
		signals = append(signals, mkSignal(circleai.FeedbackPositive, time.Time{}, ""))
	}

	a := analyser.Analyse(signals)
	if a.VerbosityDelta != verbosityDown {
		t.Errorf("verbosityDelta: got %v want %v", a.VerbosityDelta, verbosityDown)
	}
	if a.FormalityDelta != 0 {
		t.Errorf("formalityDelta: got %v want 0", a.FormalityDelta)
	}
	if len(a.PreferredTopics) != 0 {
		t.Errorf("preferredTopics: got %v want empty", a.PreferredTopics)
	}
}

func TestFeedbackAnalyser_PositiveMajorityRaisesVerbosity(t *testing.T) {
	analyser := circleai.NewFeedbackAnalyserDefault()
	var signals []circleai.FeedbackSignal
	for i := 0; i < 8; i++ {
		signals = append(signals, mkSignal(circleai.FeedbackPositive, time.Time{}, ""))
	}
	for i := 0; i < 2; i++ {
		signals = append(signals, mkSignal(circleai.FeedbackNegative, time.Time{}, ""))
	}

	if got := analyser.Analyse(signals).VerbosityDelta; got != verbosityUp {
		t.Errorf("verbosityDelta: got %v want %v", got, verbosityUp)
	}
}

func TestFeedbackAnalyser_BalancedWindow(t *testing.T) {
	analyser := circleai.NewFeedbackAnalyserDefault()
	var signals []circleai.FeedbackSignal
	for i := 0; i < 5; i++ {
		signals = append(signals, mkSignal(circleai.FeedbackPositive, time.Time{}, ""))
	}
	for i := 0; i < 5; i++ {
		signals = append(signals, mkSignal(circleai.FeedbackNegative, time.Time{}, ""))
	}
	if got := analyser.Analyse(signals).VerbosityDelta; got != 0 {
		t.Errorf("verbosityDelta: got %v want 0", got)
	}
}

func TestFeedbackAnalyser_ExactlySeventyPercentNotCrossing(t *testing.T) {
	analyser, _ := circleai.NewFeedbackAnalyser(10)
	// Exactly 7/10 negative — 0.70 is not > 0.70.
	var signals []circleai.FeedbackSignal
	for i := 0; i < 7; i++ {
		signals = append(signals, mkSignal(circleai.FeedbackNegative, time.Time{}, ""))
	}
	for i := 0; i < 3; i++ {
		signals = append(signals, mkSignal(circleai.FeedbackPositive, time.Time{}, ""))
	}
	if got := analyser.Analyse(signals).VerbosityDelta; got != 0 {
		t.Errorf("verbosityDelta: got %v want 0 (strict >)", got)
	}
}

func TestFeedbackAnalyser_OnlyMostRecentWindow(t *testing.T) {
	analyser, _ := circleai.NewFeedbackAnalyser(3)
	// Older bulk is positive; the 3 newest are negative → window is 100% negative.
	var signals []circleai.FeedbackSignal
	for i := 0; i < 10; i++ {
		signals = append(signals, mkSignal(circleai.FeedbackPositive, time.UnixMilli(1000+int64(i)).UTC(), ""))
	}
	for i := 0; i < 3; i++ {
		signals = append(signals, mkSignal(circleai.FeedbackNegative, time.UnixMilli(9_000_000+int64(i)).UTC(), ""))
	}
	if got := analyser.Analyse(signals).VerbosityDelta; got != verbosityDown {
		t.Errorf("verbosityDelta: got %v want %v", got, verbosityDown)
	}
}

func TestFeedbackAnalyser_IgnoresCorrectionSignals(t *testing.T) {
	analyser := circleai.NewFeedbackAnalyserDefault()
	// 8 negative + 2 correction = 8/10 = 80% negative → down.
	var signals []circleai.FeedbackSignal
	for i := 0; i < 8; i++ {
		signals = append(signals, mkSignal(circleai.FeedbackNegative, time.Time{}, ""))
	}
	for i := 0; i < 2; i++ {
		signals = append(signals, mkSignal(circleai.FeedbackCorrection, time.Time{}, ""))
	}
	if got := analyser.Analyse(signals).VerbosityDelta; got != verbosityDown {
		t.Errorf("verbosityDelta: got %v want %v", got, verbosityDown)
	}
}

// ── InMemoryFeedbackStore ────────────────────────────────────────────────────

func TestInMemoryFeedbackStore_AddIncrementsCount(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryFeedbackStoreDefault()
	if err := store.Add(ctx, mkSignal(circleai.FeedbackPositive, time.Time{}, "")); err != nil {
		t.Fatalf("Add: %v", err)
	}
	if n, _ := store.Count(ctx); n != 1 {
		t.Errorf("count: got %d want 1", n)
	}
}

func TestInMemoryFeedbackStore_GetRecentEmpty(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryFeedbackStoreDefault()
	r, err := store.GetRecent(ctx, 10)
	if err != nil {
		t.Fatalf("GetRecent: %v", err)
	}
	if len(r) != 0 {
		t.Errorf("len: got %d want 0", len(r))
	}
}

func TestInMemoryFeedbackStore_GetRecentNewestFirst(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryFeedbackStoreDefault()
	now := time.Now().UTC()
	mustAddSignal(t, store, mkSignal(circleai.FeedbackPositive, now.Add(-10*time.Minute), "old"))
	mustAddSignal(t, store, mkSignal(circleai.FeedbackNegative, now, "new"))

	r, err := store.GetRecent(ctx, 10)
	if err != nil {
		t.Fatalf("GetRecent: %v", err)
	}
	if len(r) != 2 {
		t.Fatalf("len: got %d want 2", len(r))
	}
	if r[0].UserText != "new" {
		t.Errorf("r[0].UserText: got %q want new", r[0].UserText)
	}
}

func TestInMemoryFeedbackStore_PositiveRatioNilWhenEmpty(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryFeedbackStoreDefault()
	ratio, err := store.PositiveRatio(ctx)
	if err != nil {
		t.Fatalf("PositiveRatio: %v", err)
	}
	if ratio != nil {
		t.Errorf("ratio: got %v want nil", *ratio)
	}
}

func TestInMemoryFeedbackStore_PositiveRatioAllPositive(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryFeedbackStoreDefault()
	mustAddSignal(t, store, mkSignal(circleai.FeedbackPositive, time.Time{}, ""))
	mustAddSignal(t, store, mkSignal(circleai.FeedbackPositive, time.Time{}, ""))
	ratio, _ := store.PositiveRatio(ctx)
	if ratio == nil || *ratio != 1.0 {
		t.Errorf("ratio: got %v want 1.0", ratio)
	}
}

func TestInMemoryFeedbackStore_PositiveRatioMixed(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryFeedbackStoreDefault()
	mustAddSignal(t, store, mkSignal(circleai.FeedbackPositive, time.Time{}, ""))
	mustAddSignal(t, store, mkSignal(circleai.FeedbackPositive, time.Time{}, ""))
	mustAddSignal(t, store, mkSignal(circleai.FeedbackNegative, time.Time{}, ""))
	ratio, _ := store.PositiveRatio(ctx)
	if ratio == nil || *ratio <= 0.66 || *ratio >= 0.68 { // 2/3
		t.Errorf("ratio: got %v want ~0.667", ratio)
	}
}

func TestInMemoryFeedbackStore_FifoEviction(t *testing.T) {
	ctx := context.Background()
	store, err := circleai.NewInMemoryFeedbackStore(3)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	for i := 0; i < 5; i++ {
		mustAddSignal(t, store, mkSignal(circleai.FeedbackPositive, time.Time{}, ""))
	}
	if n, _ := store.Count(ctx); n != 3 {
		t.Errorf("count: got %d want 3", n)
	}
}

func TestInMemoryFeedbackStore_RejectsNonPositiveMax(t *testing.T) {
	if _, err := circleai.NewInMemoryFeedbackStore(0); err == nil {
		t.Errorf("expected error for maxSignals=0")
	}
}

func mustAddSignal(t *testing.T, store circleai.IFeedbackStore, s circleai.FeedbackSignal) {
	t.Helper()
	if err := store.Add(context.Background(), s); err != nil {
		t.Fatalf("Add: %v", err)
	}
}
