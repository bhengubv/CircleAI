#ifndef CIRCLE_AI_RESEARCH_H
#define CIRCLE_AI_RESEARCH_H

/*
 * research.h — CircleAI.Research (C11 port of Contracts.cs +
 * InMemoryResearch.cs + NullImplementations.cs).
 *
 *   Records : ResearchPaper(PaperId, Title, Authors[], Abstract,
 *                           DateTimeOffset PublishedAtUtc, Doi?);
 *             Citation(FromPaperId, ToPaperId, Context).
 *   Corpus  : IResearchCorpus -> InMemoryResearchCorpus. Add(paper) keyed by
 *               PaperId; Get(paperId) -> paper? (paperId required);
 *               Search(query, topK=10) scores Title(+3)/Abstract(+1)/Authors(+1)
 *               (OrdinalIgnoreCase Contains), keeps score>0, orders by score desc
 *               (stable), takes topK (query non-null, topK>0). BackendId
 *               "in-memory".
 *   Retrieval: IPaperRetrieval -> InMemoryPaperRetrieval. Add(paperId, bytes);
 *               FetchFullText(paperId) -> bytes? (paperId required). BackendId
 *               "in-memory".
 *   Citations: ICitationGraph -> InMemoryCitationGraph. Link(c) appends to both
 *               forward[From] and backward[To]; ForwardCitations(paperId) /
 *               BackwardCitations(paperId) in insertion order (paperId required).
 *               BackendId "in-memory".
 *   Null variants return null / empty.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Time as
 * int64 Unix ms UTC. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ResearchPaper(PaperId, Title, Authors[], Abstract, PublishedAtUtc, Doi?). */
typedef struct {
    char   *paper_id;          /* owned, non-null */
    char   *title;             /* owned, non-null */
    char  **authors;           /* owned; NULL when author_count == 0 */
    size_t  author_count;
    char   *abstract_text;     /* owned, non-null */
    int64_t published_at_utc_ms;
    char   *doi;               /* owned, or NULL */
} ca_research_paper_t;

void ca_research_paper_free(ca_research_paper_t *p);
void ca_research_paper_free_array(ca_research_paper_t *arr, size_t count);

/* Citation(FromPaperId, ToPaperId, Context). */
typedef struct {
    char *from_paper_id; /* owned, non-null */
    char *to_paper_id;   /* owned, non-null */
    char *context;       /* owned, non-null */
} ca_citation_t;

void ca_citation_free(ca_citation_t *c);
void ca_citation_free_array(ca_citation_t *arr, size_t count);

/* ── IResearchCorpus -> InMemoryResearchCorpus ──────────────────────────── */

typedef struct ca_research_corpus ca_research_corpus_t;

ca_research_corpus_t *ca_research_corpus_create(void); /* NULL on OOM */
void ca_research_corpus_destroy(ca_research_corpus_t *c);
const char *ca_research_corpus_backend_id(const ca_research_corpus_t *c);

/* Add(paper) — keyed by PaperId (replace). 0 / -1 on bad args / OOM. */
int ca_research_corpus_add(ca_research_corpus_t *c, const ca_research_paper_t *paper);
/* Get(paperId) -> fresh copy into *out, true; false on miss / bad args. */
bool ca_research_corpus_get(const ca_research_corpus_t *c, const char *paper_id,
                            ca_research_paper_t *out);
/* Search(query, topK) -> fresh owned array ordered by score desc, top_k.
 * NULL + 0 empty; NULL + SIZE_MAX on error (query required, top_k > 0). */
ca_research_paper_t *ca_research_corpus_search(const ca_research_corpus_t *c,
                                               const char *query, int top_k,
                                               size_t *out_count);

const char *ca_research_null_corpus_backend_id(void); /* "null" */

/* ── IPaperRetrieval -> InMemoryPaperRetrieval ──────────────────────────── */

typedef struct ca_paper_retrieval ca_paper_retrieval_t;

ca_paper_retrieval_t *ca_paper_retrieval_create(void); /* NULL on OOM */
void ca_paper_retrieval_destroy(ca_paper_retrieval_t *r);
const char *ca_paper_retrieval_backend_id(const ca_paper_retrieval_t *r);

/* Add(paperId, bytes, len) — keyed by PaperId (replace). 0 / -1. len may be 0. */
int ca_paper_retrieval_add(ca_paper_retrieval_t *r, const char *paper_id,
                           const uint8_t *bytes, size_t len);
/* FetchFullText(paperId) -> fresh owned buffer (*out_len set), or NULL when
 * absent (with *out_len 0). *out_len SIZE_MAX on bad args (paperId required). */
uint8_t *ca_paper_retrieval_fetch(const ca_paper_retrieval_t *r,
                                  const char *paper_id, size_t *out_len);

const char *ca_research_null_retrieval_backend_id(void); /* "null" */

/* ── ICitationGraph -> InMemoryCitationGraph ────────────────────────────── */

typedef struct ca_citation_graph ca_citation_graph_t;

ca_citation_graph_t *ca_citation_graph_create(void); /* NULL on OOM */
void ca_citation_graph_destroy(ca_citation_graph_t *g);
const char *ca_citation_graph_backend_id(const ca_citation_graph_t *g);

/* Link(c) — records forward[From] + backward[To]. 0 / -1 on bad args / OOM. */
int ca_citation_graph_link(ca_citation_graph_t *g, const ca_citation_t *c);
/* ForwardCitations(paperId) — where FromPaperId == paperId, insertion order.
 * NULL + 0 empty; NULL + SIZE_MAX on error (paperId required). */
ca_citation_t *ca_citation_graph_forward(const ca_citation_graph_t *g,
                                         const char *paper_id, size_t *out_count);
/* BackwardCitations(paperId) — where ToPaperId == paperId, insertion order. */
ca_citation_t *ca_citation_graph_backward(const ca_citation_graph_t *g,
                                          const char *paper_id, size_t *out_count);

const char *ca_research_null_citation_graph_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_RESEARCH_H */
