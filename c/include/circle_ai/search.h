#ifndef CIRCLE_AI_SEARCH_H
#define CIRCLE_AI_SEARCH_H

/*
 * search.h — CircleAI.Search (C11 port of VectorSearch.cs / SimdOps.cs +
 * SearchPrimitives.cs). Shared search-relevance helpers.
 *
 *   VectorMath.CosineSimilarity(a, b)  -> ca_search_cosine_similarity
 *   SimdOps.CosineSimilarity(a, b)     -> ca_search_simd_cosine_similarity
 *       Both compute dot / (||a|| * ||b||). The C# code special-cases a
 *       hardware-SIMD path but the result is identical to the scalar fallback,
 *       so a single scalar (double-accumulated for reproducibility) impl backs
 *       both entry points. On a length mismatch or zero length they return NaN
 *       (the C# code throws ArgumentException there).
 *   SearchTokenisation.Tokenise(text) -> ca_search_tokenise
 *       Splits on whitespace + , . ; : ( ) [ ] " ' , lowercases (ASCII), drops
 *       empties. NULL text -> NULL + SIZE_MAX (C# throws).
 *   SearchScoring.TermFrequency(term, docTokens) -> ca_search_term_frequency
 *       count(term in docTokens, Ordinal) / docTokens.Count; 0 when empty.
 *   SearchScoring.SimpleRelevance(queryTokens, docTokens) -> ca_search_simple_relevance
 *       sum over query terms of TermFrequency(term, docTokens); 0 when either
 *       side is empty.
 *
 * Conventions: ca_ prefix, owned string arrays freed with ca_search_tokens_free,
 * errors via NULL / count SIZE_MAX. Pure C11 + libc (+ libm).
 */

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Cosine similarity dot/(||a||*||b||). Returns NaN on len mismatch / len == 0. */
float ca_search_cosine_similarity(const float *a, const float *b, size_t len);
/* SimdOps variant — identical result (scalar-backed). */
float ca_search_simd_cosine_similarity(const float *a, const float *b, size_t len);

/* Tokenise `text` into a fresh owned array of lowercased tokens. *out_count set.
 * NULL + 0 for a token-free string; NULL + SIZE_MAX when text is NULL. */
char **ca_search_tokenise(const char *text, size_t *out_count);
/* Free an owned token array. */
void ca_search_tokens_free(char **tokens, size_t count);

/* count(term == t, Ordinal) / doc_count. 0 when doc_count == 0. */
double ca_search_term_frequency(const char *term, char *const *doc_tokens,
                                size_t doc_count);

/* Sum over query tokens of term-frequency in the doc tokens. 0 when either is
 * empty. */
double ca_search_simple_relevance(char *const *query_tokens, size_t query_count,
                                  char *const *doc_tokens, size_t doc_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SEARCH_H */
