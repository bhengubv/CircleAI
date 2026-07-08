// memory_feedback_analyser.go
//
// Analyses a window of FeedbackSignal records and produces PersonaAdaptation
// deltas. Ported from CircleAI.Memory.FeedbackAnalyser (C#) and mirrors the
// TypeScript pilot (memory/feedback_analyser.ts) 1:1.
//
// Rules (applied to the most-recent N signals, default N=20):
//   - >70% negative signals → VerbosityDelta = -0.1
//   - >70% positive signals → VerbosityDelta = +0.05
//   - FormalityDelta is always 0 (reserved for future heuristics)
//   - PreferredTopics is always empty — FeedbackSignal carries no topic tags
//
// The C# PersonaAdaptation holds `float` (float32) deltas. Go float32 is native,
// so the -0.1 / +0.05 constants are float32 literals — this keeps the cross-
// language fixture contract byte-identical.
//
// InMemoryFeedbackStore (the concrete IFeedbackStore) also lives here; it
// mirrors the C# InMemoryFeedbackStore + the TS stores.ts InMemoryFeedbackStore.

package circleai

import (
	"context"
	"errors"
	"sort"
	"sync"
)

// ---------------------------------------------------------------------------
// PersonaAdaptation + FeedbackAnalyser
// ---------------------------------------------------------------------------

// PersonaAdaptation holds deltas to apply to PersonaState after analysing
// feedback signals.
type PersonaAdaptation struct {
	// VerbosityDelta is the change to apply to the verbosity preference.
	VerbosityDelta float32

	// FormalityDelta is the change to apply to the formality preference.
	// Always 0 — reserved for future heuristics.
	FormalityDelta float32

	// PreferredTopics is the set of topics to boost. Always empty —
	// FeedbackSignal carries no topic metadata.
	PreferredTopics []string
}

// FP32 delta constants, matching the C# `float` literals -0.1f / +0.05f.
const (
	verbosityDown float32 = -0.1
	verbosityUp   float32 = 0.05
)

// FeedbackAnalyser analyses recent FeedbackSignal records and produces
// PersonaAdaptation adjustments.
type FeedbackAnalyser struct {
	windowSize int
}

// NewFeedbackAnalyser creates a FeedbackAnalyser considering the most-recent
// windowSize signals. windowSize must be at least 1.
func NewFeedbackAnalyser(windowSize int) (*FeedbackAnalyser, error) {
	if windowSize < 1 {
		return nil, errors.New("window size must be at least 1")
	}
	return &FeedbackAnalyser{windowSize: windowSize}, nil
}

// NewFeedbackAnalyserDefault creates a FeedbackAnalyser with the default
// window size of 20.
func NewFeedbackAnalyserDefault() *FeedbackAnalyser {
	return &FeedbackAnalyser{windowSize: 20}
}

// Analyse computes persona adaptation from the provided signals.
//
// VerbosityDelta is:
//   - -0.1  when more than 70% of the window is negative
//   - +0.05 when more than 70% of the window is positive
//   - 0     otherwise
//
// FormalityDelta is always 0 and PreferredTopics is always empty because
// FeedbackSignal carries no topic metadata.
func (a *FeedbackAnalyser) Analyse(signals []FeedbackSignal) PersonaAdaptation {
	// Most-recent-N by RecordedAtUTC descending.
	window := make([]FeedbackSignal, len(signals))
	copy(window, signals)
	sort.SliceStable(window, func(i, j int) bool {
		return window[i].RecordedAtUTC.After(window[j].RecordedAtUTC)
	})
	if len(window) > a.windowSize {
		window = window[:a.windowSize]
	}

	if len(window) == 0 {
		return PersonaAdaptation{VerbosityDelta: 0, FormalityDelta: 0, PreferredTopics: []string{}}
	}

	positiveCount := 0
	negativeCount := 0
	for _, s := range window {
		switch s.Polarity {
		case FeedbackPositive:
			positiveCount++
		case FeedbackNegative:
			negativeCount++
		}
	}
	total := len(window)

	var verbosityDelta float32
	negativeRatio := float32(negativeCount) / float32(total)
	positiveRatio := float32(positiveCount) / float32(total)

	if negativeRatio > 0.70 {
		verbosityDelta = verbosityDown
	} else if positiveRatio > 0.70 {
		verbosityDelta = verbosityUp
	}

	// FeedbackSignal has no tags — topic extraction is deferred.
	return PersonaAdaptation{VerbosityDelta: verbosityDelta, FormalityDelta: 0, PreferredTopics: []string{}}
}

// ---------------------------------------------------------------------------
// InMemoryFeedbackStore
// ---------------------------------------------------------------------------

// InMemoryFeedbackStore is an in-memory, thread-safe IFeedbackStore. Data is
// lost on process exit; for tests and headless CLI use. Capacity is capped
// (FIFO eviction). Ported from CircleAI.Memory.InMemoryFeedbackStore.
type InMemoryFeedbackStore struct {
	mu         sync.Mutex
	maxSignals int
	signals    []FeedbackSignal
}

// NewInMemoryFeedbackStore creates a store capped at maxSignals. When the cap
// is exceeded the oldest signals are evicted (FIFO). maxSignals must be
// positive.
func NewInMemoryFeedbackStore(maxSignals int) (*InMemoryFeedbackStore, error) {
	if maxSignals <= 0 {
		return nil, errors.New("maxSignals must be positive")
	}
	return &InMemoryFeedbackStore{maxSignals: maxSignals}, nil
}

// NewInMemoryFeedbackStoreDefault creates a store with the default cap of 10000.
func NewInMemoryFeedbackStoreDefault() *InMemoryFeedbackStore {
	return &InMemoryFeedbackStore{maxSignals: 10000}
}

// Add records a new feedback signal, evicting the oldest once the cap is
// exceeded (FIFO).
func (s *InMemoryFeedbackStore) Add(_ context.Context, signal FeedbackSignal) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.signals = append(s.signals, signal)
	for len(s.signals) > s.maxSignals {
		s.signals = s.signals[1:]
	}
	return nil
}

// GetRecent returns the most recent count signals, newest-first.
func (s *InMemoryFeedbackStore) GetRecent(_ context.Context, count int) ([]FeedbackSignal, error) {
	s.mu.Lock()
	snapshot := make([]FeedbackSignal, len(s.signals))
	copy(snapshot, s.signals)
	s.mu.Unlock()

	sort.SliceStable(snapshot, func(i, j int) bool {
		return snapshot[i].RecordedAtUTC.After(snapshot[j].RecordedAtUTC)
	})
	if count < 0 {
		count = 0
	}
	if count > len(snapshot) {
		count = len(snapshot)
	}
	out := make([]FeedbackSignal, count)
	copy(out, snapshot[:count])
	return out, nil
}

// Count returns the total number of signals stored.
func (s *InMemoryFeedbackStore) Count(_ context.Context) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.signals), nil
}

// PositiveRatio returns the fraction of stored signals that are
// FeedbackPositive (0.0–1.0), or nil when no signals are available.
func (s *InMemoryFeedbackStore) PositiveRatio(_ context.Context) (*float64, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if len(s.signals) == 0 {
		return nil, nil
	}
	pos := 0
	for _, sig := range s.signals {
		if sig.Polarity == FeedbackPositive {
			pos++
		}
	}
	ratio := float64(pos) / float64(len(s.signals))
	return &ratio, nil
}

// Compile-time assertion that the concrete store satisfies the interface.
var _ IFeedbackStore = (*InMemoryFeedbackStore)(nil)
