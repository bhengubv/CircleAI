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

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_DOC_ANALYTICS_H */
