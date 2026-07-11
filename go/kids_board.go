// kids_board.go
//
// Ports the CircleAI.Kids primitive vertical (KidsPrimitives.cs):
//   AgeAppropriateness (enum) -> AgeAppropriateness (int consts, stable ordinals)
//   KidsContent / DailyTime / TimeLog (records) -> value structs
//   IKidsBoard               -> KidsBoard interface (I-prefix dropped)
//   InMemoryKidsBoard        -> InMemoryKidsBoard
//
// The KidsDomainContext / KidsCompanionAdapter (LLM glue) are out of scope.
//
// DETERMINISM: ContentFor orders by Title ascending. UsedToday sums durations
// for a kid+kind on now's calendar date. OverLimit compares used against the
// per-kind cap (screen/reading; any other kind is treated as unbounded, matching
// the C# TimeSpan.MaxValue branch that can never be exceeded).

package circleai

import (
	"sort"
	"strings"
	"sync"
	"time"
)

// AgeAppropriateness enumerates content age bands. Ports the AgeAppropriateness
// enum; ordinals are stable (Toddler=0..Teen=5).
type AgeAppropriateness int

const (
	AgeAppropriatenessToddler AgeAppropriateness = iota
	AgeAppropriatenessPreschool
	AgeAppropriatenessEarlyPrimary
	AgeAppropriatenessLatePrimary
	AgeAppropriatenessPreTeen
	AgeAppropriatenessTeen
)

// KidsContent is an age-rated content item. Ports the KidsContent record. Tags
// mirrors the C# IReadOnlyList<string>.
type KidsContent struct {
	ContentId string
	Title     string
	AgeBand   AgeAppropriateness
	Kind      string
	Tags      []string
}

// DailyTime is a kid's daily screen/reading limits. Ports the DailyTime record.
type DailyTime struct {
	KidName      string
	ScreenLimit  time.Duration
	ReadingLimit time.Duration
}

// TimeLog is a logged usage interval. Ports the TimeLog record.
type TimeLog struct {
	KidName  string
	Kind     string
	Duration time.Duration
	AtUtc    time.Time
}

// KidsBoard is the content/limits/usage board. Ports IKidsBoard.
type KidsBoard interface {
	AddContent(c KidsContent)
	// ContentFor lists content in a band ordered by Title.
	ContentFor(band AgeAppropriateness) []KidsContent
	SetLimits(d DailyTime)
	LimitsFor(kidName string) (DailyTime, bool)
	RecordTime(t TimeLog)
	// UsedToday sums a kid's usage of a kind on now's date.
	UsedToday(kidName, kind string, now time.Time) time.Duration
	// OverLimit reports whether a kid's usage of a kind exceeds its cap today.
	OverLimit(kidName, kind string, now time.Time) bool
}

// InMemoryKidsBoard is a concurrency-safe in-memory KidsBoard. Ports
// InMemoryKidsBoard.
type InMemoryKidsBoard struct {
	mu      sync.Mutex
	content map[string]KidsContent
	limits  map[string]DailyTime
	logs    []TimeLog
}

// NewInMemoryKidsBoard constructs an empty board.
func NewInMemoryKidsBoard() *InMemoryKidsBoard {
	return &InMemoryKidsBoard{
		content: make(map[string]KidsContent),
		limits:  make(map[string]DailyTime),
	}
}

// AddContent stores (or replaces by ContentId) a content item. Ports AddContent.
func (b *InMemoryKidsBoard) AddContent(c KidsContent) {
	b.mu.Lock()
	b.content[c.ContentId] = c
	b.mu.Unlock()
}

// ContentFor lists content in a band ordered by Title. Ports ContentFor.
func (b *InMemoryKidsBoard) ContentFor(band AgeAppropriateness) []KidsContent {
	b.mu.Lock()
	out := make([]KidsContent, 0)
	for _, c := range b.content {
		if c.AgeBand == band {
			out = append(out, c)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].Title < out[j].Title })
	return out
}

// SetLimits stores (or replaces by KidName) a kid's limits. Ports SetLimits.
func (b *InMemoryKidsBoard) SetLimits(d DailyTime) {
	b.mu.Lock()
	b.limits[d.KidName] = d
	b.mu.Unlock()
}

// LimitsFor returns a kid's limits, or (zero,false). Ports LimitsFor.
func (b *InMemoryKidsBoard) LimitsFor(kidName string) (DailyTime, bool) {
	b.mu.Lock()
	d, ok := b.limits[kidName]
	b.mu.Unlock()
	return d, ok
}

// RecordTime appends a usage log. Ports RecordTime.
func (b *InMemoryKidsBoard) RecordTime(t TimeLog) {
	b.mu.Lock()
	b.logs = append(b.logs, t)
	b.mu.Unlock()
}

// UsedToday sums a kid's usage of a kind on now's date. Ports UsedToday (date
// comparison matches the C# AtUtc.Date == now.Date on the same calendar day).
func (b *InMemoryKidsBoard) UsedToday(kidName, kind string, now time.Time) time.Duration {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.usedTodayLocked(kidName, kind, now)
}

// usedTodayLocked sums a kid's usage of a kind on now's date. The caller must
// hold b.mu.
func (b *InMemoryKidsBoard) usedTodayLocked(kidName, kind string, now time.Time) time.Duration {
	var total time.Duration
	for _, l := range b.logs {
		if l.KidName == kidName && l.Kind == kind && sameCalendarDay(l.AtUtc, now) {
			total += l.Duration
		}
	}
	return total
}

// OverLimit reports whether a kid's usage of a kind exceeds its cap today. Ports
// OverLimit.
func (b *InMemoryKidsBoard) OverLimit(kidName, kind string, now time.Time) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	limits, ok := b.limits[kidName]
	if !ok {
		return false
	}
	used := b.usedTodayLocked(kidName, kind, now)
	switch {
	case strings.EqualFold(kind, "screen"):
		return used > limits.ScreenLimit
	case strings.EqualFold(kind, "reading"):
		return used > limits.ReadingLimit
	default:
		// C# uses TimeSpan.MaxValue here; usage can never exceed it.
		return false
	}
}

// sameCalendarDay reports whether a and b fall on the same calendar day (in
// their respective locations), matching the C# DateTimeOffset.Date equality.
func sameCalendarDay(a, b time.Time) bool {
	ay, am, ad := a.Date()
	by, bm, bd := b.Date()
	return ay == by && am == bm && ad == bd
}

// Interface guard.
var _ KidsBoard = (*InMemoryKidsBoard)(nil)
