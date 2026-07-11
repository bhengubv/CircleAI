// personal_mental_board.go
//
// Ports the CircleAI.Personal.Mental primitive vertical
// (PersonalMentalPrimitives.cs):
//   Mood (enum)       -> Mood (int consts, stable ordinals)
//   MoodLog / JournalEntry / CopingStrategy (records) -> value structs
//   IMentalHealthBoard        -> MentalHealthBoard interface
//   InMemoryMentalHealthBoard -> InMemoryMentalHealthBoard
//
// The PersonalMentalDomainContext (static prompt strings) and
// PersonalMentalCompanionAdapter (LLM-prompt wrapper) are out of scope for the
// deterministic in-memory board.
//
// TIME WINDOW: Last7Days / AvgMood7Day use a rolling window of now-7d where now is
// the wall clock, exactly as the C# uses DateTimeOffset.UtcNow.AddDays(-7). Tests
// use recent timestamps so the window is deterministic in practice.
//
// AvgMood7Day returns NaN when the window is empty (ports the C# double.NaN
// sentinel).

package circleai

import (
	"errors"
	"math"
	"sort"
	"strings"
	"sync"
	"time"
)

// Mood is a coarse mood rating. Ordinals match the C# enum declaration order
// (VeryLow=0, Low=1, Neutral=2, Good=3, Great=4) and are the values averaged by
// AvgMood7Day. Ports Mood.
type Mood int

const (
	// MoodVeryLow is the lowest rating.
	MoodVeryLow Mood = iota
	// MoodLow is a low rating.
	MoodLow
	// MoodNeutral is a neutral rating.
	MoodNeutral
	// MoodGood is a good rating.
	MoodGood
	// MoodGreat is the highest rating.
	MoodGreat
)

// String renders the C# enum member name for a Mood.
func (m Mood) String() string {
	switch m {
	case MoodVeryLow:
		return "VeryLow"
	case MoodLow:
		return "Low"
	case MoodNeutral:
		return "Neutral"
	case MoodGood:
		return "Good"
	case MoodGreat:
		return "Great"
	default:
		return "Unknown"
	}
}

// MoodLog is a timestamped mood entry. Ports the MoodLog record. Note is a pointer
// to mirror the nullable C# string?.
type MoodLog struct {
	Mood  Mood
	AtUtc time.Time
	Note  *string
}

// JournalEntry is a journal entry. Ports the JournalEntry record.
type JournalEntry struct {
	EntryId string
	Title   string
	Body    string
	AtUtc   time.Time
}

// CopingStrategy is a coping-strategy library entry. Ports the CopingStrategy
// record.
type CopingStrategy struct {
	StrategyId  string
	Title       string
	Description string
	Tags        []string
}

// MentalHealthBoard is the mood/journal/coping-strategy board. Ports
// IMentalHealthBoard. Entries is exposed as a method.
type MentalHealthBoard interface {
	LogMood(m MoodLog)
	// Last7Days lists mood logs from the last 7 days, earliest first.
	Last7Days() []MoodLog
	// AddEntry stores a journal entry; errors on blank EntryId.
	AddEntry(e JournalEntry) error
	// Entries lists journal entries, most recent first.
	Entries() []JournalEntry
	RegisterStrategy(s CopingStrategy)
	// StrategiesByTag lists strategies carrying tag (case-insensitive); errors on
	// blank tag.
	StrategiesByTag(tag string) ([]CopingStrategy, error)
	// AvgMood7Day is the mean mood ordinal over the last 7 days (NaN if none).
	AvgMood7Day() float64
}

// InMemoryMentalHealthBoard is a concurrency-safe in-memory MentalHealthBoard.
// Ports InMemoryMentalHealthBoard (mood logs in an ordered list guarded by a
// mutex; journal entries + strategies in maps).
type InMemoryMentalHealthBoard struct {
	mu      sync.RWMutex
	moods   []MoodLog
	entries map[string]JournalEntry
	strats  map[string]CopingStrategy
}

// NewInMemoryMentalHealthBoard constructs an empty board.
func NewInMemoryMentalHealthBoard() *InMemoryMentalHealthBoard {
	return &InMemoryMentalHealthBoard{
		moods:   make([]MoodLog, 0),
		entries: make(map[string]JournalEntry),
		strats:  make(map[string]CopingStrategy),
	}
}

// LogMood appends a mood log. Ports LogMood.
func (b *InMemoryMentalHealthBoard) LogMood(m MoodLog) {
	b.mu.Lock()
	b.moods = append(b.moods, m)
	b.mu.Unlock()
}

// Last7Days lists mood logs at or after now-7d, ordered by AtUtc ascending. Ports
// Last7Days.
func (b *InMemoryMentalHealthBoard) Last7Days() []MoodLog {
	cutoff := time.Now().UTC().AddDate(0, 0, -7)
	b.mu.RLock()
	out := make([]MoodLog, 0)
	for _, m := range b.moods {
		if !m.AtUtc.Before(cutoff) {
			out = append(out, m)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.Before(out[j].AtUtc) })
	return out
}

// AddEntry stores (or replaces by EntryId) a journal entry. Ports AddEntry
// (ArgumentException on blank EntryId -> error).
func (b *InMemoryMentalHealthBoard) AddEntry(e JournalEntry) error {
	if strings.TrimSpace(e.EntryId) == "" {
		return errors.New("EntryId required")
	}
	b.mu.Lock()
	b.entries[e.EntryId] = e
	b.mu.Unlock()
	return nil
}

// Entries lists journal entries ordered by AtUtc descending (newest first). Ports
// the Entries property. Equal timestamps break by EntryId for determinism.
func (b *InMemoryMentalHealthBoard) Entries() []JournalEntry {
	b.mu.RLock()
	out := make([]JournalEntry, 0, len(b.entries))
	for _, e := range b.entries {
		out = append(out, e)
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool {
		if !out[i].AtUtc.Equal(out[j].AtUtc) {
			return out[i].AtUtc.After(out[j].AtUtc)
		}
		return out[i].EntryId < out[j].EntryId
	})
	return out
}

// RegisterStrategy stores (or replaces by StrategyId) a coping strategy. Ports
// RegisterStrategy.
func (b *InMemoryMentalHealthBoard) RegisterStrategy(s CopingStrategy) {
	s.Tags = append([]string(nil), s.Tags...)
	b.mu.Lock()
	b.strats[s.StrategyId] = s
	b.mu.Unlock()
}

// StrategiesByTag lists strategies carrying tag (case-insensitive). Ports
// StrategiesByTag (ArgumentException on blank tag -> error). Result is sorted by
// StrategyId for determinism (C# leaves ConcurrentDictionary order undefined).
func (b *InMemoryMentalHealthBoard) StrategiesByTag(tag string) ([]CopingStrategy, error) {
	if strings.TrimSpace(tag) == "" {
		return nil, errors.New("tag required")
	}
	b.mu.RLock()
	out := make([]CopingStrategy, 0)
	for _, s := range b.strats {
		for _, t := range s.Tags {
			if strings.EqualFold(t, tag) {
				out = append(out, s)
				break
			}
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].StrategyId < out[j].StrategyId })
	return out, nil
}

// AvgMood7Day returns the mean mood ordinal over the last 7 days, or NaN when the
// window is empty. Ports AvgMood7Day (Select((int)Mood).Average(); double.NaN on
// empty).
func (b *InMemoryMentalHealthBoard) AvgMood7Day() float64 {
	items := b.Last7Days()
	if len(items) == 0 {
		return math.NaN()
	}
	var sum int
	for _, m := range items {
		sum += int(m.Mood)
	}
	return float64(sum) / float64(len(items))
}

// Interface guard.
var _ MentalHealthBoard = (*InMemoryMentalHealthBoard)(nil)
