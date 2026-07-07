// memory_recall.go
//
// Fused associative recall (Reciprocal Rank Fusion). Ported from
// CircleAI.Companion (IRecall, FusedRecall) — the C# reference — and mirrors the
// TypeScript pilot (memory/recall.ts) 1:1.
//
// Fuses two memory systems with incomparable score spaces — episodic cosine
// similarity and graph association (Personalised PageRank) — into one ranked
// context. RRF combines ranked lists by *position*, so it needs no shared score
// scale: each source contributes 1 / (k + rank).
//
// Cold-start is automatic: a new user has an empty graph, so only episodic
// contributes and the fused order equals the episodic order — no special case.

package circleai

import (
	"context"
	"errors"
	"sort"
	"strconv"
	"strings"
	"time"
	"unicode"
)

// IRecall is unified memory recall — the most relevant memories for a turn.
type IRecall interface {
	// Recall returns the topK most relevant memories for the current turn.
	// query drives graph association; queryEmbedding drives episodic cosine
	// similarity (may be nil → episodic recency fallback).
	Recall(ctx context.Context, query string, queryEmbedding []float32, topK int) ([]MemoryHit, error)
}

// FusedRecallOptions tunes FusedRecall.
type FusedRecallOptions struct {
	// CandidatePoolSize is the number of candidates pulled from each source
	// before fusion. Default 20.
	CandidatePoolSize int
	// RrfK is the RRF damping constant k. Default 60 (the standard value).
	RrfK int
	// GraphConfidenceThreshold drops graph hits whose backing confidence
	// (metadata key "confidence") is below it. Applied only when a hit actually
	// carries a confidence value. Default 0.4.
	GraphConfidenceThreshold float64
}

var fusedRecallDefaults = FusedRecallOptions{
	CandidatePoolSize:        20,
	RrfK:                     60,
	GraphConfidenceThreshold: 0.4,
}

// FusedRecall is Reciprocal-Rank-Fusion recall over episodic similarity + graph
// association.
type FusedRecall struct {
	episodic IEpisodicMemoryStore
	graph    IHippoRagStore // may be nil
	opts     FusedRecallOptions
}

// NewFusedRecall creates a FusedRecall. graph may be nil (cold-start / pure
// episodic). opts may be nil for defaults; any zero field in opts falls back to
// its default.
func NewFusedRecall(episodic IEpisodicMemoryStore, graph IHippoRagStore, opts *FusedRecallOptions) (*FusedRecall, error) {
	if episodic == nil {
		return nil, errors.New("episodic required")
	}
	merged := fusedRecallDefaults
	if opts != nil {
		if opts.CandidatePoolSize != 0 {
			merged.CandidatePoolSize = opts.CandidatePoolSize
		}
		if opts.RrfK != 0 {
			merged.RrfK = opts.RrfK
		}
		if opts.GraphConfidenceThreshold != 0 {
			merged.GraphConfidenceThreshold = opts.GraphConfidenceThreshold
		}
	}
	return &FusedRecall{episodic: episodic, graph: graph, opts: merged}, nil
}

// Recall runs episodic similarity (or recency), best-effort graph association,
// and fuses them by Reciprocal Rank Fusion.
func (r *FusedRecall) Recall(ctx context.Context, query string, queryEmbedding []float32, topK int) ([]MemoryHit, error) {
	if topK <= 0 {
		return nil, errors.New("topK must be positive")
	}

	pool := r.opts.CandidatePoolSize

	// Fast path: episodic similarity (or recency when the embedding is nil).
	episodic, err := r.episodic.Search(ctx, queryEmbedding, pool)
	if err != nil {
		return nil, err
	}

	// Slow path: graph association. Optional and best-effort — a missing, empty,
	// or failing graph degrades to pure episodic, never propagates the error. An
	// empty query cannot seed a graph walk, so skip it.
	var graph []MemoryHit
	if r.graph != nil && strings.TrimSpace(query) != "" {
		if hits, gerr := r.graph.MultiHopRecall(ctx, query, pool); gerr == nil {
			graph = hits
		}
	}

	// Reciprocal Rank Fusion: accumulate 1 / (k + rank) per candidate across both
	// ranked lists, keyed by normalised text so a memory surfaced by both sources
	// reinforces rather than duplicates.
	k := float64(r.opts.RrfK)

	type fusedEntry struct {
		item  MemoryItem
		score float64
	}
	fused := make(map[string]*fusedEntry)
	var order []string // insertion order, for stable output among equal scores

	accumulate := func(item MemoryItem, oneBasedRank int) {
		key := normaliseKey(item.Text)
		if key == "" {
			return
		}
		contribution := 1.0 / (k + float64(oneBasedRank))
		if existing, ok := fused[key]; ok {
			existing.score += contribution
		} else {
			fused[key] = &fusedEntry{item: item, score: contribution}
			order = append(order, key)
		}
	}

	for i, e := range episodic {
		accumulate(adaptEpisodic(e), i+1)
	}
	for i, h := range graph {
		if isBelowConfidence(h, r.opts.GraphConfidenceThreshold) {
			continue
		}
		accumulate(h.Item, i+1)
	}

	// Rank position for stable tie-breaking mirroring the pilot's Map order.
	pos := make(map[string]int, len(order))
	for i, key := range order {
		pos[key] = i
	}

	result := make([]MemoryHit, 0, len(fused))
	for _, e := range fused {
		result = append(result, MemoryHit{Item: e.item, Score: e.score})
	}
	sort.SliceStable(result, func(i, j int) bool {
		if result[i].Score != result[j].Score {
			return result[i].Score > result[j].Score
		}
		return pos[normaliseKey(result[i].Item.Text)] < pos[normaliseKey(result[j].Item.Text)]
	})
	if topK < len(result) {
		result = result[:topK]
	}
	return result, nil
}

// isBelowConfidence reports whether a graph hit carries a confidence value below
// the threshold. A hit with no confidence metadata is never below (gate no-op).
func isBelowConfidence(hit MemoryHit, threshold float64) bool {
	if hit.Item.Metadata == nil {
		return false
	}
	raw, ok := hit.Item.Metadata["confidence"]
	if !ok {
		return false
	}
	c, err := strconv.ParseFloat(raw, 64)
	if err != nil {
		return false
	}
	return c < threshold
}

// adaptEpisodic maps an episodic entry into the shared MemoryItem currency,
// keyed by the user's text and stamped with episodic provenance metadata.
func adaptEpisodic(e EpisodicMemoryEntry) MemoryItem {
	meta := map[string]string{
		"source":     "episodic",
		"recordedAt": e.RecordedAtUTC.UTC().Format(time.RFC3339Nano),
	}
	if e.AssistantText != "" {
		meta["assistantText"] = e.AssistantText
	}
	if e.AppContext != nil && *e.AppContext != "" {
		meta["appContext"] = *e.AppContext
	}
	return MemoryItem{ID: e.ID.String(), Text: e.UserText, Metadata: meta}
}

// normaliseKey lowercases and collapses internal whitespace so equivalent texts
// fuse to one key.
func normaliseKey(text string) string {
	trimmed := strings.TrimSpace(text)
	if trimmed == "" {
		return ""
	}
	var b strings.Builder
	prevSpace := false
	for _, ch := range trimmed {
		if unicode.IsSpace(ch) {
			if !prevSpace {
				b.WriteRune(' ')
				prevSpace = true
			}
		} else {
			b.WriteRune(unicode.ToLower(ch))
			prevSpace = false
		}
	}
	return b.String()
}

// Compile-time assertion that the concrete recall satisfies the interface.
var _ IRecall = (*FusedRecall)(nil)
