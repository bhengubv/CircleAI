#ifndef CIRCLE_AI_VISUALIZATION_H
#define CIRCLE_AI_VISUALIZATION_H

/*
 * visualization.h — CircleAI.Visualization (C11 port of Contracts.cs +
 * InMemoryVisualization.cs + NullImplementations.cs).
 *
 *   Records : DashboardDefinition(DashboardId, Title, JsonSpec);
 *             ApiDoc(DocId, Title, OpenApiJson);
 *             GeneratedSite(SiteId, Files{path->bytes}).
 *   Store   : IDashboardDefinitionStore -> InMemoryDashboardStore. Upsert(d)
 *               keyed by DashboardId (DashboardId required); Get(id) (id
 *               required); List() insertion order. BackendId "in-memory".
 *   ApiDoc  : IApiDocBuilder -> JsonApiDocBuilder. Build(openApiSpec) extracts
 *               info.title (default "API"), DocId = title lowercased with spaces
 *               replaced by '-', OpenApiJson = the spec verbatim. Requires a
 *               non-blank spec. BackendId "json-normaliser".
 *   Site    : ISiteBuilder -> StaticSiteBuilder. Build(siteSpec) reads a
 *               pages[] array of {path, html}; each becomes one file
 *               (path -> UTF-8 html). SiteId "site-<hex>". Requires pages[].
 *               BackendId "static".
 *   Null variants: store no-op/null/empty; apidoc -> ("00000000-...","","{}");
 *               site -> ("00000000-...", {}).
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

/* DashboardDefinition(DashboardId, Title, JsonSpec). */
typedef struct {
    char *dashboard_id; /* owned, non-null */
    char *title;        /* owned, non-null */
    char *json_spec;    /* owned, non-null */
} ca_dashboard_definition_t;

void ca_dashboard_definition_free(ca_dashboard_definition_t *d);
void ca_dashboard_definition_free_array(ca_dashboard_definition_t *arr, size_t count);

/* ApiDoc(DocId, Title, OpenApiJson). */
typedef struct {
    char *doc_id;        /* owned, non-null */
    char *title;         /* owned, non-null */
    char *open_api_json; /* owned, non-null */
} ca_api_doc_t;

void ca_api_doc_free(ca_api_doc_t *d);

/* One generated file (path -> bytes). */
typedef struct {
    char    *path;  /* owned, non-null */
    uint8_t *bytes; /* owned, or NULL when len == 0 */
    size_t   len;
} ca_site_file_t;

/* GeneratedSite(SiteId, Files{...}). */
typedef struct {
    char           *site_id; /* owned, non-null */
    ca_site_file_t *files;   /* owned; NULL when file_count == 0 */
    size_t          file_count;
} ca_generated_site_t;

void ca_generated_site_free(ca_generated_site_t *s);

/* ── IDashboardDefinitionStore -> InMemoryDashboardStore ─────────────────── */

typedef struct ca_dashboard_definition_store ca_dashboard_definition_store_t;

ca_dashboard_definition_store_t *ca_dashboard_definition_store_create(void); /* NULL OOM */
void ca_dashboard_definition_store_destroy(ca_dashboard_definition_store_t *s);
const char *ca_dashboard_definition_store_backend_id(const ca_dashboard_definition_store_t *s);

/* Upsert(d) — keyed by DashboardId (replace). 0 / -1 on bad args / OOM. */
int ca_dashboard_definition_store_upsert(ca_dashboard_definition_store_t *s,
                                         const ca_dashboard_definition_t *d);
/* Get(id) -> fresh copy into *out, true; false on miss / bad args. */
bool ca_dashboard_definition_store_get(const ca_dashboard_definition_store_t *s,
                                       const char *id, ca_dashboard_definition_t *out);
/* List() insertion order. NULL + 0 empty; NULL + SIZE_MAX error. */
ca_dashboard_definition_t *ca_dashboard_definition_store_list(
    const ca_dashboard_definition_store_t *s, size_t *out_count);

const char *ca_viz_null_dashboard_store_backend_id(void); /* "null" */

/* ── IApiDocBuilder -> JsonApiDocBuilder ────────────────────────────────── */

/* Build(openApiSpec) -> fresh ApiDoc into *out, true; false on a blank spec /
 * bad args (out cleared). BackendId "json-normaliser". */
bool ca_api_doc_build(const char *open_api_spec, ca_api_doc_t *out);
const char *ca_api_doc_builder_backend_id(void); /* "json-normaliser" */
const char *ca_viz_null_api_doc_builder_backend_id(void); /* "null" */

/* ── ISiteBuilder -> StaticSiteBuilder ──────────────────────────────────── */

/* Build(siteSpec) -> fresh GeneratedSite into *out, true; false on a blank spec
 * or a spec missing a pages[] array (out cleared). Each page needs a non-blank
 * "path" and a non-null "html". BackendId "static". */
bool ca_site_build(const char *site_spec, ca_generated_site_t *out);
const char *ca_site_builder_backend_id(void); /* "static" */
const char *ca_viz_null_site_builder_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_VISUALIZATION_H */
