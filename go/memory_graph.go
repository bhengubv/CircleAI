// memory_graph.go
//
// Personal knowledge graph + HippoRAG multi-hop recall (Personalised PageRank).
//
// Ported from CircleAI.Domain (MemoryItem / MemoryHit / IHippoRagStore) and
// CircleAI.Companion (SqliteKnowledgeGraph, SqliteHippoRagStore) — the C#
// reference — and mirrors the TypeScript pilot (memory/graph.ts) 1:1. This is
// the in-memory port: identical algorithms, no SQLite.
//
// HippoRAG (Wang et al. 2024): each memory item is a node in the personal KG;
// at recall time the query's entities seed a Personalised PageRank walk, and the
// nodes with the highest steady-state probability are the multi-hop matches.

package circleai

import (
	"context"
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// Shared recall currency (CircleAI.Domain Contracts)
// ---------------------------------------------------------------------------

// MemoryItem is one recallable memory with optional string metadata.
type MemoryItem struct {
	ID       string
	Text     string
	Metadata map[string]string
}

// MemoryHit is a recalled memory paired with its relevance score.
type MemoryHit struct {
	Item  MemoryItem
	Score float64
}

// IHippoRagStore is the HippoRAG-pattern memory + knowledge-graph +
// Personalised PageRank recall seam.
type IHippoRagStore interface {
	// BackendId identifies the backing implementation.
	BackendId() string
	// Index ensures the memory item exists as a node the walker can land on.
	Index(ctx context.Context, item MemoryItem) error
	// MultiHopRecall seeds a Personalised PageRank walk from the query's terms
	// and returns the topK reached nodes.
	MultiHopRecall(ctx context.Context, query string, topK int) ([]MemoryHit, error)
}

// ---------------------------------------------------------------------------
// Knowledge graph node + triple
// ---------------------------------------------------------------------------

// KnowledgeNode is a node in the personal knowledge graph.
type KnowledgeNode struct {
	ID         string
	Kind       string
	Name       string
	Properties map[string]string
}

// KnowledgeTriple is one (subject, predicate, object) triple with provenance
// (source + confidence).
type KnowledgeTriple struct {
	Subject       string
	Predicate     string
	Object        string
	Source        *string
	Confidence    float64
	RecordedAtUTC time.Time
}

const tripleSep = " "

// KnowledgeGraph is an in-memory personal knowledge graph. Triples are keyed by
// (subject, predicate, object) — re-adding the same triple replaces its
// provenance, matching the C# SQLite store's INSERT OR REPLACE on the composite
// primary key. Safe for concurrent use.
type KnowledgeGraph struct {
	mu      sync.Mutex
	nodes   map[string]KnowledgeNode
	triples map[string]KnowledgeTriple
}

// NewKnowledgeGraph returns an empty knowledge graph.
func NewKnowledgeGraph() *KnowledgeGraph {
	return &KnowledgeGraph{
		nodes:   make(map[string]KnowledgeNode),
		triples: make(map[string]KnowledgeTriple),
	}
}

// UpsertNode inserts or replaces a node by id.
func (g *KnowledgeGraph) UpsertNode(node KnowledgeNode) error {
	if strings.TrimSpace(node.ID) == "" {
		return errors.New("node.ID required")
	}
	g.mu.Lock()
	defer g.mu.Unlock()
	g.nodes[node.ID] = node
	return nil
}

// GetNode returns the node with the given id, or nil.
func (g *KnowledgeGraph) GetNode(id string) *KnowledgeNode {
	g.mu.Lock()
	defer g.mu.Unlock()
	n, ok := g.nodes[id]
	if !ok {
		return nil
	}
	cp := n
	return &cp
}

// AddTriple adds (or replaces) a triple with full provenance. Re-adding the same
// (subject, predicate, object) replaces the prior provenance.
func (g *KnowledgeGraph) AddTriple(subject, predicate, object string, source *string, confidence float64) error {
	if strings.TrimSpace(subject) == "" {
		return errors.New("subject required")
	}
	if strings.TrimSpace(predicate) == "" {
		return errors.New("predicate required")
	}
	if strings.TrimSpace(object) == "" {
		return errors.New("object required")
	}
	if confidence < 0 || confidence > 1 {
		return errors.New("confidence must be in [0,1]")
	}

	key := subject + tripleSep + predicate + tripleSep + object
	g.mu.Lock()
	defer g.mu.Unlock()
	g.triples[key] = KnowledgeTriple{
		Subject:       subject,
		Predicate:     predicate,
		Object:        object,
		Source:        source,
		Confidence:    confidence,
		RecordedAtUTC: time.Now().UTC(),
	}
	return nil
}

// AllTriples returns every triple — used by HippoRAG for the graph walk.
func (g *KnowledgeGraph) AllTriples() []KnowledgeTriple {
	g.mu.Lock()
	defer g.mu.Unlock()
	out := make([]KnowledgeTriple, 0, len(g.triples))
	for _, t := range g.triples {
		out = append(out, t)
	}
	return out
}

// ReadTriples returns the raw triples for one subject (inspection / debugging).
func (g *KnowledgeGraph) ReadTriples(subject string) ([]KnowledgeTriple, error) {
	if strings.TrimSpace(subject) == "" {
		return nil, errors.New("subject required")
	}
	g.mu.Lock()
	defer g.mu.Unlock()
	var out []KnowledgeTriple
	for _, t := range g.triples {
		if t.Subject == subject {
			out = append(out, t)
		}
	}
	return out, nil
}

// ---------------------------------------------------------------------------
// HippoRagStore — Personalised PageRank multi-hop recall
// ---------------------------------------------------------------------------

// HippoRagStore is real HippoRAG recall over a KnowledgeGraph. It walks the
// personal graph via Personalised PageRank (power iteration) seeded from the
// query's terms.
//
// Three precision guarantees carried from the C# reference:
//  1. No query term touches the graph → returns empty (never fabricates an
//     association from arbitrary nodes).
//  2. Seed nodes are excluded from results (recall returns the *associated*
//     nodes the walk reached, not the query echoed back).
//  3. Edge spread is confidence-weighted — a high-confidence edge carries more
//     of the walk's mass than a guessed one, so a shaky belief does not steer
//     recall like a stated fact.
type HippoRagStore struct {
	kg             *KnowledgeGraph
	walkIterations int
	damping        float64
}

// NewHippoRagStore returns a HippoRAG store over kg with default tuning
// (walkIterations 32, damping 0.85).
func NewHippoRagStore(kg *KnowledgeGraph) (*HippoRagStore, error) {
	return NewHippoRagStoreTuned(kg, 32, 0.85)
}

// NewHippoRagStoreTuned returns a HippoRAG store with explicit walk iterations
// and damping factor.
func NewHippoRagStoreTuned(kg *KnowledgeGraph, walkIterations int, damping float64) (*HippoRagStore, error) {
	if kg == nil {
		return nil, errors.New("kg required")
	}
	return &HippoRagStore{kg: kg, walkIterations: walkIterations, damping: damping}, nil
}

// BackendId identifies this backend.
func (h *HippoRagStore) BackendId() string { return "inmemory-hippo-ppr" }

// Index ensures the memory item exists as a node so the walker can land on it.
// The graph itself is populated by the KnowledgeGraphExtractor.
func (h *HippoRagStore) Index(_ context.Context, item MemoryItem) error {
	if strings.TrimSpace(item.ID) == "" {
		return errors.New("item.ID required")
	}
	src := item.ID
	if err := h.kg.AddTriple(item.ID, "memory_text", item.Text, &src, 1.0); err != nil {
		return err
	}
	for k, v := range item.Metadata {
		if err := h.kg.AddTriple(item.ID, k, v, &src, 0.9); err != nil {
			return err
		}
	}
	return nil
}

// MultiHopRecall seeds a Personalised PageRank walk from the query's terms and
// returns the topK reached nodes (seeds excluded).
func (h *HippoRagStore) MultiHopRecall(_ context.Context, query string, topK int) ([]MemoryHit, error) {
	if strings.TrimSpace(query) == "" {
		return nil, errors.New("query required")
	}
	if topK <= 0 {
		return nil, errors.New("topK must be positive")
	}

	triples := h.kg.AllTriples()
	if len(triples) == 0 {
		return []MemoryHit{}, nil
	}

	// Adjacency list: subject -> [(object, confidence)].
	type edge struct {
		nbr  string
		conf float64
	}
	outgoing := make(map[string][]edge)
	allNodes := make(map[string]struct{})
	for _, t := range triples {
		allNodes[t.Subject] = struct{}{}
		allNodes[t.Object] = struct{}{}
		outgoing[t.Subject] = append(outgoing[t.Subject], edge{nbr: t.Object, conf: t.Confidence})
	}

	// Seed the personalisation vector from query terms that appear as nodes.
	queryTerms := make(map[string]struct{})
	for _, tok := range splitNonAlnum(query) {
		if tok != "" {
			queryTerms[strings.ToLower(tok)] = struct{}{}
		}
	}
	var seedNodes []string
	for n := range allNodes {
		if _, ok := queryTerms[strings.ToLower(n)]; ok {
			seedNodes = append(seedNodes, n)
		}
	}
	// Precision guarantee 1: no genuine association → return nothing.
	if len(seedNodes) == 0 {
		return []MemoryHit{}, nil
	}

	rank := make(map[string]float64, len(allNodes))
	for n := range allNodes {
		rank[n] = 0
	}
	seedMass := 1.0 / float64(len(seedNodes))
	for _, s := range seedNodes {
		rank[s] = seedMass
	}

	// Power-iteration Personalised PageRank.
	for iter := 0; iter < h.walkIterations; iter++ {
		next := make(map[string]float64, len(allNodes))
		for n := range allNodes {
			next[n] = 0
		}

		// Random-jump component (personalisation): mass returns to the seeds.
		for _, seed := range seedNodes {
			next[seed] += (1 - h.damping) * seedMass
		}

		// Walk component.
		for node, mass := range rank {
			if mass <= 0 {
				continue
			}
			nbrs := outgoing[node]
			if len(nbrs) == 0 {
				// Dangling node: redistribute via personalisation.
				for _, seed := range seedNodes {
					next[seed] += (h.damping * mass) / float64(len(seedNodes))
				}
				continue
			}
			// Precision guarantee 3: confidence-weighted spread. With equal
			// confidences this reduces to the plain 1/count split.
			var totalConf float64
			for _, e := range nbrs {
				totalConf += e.conf
			}
			for _, e := range nbrs {
				var weight float64
				if totalConf > 0 {
					weight = e.conf / totalConf
				} else {
					weight = 1.0 / float64(len(nbrs))
				}
				next[e.nbr] += h.damping * mass * weight
			}
		}

		rank = next
	}

	// Precision guarantee 2: exclude the seeds — they are the query's own terms.
	seedSet := make(map[string]struct{}, len(seedNodes))
	for _, s := range seedNodes {
		seedSet[s] = struct{}{}
	}

	type kv struct {
		key   string
		value float64
	}
	var ranked []kv
	for key, value := range rank {
		if value <= 0 {
			continue
		}
		if _, isSeed := seedSet[key]; isSeed {
			continue
		}
		ranked = append(ranked, kv{key: key, value: value})
	}
	// Highest PPR mass first. Ties broken by key for deterministic order.
	sort.SliceStable(ranked, func(i, j int) bool {
		if ranked[i].value != ranked[j].value {
			return ranked[i].value > ranked[j].value
		}
		return ranked[i].key < ranked[j].key
	})
	if topK < len(ranked) {
		ranked = ranked[:topK]
	}

	hits := make([]MemoryHit, 0, len(ranked))
	for _, r := range ranked {
		node := h.kg.GetNode(r.key)
		text := r.key
		var props map[string]string
		if node != nil {
			if node.Name != "" {
				text = node.Name
			}
			props = node.Properties
		}
		hits = append(hits, MemoryHit{
			Item:  MemoryItem{ID: r.key, Text: text, Metadata: props},
			Score: r.value,
		})
	}
	return hits, nil
}

// splitNonAlnum splits s on any run of non-alphanumeric ASCII characters,
// matching the C#/TS regex [^A-Za-z0-9]+.
func splitNonAlnum(s string) []string {
	return strings.FieldsFunc(s, func(r rune) bool {
		isAlnum := (r >= 'A' && r <= 'Z') || (r >= 'a' && r <= 'z') || (r >= '0' && r <= '9')
		return !isAlnum
	})
}

// Compile-time assertion that the concrete store satisfies the interface.
var _ IHippoRagStore = (*HippoRagStore)(nil)
