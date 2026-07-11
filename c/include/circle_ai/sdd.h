#ifndef CIRCLE_AI_SDD_H
#define CIRCLE_AI_SDD_H

/*
 * sdd.h — CircleAI.SDD (C11 port of Contracts.cs + InMemorySDD.cs +
 * NullImplementations.cs). Spec-Driven Development surface.
 *
 *   Records : Specification(SpecId, Title, Body, Schema?, Metadata?);
 *             SpecValidationResult(IsValid, Errors[]);
 *             ScaffoldedProject(ProjectId, Files{path->bytes}).
 *   Store   : ISpecificationStore -> InMemorySpecificationStore. Upsert(spec)
 *               keyed by SpecId (SpecId required); Get(specId) (specId required);
 *               List() insertion order. BackendId "in-memory".
 *   Validate: ISpecificationValidator -> JsonShapeSpecificationValidator.
 *               Validate(spec): "Title is required." when Title blank; "Body is
 *               required." when Body blank; when Schema present: must parse as a
 *               JSON object ("Schema must be a JSON object.") declaring a
 *               top-level 'type' ("Schema must declare a top-level 'type'.") else
 *               "Schema is not valid JSON: ...". IsValid == no errors. BackendId
 *               "json-shape".
 *   Scaffold: ISpecToScaffold -> HelloWorldSpecToScaffold. Scaffold(spec,
 *               targetLanguage) emits a minimal project for csharp/c#,
 *               typescript/ts, python/py; ProjectId "<name>-<lang>". Unsupported
 *               language -> failure. BackendId "hello-world".
 *   Null variants: store no-op/null/empty; validator always invalid ("No real
 *               validator wired."); scaffold empty ("00000000-...").
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Optional metadata (key/value) pair. */
typedef struct {
    char *key;   /* owned */
    char *value; /* owned */
} ca_spec_meta_t;

/* Specification(SpecId, Title, Body, Schema?, Metadata?). */
typedef struct {
    char           *spec_id;   /* owned, non-null */
    char           *title;     /* owned, non-null */
    char           *body;      /* owned, non-null */
    char           *schema;    /* owned, or NULL */
    ca_spec_meta_t *metadata;  /* owned; NULL when metadata_count == 0 */
    size_t          metadata_count;
} ca_specification_t;

void ca_specification_free(ca_specification_t *s);
void ca_specification_free_array(ca_specification_t *arr, size_t count);

/* SpecValidationResult(IsValid, Errors[]). */
typedef struct {
    bool   is_valid;
    char **errors;      /* owned; NULL when error_count == 0 */
    size_t error_count;
} ca_spec_validation_result_t;

void ca_spec_validation_result_free(ca_spec_validation_result_t *r);

/* One scaffolded file (path -> bytes). */
typedef struct {
    char    *path;  /* owned, non-null */
    uint8_t *bytes; /* owned, or NULL when len == 0 */
    size_t   len;
} ca_scaffold_file_t;

/* ScaffoldedProject(ProjectId, Files{...}). */
typedef struct {
    char               *project_id; /* owned, non-null */
    ca_scaffold_file_t *files;      /* owned; NULL when file_count == 0 */
    size_t              file_count;
} ca_scaffolded_project_t;

void ca_scaffolded_project_free(ca_scaffolded_project_t *p);

/* ── ISpecificationStore -> InMemorySpecificationStore ──────────────────── */

typedef struct ca_specification_store ca_specification_store_t;

ca_specification_store_t *ca_specification_store_create(void); /* NULL on OOM */
void ca_specification_store_destroy(ca_specification_store_t *s);
const char *ca_specification_store_backend_id(const ca_specification_store_t *s);

/* Upsert(spec) — keyed by SpecId (replace). 0 / -1 on bad args / OOM. */
int ca_specification_store_upsert(ca_specification_store_t *s,
                                  const ca_specification_t *spec);
/* Get(specId) -> fresh copy into *out, true; false on miss / bad args. */
bool ca_specification_store_get(const ca_specification_store_t *s,
                                const char *spec_id, ca_specification_t *out);
/* List() insertion order. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_specification_t *ca_specification_store_list(const ca_specification_store_t *s,
                                                size_t *out_count);

const char *ca_sdd_null_spec_store_backend_id(void); /* "null" */

/* ── ISpecificationValidator -> JsonShapeSpecificationValidator ─────────── */

/* Validate(spec) -> fresh result into *out, true; false on bad args (out
 * cleared). BackendId "json-shape". */
bool ca_spec_validate(const ca_specification_t *spec,
                      ca_spec_validation_result_t *out);
const char *ca_spec_validator_backend_id(void); /* "json-shape" */

/* Null validator: always invalid with a single "No real validator wired." */
bool ca_sdd_null_spec_validate(const ca_specification_t *spec,
                               ca_spec_validation_result_t *out);
const char *ca_sdd_null_spec_validator_backend_id(void); /* "null" */

/* ── ISpecToScaffold -> HelloWorldSpecToScaffold ────────────────────────── */

/* Scaffold(spec, targetLanguage) -> fresh project into *out, true; false on bad
 * args (spec/targetLanguage required) or unsupported language. BackendId
 * "hello-world". */
bool ca_spec_scaffold(const ca_specification_t *spec, const char *target_language,
                      ca_scaffolded_project_t *out);
const char *ca_spec_scaffold_backend_id(void); /* "hello-world" */

const char *ca_sdd_null_spec_scaffold_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SDD_H */
