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
	// WorkCount returns the number of works.
	WorkCount() int
	// RemoveWork drops a work by id (cascading its critiques), returning true if present.
	RemoveWork(workId string) bool
	// WorksByAuthor lists an author's works (case-insensitive), newest-first.
	WorksByAuthor(author string) []CreativeWork
	// WorksByMedium lists works in a medium (case-insensitive), newest-first.
	WorksByMedium(medium string) []CreativeWork
	// TopRatedWork returns the work with the highest mean critique score, or (zero,false).
	TopRatedWork() (CreativeWork, bool)
	// AllTags lists every distinct tag (case-insensitive), ordered case-insensitively.
	AllTags() []string
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

// WorkCount returns the number of works. Ports InMemoryCreativeBoard.WorkCount.
func (b *InMemoryCreativeBoard) WorkCount() int {
	b.mu.Lock()
	defer b.mu.Unlock()
	return len(b.works)
}

// RemoveWork drops a work by id and, if it was present, cascades by removing
// every critique of that work. Returns true if the work was present. Ports
// InMemoryCreativeBoard.RemoveWork.
func (b *InMemoryCreativeBoard) RemoveWork(workId string) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	_, removed := b.works[workId]
	if !removed {
		return false
	}
	delete(b.works, workId)
	kept := b.critiques[:0]
	for _, c := range b.critiques {
		if c.WorkId != workId {
			kept = append(kept, c)
		}
	}
	b.critiques = kept
	return true
}

// WorksByAuthor lists an author's works (case-insensitive), ordered by CreatedUtc
// descending. Ports InMemoryCreativeBoard.WorksByAuthor.
func (b *InMemoryCreativeBoard) WorksByAuthor(author string) []CreativeWork {
	b.mu.Lock()
	out := make([]CreativeWork, 0)
	for _, w := range b.works {
		if strings.EqualFold(w.Author, author) {
			out = append(out, w)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].CreatedUtc.After(out[j].CreatedUtc) })
	return out
}

// WorksByMedium lists works in a medium (case-insensitive), ordered by CreatedUtc
// descending. Ports InMemoryCreativeBoard.WorksByMedium.
func (b *InMemoryCreativeBoard) WorksByMedium(medium string) []CreativeWork {
	b.mu.Lock()
	out := make([]CreativeWork, 0)
	for _, w := range b.works {
		if strings.EqualFold(w.Medium, medium) {
			out = append(out, w)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].CreatedUtc.After(out[j].CreatedUtc) })
	return out
}

// TopRatedWork returns the work with the highest mean critique score, or
// (zero,false) when no surviving work has critiques. Critiques are grouped by
// WorkId (Ordinal, first-encounter order over insertion order); groups are stably
// ordered by average score descending; the first group whose work still exists
// wins. Ports InMemoryCreativeBoard.TopRatedWork.
func (b *InMemoryCreativeBoard) TopRatedWork() (CreativeWork, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()
	type group struct {
		workId string
		sum    float64
		count  int
		order  int
	}
	groups := make(map[string]*group)
	ordered := make([]*group, 0)
	for _, c := range b.critiques {
		g, ok := groups[c.WorkId]
		if !ok {
			g = &group{workId: c.WorkId, order: len(ordered)}
			groups[c.WorkId] = g
			ordered = append(ordered, g)
		}
		g.sum += float64(c.Score)
		g.count++
	}
	// Stable order by average descending; encounter order (already in `ordered`)
	// is the tie-break, matching C# GroupBy + OrderByDescending stability.
	sort.SliceStable(ordered, func(i, j int) bool {
		return ordered[i].sum/float64(ordered[i].count) > ordered[j].sum/float64(ordered[j].count)
	})
	for _, g := range ordered {
		if w, ok := b.works[g.workId]; ok {
			return w, true
		}
	}
	return CreativeWork{}, false
}

// AllTags lists every distinct tag across all works (case-insensitive dedup,
// keeping the first-encountered spelling), ordered case-insensitively. Works are
// visited in WorkId order so the retained spelling is deterministic. Ports
// InMemoryCreativeBoard.AllTags.
func (b *InMemoryCreativeBoard) AllTags() []string {
	b.mu.Lock()
	ids := make([]string, 0, len(b.works))
	for id := range b.works {
		ids = append(ids, id)
	}
	sort.Strings(ids)
	seen := make(map[string]struct{})
	out := make([]string, 0)
	for _, id := range ids {
		for _, t := range b.works[id].Tags {
			key := strings.ToUpper(t)
			if _, dup := seen[key]; dup {
				continue
			}
			seen[key] = struct{}{}
			out = append(out, t)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return ordinalIgnoreCaseLess(out[i], out[j]) })
	return out
}

// Interface guard.
var _ CreativeBoard = (*InMemoryCreativeBoard)(nil)
