// research_board.go
//
// Ports CircleAI.Research:
//   ResearchPaper / Citation                 (Contracts.cs)
//   IResearchCorpus / IPaperRetrieval / ICitationGraph (Contracts.cs)
//   InMemoryResearchCorpus / InMemoryPaperRetrieval / InMemoryCitationGraph (InMemoryResearch.cs)
//   NullResearchCorpus / NullPaperRetrieval / NullCitationGraph (NullImplementations.cs)
//
// ValueTask<T?> Get -> (T, bool); ReadOnlyMemory<byte>? -> []byte (nil == none).
// Search uses the same substring-score heuristic (title x3, abstract x1,
// authors x1) filtered to score>0, ordered descending, Take(topK).

package circleai

import (
	"context"
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// ResearchPaper is a single paper. Ports the ResearchPaper record. Doi is a
// *string to model the nullable field.
type ResearchPaper struct {
	PaperID       string
	Title         string
	Authors       []string
	Abstract      string
	PublishedAtUTC time.Time
	Doi           *string
}

// Citation is a directed citation edge with context. Ports Citation.
type Citation struct {
	FromPaperID string
	ToPaperID   string
	Context     string
}

// IResearchCorpus is a paper corpus. Ports IResearchCorpus.
type IResearchCorpus interface {
	BackendID() string
	Get(ctx context.Context, paperID string) (ResearchPaper, bool, error)
	Search(ctx context.Context, query string, topK int) ([]ResearchPaper, error)
}

// IPaperRetrieval fetches full text. Ports IPaperRetrieval.
type IPaperRetrieval interface {
	BackendID() string
	// FetchFullText returns the bytes, or (nil,false) when unavailable.
	FetchFullText(ctx context.Context, paperID string) ([]byte, bool, error)
}

// ICitationGraph resolves citations. Ports ICitationGraph.
type ICitationGraph interface {
	BackendID() string
	ForwardCitations(ctx context.Context, paperID string) ([]Citation, error)
	BackwardCitations(ctx context.Context, paperID string) ([]Citation, error)
}

// ---------------------------------------------------------------------------
// InMemoryResearchCorpus
// ---------------------------------------------------------------------------

// InMemoryResearchCorpus is a substring-scored in-memory corpus. Ports
// InMemoryResearchCorpus.
type InMemoryResearchCorpus struct {
	mu     sync.RWMutex
	papers map[string]ResearchPaper
}

// NewInMemoryResearchCorpus constructs an empty corpus.
func NewInMemoryResearchCorpus() *InMemoryResearchCorpus {
	return &InMemoryResearchCorpus{papers: make(map[string]ResearchPaper)}
}

// BackendID returns "in-memory".
func (c *InMemoryResearchCorpus) BackendID() string { return "in-memory" }

// Add stores (or replaces by PaperID) a paper. Ports Add.
func (c *InMemoryResearchCorpus) Add(paper ResearchPaper) {
	c.mu.Lock()
	c.papers[paper.PaperID] = paper
	c.mu.Unlock()
}

// Get returns the paper for paperID. Ports GetAsync. Errors on empty id.
func (c *InMemoryResearchCorpus) Get(ctx context.Context, paperID string) (ResearchPaper, bool, error) {
	if strings.TrimSpace(paperID) == "" {
		return ResearchPaper{}, false, errors.New("paperId required")
	}
	c.mu.RLock()
	p, ok := c.papers[paperID]
	c.mu.RUnlock()
	return p, ok, nil
}

// Search returns up to topK papers scoring > 0, ordered by descending score.
// Ports SearchAsync.
func (c *InMemoryResearchCorpus) Search(ctx context.Context, query string, topK int) ([]ResearchPaper, error) {
	if topK <= 0 {
		return nil, errors.New("topK out of range")
	}
	c.mu.RLock()
	type scored struct {
		p     ResearchPaper
		score int
	}
	hits := make([]scored, 0)
	for _, p := range c.papers {
		if s := researchScore(p, query); s > 0 {
			hits = append(hits, scored{p: p, score: s})
		}
	}
	c.mu.RUnlock()
	sort.SliceStable(hits, func(i, j int) bool { return hits[i].score > hits[j].score })
	if topK < len(hits) {
		hits = hits[:topK]
	}
	out := make([]ResearchPaper, len(hits))
	for i, h := range hits {
		out[i] = h.p
	}
	return out, nil
}

func researchScore(p ResearchPaper, q string) int {
	s := 0
	if p.Title != "" && strings.Contains(strings.ToLower(p.Title), strings.ToLower(q)) {
		s += 3
	}
	if p.Abstract != "" && strings.Contains(strings.ToLower(p.Abstract), strings.ToLower(q)) {
		s += 1
	}
	for _, a := range p.Authors {
		if strings.Contains(strings.ToLower(a), strings.ToLower(q)) {
			s += 1
			break
		}
	}
	return s
}

var _ IResearchCorpus = (*InMemoryResearchCorpus)(nil)

// ---------------------------------------------------------------------------
// InMemoryPaperRetrieval
// ---------------------------------------------------------------------------

// InMemoryPaperRetrieval serves registered full-text bytes. Ports
// InMemoryPaperRetrieval.
type InMemoryPaperRetrieval struct {
	mu    sync.RWMutex
	texts map[string][]byte
}

// NewInMemoryPaperRetrieval constructs an empty retrieval store.
func NewInMemoryPaperRetrieval() *InMemoryPaperRetrieval {
	return &InMemoryPaperRetrieval{texts: make(map[string][]byte)}
}

// BackendID returns "in-memory".
func (r *InMemoryPaperRetrieval) BackendID() string { return "in-memory" }

// Add registers full text for paperID. Ports Add. Errors on empty id.
func (r *InMemoryPaperRetrieval) Add(paperID string, fullText []byte) error {
	if strings.TrimSpace(paperID) == "" {
		return errors.New("paperId required")
	}
	r.mu.Lock()
	r.texts[paperID] = fullText
	r.mu.Unlock()
	return nil
}

// FetchFullText returns registered bytes for paperID. Ports FetchFullTextAsync.
func (r *InMemoryPaperRetrieval) FetchFullText(ctx context.Context, paperID string) ([]byte, bool, error) {
	if strings.TrimSpace(paperID) == "" {
		return nil, false, errors.New("paperId required")
	}
	r.mu.RLock()
	b, ok := r.texts[paperID]
	r.mu.RUnlock()
	if !ok {
		return nil, false, nil
	}
	return b, true, nil
}

var _ IPaperRetrieval = (*InMemoryPaperRetrieval)(nil)

// ---------------------------------------------------------------------------
// InMemoryCitationGraph
// ---------------------------------------------------------------------------

// InMemoryCitationGraph is a plain forward/backward adjacency list. Ports
// InMemoryCitationGraph.
type InMemoryCitationGraph struct {
	mu       sync.Mutex
	forward  map[string][]Citation
	backward map[string][]Citation
}

// NewInMemoryCitationGraph constructs an empty graph.
func NewInMemoryCitationGraph() *InMemoryCitationGraph {
	return &InMemoryCitationGraph{
		forward:  make(map[string][]Citation),
		backward: make(map[string][]Citation),
	}
}

// BackendID returns "in-memory".
func (g *InMemoryCitationGraph) BackendID() string { return "in-memory" }

// Link records a citation in both directions. Ports Link.
func (g *InMemoryCitationGraph) Link(c Citation) {
	g.mu.Lock()
	g.forward[c.FromPaperID] = append(g.forward[c.FromPaperID], c)
	g.backward[c.ToPaperID] = append(g.backward[c.ToPaperID], c)
	g.mu.Unlock()
}

// ForwardCitations returns citations from paperID. Ports ForwardCitationsAsync.
func (g *InMemoryCitationGraph) ForwardCitations(ctx context.Context, paperID string) ([]Citation, error) {
	if strings.TrimSpace(paperID) == "" {
		return nil, errors.New("paperId required")
	}
	g.mu.Lock()
	defer g.mu.Unlock()
	return append([]Citation(nil), g.forward[paperID]...), nil
}

// BackwardCitations returns citations to paperID. Ports BackwardCitationsAsync.
func (g *InMemoryCitationGraph) BackwardCitations(ctx context.Context, paperID string) ([]Citation, error) {
	if strings.TrimSpace(paperID) == "" {
		return nil, errors.New("paperId required")
	}
	g.mu.Lock()
	defer g.mu.Unlock()
	return append([]Citation(nil), g.backward[paperID]...), nil
}

var _ ICitationGraph = (*InMemoryCitationGraph)(nil)

// ---------------------------------------------------------------------------
// Null implementations
// ---------------------------------------------------------------------------

// NullResearchCorpus is a fail-safe empty corpus. Ports NullResearchCorpus.
type NullResearchCorpus struct{}

// NullResearchCorpusInstance is the shared singleton (C# .Instance).
var NullResearchCorpusInstance = NullResearchCorpus{}

func (NullResearchCorpus) BackendID() string { return "null" }
func (NullResearchCorpus) Get(context.Context, string) (ResearchPaper, bool, error) {
	return ResearchPaper{}, false, nil
}
func (NullResearchCorpus) Search(context.Context, string, int) ([]ResearchPaper, error) {
	return []ResearchPaper{}, nil
}

// NullPaperRetrieval is a fail-safe empty retrieval. Ports NullPaperRetrieval.
type NullPaperRetrieval struct{}

// NullPaperRetrievalInstance is the shared singleton.
var NullPaperRetrievalInstance = NullPaperRetrieval{}

func (NullPaperRetrieval) BackendID() string { return "null" }
func (NullPaperRetrieval) FetchFullText(context.Context, string) ([]byte, bool, error) {
	return nil, false, nil
}

// NullCitationGraph is a fail-safe empty graph. Ports NullCitationGraph.
type NullCitationGraph struct{}

// NullCitationGraphInstance is the shared singleton.
var NullCitationGraphInstance = NullCitationGraph{}

func (NullCitationGraph) BackendID() string { return "null" }
func (NullCitationGraph) ForwardCitations(context.Context, string) ([]Citation, error) {
	return []Citation{}, nil
}
func (NullCitationGraph) BackwardCitations(context.Context, string) ([]Citation, error) {
	return []Citation{}, nil
}

var (
	_ IResearchCorpus = NullResearchCorpus{}
	_ IPaperRetrieval = NullPaperRetrieval{}
	_ ICitationGraph  = NullCitationGraph{}
)
