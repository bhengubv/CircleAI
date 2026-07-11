// search_primitives.go
//
// Ports CircleAI.Search:
//   SearchTokenisation (SearchPrimitives.cs) -> SearchTokenise
//   SearchScoring      (SearchPrimitives.cs) -> SearchTermFrequency / SearchSimpleRelevance
//   VectorMath         (VectorSearch.cs)     -> SearchCosineSimilarity
//   SimdOps            (SimdOps.cs)          -> SimdCosineSimilarity
//
// The C# CosineSimilarity has a hardware-accelerated SIMD path and a scalar
// fallback that produce the same result. Go's plain scalar loop is the exact
// numerical equivalent of the fallback, so both VectorMath and SimdOps map to a
// single scalar implementation each (kept as two exported funcs to preserve the
// two named entry points). Both throw ArgumentException on mismatched or
// zero-length input; the Go ports return an error instead.

package circleai

import (
	"errors"
	"math"
	"strings"
)

// searchTokeniseSeparators mirrors the C# split set.
var searchTokeniseSeparators = " \n\r\t,.;:()[]\"'"

// SearchTokenise splits text into lowercased tokens. Ports
// SearchTokenisation.Tokenise. Panics on nil is not applicable in Go; an empty
// string yields an empty slice.
func SearchTokenise(text string) []string {
	out := make([]string, 0)
	for _, tok := range strings.FieldsFunc(text, func(r rune) bool {
		return strings.ContainsRune(searchTokeniseSeparators, r)
	}) {
		trimmed := strings.ToLower(strings.TrimSpace(tok))
		if trimmed != "" {
			out = append(out, trimmed)
		}
	}
	return out
}

// SearchTermFrequency returns the fraction of docTokens equal to term. Ports
// SearchScoring.TermFrequency (ordinal string equality).
func SearchTermFrequency(term string, docTokens []string) float64 {
	if len(docTokens) == 0 {
		return 0
	}
	c := 0
	for _, t := range docTokens {
		if t == term {
			c++
		}
	}
	return float64(c) / float64(len(docTokens))
}

// SearchSimpleRelevance sums the term frequency of every query token in the
// document. Ports SearchScoring.SimpleRelevance.
func SearchSimpleRelevance(queryTokens, docTokens []string) float64 {
	if len(queryTokens) == 0 || len(docTokens) == 0 {
		return 0
	}
	var score float64
	for _, q := range queryTokens {
		score += SearchTermFrequency(q, docTokens)
	}
	return score
}

// errVectorLength mirrors the ArgumentException thrown by both cosine impls.
var errVectorLength = errors.New("vectors must be the same non-zero length")

// SearchCosineSimilarity returns the cosine similarity of a and b. Ports
// VectorMath.CosineSimilarity — errors on mismatched or zero length.
func SearchCosineSimilarity(a, b []float32) (float32, error) {
	if len(a) != len(b) || len(a) == 0 {
		return 0, errVectorLength
	}
	return cosineScalarF32(a, b), nil
}

// SimdCosineSimilarity returns the cosine similarity of a and b. Ports
// SimdOps.CosineSimilarity — errors on mismatched or zero length. Numerically
// identical to SearchCosineSimilarity.
func SimdCosineSimilarity(a, b []float32) (float32, error) {
	if len(a) != len(b) || len(a) == 0 {
		return 0, errVectorLength
	}
	return cosineScalarF32(a, b), nil
}

func cosineScalarF32(a, b []float32) float32 {
	var dot, normA, normB float32
	for i := range a {
		dot += a[i] * b[i]
		normA += a[i] * a[i]
		normB += b[i] * b[i]
	}
	return dot / (float32(math.Sqrt(float64(normA))) * float32(math.Sqrt(float64(normB))))
}
