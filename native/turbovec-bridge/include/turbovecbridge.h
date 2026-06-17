/*
 * turbovecbridge.h — Authoritative ABI for the C bridge over turbovec.
 *
 * Managed P/Invoke bindings (CircleAI.Embeddings.Local/TurboVecInterop.cs)
 * derive their signatures from THIS file. Update in lock-step.
 *
 * Conventions:
 *   - All functions are extern "C" (Rust cdylib).
 *   - i32 / i64 / f32 are 32 / 64 / 32 bit respectively.
 *   - All pointer-returning constructors return NULL on failure;
 *     status-code-returning ops return one of TVB_*.
 *   - Strings are null-terminated UTF-8.
 *   - Handles must be freed exactly once via tvb_index_free.
 */
#ifndef TURBOVECBRIDGE_H
#define TURBOVECBRIDGE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Status codes. */
#define TVB_OK                0
#define TVB_ERR_NULL_HANDLE  (-1)
#define TVB_ERR_INVALID_ARG  (-2)
#define TVB_ERR_PANIC        (-3)
#define TVB_ERR_CONSTRUCT    (-4)
#define TVB_ERR_ADD          (-5)
#define TVB_ERR_IO           (-6)
#define TVB_ERR_INVALID_UTF8 (-7)

/* Opaque handle. */
typedef struct TvbIndex TvbIndex;

/* Lifecycle. */
TvbIndex* tvb_index_new(int32_t dim, int32_t bit_width);
void      tvb_index_free(TvbIndex* handle);

/* Accessors. */
int64_t tvb_index_len(const TvbIndex* handle);
int32_t tvb_index_dim(const TvbIndex* handle);
int32_t tvb_index_bit_width(const TvbIndex* handle);

/* Mutation + search. */
int32_t tvb_index_add(
    TvbIndex*    handle,
    const float* vectors,
    int32_t      count);

int32_t tvb_index_search(
    const TvbIndex* handle,
    const float*    query,
    int32_t         k,
    int64_t*        out_indices,
    float*          out_scores);

/* Persistence. */
int32_t   tvb_index_save(const TvbIndex* handle, const char* path);
TvbIndex* tvb_index_load(const char* path);

/* Version. */
int32_t tvb_abi_version(void);

#ifdef __cplusplus
}
#endif

#endif /* TURBOVECBRIDGE_H */
