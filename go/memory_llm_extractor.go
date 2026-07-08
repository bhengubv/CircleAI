// memory_llm_extractor.go
//
// LLM-backed knowledge-graph extraction: turn → (subject, predicate, object)
// triples. Ported from CircleAI.Companion (LlmKnowledgeGraphExtractor) — the C#
// reference — and mirrors the TypeScript pilot (memory/llm_extractor.ts) 1:1.
//
// Wraps an on-device IChatGenerator to ask an LLM to extract triples from a
// single conversation turn. The extraction prompt asks for strict-JSON output;
// the parser is defensive against the model emitting extra prose or fences.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"math"
	"strings"
	"time"
)

// defaultLlmTripleConfidence is used when the model omits (or malforms) the "c"
// field.
const defaultLlmTripleConfidence = 0.75

// llmExtractorSystemPrompt is the verbatim extraction prompt (copied from the
// TS/C# reference).
const llmExtractorSystemPrompt = "You are a knowledge-graph extractor. Read the conversation turn between USER and ASSISTANT. " +
	"Identify entities (people, places, things, concepts) and facts. " +
	"Output a single JSON array of triples like [{\"s\":\"Subject\",\"p\":\"predicate\",\"o\":\"object\",\"c\":0.0-1.0}, ...]. " +
	"Only output the JSON — no prose, no markdown fences."

// LlmKnowledgeGraphExtractor is a model-backed IKnowledgeGraphExtractor: it asks
// an LLM for triples and parses its JSON reply.
type LlmKnowledgeGraphExtractor struct {
	ai IChatGenerator
}

// NewLlmKnowledgeGraphExtractor wraps the given chat generator. ai is required.
func NewLlmKnowledgeGraphExtractor(ai IChatGenerator) (*LlmKnowledgeGraphExtractor, error) {
	if ai == nil {
		return nil, errors.New("ai required")
	}
	return &LlmKnowledgeGraphExtractor{ai: ai}, nil
}

// ExtractFromTurn asks the LLM to extract triples from a single turn. Returns
// nil when both texts are blank (no LLM call), when the generator errors, or
// when the reply parses to no triples.
func (e *LlmKnowledgeGraphExtractor) ExtractFromTurn(ctx context.Context, userText, assistantText string, sourceEpisodeID *string) ([]KnowledgeTriple, error) {
	if isBlankLlm(userText) && isBlankLlm(assistantText) {
		return nil, nil
	}

	userMsg := "USER:\n" + userText + "\nASSISTANT:\n" + assistantText + "\n"

	reply, err := e.ai.Generate(ctx, []ChatMessage{
		{Role: "system", Content: llmExtractorSystemPrompt},
		{Role: "user", Content: userMsg},
	}, nil)
	if err != nil {
		// LLM call failed — degrade gracefully, no triples this turn.
		return nil, nil
	}

	return parseLlmTriples(reply, sourceEpisodeID), nil
}

// parseLlmTriples parses the model's reply into triples. It finds the first '['
// and last ']', JSON-unmarshals the slice, and reads s/p/o/c from each object.
// Any structural problem yields nil rather than panicking. Mirrors ParseTriples
// in the C# reference: non-object array entries are skipped, a non-numeric "c"
// falls back to the default confidence, and s/p/o are read only when they are
// JSON strings.
func parseLlmTriples(raw string, sourceEpisodeID *string) []KnowledgeTriple {
	if isBlankLlm(raw) {
		return nil
	}
	firstBracket := strings.IndexByte(raw, '[')
	lastBracket := strings.LastIndexByte(raw, ']')
	if firstBracket < 0 || lastBracket <= firstBracket {
		return nil
	}
	jsonSlice := raw[firstBracket : lastBracket+1]

	// Decode element-by-element so a single non-object entry (e.g. 1, "two",
	// null) is skipped rather than failing the whole array — matching the
	// per-entry ValueKind check in the C#/TS reference.
	var elements []json.RawMessage
	if err := json.Unmarshal([]byte(jsonSlice), &elements); err != nil {
		// Malformed JSON — return nothing.
		return nil
	}

	now := time.Now().UTC()
	hits := make([]KnowledgeTriple, 0, len(elements))
	for _, el := range elements {
		var obj map[string]json.RawMessage
		if err := json.Unmarshal(el, &obj); err != nil {
			continue // not a JSON object (number, string, null, array) — skip
		}

		s := jsonString(obj["s"])
		p := jsonString(obj["p"])
		o := jsonString(obj["o"])
		c := jsonConfidence(obj["c"])

		if isBlankLlm(s) || isBlankLlm(p) || isBlankLlm(o) {
			continue
		}
		hits = append(hits, KnowledgeTriple{
			Subject:       s,
			Predicate:     p,
			Object:        o,
			Source:        sourceEpisodeID,
			Confidence:    c,
			RecordedAtUTC: now,
		})
	}
	return hits
}

// jsonString returns the string value when raw is a JSON string, else "".
func jsonString(raw json.RawMessage) string {
	if len(raw) == 0 {
		return ""
	}
	var s string
	if err := json.Unmarshal(raw, &s); err != nil {
		return ""
	}
	return s
}

// jsonConfidence returns clamped [0,1] confidence when raw is a finite JSON
// number, else the default confidence — mirroring the C#/TS "c is a Number"
// check.
func jsonConfidence(raw json.RawMessage) float64 {
	if len(raw) == 0 {
		return defaultLlmTripleConfidence
	}
	var n float64
	if err := json.Unmarshal(raw, &n); err != nil {
		return defaultLlmTripleConfidence
	}
	if math.IsNaN(n) || math.IsInf(n, 0) {
		return defaultLlmTripleConfidence
	}
	return clampFloat(n, 0, 1)
}

// isBlankLlm reports whether s is empty or whitespace-only.
func isBlankLlm(s string) bool {
	return strings.TrimSpace(s) == ""
}

// clampFloat clamps x to [lo, hi].
func clampFloat(x, lo, hi float64) float64 {
	if x < lo {
		return lo
	}
	if x > hi {
		return hi
	}
	return x
}

// Compile-time assertion that the concrete extractor satisfies the interface.
var _ IKnowledgeGraphExtractor = (*LlmKnowledgeGraphExtractor)(nil)
