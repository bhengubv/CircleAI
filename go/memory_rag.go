// memory_rag.go
//
// Retrieval-augmented context assembly. Ported from CircleAI.Memory (C#) and
// mirrors the TypeScript pilot (memory/rag.ts) 1:1:
//   • ITextEmbedder (CircleAI.Embeddings) — the semantic-ranking seam
//   • RagContextBuilder — retrieves the most relevant episodes and formats them
//     as a compact context block for injection into the B! system prompt
//   • RagPipelineBuilder — fluent factory with sensible defaults
//
// RAG is strictly best-effort: any retrieval / embedding failure degrades to an
// empty string and must never block inference. In-memory port — the C#
// WithSqliteStore convenience is intentionally omitted (no SQLite backend in
// this tree); use WithStore / WithInMemoryStore instead.

package circleai

import (
	"context"
	"errors"
	"strings"
	"time"
)

// ---------------------------------------------------------------------------
// ITextEmbedder — CircleAI.Embeddings.ITextEmbedder
// ---------------------------------------------------------------------------

// ITextEmbedder produces an embedding vector for a text.
type ITextEmbedder interface {
	// Generate returns the embedding vector for text.
	Generate(ctx context.Context, text string) ([]float32, error)
}

// ---------------------------------------------------------------------------
// RagContextBuilder — CircleAI.Memory.RagContextBuilder
// ---------------------------------------------------------------------------

// RagContextBuilder retrieves the most semantically relevant episodes from an
// IEpisodicMemoryStore and formats them as a compact context block for
// injection into the B! system prompt.
type RagContextBuilder struct {
	store            IEpisodicMemoryStore
	embedder         ITextEmbedder // may be nil
	topK             int
	maxCharsPerEntry int
}

// NewRagContextBuilder creates a RagContextBuilder.
//
// store is the episodic store to query (required). embedder is optional: when
// non-nil, uses semantic similarity to rank results; when nil, falls back to
// recency ranking. topK is the maximum number of episodes to include (floored
// at 1). maxCharsPerEntry is the maximum characters taken from each episode's
// texts (floored at 50).
func NewRagContextBuilder(store IEpisodicMemoryStore, embedder ITextEmbedder, topK, maxCharsPerEntry int) (*RagContextBuilder, error) {
	if store == nil {
		return nil, errors.New("store required")
	}
	if topK < 1 {
		topK = 1
	}
	if maxCharsPerEntry < 50 {
		maxCharsPerEntry = 50
	}
	return &RagContextBuilder{
		store:            store,
		embedder:         embedder,
		topK:             topK,
		maxCharsPerEntry: maxCharsPerEntry,
	}, nil
}

// BuildContext builds a context block for the given query text. Returns an
// empty string when the query is blank, the store is empty, or all retrievals
// fail (RAG is best-effort and must never block inference).
func (b *RagContextBuilder) BuildContext(ctx context.Context, query string) string {
	if strings.TrimSpace(query) == "" {
		return ""
	}

	var queryEmbedding []float32
	if b.embedder != nil {
		if emb, err := b.embedder.Generate(ctx, query); err == nil {
			queryEmbedding = emb
		}
		// Embedding failure is non-fatal — fall back to recency.
	}

	entries, err := b.store.Search(ctx, queryEmbedding, b.topK)
	if err != nil {
		// RAG is strictly best-effort — never break inference.
		return ""
	}
	if len(entries) == 0 {
		return ""
	}
	return b.formatEntries(entries)
}

func (b *RagContextBuilder) formatEntries(entries []EpisodicMemoryEntry) string {
	// Half-budget per side, integer-divided to match the C# `_maxCharsPerEntry / 2`.
	half := b.maxCharsPerEntry / 2
	var sb strings.Builder
	sb.WriteString("[Relevant past exchanges — for context only]\n")

	for _, e := range entries {
		user := truncateText(e.UserText, half)
		asst := truncateText(e.AssistantText, half)
		when := formatWhen(e.RecordedAtUTC) + " UTC"

		sb.WriteString("• [")
		sb.WriteString(when)
		sb.WriteString("] ")
		if e.AppContext != nil && strings.TrimSpace(*e.AppContext) != "" {
			sb.WriteString("(")
			sb.WriteString(*e.AppContext)
			sb.WriteString(") ")
		}
		sb.WriteString("User: ")
		sb.WriteString(user)
		sb.WriteString("\n")
		sb.WriteString("  B!: ")
		sb.WriteString(asst)
		sb.WriteString("\n")
	}

	return sb.String()
}

// truncateText truncates to maxLen runes, replacing the last kept rune with an
// ellipsis (matches C# Truncate, which counts UTF-16 chars; for the ASCII-only
// test inputs the two agree).
func truncateText(text string, maxLen int) string {
	if text == "" {
		return ""
	}
	runes := []rune(text)
	if len(runes) <= maxLen {
		return text
	}
	return string(runes[:maxLen-1]) + "…"
}

// formatWhen formats a UTC time as "yyyy-MM-dd HH:mm" (matches the C# ToString
// on a UTC value).
func formatWhen(t time.Time) string {
	return t.UTC().Format("2006-01-02 15:04")
}

// ---------------------------------------------------------------------------
// RagPipelineBuilder — CircleAI.Memory.RagPipelineBuilder
// ---------------------------------------------------------------------------

// RagPipelineBuilder is a fluent builder for constructing a RagContextBuilder
// with an episodic store, optional embedder, and tuning parameters.
type RagPipelineBuilder struct {
	store            IEpisodicMemoryStore
	embedder         ITextEmbedder
	topK             int
	maxCharsPerEntry int
	err              error // first configuration error, surfaced at Build
}

// NewRagPipelineBuilder creates a new RagPipelineBuilder instance with the
// default topK (5) and maxCharsPerEntry (300).
func NewRagPipelineBuilder() *RagPipelineBuilder {
	return &RagPipelineBuilder{topK: 5, maxCharsPerEntry: 300}
}

// WithStore sets the episodic memory store to retrieve past exchanges from.
func (b *RagPipelineBuilder) WithStore(store IEpisodicMemoryStore) *RagPipelineBuilder {
	if store == nil {
		if b.err == nil {
			b.err = errors.New("store required")
		}
		return b
	}
	b.store = store
	return b
}

// WithInMemoryStore creates an InMemoryEpisodicStore (default cap) and uses it.
// Suitable for tests and short-lived processes where persistence is not needed.
func (b *RagPipelineBuilder) WithInMemoryStore() *RagPipelineBuilder {
	b.store = NewInMemoryEpisodicStoreDefault()
	return b
}

// WithEmbedder sets the text embedder for semantic similarity search. When not
// set, the builder falls back to recency-based retrieval.
func (b *RagPipelineBuilder) WithEmbedder(embedder ITextEmbedder) *RagPipelineBuilder {
	if embedder == nil {
		if b.err == nil {
			b.err = errors.New("embedder required")
		}
		return b
	}
	b.embedder = embedder
	return b
}

// WithTopK sets the max number of relevant past episodes to include. Min 1.
func (b *RagPipelineBuilder) WithTopK(topK int) *RagPipelineBuilder {
	if topK < 1 {
		if b.err == nil {
			b.err = errors.New("topK must be at least 1")
		}
		return b
	}
	b.topK = topK
	return b
}

// WithMaxCharsPerEntry sets the max characters taken from each episode's texts.
// Min 50.
func (b *RagPipelineBuilder) WithMaxCharsPerEntry(maxChars int) *RagPipelineBuilder {
	if maxChars < 50 {
		if b.err == nil {
			b.err = errors.New("maxChars must be at least 50")
		}
		return b
	}
	b.maxCharsPerEntry = maxChars
	return b
}

// Build builds the RagContextBuilder from the accumulated configuration.
// Returns an error when no store was configured or a prior fluent call failed.
func (b *RagPipelineBuilder) Build() (*RagContextBuilder, error) {
	if b.err != nil {
		return nil, b.err
	}
	if b.store == nil {
		return nil, errors.New("an episodic memory store is required; call WithStore() or WithInMemoryStore() before Build()")
	}
	return NewRagContextBuilder(b.store, b.embedder, b.topK, b.maxCharsPerEntry)
}
