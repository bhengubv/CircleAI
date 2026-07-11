// gaming_board.go
//
// Ports the CircleAI.Gaming primitive vertical (GamingPrimitives.cs):
//   GameTitle / PlaySession / AchievementUnlock (records) -> value structs
//   IGamingBoard             -> GamingBoard interface (I-prefix dropped)
//   InMemoryGamingBoard      -> InMemoryGamingBoard
//
// The GamingDomainContext / GamingCompanionAdapter (LLM glue) are out of scope.
//
// DETERMINISM: TitlesByGenre mirrors a ConcurrentDictionary in C# (no defined
// order); this port sorts by TitleId. AchievementsFor orders by AtUtc
// descending. MostPlayed groups sessions by TitleId and orders by total play
// time descending; sum-ties break by TitleId ascending for stable output.
// TotalPlayTime and MostPlayed accumulate durations as int64 nanoseconds, the
// native time.Duration unit, matching the C# millisecond accumulation exactly
// for whole-millisecond inputs.

package circleai

import (
	"sort"
	"strings"
	"sync"
	"time"
)

// GameTitle is a game title. Ports the GameTitle record.
type GameTitle struct {
	TitleId  string
	Name     string
	Genre    string
	Platform string
}

// PlaySession is a play session. Ports the PlaySession record.
type PlaySession struct {
	SessionId string
	UserId    string
	TitleId   string
	Duration  time.Duration
	AtUtc     time.Time
}

// AchievementUnlock is an unlocked achievement. Ports the AchievementUnlock
// record.
type AchievementUnlock struct {
	UnlockId    string
	UserId      string
	TitleId     string
	Achievement string
	AtUtc       time.Time
}

// GamingBoard is the titles/sessions/achievements board. Ports IGamingBoard.
type GamingBoard interface {
	AddTitle(t GameTitle)
	GetTitle(id string) (GameTitle, bool)
	// TitlesByGenre lists titles of a genre (case-insensitive), sorted by TitleId.
	TitlesByGenre(genre string) []GameTitle
	RecordSession(s PlaySession)
	// TotalPlayTime sums a user's session durations for a title.
	TotalPlayTime(userId, titleId string) time.Duration
	Unlock(u AchievementUnlock)
	// AchievementsFor lists a user's unlocks newest-first.
	AchievementsFor(userId string) []AchievementUnlock
	// MostPlayed returns a user's top-K titles by total play time. Panics on
	// topK <= 0, matching the C# ArgumentOutOfRangeException.
	MostPlayed(userId string, topK int) []GameTitle
}

// InMemoryGamingBoard is a concurrency-safe in-memory GamingBoard. Ports
// InMemoryGamingBoard.
type InMemoryGamingBoard struct {
	mu       sync.Mutex
	titles   map[string]GameTitle
	sessions []PlaySession
	unlocks  []AchievementUnlock
}

// NewInMemoryGamingBoard constructs an empty board.
func NewInMemoryGamingBoard() *InMemoryGamingBoard {
	return &InMemoryGamingBoard{titles: make(map[string]GameTitle)}
}

// AddTitle stores (or replaces by TitleId) a title. Ports AddTitle.
func (b *InMemoryGamingBoard) AddTitle(t GameTitle) {
	b.mu.Lock()
	b.titles[t.TitleId] = t
	b.mu.Unlock()
}

// GetTitle returns the title for id, or (zero,false). Ports GetTitle.
func (b *InMemoryGamingBoard) GetTitle(id string) (GameTitle, bool) {
	b.mu.Lock()
	t, ok := b.titles[id]
	b.mu.Unlock()
	return t, ok
}

// TitlesByGenre lists titles of a genre (case-insensitive), sorted by TitleId.
// Ports TitlesByGenre.
func (b *InMemoryGamingBoard) TitlesByGenre(genre string) []GameTitle {
	b.mu.Lock()
	out := make([]GameTitle, 0)
	for _, t := range b.titles {
		if strings.EqualFold(t.Genre, genre) {
			out = append(out, t)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].TitleId < out[j].TitleId })
	return out
}

// RecordSession appends a play session. Ports RecordSession.
func (b *InMemoryGamingBoard) RecordSession(s PlaySession) {
	b.mu.Lock()
	b.sessions = append(b.sessions, s)
	b.mu.Unlock()
}

// TotalPlayTime sums a user's session durations for a title. Ports
// TotalPlayTime.
func (b *InMemoryGamingBoard) TotalPlayTime(userId, titleId string) time.Duration {
	b.mu.Lock()
	defer b.mu.Unlock()
	var total time.Duration
	for _, s := range b.sessions {
		if s.UserId == userId && s.TitleId == titleId {
			total += s.Duration
		}
	}
	return total
}

// Unlock appends an achievement unlock. Ports Unlock.
func (b *InMemoryGamingBoard) Unlock(u AchievementUnlock) {
	b.mu.Lock()
	b.unlocks = append(b.unlocks, u)
	b.mu.Unlock()
}

// AchievementsFor lists a user's unlocks newest-first. Ports AchievementsFor.
func (b *InMemoryGamingBoard) AchievementsFor(userId string) []AchievementUnlock {
	b.mu.Lock()
	out := make([]AchievementUnlock, 0)
	for _, u := range b.unlocks {
		if u.UserId == userId {
			out = append(out, u)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.After(out[j].AtUtc) })
	return out
}

// MostPlayed returns a user's top-K titles by total play time. Ports MostPlayed.
func (b *InMemoryGamingBoard) MostPlayed(userId string, topK int) []GameTitle {
	if topK <= 0 {
		panic("topK out of range")
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	totals := make(map[string]time.Duration)
	for _, s := range b.sessions {
		if s.UserId == userId {
			totals[s.TitleId] += s.Duration
		}
	}
	type kv struct {
		titleID string
		total   time.Duration
	}
	ranked := make([]kv, 0, len(totals))
	for id, t := range totals {
		ranked = append(ranked, kv{id, t})
	}
	sort.SliceStable(ranked, func(i, j int) bool {
		if ranked[i].total != ranked[j].total {
			return ranked[i].total > ranked[j].total
		}
		return ranked[i].titleID < ranked[j].titleID
	})
	out := make([]GameTitle, 0)
	for _, r := range ranked {
		if len(out) >= topK {
			break
		}
		if t, ok := b.titles[r.titleID]; ok {
			out = append(out, t)
		}
	}
	return out
}

// Interface guard.
var _ GamingBoard = (*InMemoryGamingBoard)(nil)
