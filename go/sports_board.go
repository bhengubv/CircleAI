// sports_board.go
//
// Ports the CircleAI.Sports primitive vertical (SportsPrimitives.cs):
//   DistanceKind (enum)      -> DistanceKind (int consts, stable ordinals)
//   Activity / PersonalBest / TrainingSession (records) -> value structs
//     (Activity is named SportsActivity in Go — the flat package already has a
//      CRM Activity type; the C# type is CircleAI.Sports.Activity)
//   ISportsBoard             -> SportsBoard interface (I-prefix dropped)
//   InMemorySportsBoard      -> InMemorySportsBoard
//
// SportsDomainContext (static prompt strings) and SportsCompanionAdapter
// (LLM-prompt wrapper over ICompanionSession) are out of scope for the
// deterministic in-memory board and are not ported.
//
// DETERMINISM: History orders by AtUtc descending; TotalKmThisWeek sums the
// current calendar week (week start = now.Date - now.Weekday, matching C#
// now.Date.AddDays(-(int)now.DayOfWeek) where Sunday=0). Best picks the
// smallest-Duration qualifying activity. Upcoming orders by ScheduledUtc
// ascending. Session storage mirrors the C# ConcurrentDictionary (last write
// by SessionId wins).

package circleai

import (
	"errors"
	"sort"
	"sync"
	"time"
)

// DistanceKind enumerates distance-based activity kinds. Ports the DistanceKind
// enum; ordinals are stable (Run=0..Row=4).
type DistanceKind int

const (
	DistanceKindRun DistanceKind = iota
	DistanceKindBike
	DistanceKindSwim
	DistanceKindWalk
	DistanceKindRow
)

// SportsActivity is a logged distance activity. Ports the Activity record.
// Duration mirrors the C# TimeSpan. (Named SportsActivity because the flat
// package already defines a CRM Activity type.)
type SportsActivity struct {
	ActivityId string
	UserId     string
	Kind       DistanceKind
	DistanceKm float64
	Duration   time.Duration
	AtUtc      time.Time
}

// PersonalBest is a best-time record for a distance. Ports the PersonalBest
// record.
type PersonalBest struct {
	UserId      string
	Kind        DistanceKind
	DistanceKm  float64
	Time        time.Duration
	AchievedUtc time.Time
}

// TrainingSession is a scheduled training session. Ports the TrainingSession
// record.
type TrainingSession struct {
	SessionId   string
	UserId      string
	Plan        string
	ScheduledUtc time.Time
	Completed   bool
}

// SportsBoard is the activities/sessions/personal-bests board. Ports
// ISportsBoard.
type SportsBoard interface {
	Log(a SportsActivity)
	// History lists a user's activities newest-first, capped at limit.
	History(userId string, limit int) []SportsActivity
	// TotalKmThisWeek sums distance for kind in the calendar week containing now.
	TotalKmThisWeek(userId string, kind DistanceKind, now time.Time) float64
	// Best returns the fastest activity meeting distanceKm as a PersonalBest, or
	// (zero,false) if none qualify.
	Best(userId string, kind DistanceKind, distanceKm float64) (PersonalBest, bool)
	Schedule(s TrainingSession)
	// Complete marks a session done; errors on unknown id.
	Complete(sessionId string) error
	// Upcoming lists a user's incomplete future sessions ordered by ScheduledUtc.
	Upcoming(userId string) []TrainingSession
}

// InMemorySportsBoard is a concurrency-safe in-memory SportsBoard. Ports
// InMemorySportsBoard.
type InMemorySportsBoard struct {
	mu         sync.Mutex
	activities []SportsActivity
	sessions   map[string]TrainingSession
}

// NewInMemorySportsBoard constructs an empty board.
func NewInMemorySportsBoard() *InMemorySportsBoard {
	return &InMemorySportsBoard{sessions: make(map[string]TrainingSession)}
}

// Log appends an activity. Ports Log.
func (b *InMemorySportsBoard) Log(a SportsActivity) {
	b.mu.Lock()
	b.activities = append(b.activities, a)
	b.mu.Unlock()
}

// History returns a user's activities newest-first, capped at limit. Ports
// History (limit default is 50 at the C# call site; callers pass it explicitly).
// Panics on limit <= 0, matching the C# ArgumentOutOfRangeException.
func (b *InMemorySportsBoard) History(userId string, limit int) []SportsActivity {
	if limit <= 0 {
		panic("limit out of range")
	}
	b.mu.Lock()
	out := make([]SportsActivity, 0)
	for _, a := range b.activities {
		if a.UserId == userId {
			out = append(out, a)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.After(out[j].AtUtc) })
	if len(out) > limit {
		out = out[:limit]
	}
	return out
}

// TotalKmThisWeek sums distance for a kind since the start of now's week. Ports
// TotalKmThisWeek.
func (b *InMemorySportsBoard) TotalKmThisWeek(userId string, kind DistanceKind, now time.Time) float64 {
	weekStart := weekStartOf(now)
	b.mu.Lock()
	defer b.mu.Unlock()
	var sum float64
	for _, a := range b.activities {
		if a.UserId == userId && a.Kind == kind && !a.AtUtc.Before(weekStart) {
			sum += a.DistanceKm
		}
	}
	return sum
}

// Best returns the fastest activity meeting distanceKm. Ports Best (returns null
// -> (zero,false)).
func (b *InMemorySportsBoard) Best(userId string, kind DistanceKind, distanceKm float64) (PersonalBest, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()
	var best *SportsActivity
	for i := range b.activities {
		a := &b.activities[i]
		if a.UserId == userId && a.Kind == kind && a.DistanceKm >= distanceKm {
			if best == nil || a.Duration < best.Duration {
				best = a
			}
		}
	}
	if best == nil {
		return PersonalBest{}, false
	}
	return PersonalBest{UserId: userId, Kind: kind, DistanceKm: distanceKm, Time: best.Duration, AchievedUtc: best.AtUtc}, true
}

// Schedule stores (or replaces by SessionId) a session. Ports Schedule.
func (b *InMemorySportsBoard) Schedule(s TrainingSession) {
	b.mu.Lock()
	b.sessions[s.SessionId] = s
	b.mu.Unlock()
}

// Complete marks a session completed. Ports Complete (throws on unknown id ->
// error).
func (b *InMemorySportsBoard) Complete(sessionId string) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	s, ok := b.sessions[sessionId]
	if !ok {
		return errors.New("Unknown session " + sessionId)
	}
	s.Completed = true
	b.sessions[sessionId] = s
	return nil
}

// Upcoming lists a user's incomplete future sessions ordered by ScheduledUtc.
// Ports Upcoming (future = ScheduledUtc >= now UTC).
func (b *InMemorySportsBoard) Upcoming(userId string) []TrainingSession {
	now := time.Now().UTC()
	b.mu.Lock()
	out := make([]TrainingSession, 0)
	for _, s := range b.sessions {
		if s.UserId == userId && !s.Completed && !s.ScheduledUtc.Before(now) {
			out = append(out, s)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].ScheduledUtc.Before(out[j].ScheduledUtc) })
	return out
}

// weekStartOf returns midnight on the Sunday that begins now's week, matching
// the C# now.Date.AddDays(-(int)now.DayOfWeek) idiom (Sunday=0).
func weekStartOf(now time.Time) time.Time {
	d := time.Date(now.Year(), now.Month(), now.Day(), 0, 0, 0, 0, now.Location())
	return d.AddDate(0, 0, -int(d.Weekday()))
}

// Interface guard.
var _ SportsBoard = (*InMemorySportsBoard)(nil)
