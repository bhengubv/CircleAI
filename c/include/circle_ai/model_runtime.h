#ifndef CIRCLE_AI_MODEL_RUNTIME_H
#define CIRCLE_AI_MODEL_RUNTIME_H

/*
 * model_runtime.h — Core model-management runtime (C11 port).
 *
 * Ports CircleAI.Core:
 *   - IModelSource + ModelScopeSource + HuggingFaceSource(tombstone) +
 *     SourceDownloadHelper  (network abstracted behind an in-memory source seam)
 *   - IModelDownloader + ModelDownloader (+ DownloadProgress / DownloadProgressReport)
 *   - IModelManager + LocalModelManager
 *   - IModelLoader + LocalModelLoader (registry-driven, single-file vs bundle,
 *     SHA-256 checksum verification)
 *   - SafeModelHandle / PlatformInterop (opaque native-pointer handle with an
 *     injected release callback)
 *
 * Downloads never touch the network: an IModelSource is injected, and the
 * bundled ca_inmemory_model_source / ca_modelscope_source write bytes that the
 * test has registered for a URL. This is the deterministic seam the C# stack
 * fills with ModelScopeSource over HTTP.
 *
 * File layout on disk matches the C# stack (LocalModelManager writes
 * <dir>/<sanitised-id>/pytorch_model.bin; the registry manifest lives at
 * <dir>/<modelId>/installed.json via registry.h). Pure C11 + libc; the SHA-256
 * is a self-contained implementation. No JSON lib dep.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#include "models_v15.h"   /* ca_bundle_file_t */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * SHA-256 (exposed — checksum verification + tests)
 * ===========================================================================
 *
 * Streaming SHA-256, distinct from multimodal.h's one-shot ca_sha256_hex(data,
 * len, out) — these produce a raw 32-byte digest and hash files without loading
 * them wholesale, as model weights can be large.
 */

/* Hash len bytes into out[32]. */
void ca_mr_sha256(const uint8_t *data, size_t len, uint8_t out[32]);

/* Hash the whole file at path into out[32] (streamed). Returns false if the
 * file can't be read. */
bool ca_mr_sha256_file(const char *path, uint8_t out[32]);

/* Lowercase-hex encode digest[32] into out (>= 65 bytes incl. NUL). */
void ca_mr_sha256_hex(const uint8_t digest[32], char out[65]);

/* ===========================================================================
 * DownloadProgress / DownloadProgressReport
 * =========================================================================== */

typedef struct {
    const char *file_name;    /* borrowed */
    int64_t     bytes_received;
    int64_t     total_bytes;  /* -1 = unknown */
    double      bytes_per_second;
    double      estimated_seconds_remaining;
} ca_download_progress_report_t;

/* Progress callback seam. user is passed through untouched. */
typedef void (*ca_download_progress_fn)(void *user, const ca_download_progress_report_t *p);

/* ===========================================================================
 * IModelSource — deterministic in-memory seam
 * =========================================================================== */

typedef struct ca_model_source ca_model_source_t;

/* Source name (e.g. "ModelScope"). Borrowed. */
const char *ca_model_source_name(const ca_model_source_t *s);
/* Quick reachability check — never throws; returns false on any failure. */
bool ca_model_source_is_available(ca_model_source_t *s);
/* Download url → local_path, reporting progress. Returns false on failure
 * (unknown URL, host-rule violation, IO error). */
bool ca_model_source_download(ca_model_source_t *s, const char *url,
                              const char *local_path,
                              ca_download_progress_fn progress, void *progress_user);
void ca_model_source_destroy(ca_model_source_t *s);

/* In-memory source: register url → bytes, then download() writes them to disk.
 * available defaults to true. */
ca_model_source_t *ca_inmemory_model_source_create(const char *name);
/* Register (or replace) the payload served for url. Deep-copies the bytes.
 * Returns false on OOM. */
bool ca_inmemory_model_source_add(ca_model_source_t *s, const char *url,
                                  const uint8_t *data, size_t len);
/* Toggle the is_available result (for fallback-chain tests). */
void ca_inmemory_model_source_set_available(ca_model_source_t *s, bool available);

/* ModelScopeSource — an in-memory source named "ModelScope" that enforces the
 * modelscope.cn host rule on download() (mirrors the C# host guard). Register
 * payloads with ca_inmemory_model_source_add. */
ca_model_source_t *ca_modelscope_source_create(void);

/* HuggingFaceSource tombstone — always returns NULL (the type was removed in C#
 * as a compile-time error; here it is a runtime no-op that never constructs). */
ca_model_source_t *ca_huggingface_source_create(void);

/* ===========================================================================
 * IModelDownloader + ModelDownloader
 * =========================================================================== */

typedef struct ca_model_downloader ca_model_downloader_t;

/* Forward decl — the downloader's embedded registry uses the loader row shape
 * (which carries file_name + primary/fallback URLs, matching C#'s ModelEntry). */
struct ca_model_info; /* see ca_model_info_t below */

/*
 * Create a downloader over a list of sources (walked in order, falling through
 * on failure). The downloader borrows the source pointers; if owns_sources is
 * true it destroys them on ca_model_downloader_destroy. registry/registry_count
 * is the embedded model registry (ca_model_info_t rows) used by
 * ca_model_downloader_download_model — pass NULL/0 when only the candidate-URL
 * path is used. Returns NULL when sources is NULL or source_count == 0.
 */
ca_model_downloader_t *ca_model_downloader_create(
    ca_model_source_t **sources, size_t source_count, bool owns_sources,
    const struct ca_model_info *registry, size_t registry_count);

void ca_model_downloader_destroy(ca_model_downloader_t *d);

/* Progress event (mirrors ModelDownloader.ProgressChanged). Optional. */
void ca_model_downloader_set_progress(ca_model_downloader_t *d,
                                      ca_download_progress_fn fn, void *user);

/*
 * Resolve modelId in the registry and download its single file into local_path/
 * (a directory, created if needed). Bundle entries are rejected (returns false).
 * Returns false when the id is unknown, the entry is a bundle, no URL is
 * configured, or all sources fail. On success the file lands at
 * <local_path>/<FileName>.
 */
bool ca_model_downloader_download_model(ca_model_downloader_t *d,
                                        const char *model_id, const char *local_path);

/*
 * Download a single file by trying each candidate URL in order (first = primary,
 * rest = fallbacks). Writes to local_file_path. On success, out_winner (when
 * non-NULL) is set to a freshly-allocated copy of the winning source name (caller
 * frees). Returns false when no candidate succeeds.
 */
bool ca_model_downloader_download_from_candidates(
    ca_model_downloader_t *d, const char **candidate_urls, size_t candidate_count,
    const char *local_file_path, char **out_winner,
    ca_download_progress_fn progress, void *progress_user);

/* ===========================================================================
 * IModelManager + LocalModelManager
 * =========================================================================== */

typedef struct ca_local_model_manager ca_local_model_manager_t;

/*
 * Create a manager rooted at models_directory (created if missing). downloader
 * is borrowed (may be NULL — then GetModelPath fails if the model is absent).
 * Returns NULL on OOM.
 */
ca_local_model_manager_t *ca_local_model_manager_create(
    const char *models_directory, ca_model_downloader_t *downloader /* borrowed */);

void ca_local_model_manager_destroy(ca_local_model_manager_t *m);

/*
 * Resolve modelId to a local directory, downloading it (via the injected
 * downloader) when <dir>/<sanitised-id>/pytorch_model.bin is absent. When
 * expected_checksum is non-NULL (length 32), the resolved pytorch_model.bin is
 * SHA-256-verified and a mismatch fails the call. On success, *out_path is a
 * freshly-allocated model directory path (caller frees). Returns false on
 * failure. modelId '/' and '\\' are sanitised to '_'.
 */
bool ca_local_model_manager_get_model_path(
    ca_local_model_manager_t *m, const char *model_id,
    const uint8_t *expected_checksum /* may be NULL */, char **out_path);

/* Standalone checksum verification: SHA-256 of model_path equals
 * expected_checksum[32]. Returns false on a read error or mismatch. */
bool ca_local_model_manager_verify(const char *model_path,
                                   const uint8_t *expected_checksum);

/* ===========================================================================
 * SafeModelHandle
 * ===========================================================================
 *
 * Opaque native-pointer wrapper with an injected release callback, invoked once
 * on destroy (ReleaseHandle) if the pointer is non-NULL. Mirrors the C# SafeHandle
 * ownsHandle==true semantics.
 */

typedef struct ca_safe_model_handle ca_safe_model_handle_t;
typedef void (*ca_release_callback_fn)(void *native_handle);

/* Wrap native_handle with release_callback. Returns NULL when release_callback
 * is NULL (mirrors the ArgumentNullException). native_handle may be NULL (an
 * invalid handle). */
ca_safe_model_handle_t *ca_safe_model_handle_create(void *native_handle,
                                                    ca_release_callback_fn release_callback);
/* True when the wrapped pointer is NULL. */
bool  ca_safe_model_handle_is_invalid(const ca_safe_model_handle_t *h);
/* The wrapped native pointer (borrowed). */
void *ca_safe_model_handle_get(const ca_safe_model_handle_t *h);
/* Release the handle (idempotent — the callback fires at most once), then free
 * the wrapper. */
void  ca_safe_model_handle_destroy(ca_safe_model_handle_t *h);

/* ===========================================================================
 * IModelLoader + LocalModelLoader
 * ===========================================================================
 *
 * Registry-driven. Each ca_model_info_t is either a legacy single-file entry
 * (file_name/primary_url/fallback_url/checksum) OR a bundle (repo + bundle_files);
 * is_bundle selects which. The loader borrows the registry array (kept alive by
 * the caller). Downloads go through an injected source.
 */

typedef struct ca_model_info {
    const char       *name;         /* registry key (model name) */
    /* legacy single-file shape (any may be NULL for a bundle) */
    const char       *file_name;
    const char       *primary_url;
    const char       *fallback_url;
    const char       *checksum;     /* "sha256:<hex>" or bare hex; may be NULL */
    int64_t           size_bytes;
    const char       *version;
    const char       *architecture;
    const char       *quantization_type;
    /* bundle shape */
    const char       *repo;
    int64_t           total_bytes;
    ca_bundle_file_t *bundle_files; /* NULL when bundle_count == 0 */
    size_t            bundle_count;
} ca_model_info_t;

/* True when the entry carries a non-empty bundle_files array. */
bool ca_model_info_is_bundle(const ca_model_info_t *info);

typedef struct ca_local_model_loader ca_local_model_loader_t;

/*
 * Create a loader over model_dir (created if missing) and a borrowed registry.
 * source is the injected download source (borrowed; may be NULL, then
 * DownloadModel fails). Returns NULL on OOM.
 */
ca_local_model_loader_t *ca_local_model_loader_create(
    const char *model_dir,
    const ca_model_info_t *registry, size_t registry_count,
    ca_model_source_t *source /* borrowed */);

void ca_local_model_loader_destroy(ca_local_model_loader_t *l);

/*
 * Download model_name: if the target already exists and verifies, returns its
 * path; otherwise fetches from primary then fallback URL (via the injected
 * source) and verifies the checksum. On success *out_path is a freshly-allocated
 * local file path (caller frees). Returns false when the model is unsupported,
 * is a bundle (routed elsewhere in C#), or every source fails. A "sha256:TBD"
 * (or NULL) checksum skips verification, matching C#.
 */
bool ca_local_model_loader_download_model(ca_local_model_loader_t *l,
                                          const char *model_name, char **out_path);

/*
 * The expected local path for model_name. For a bundle this is
 * <dir>/<name>/llm.mnn.weight; for a single file <dir>/<file_name>. On success
 * *out_path is freshly-allocated (caller frees). Returns false when the model is
 * unknown.
 */
bool ca_local_model_loader_get_model_path(const ca_local_model_loader_t *l,
                                          const char *model_name, char **out_path);

/*
 * True when the model exists on disk AND verifies its checksum (the bundle
 * anchor's SHA for bundles, the entry checksum for single files). Never throws —
 * any error path returns false.
 */
bool ca_local_model_loader_model_exists(const ca_local_model_loader_t *l,
                                        const char *model_name);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_MODEL_RUNTIME_H */
