#ifndef CIRCLE_AI_CIRCLE_ENGINE_H
#define CIRCLE_AI_CIRCLE_ENGINE_H

/*
 * circle_engine.h — top-level facade + module contracts (C11 port).
 *
 * Ports CircleAI.Core:
 *   - IModelLoader          (vtable seam)
 *   - ICircleModule         (vtable seam)
 *   - IEmbeddingService     (vtable seam; extends ICircleModule)
 *   - CircleEngine          (holds the loader, an embedding-service slot, and a
 *                            type-keyed module bag — here keyed by string type
 *                            name, since C has no generics)
 *
 * CircleEngine deliberately knows nothing about downstream assemblies: modules
 * attach via ca_circle_engine_register_module(engine, "TypeKey", instance) and
 * are pulled back with ca_circle_engine_get_module(engine, "TypeKey").
 *
 * In-memory only. Pure C11 + libc.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * IModelLoader — vtable seam
 * ===========================================================================
 *
 * download_model: resolve/fetch model_name; on success set *out_path (caller
 *   frees) and return true.
 * get_model_path: expected local path for model_name; *out_path caller-frees.
 * model_exists:   true when present + verified on disk.
 * check_for_critical_update: true when a "[CRITICAL]" update is advertised.
 * dispose:        release resources.
 * user is the implementation's own state, passed to every call.
 */
typedef struct {
    bool (*download_model)(void *user, const char *model_name, char **out_path);
    bool (*get_model_path)(void *user, const char *model_name, char **out_path);
    bool (*model_exists)(void *user, const char *model_name);
    bool (*check_for_critical_update)(void *user);
    void (*dispose)(void *user);
    void *user;
} ca_model_loader_t;

/* ===========================================================================
 * ICircleModule — vtable seam
 * ===========================================================================
 *
 * module_name:     canonical name.
 * init:            wire the module into the engine (returns true on success).
 * is_model_loaded: readiness flag.
 * dispose:         release resources.
 */
struct ca_circle_engine; /* fwd */

typedef struct {
    const char *(*module_name)(void *user);
    bool        (*init)(void *user, struct ca_circle_engine *engine);
    bool        (*is_model_loaded)(void *user);
    void        (*dispose)(void *user);
    void       *user;
} ca_circle_module_t;

/* ===========================================================================
 * IEmbeddingService — vtable seam (extends ICircleModule)
 * ===========================================================================
 *
 * generate_embedding: return a freshly-malloc'd float array (caller frees) of
 *   length *out_len, or NULL on failure.
 * embedding_size: the vector dimension.
 * The module_base fields mirror ICircleModule (IEmbeddingService : ICircleModule).
 */
typedef struct {
    ca_circle_module_t module_base;
    float *(*generate_embedding)(void *user, const char *text, size_t *out_len);
    int    (*embedding_size)(void *user);
} ca_embedding_service_t;

/* ===========================================================================
 * CircleEngine
 * =========================================================================== */

typedef struct ca_circle_engine ca_circle_engine_t;

/* Construct with a model loader (borrowed; must be non-NULL — mirrors the C#
 * ArgumentNullException). Returns NULL when model_loader is NULL or on OOM. */
ca_circle_engine_t *ca_circle_engine_create(ca_model_loader_t *model_loader);

/* Destroy the engine. Does NOT dispose the borrowed loader or registered
 * modules — the caller owns their lifetimes (matches C#, where CircleEngine is
 * not IDisposable and only holds references). */
void ca_circle_engine_destroy(ca_circle_engine_t *engine);

/* The model loader (borrowed). */
ca_model_loader_t *ca_circle_engine_model_loader(const ca_circle_engine_t *engine);

/* Optional embedding-service slot (C#'s object? EmbeddingService). Borrowed. */
void  ca_circle_engine_set_embedding_service(ca_circle_engine_t *engine, void *service);
void *ca_circle_engine_get_embedding_service(const ca_circle_engine_t *engine);

/* Register a module instance under a string type key (replaces any existing
 * entry for that key). module must be non-NULL. Returns engine for chaining, or
 * NULL on a NULL arg / OOM. */
ca_circle_engine_t *ca_circle_engine_register_module(ca_circle_engine_t *engine,
                                                     const char *type_key, void *module);

/* Retrieve a previously-registered module, or NULL if none was registered for
 * that key. */
void *ca_circle_engine_get_module(const ca_circle_engine_t *engine, const char *type_key);

/* True when a module is registered under type_key. */
bool ca_circle_engine_has_module(const ca_circle_engine_t *engine, const char *type_key);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_CIRCLE_ENGINE_H */
