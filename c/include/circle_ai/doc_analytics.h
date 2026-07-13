#ifndef CIRCLE_AI_DOC_ANALYTICS_H
#define CIRCLE_AI_DOC_ANALYTICS_H

/*
 * doc_analytics.h — CircleAI.DocAnalytics (C11 port of Contracts.cs +
 * InMemoryDocumentTracker.cs + NullImplementations.cs).
 *
 *   Records : DocumentView(DocumentId, ViewerId, DateTimeOffset AtUtc,
 *                          TimeSpan Duration, int PagesViewed);
 *             DocumentInsight(DocumentId, TotalViews, UniqueViewers,
 *                             double AvgDurationSeconds).
 *   Tracker : IDocumentTracker + IDocumentInsights -> InMemoryDocumentTracker.
 *               RecordView(view) appends per DocumentId (DocumentId required),
 *               ListViews(documentId) returns them in insertion order,
 *               Compute(documentId) -> insight? (null when no views).
 *               BackendId "in-memory". Null tracker -> record no-op, list empty,
 *               compute null.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. AtUtc as
 * int64 Unix ms UTC; Duration as int64 ms. Linear arrays, no pthreads. C11+libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* DocumentView(DocumentId, ViewerId, AtUtc, Duration, PagesViewed). */
typedef struct {
    char   *document_id;  /* owned, non-null */
    char   *viewer_id;    /* owned, non-null */
    int64_t at_utc_ms;
    int64_t duration_ms;  /* TimeSpan as ms */
    int     pages_viewed;
} ca_doc_view_t;

void ca_doc_view_free(ca_doc_view_t *v);
void ca_doc_view_free_array(ca_doc_view_t *arr, size_t count);

/* DocumentInsight(DocumentId, TotalViews, UniqueViewers, AvgDurationSeconds). */
typedef struct {
    char  *document_id;          /* owned, non-null */
    int    total_views;
    int    unique_viewers;
    double avg_duration_seconds;
} ca_doc_insight_t;

void ca_doc_insight_free(ca_doc_insight_t *i);

/* (DocumentId, Views) pair — one bucket of TopDocuments(). */
typedef struct {
    char *document_id;  /* owned, non-null */
    int   views;
} ca_doc_top_t;

void ca_doc_top_free_array(ca_doc_top_t *arr, size_t count);

/* ── IDocumentTracker + IDocumentInsights -> InMemoryDocumentTracker ─────── */

typedef struct ca_doc_tracker ca_doc_tracker_t;

ca_doc_tracker_t *ca_doc_tracker_create(void); /* NULL on OOM */
void ca_doc_tracker_destroy(ca_doc_tracker_t *t);
const char *ca_doc_tracker_backend_id(const ca_doc_tracker_t *t); /* "in-memory" */

/* RecordView(view) — appends under DocumentId. 0 / -1 on bad args (null / empty
 * DocumentId) or OOM. */
int ca_doc_tracker_record_view(ca_doc_tracker_t *t, const ca_doc_view_t *view);

/* ListViews(documentId) in insertion order. NULL + 0 empty; NULL + SIZE_MAX on
 * error (documentId required). */
ca_doc_view_t *ca_doc_tracker_list_views(const ca_doc_tracker_t *t,
                                         const char *document_id,
                                         size_t *out_count);

/* Compute(documentId) -> fresh insight into *out, true; false on no-views / bad
 * args (documentId required). */
bool ca_doc_tracker_compute(const ca_doc_tracker_t *t, const char *document_id,
                            ca_doc_insight_t *out);

const char *ca_doc_null_tracker_backend_id(void); /* "null" */
const char *ca_doc_null_insights_backend_id(void); /* "null" */

/* DocumentCount — number of distinct documents with at least one recorded view.
 * NULL tracker → 0. */
size_t ca_doc_tracker_document_count(const ca_doc_tracker_t *t);

/* TotalViews — total views recorded across every tracked document. NULL
 * tracker → 0. */
size_t ca_doc_tracker_total_views(const ca_doc_tracker_t *t);

/* Clear(documentId) — drop all recorded views for a document. Returns true if
 * anything was removed. documentId required (non-null / non-whitespace): a
 * whitespace/NULL id returns false. */
bool ca_doc_tracker_clear(ca_doc_tracker_t *t, const char *document_id);

/* TopDocuments(topK) -> fresh owned array of (DocumentId, Views), highest first
 * (grouped by first-appearance order, stable on ties), capped at topK. topK must
 * be > 0 (NULL + SIZE_MAX otherwise / on bad args). NULL + 0 when empty. Free
 * with ca_doc_top_free_array. Use 5 for the C# default. */
ca_doc_top_t *ca_doc_tracker_top_documents(const ca_doc_tracker_t *t, int top_k,
                                           size_t *out_count);

/* RecentViews(documentId, limit) -> fresh owned array of the document's views,
 * newest-first by AtUtc, first `limit`. documentId required; limit must be > 0
 * (NULL + SIZE_MAX on bad args). NULL + 0 when the document is unknown. Use 20
 * for the C# default. */
ca_doc_view_t *ca_doc_tracker_recent_views(const ca_doc_tracker_t *t,
                                           const char *document_id, int limit,
                                           size_t *out_count);

/* TotalPagesViewed(documentId) — sum of PagesViewed across the document's views;
 * 0 when unknown. documentId required (whitespace/NULL → 0). */
int ca_doc_tracker_total_pages_viewed(const ca_doc_tracker_t *t,
                                      const char *document_id);

/* MostEngagedViewer(documentId) -> owned string naming the viewer with the most
 * cumulative Duration on the document (grouped by ViewerId Ordinal, ties keep
 * first-appearance order). NULL when the document has no views / bad args / OOM.
 * documentId required. Caller frees with free(). */
char *ca_doc_tracker_most_engaged_viewer(const ca_doc_tracker_t *t,
                                         const char *document_id);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_DOC_ANALYTICS_H */
