#ifndef CIRCLE_AI_MULTIMODAL_H
#define CIRCLE_AI_MULTIMODAL_H

/*
 * multimodal.h — compressed semantic memory for media artefacts (C11 port).
 *
 * Ported from CircleAI.Memory.Multimodal (C#) and mirroring the verified
 * TypeScript reference (memory/multimodal.ts) 1:1:
 *   - MediaModality
 *   - MultimodalMemoryEntry
 *   - IMultimodalCaptioner + CaptionResult + HeuristicMultimodalCaptioner
 *   - IMultimodalMemoryStore + InMemoryMultimodalMemoryStore (cosine search)
 *   - MultimodalMemoryIngester (dedup + caption + persist)
 *
 * The whole point: the raw pixels / samples / frames are NEVER stored — only the
 * caption, the embedding, and a SHA-256 of the original bytes. Raw bytes arrive
 * as (const uint8_t*, len) and never leave the captioner. A small self-contained
 * FIPS 180-4 SHA-256 is provided (ca_sha256_hex).
 *
 * In-memory only: dynamic arrays + linear search, keyed by SHA-256
 * (case-insensitive, matching the C# OrdinalIgnoreCase dictionary). Every owning
 * struct holds strdup'd copies with a matching *_free / *_destroy (NULL-safe).
 *
 * Pure C11 + libc. Links against -lm (cosine).
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * SHA-256 (FIPS 180-4) — small self-contained implementation
 * =========================================================================== */

/* Write the lowercase hex SHA-256 of the len input bytes into out_hex (needs
 * >= 65 bytes: 64 hex chars + NUL). NULL data with len 0 hashes the empty
 * string. Returns out_hex. */
char *ca_sha256_hex(const uint8_t *data, size_t len, char out_hex[65]);

/* ===========================================================================
 * MediaModality
 * =========================================================================== */

typedef enum {
    CA_MEDIA_IMAGE         = 0,
    CA_MEDIA_AUDIO         = 1,
    CA_MEDIA_VIDEO         = 2,
    CA_MEDIA_TEXT_DOCUMENT = 3
} ca_media_modality_t;

/* ===========================================================================
 * MultimodalMemoryEntry
 * =========================================================================== */

/* Optional integer fields use a has_* flag (C# int?/long?). */
typedef struct {
    char               *id;               /* owned */
    int64_t             recorded_at_ms;   /* Unix ms UTC */
    ca_media_modality_t modality;
    char               *caption;          /* owned */
    float              *embedding;        /* owned, or NULL */
    size_t              embedding_len;
    char               *source_sha256;    /* owned; hex-lower */
    char               *source_mime_type; /* owned, or NULL */
    int64_t             source_byte_count;
    char               *source_uri;       /* owned, or NULL */
    bool                has_width;  int32_t width_px;
    bool                has_height; int32_t height_px;
    bool                has_duration; int64_t duration_ms;
    int                 reference_count;  /* mutable; default 1 */
    char              **tag_keys;         /* owned array, or NULL */
    char              **tag_values;       /* owned array, or NULL */
    size_t              tag_count;
} ca_multimodal_entry_t;

void ca_multimodal_entry_free(ca_multimodal_entry_t *e);
void ca_multimodal_entry_free_array(ca_multimodal_entry_t *arr, size_t count);

/* Borrowed tag lookup by key (case-sensitive). NULL when absent. */
const char *ca_multimodal_entry_get_tag(const ca_multimodal_entry_t *e, const char *key);

/* ===========================================================================
 * Captioner — CaptionResult + IMultimodalCaptioner seam
 * =========================================================================== */

/* Output of a single captioning call. caption is owned by the result and freed
 * with ca_caption_result_free. embedding owned (or NULL). */
typedef struct {
    char   *caption;          /* owned; must not be empty */
    float  *embedding;        /* owned, or NULL */
    size_t  embedding_len;
    bool    has_width;  int32_t width_px;
    bool    has_height; int32_t height_px;
    bool    has_duration; int64_t duration_ms;
} ca_caption_result_t;

void ca_caption_result_free(ca_caption_result_t *r);

/* A captioner is a pair of function pointers + an opaque user context.
 *  - can_caption: true when this captioner handles (modality, mime) — mime may
 *    be NULL.
 *  - caption: fill *out (owned by caller — free with ca_caption_result_free).
 *    Must not retain the bytes. Returns true on success. */
typedef struct {
    void *user;
    bool (*can_caption)(void *user, ca_media_modality_t modality, const char *mime_type);
    bool (*caption)(void *user, ca_media_modality_t modality,
                    const uint8_t *bytes, size_t len, const char *mime_type,
                    ca_caption_result_t *out);
} ca_captioner_t;

/* The heuristic captioner: canCaption==true always; emits a descriptive shell
 * caption ("[Image — no captioner wired. <mime>, <n> bytes.]") with no
 * embedding. Returns a captioner whose user is NULL (stateless). */
ca_captioner_t ca_heuristic_captioner(void);

/* MIME sniffer used by the heuristic captioner (exposed for tests). Returns a
 * borrowed static string ("image/jpeg", "image/png", "image/gif", "audio/wav",
 * "application/pdf", or "application/octet-stream"); the declared mime wins when
 * non-blank. */
const char *ca_detect_mime(const uint8_t *bytes, size_t len, const char *declared);

/* ===========================================================================
 * InMemoryMultimodalMemoryStore
 * =========================================================================== */

typedef struct ca_multimodal_store ca_multimodal_store_t;

ca_multimodal_store_t *ca_multimodal_store_create(void);
void                   ca_multimodal_store_destroy(ca_multimodal_store_t *store);

/* Add a deep copy of entry (upsert by SHA-256, case-insensitive). Returns false
 * on a NULL store/entry or a blank source_sha256. */
bool ca_multimodal_store_add(ca_multimodal_store_t *store, const ca_multimodal_entry_t *entry);

/* Fetch by hash (case-insensitive) into *out (deep copy). Returns true if found;
 * the caller frees *out with ca_multimodal_entry_free. */
bool ca_multimodal_store_get_by_hash(const ca_multimodal_store_t *store,
                                     const char *source_sha256,
                                     ca_multimodal_entry_t *out);

/* Increment reference_count for the entry whose hash matches (no-op if unknown). */
void ca_multimodal_store_reinforce(ca_multimodal_store_t *store, const char *source_sha256);

/* Cosine top-top_k search; recency fallback (newest-first) when query NULL.
 * Only entries with a matching-dimension embedding participate in cosine
 * ranking. top_k <= 0 → default 5. Returns a fresh deep-copied array (caller
 * frees with ca_multimodal_entry_free_array); *out_count set (0 → NULL). */
ca_multimodal_entry_t *ca_multimodal_store_search(const ca_multimodal_store_t *store,
                                                  const float *query, size_t query_len,
                                                  int top_k, size_t *out_count);

/* Most-recent count entries, newest-first. count <= 0 → default 10. */
ca_multimodal_entry_t *ca_multimodal_store_get_recent(const ca_multimodal_store_t *store,
                                                      int count, size_t *out_count);

/* Remove entries recorded strictly before cutoff_ms; return the number removed. */
size_t ca_multimodal_store_prune_older_than(ca_multimodal_store_t *store, int64_t cutoff_ms);

size_t ca_multimodal_store_count(const ca_multimodal_store_t *store);

/* ===========================================================================
 * MultimodalMemoryIngester
 * =========================================================================== */

typedef struct ca_multimodal_ingester ca_multimodal_ingester_t;

/* Per-call ingest options. Any field may be NULL. */
typedef struct {
    const char        *mime_type;
    const char        *source_uri;
    const char *const *tag_keys;
    const char *const *tag_values;
    size_t             tag_count;
} ca_ingest_options_t;

/* Outcome of an ingest. entry is a deep copy owned by the caller (free with
 * ca_multimodal_entry_free); was_deduplicated true when the SHA-256 already
 * existed. */
typedef struct {
    ca_multimodal_entry_t entry;
    bool                  was_deduplicated;
} ca_ingestion_result_t;

void ca_ingestion_result_free(ca_ingestion_result_t *r);

/* Create an ingester over an ordered list of captioners (tried in order; the
 * first whose can_caption returns true wins, else the last is used) and a store
 * (both borrowed — kept alive by the caller). Copies the captioner list.
 * Returns NULL on a NULL/empty captioner list or NULL store. */
ca_multimodal_ingester_t *ca_multimodal_ingester_create(
    const ca_captioner_t *captioners, size_t captioner_count,
    ca_multimodal_store_t *store /* borrowed */);
void ca_multimodal_ingester_destroy(ca_multimodal_ingester_t *ing);

/*
 * Ingest raw media bytes. Hashes the source, dedupes (reinforcing + returning
 * the existing entry when the hash is known), else captions + persists. Fills
 * *out (caller frees with ca_ingestion_result_free). opts may be NULL. Returns
 * false on empty bytes or an internal failure.
 */
bool ca_multimodal_ingester_ingest(ca_multimodal_ingester_t *ing,
                                   ca_media_modality_t modality,
                                   const uint8_t *bytes, size_t len,
                                   const ca_ingest_options_t *opts,
                                   ca_ingestion_result_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_MULTIMODAL_H */
