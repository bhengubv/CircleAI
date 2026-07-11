/*
 * search.c — CircleAI.Search (C11 port).
 *
 * Cosine similarity uses double accumulators (matching the reproducibility note
 * used elsewhere in the C tree). Tokenisation splits on the same delimiter set
 * as the C# code and lowercases ASCII. Scoring mirrors TermFrequency /
 * SimpleRelevance. Pure C11 + libc (+ libm).
 */

#include "circle_ai/search.h"
#include "board_common.h"
#include <math.h>

static float cosine_scalar(const float *a, const float *b, size_t len) {
    if (!a || !b || len == 0) return NAN;
    double dot = 0.0, na = 0.0, nb = 0.0;
    for (size_t i = 0; i < len; ++i) {
        dot += (double)a[i] * b[i];
        na  += (double)a[i] * a[i];
        nb  += (double)b[i] * b[i];
    }
    return (float)(dot / (sqrt(na) * sqrt(nb)));
}

float ca_search_cosine_similarity(const float *a, const float *b, size_t len) {
    return cosine_scalar(a, b, len);
}
float ca_search_simd_cosine_similarity(const float *a, const float *b, size_t len) {
    return cosine_scalar(a, b, len);
}

/* Is `c` one of the C# split delimiters? */
static bool is_delim(char c) {
    switch (c) {
        case ' ': case '\n': case '\r': case '\t':
        case ',': case '.': case ';': case ':':
        case '(': case ')': case '[': case ']':
        case '"': case '\'':
            return true;
        default:
            return false;
    }
}

char **ca_search_tokenise(const char *text, size_t *out_count) {
    if (!out_count) return NULL;
    if (!text) { *out_count = (size_t)-1; return NULL; }

    char **toks = NULL;
    size_t n = 0, cap = 0;
    const char *p = text;
    while (*p) {
        while (*p && is_delim(*p)) p++;
        if (!*p) break;
        const char *start = p;
        while (*p && !is_delim(*p)) p++;
        size_t tlen = (size_t)(p - start);
        /* C# trims the token then checks length > 0; the delimiter split already
         * excludes surrounding whitespace, so a non-empty run is a token. */
        if (tlen == 0) continue;
        if (n == cap) {
            size_t nc = cap ? cap * 2 : 8;
            char **nt = (char **)realloc(toks, nc * sizeof(char *));
            if (!nt) { cab_strv_free(toks, n); *out_count = (size_t)-1; return NULL; }
            toks = nt; cap = nc;
        }
        char *tok = (char *)malloc(tlen + 1);
        if (!tok) { cab_strv_free(toks, n); *out_count = (size_t)-1; return NULL; }
        for (size_t i = 0; i < tlen; ++i) tok[i] = (char)tolower((unsigned char)start[i]);
        tok[tlen] = '\0';
        toks[n++] = tok;
    }
    *out_count = n;
    return toks;
}

void ca_search_tokens_free(char **tokens, size_t count) {
    cab_strv_free(tokens, count);
}

double ca_search_term_frequency(const char *term, char *const *doc_tokens,
                                size_t doc_count) {
    if (doc_count == 0 || !term) return 0.0;
    size_t c = 0;
    for (size_t i = 0; i < doc_count; ++i)
        if (cab_ord_eq(doc_tokens[i], term)) c++;
    return (double)c / (double)doc_count;
}

double ca_search_simple_relevance(char *const *query_tokens, size_t query_count,
                                  char *const *doc_tokens, size_t doc_count) {
    if (query_count == 0 || doc_count == 0) return 0.0;
    double score = 0.0;
    for (size_t i = 0; i < query_count; ++i)
        score += ca_search_term_frequency(query_tokens[i], doc_tokens, doc_count);
    return score;
}
