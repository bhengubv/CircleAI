// companion_predictive_engine.go
//
// Ported from CircleAI.Companion (HerJarvisContracts.cs + HerJarvisRealImplementations.cs)
// — the C# reference:
//   - IPredictiveEngine                (contract 14)
//   - AnticipatedNeed                  (record)
//   - HistogramPredictiveEngine        (concrete: time-of-day histogram)
//   - SequencePredictiveEngine         (concrete: first-order Markov sequence)
//
// A predictive engine anticipates upcoming needs over a horizon. In-memory,
// deterministic. C# ValueTask<IReadOnlyList<AnticipatedNeed>> becomes a
// synchronous ([]AnticipatedNeed, error) that honours ctx cancellation.

package circleai

import (
	"context"
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// AnticipatedNeed is a predicted upcoming need with the time it is expected by
// and the probability of its occurrence. Ported from the C# record
// AnticipatedNeed(string Description, DateTimeOffset ExpectedByUtc, double Probability).
type AnticipatedNeed struct {
	Description   string
	ExpectedByUTC time.Time
	Probability   float64
}

// IPredictiveEngine is the anticipation contract (C# IPredictiveEngine).
// Anticipate returns needs likely to arise within horizonMinutes, ordered by
// descending probability.
type IPredictiveEngine interface {
	Anticipate(ctx context.Context, horizonMinutes int) ([]AnticipatedNeed, error)
}

// nowFunc is the clock used by the predictive engines; overridable in tests so
// the time-of-day histogram is deterministic. Defaults to time.Now().UTC().
type nowFunc func() time.Time

// HistogramPredictiveEngine models each recurring need as a 24×7 hour-of-week
// histogram and anticipates needs whose upcoming slots (sampled every 30 min out
// to the horizon) have fired before. Ported from the C# HistogramPredictiveEngine.
// Description keys are matched case-insensitively (StringComparer.OrdinalIgnoreCase).
type HistogramPredictiveEngine struct {
	mu    sync.Mutex
	hist  map[string][]int64 // lowered(description) -> [24*7] counts
	names map[string]string  // lowered(description) -> display
	now   nowFunc
}

// NewHistogramPredictiveEngine returns an empty HistogramPredictiveEngine using
// the real UTC clock.
func NewHistogramPredictiveEngine() *HistogramPredictiveEngine {
	return NewHistogramPredictiveEngineAt(func() time.Time { return time.Now().UTC() })
}

// NewHistogramPredictiveEngineAt returns an empty HistogramPredictiveEngine
// driven by the supplied clock. Injecting the clock makes Anticipate
// deterministic for tests and for hosts that replay a fixed timeline. A nil
// clock falls back to the real UTC clock.
func NewHistogramPredictiveEngineAt(clock func() time.Time) *HistogramPredictiveEngine {
	if clock == nil {
		clock = func() time.Time { return time.Now().UTC() }
	}
	return &HistogramPredictiveEngine{
		hist:  make(map[string][]int64),
		names: make(map[string]string),
		now:   clock,
	}
}

// slotOf returns the hour-of-week histogram index for t: dayOfWeek*24 + hour,
// matching the C# (int)atUtc.DayOfWeek * 24 + atUtc.UtcDateTime.Hour. In both
// .NET and Go, Sunday == 0.
func slotOf(t time.Time) int {
	u := t.UTC()
	return int(u.Weekday())*24 + u.Hour()
}

// Observe records that the given need occurred at atUTC. Mirrors the C#
// Observe(string description, DateTimeOffset atUtc).
func (e *HistogramPredictiveEngine) Observe(description string, atUTC time.Time) error {
	if strings.TrimSpace(description) == "" {
		return errors.New("description required")
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	key := strings.ToLower(description)
	arr, ok := e.hist[key]
	if !ok {
		arr = make([]int64, 24*7)
		e.hist[key] = arr
		e.names[key] = description
	}
	arr[slotOf(atUTC)]++
	return nil
}

// Anticipate returns, for each tracked need with upcoming-slot activity, an
// AnticipatedNeed whose probability is upcoming/total, ordered by descending
// probability. Mirrors the C# AnticipateAsync exactly, including the 30-minute
// sampling stride and the ExpectedByUtc = now + horizon/2 midpoint.
func (e *HistogramPredictiveEngine) Anticipate(ctx context.Context, horizonMinutes int) ([]AnticipatedNeed, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if horizonMinutes <= 0 {
		return nil, errors.New("horizonMinutes out of range")
	}
	now := e.now()
	e.mu.Lock()
	defer e.mu.Unlock()

	results := make([]AnticipatedNeed, 0)
	// Deterministic iteration order over the map for stable tie-breaking.
	keys := make([]string, 0, len(e.hist))
	for k := range e.hist {
		keys = append(keys, k)
	}
	sort.Strings(keys)

	for _, k := range keys {
		arr := e.hist[k]
		var total int64
		for _, v := range arr {
			total += v
		}
		var upcoming int64
		for min := 0; min <= horizonMinutes; min += 30 {
			when := now.Add(time.Duration(min) * time.Minute)
			upcoming += arr[slotOf(when)]
		}
		if total == 0 || upcoming == 0 {
			continue
		}
		results = append(results, AnticipatedNeed{
			Description:   e.names[k],
			ExpectedByUTC: now.Add(time.Duration(horizonMinutes/2) * time.Minute),
			Probability:   float64(upcoming) / float64(total),
		})
	}
	sort.SliceStable(results, func(i, j int) bool {
		return results[i].Probability > results[j].Probability
	})
	return results, nil
}

// SequencePredictiveEngine is a first-order Markov variant of IPredictiveEngine.
// It learns need→need transitions from an observed event stream and, given the
// most recent event, anticipates the successors most likely to follow. The
// probability of a candidate successor is its transition frequency from the last
// event; the whole set is emitted when there is no "last event" yet (cold start),
// weighted by unconditional frequency. Deterministic and in-memory. This is the
// "what usually comes next" counterpart to the histogram's "what usually happens
// at this hour".
type SequencePredictiveEngine struct {
	mu            sync.Mutex
	transitions   map[string]map[string]int64 // lowered(from) -> lowered(to) -> count
	names         map[string]string           // lowered(need) -> display
	unconditional map[string]int64            // lowered(need) -> total occurrences
	last          string                      // lowered last observed need ("" until first Observe)
	avgGapMin     float64
	now           nowFunc
}

// NewSequencePredictiveEngine returns an empty SequencePredictiveEngine using the
// real UTC clock. avgGapMin (default 30) is the assumed spacing used to place
// ExpectedByUtc when no timing information is supplied.
func NewSequencePredictiveEngine() *SequencePredictiveEngine {
	return NewSequencePredictiveEngineAt(func() time.Time { return time.Now().UTC() })
}

// NewSequencePredictiveEngineAt returns an empty SequencePredictiveEngine driven
// by the supplied clock, making Anticipate's ExpectedByUtc deterministic for
// tests. A nil clock falls back to the real UTC clock.
func NewSequencePredictiveEngineAt(clock func() time.Time) *SequencePredictiveEngine {
	if clock == nil {
		clock = func() time.Time { return time.Now().UTC() }
	}
	return &SequencePredictiveEngine{
		transitions:   make(map[string]map[string]int64),
		names:         make(map[string]string),
		unconditional: make(map[string]int64),
		avgGapMin:     30,
		now:           clock,
	}
}

// Observe appends a need to the event stream, learning the transition from the
// previous need (if any) to this one. Mirrors the shape of the histogram engine's
// Observe but records ordering rather than time-of-day.
func (e *SequencePredictiveEngine) Observe(description string) error {
	if strings.TrimSpace(description) == "" {
		return errors.New("description required")
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	key := strings.ToLower(description)
	if _, ok := e.names[key]; !ok {
		e.names[key] = description
	}
	e.unconditional[key]++
	if e.last != "" {
		inner, ok := e.transitions[e.last]
		if !ok {
			inner = make(map[string]int64)
			e.transitions[e.last] = inner
		}
		inner[key]++
	}
	e.last = key
	return nil
}

// Anticipate returns the successors most likely to follow the most recent event,
// ordered by descending probability. Each candidate's ExpectedByUtc is placed at
// now + min(horizon, avgGap). Candidates beyond the horizon are still reported
// (the horizon bounds the ExpectedByUtc, not membership) so the caller sees the
// full ranked successor set, matching the histogram engine which also returns all
// qualifying needs.
func (e *SequencePredictiveEngine) Anticipate(ctx context.Context, horizonMinutes int) ([]AnticipatedNeed, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if horizonMinutes <= 0 {
		return nil, errors.New("horizonMinutes out of range")
	}
	now := e.now()
	e.mu.Lock()
	defer e.mu.Unlock()

	gap := e.avgGapMin
	if float64(horizonMinutes) < gap {
		gap = float64(horizonMinutes)
	}
	expectedBy := now.Add(time.Duration(gap) * time.Minute)

	// Choose the conditional distribution when we have a last event with
	// outgoing transitions; otherwise fall back to the unconditional frequencies
	// (cold start).
	var dist map[string]int64
	if e.last != "" {
		if inner, ok := e.transitions[e.last]; ok && len(inner) > 0 {
			dist = inner
		}
	}
	if dist == nil {
		dist = e.unconditional
	}
	if len(dist) == 0 {
		return []AnticipatedNeed{}, nil
	}

	var total int64
	keys := make([]string, 0, len(dist))
	for k, v := range dist {
		total += v
		keys = append(keys, k)
	}
	sort.Strings(keys)

	results := make([]AnticipatedNeed, 0, len(keys))
	for _, k := range keys {
		results = append(results, AnticipatedNeed{
			Description:   e.names[k],
			ExpectedByUTC: expectedBy,
			Probability:   float64(dist[k]) / float64(total),
		})
	}
	sort.SliceStable(results, func(i, j int) bool {
		return results[i].Probability > results[j].Probability
	})
	return results, nil
}

// Compile-time assertions.
var (
	_ IPredictiveEngine = (*HistogramPredictiveEngine)(nil)
	_ IPredictiveEngine = (*SequencePredictiveEngine)(nil)
)
