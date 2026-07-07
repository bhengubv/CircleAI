// memory_extractor.go
//
// Knowledge-graph extraction: turn → (subject, predicate, object) triples.
// Ported from CircleAI.Companion (IKnowledgeGraphExtractor,
// HeuristicKnowledgeGraphExtractor) — the C# reference — and mirrors the
// TypeScript pilot (memory/extractor.ts) 1:1.
//
// The heuristic extractor is model-free: it links the content words a turn
// mentions to the memory they came from, two-way, so a later question can reach
// an older memory across turns. It is the offline counterpart to the LLM-based
// extractor (same interface, no network) — the graph still fills, just coarsely.

package circleai

import (
	"context"
	"strings"
	"time"
)

// IKnowledgeGraphExtractor turns a conversation turn into knowledge-graph
// triples.
type IKnowledgeGraphExtractor interface {
	ExtractFromTurn(ctx context.Context, userText, assistantText string, sourceEpisodeID *string) ([]KnowledgeTriple, error)
}

const defaultTripleConfidence = 0.6

// kgStopWords are common function words that carry no association — dropped so
// links form on meaningful words (names, places, symptoms, things).
var kgStopWords = map[string]struct{}{
	"the": {}, "a": {}, "an": {}, "and": {}, "or": {}, "but": {}, "if": {}, "is": {}, "are": {}, "was": {},
	"were": {}, "be": {}, "been": {}, "being": {}, "to": {}, "of": {}, "in": {}, "on": {}, "at": {}, "for": {},
	"with": {}, "from": {}, "by": {}, "as": {}, "into": {}, "about": {}, "over": {}, "under": {},
	"my": {}, "your": {}, "our": {}, "their": {}, "his": {}, "her": {}, "its": {}, "this": {}, "that": {},
	"these": {}, "those": {}, "i": {}, "you": {}, "he": {}, "she": {}, "it": {}, "we": {}, "they": {},
	"me": {}, "him": {}, "them": {}, "us": {}, "do": {}, "does": {}, "did": {}, "done": {}, "have": {},
	"has": {}, "had": {}, "will": {}, "would": {}, "can": {}, "could": {}, "should": {}, "shall": {},
	"may": {}, "might": {}, "must": {}, "not": {}, "no": {}, "yes": {}, "so": {}, "than": {}, "then": {},
	"there": {}, "here": {}, "how": {}, "why": {}, "what": {}, "when": {}, "where": {}, "who": {},
	"which": {}, "whom": {}, "am": {}, "get": {}, "got": {}, "really": {}, "just": {}, "very": {},
	"much": {}, "many": {}, "some": {}, "any": {}, "all": {},
}

// HeuristicKnowledgeGraphExtractor is a model-free extractor: it links a turn's
// content words to their memory, two-way.
type HeuristicKnowledgeGraphExtractor struct{}

// ExtractFromTurn produces bidirectional mentions/seenin triples for each
// content word in the turn. The memory node is identified by sourceEpisodeID
// when given, else the user's words — so recall can hand back the memory the
// words came from.
func (e *HeuristicKnowledgeGraphExtractor) ExtractFromTurn(_ context.Context, userText, assistantText string, sourceEpisodeID *string) ([]KnowledgeTriple, error) {
	memory := ""
	if sourceEpisodeID != nil && strings.TrimSpace(*sourceEpisodeID) != "" {
		memory = *sourceEpisodeID
	} else {
		memory = userText
	}
	if strings.TrimSpace(memory) == "" {
		return []KnowledgeTriple{}, nil
	}

	words := contentWords(userText + " " + assistantText)
	now := time.Now().UTC()
	triples := make([]KnowledgeTriple, 0, len(words)*2)
	for _, w := range words {
		// Two-way so a walk can go word → memory → word → memory across turns.
		triples = append(triples,
			KnowledgeTriple{Subject: memory, Predicate: "mentions", Object: w, Source: sourceEpisodeID, Confidence: defaultTripleConfidence, RecordedAtUTC: now},
			KnowledgeTriple{Subject: w, Predicate: "seenin", Object: memory, Source: sourceEpisodeID, Confidence: defaultTripleConfidence, RecordedAtUTC: now},
		)
	}
	return triples, nil
}

// contentWords lowercases, splits on separators, drops short/stop words, and
// dedupes preserving order. Split set mirrors the TS/C# [ \t\n\r.,?!;:'"()/-]+.
func contentWords(text string) []string {
	seen := make(map[string]struct{})
	var result []string
	for _, raw := range strings.FieldsFunc(strings.ToLower(text), isKgSeparator) {
		if len(raw) < 3 {
			continue
		}
		if _, stop := kgStopWords[raw]; stop {
			continue
		}
		if _, ok := seen[raw]; ok {
			continue
		}
		seen[raw] = struct{}{}
		result = append(result, raw)
	}
	return result
}

// isKgSeparator matches the extractor split set: whitespace and . , ? ! ; : ' " ( ) / -.
func isKgSeparator(r rune) bool {
	switch r {
	case ' ', '\t', '\n', '\r', '.', ',', '?', '!', ';', ':', '\'', '"', '(', ')', '/', '-':
		return true
	}
	return false
}

// Compile-time assertion.
var _ IKnowledgeGraphExtractor = (*HeuristicKnowledgeGraphExtractor)(nil)
