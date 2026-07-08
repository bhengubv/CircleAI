#ifndef CIRCLE_AI_CAPABILITY_REGISTRY_H
#define CIRCLE_AI_CAPABILITY_REGISTRY_H

/*
 * capability_registry.h — CircleAI ExternalCapabilityRegistry (C11 port).
 *
 * The static registry of every external capability CircleAI has earmarked to
 * absorb, ported 1:1 from CapabilityRegistry.cs. Each CapabilityEntry names the
 * capability slug, its upstream repo (or NULL), a license classification, an
 * absorption strategy ("vendor"/"pattern-port"/"wrap"), the target CircleAI.*
 * package, and the concrete value bullets.
 *
 * The registry is immutable static data: the entries + strings live for the
 * program lifetime and are BORROWED (never freed). The lookup helpers return
 * borrowed pointers into that table. This mirrors the C# static readonly array.
 *
 * Pure C11 + libc.
 */

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* One absorption-target capability. All pointers are borrowed (static). */
typedef struct {
    const char        *id;             /* short slug */
    const char        *repo;           /* upstream GitHub path, or NULL */
    const char        *license;        /* license classification */
    const char        *strategy;       /* "vendor" / "pattern-port" / "wrap" */
    const char        *target_package; /* CircleAI.* package */
    const char *const *value_bullets;  /* borrowed array of borrowed strings */
    size_t             value_count;
} ca_capability_entry_t;

/* Borrowed pointer to the full static registry; *out_count set to its length. */
const ca_capability_entry_t *ca_capability_registry_all(size_t *out_count);

/* The number of registered capabilities. */
size_t ca_capability_registry_count(void);

/* Look up by id (case-insensitive, OrdinalIgnoreCase). Returns a borrowed
 * pointer into the registry, or NULL when absent. */
const ca_capability_entry_t *ca_capability_registry_find(const char *id);

/* List entries whose target package equals target_package (case-insensitive).
 * Returns a freshly allocated array of BORROWED entry pointers (the pointed-to
 * entries stay owned by the registry); the caller frees the returned array with
 * free(). *out_count set. Returns NULL when there are no matches (count 0), or
 * NULL with *out_count == SIZE_MAX on an allocation failure / NULL argument. */
const ca_capability_entry_t **ca_capability_registry_by_package(
    const char *target_package, size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_CAPABILITY_REGISTRY_H */
