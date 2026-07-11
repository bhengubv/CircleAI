// creative_board.go
//
// Ports the CircleAI.Creative primitive vertical (CreativePrimitives.cs):
//   CreativeWork / Inspiration / Critique (records) -> value structs
//   ICreativeBoard           -> CreativeBoard interface (I-prefix dropped)
//   InMemoryCreativeBoard    -> InMemoryCreativeBoard
//
// The CreativeDomainContext / CreativeCompanionAdapter (LLM glue) are out of
// scope.
//
// DETERMINISM: WorksByTag mirrors an unordered ConcurrentDictionary in C#; this
// port sorts by WorkId. RecentInspiration orders by SeenUtc descending then caps
// at limit. AvgScore averages a work's critique scores, returning 0 when there
// are none (the C# DefaultIfEmpty(0).Average()).

package circleai

import (
	"sort"
	"strings"
	"sync"
	"time"
)

// CreativeWork is a creative work. Ports the CreativeWork record. Tags mirrors
// the C# IReadOnlyList<string>.
type CreativeWork struct {
	WorkId     string
	Title      string
	Medium     string
	Author     string
	CreatedUtc time.Time
	Tags       []string
}

// Inspiration is a recorded inspiration prompt. Ports the Inspiration record.
type Inspiration struct {
	InspirationId string
	PromptText    string
	SourceUrl     string
	SeenUtc       time.Time
}

// Critique is a critique of a work. Ports the Critique record.
type Critique struct {
	CritiqueId string
	WorkId     string
	Reviewer   string
	Body       string
	Score      int
}

// CreativeBoard is the works/inspiration/critiques board. Ports ICreativeBoard.
type CreativeBoard interface {
	AddWork(w CreativeWork)
	GetWork(id string) (CreativeWork, bool)
	// WorksByTag lists works carrying the tag (case-insensitive), sorted by WorkId.
	WorksByTag(tag string) []CreativeWork
	RecordInspiration(i Inspiration)
	// RecentInspiration lists inspiration newest-first, capped at limit.
	RecentInspiration(limit int) []Inspiration
	AddCritique(c Critique)
	// AvgScore is the mean critique score for a work (0 when none).
	AvgScore(workId string) float64
}

// InMemoryCreativeBoard is a concurrency-safe in-memory CreativeBoard. Ports
// InMemoryCreativeBoard.
type InMemoryCreativeBoard struct {
	mu          sync.Mutex
	works       map[string]CreativeWork
	inspiration []Inspiration
	critiques   []Critique
}

// NewInMemoryCreativeBoard constructs an empty board.
func NewInMemoryCreativeBoard() *InMemoryCreativeBoard {
	return &InMemoryCreativeBoard{works: make(map[string]CreativeWork)}
}

// AddWork stores (or replaces by WorkId) a work. Ports AddWork.
func (b *InMemoryCreativeBoard) AddWork(w CreativeWork) {
	b.mu.Lock()
	b.works[w.WorkId] = w
	b.mu.Unlock()
}

// GetWork returns the work for id, or (zero,false). Ports GetWork.
func (b *InMemoryCreativeBoard) GetWork(id string) (CreativeWork, bool) {
	b.mu.Lock()
	w, ok := b.works[id]
	b.mu.Unlock()
	return w, ok
}

// WorksByTag lists works carrying the tag (case-insensitive), sorted by WorkId.
// Ports WorksByTag.
func (b *InMemoryCreativeBoard) WorksByTag(tag string) []CreativeWork {
	b.mu.Lock()
	out := make([]CreativeWork, 0)
	for _, w := range b.works {
		for _, t := range w.Tags {
			if strings.EqualFold(t, tag) {
				out = append(out, w)
				break
			}
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].WorkId < out[j].WorkId })
	return out
}

// RecordInspiration appends an inspiration. Ports RecordInspiration.
func (b *InMemoryCreativeBoard) RecordInspiration(i Inspiration) {
	b.mu.Lock()
	b.inspiration = append(b.inspiration, i)
	b.mu.Unlock()
}

// RecentInspiration lists inspiration newest-first, capped at limit. Ports
// RecentInspiration.
func (b *InMemoryCreativeBoard) RecentInspiration(limit int) []Inspiration {
	b.mu.Lock()
	out := make([]Inspiration, len(b.inspiration))
	copy(out, b.inspiration)
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].SeenUtc.After(out[j].SeenUtc) })
	if limit >= 0 && len(out) > limit {
		out = out[:limit]
	}
	return out
}

// AddCritique appends a critique. Ports AddCritique.
func (b *InMemoryCreativeBoard) AddCritique(c Critique) {
	b.mu.Lock()
	b.critiques = append(b.critiques, c)
	b.mu.Unlock()
}

// AvgScore is the mean critique score for a work (0 when none). Ports AvgScore.
func (b *InMemoryCreativeBoard) AvgScore(workId string) float64 {
	b.mu.Lock()
	defer b.mu.Unlock()
	var sum float64
	var n int
	for _, c := range b.critiques {
		if c.WorkId == workId {
			sum += float64(c.Score)
			n++
		}
	}
	if n == 0 {
		return 0.0
	}
	return sum / float64(n)
}

// Interface guard.
var _ CreativeBoard = (*InMemoryCreativeBoard)(nil)
