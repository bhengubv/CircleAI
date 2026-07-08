#ifndef CIRCLE_AI_HOST_TOOLS_UI_H
#define CIRCLE_AI_HOST_TOOLS_UI_H

/*
 * host_tools_ui.h — CircleAI.Hosting.Tools + CircleAI.Hosting.GenerativeUI
 * (C11 port).
 *
 * Ports (from src/CircleAI.Hosting/Tools + /GenerativeUI):
 *   ToolDescriptor / ToolExecutionResult
 *   IToolCatalog + InMemoryToolCatalog (keyword-substring search; scores
 *                                       name+5 / desc+2 / tags+3)
 *   IToolProvider (Discover/IsAvailable seam) + ImportFrom
 *   IToolExecutor (Execute seam)
 *   UiComponent / UiCatalogEntry / UiCatalogs.Default / RecordingRenderer
 *   IGenerativeUIRenderer (Render seam)
 *   JsonRenderParser.Parse + DescribeCatalogForPrompt
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup owning fields with
 * matching *_free, returned arrays are deep copies the caller frees.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * ToolDescriptor
 * =========================================================================== */

typedef struct {
    char  *name;           /* owned */
    char  *description;    /* owned */
    char  *provider;       /* owned */
    char  *json_schema;    /* owned; default "" */
    char  *auth_scheme;    /* owned; default "none" */
    char **tags;           /* owned array of owned strings; may be NULL */
    size_t tag_count;
    char **examples;       /* owned array; may be NULL */
    size_t example_count;
} ca_tool_descriptor_t;

void ca_tool_descriptor_free(ca_tool_descriptor_t *d);
void ca_tool_descriptor_free_array(ca_tool_descriptor_t *arr, size_t count);
ca_tool_descriptor_t *ca_tool_descriptor_copy(ca_tool_descriptor_t *dst,
                                              const ca_tool_descriptor_t *src);

typedef struct {
    bool  success;
    char *result;      /* owned (JSON/text), or NULL */
    char *error;       /* owned, or NULL */
    long  duration_ms;
} ca_tool_execution_result_t;

void ca_tool_execution_result_free(ca_tool_execution_result_t *r);

/* ===========================================================================
 * InMemoryToolCatalog
 * =========================================================================== */

typedef struct ca_tool_catalog ca_tool_catalog_t;

ca_tool_catalog_t *ca_tool_catalog_create(void);
void ca_tool_catalog_destroy(ca_tool_catalog_t *c);

int  ca_tool_catalog_count(const ca_tool_catalog_t *c);
/* Upsert (idempotent on Name, case-insensitive). Deep-copies. Returns false on
 * NULL / blank name. */
bool ca_tool_catalog_upsert(ca_tool_catalog_t *c, const ca_tool_descriptor_t *d);
/* Remove by name. Returns true when removed. */
bool ca_tool_catalog_remove(ca_tool_catalog_t *c, const char *name);
/* Get by name -> deep copy into *out (true), or false when absent. */
bool ca_tool_catalog_get(ca_tool_catalog_t *c, const char *name, ca_tool_descriptor_t *out);
/* List — all, ordered by Name (ordinal-ignore-case). Fresh array. */
ca_tool_descriptor_t *ca_tool_catalog_list(ca_tool_catalog_t *c, size_t *out_count);
/* Search — keyword substring; top_k (<=0 or blank query => empty). Fresh array.
 */
ca_tool_descriptor_t *ca_tool_catalog_search(ca_tool_catalog_t *c, const char *query,
                                             int top_k, size_t *out_count);
/* ListByProvider — exact provider (case-insensitive), ordered by Name. */
ca_tool_descriptor_t *ca_tool_catalog_list_by_provider(ca_tool_catalog_t *c,
                                                       const char *provider, size_t *out_count);

/* IToolProvider seam: discover returns a fresh descriptor array (caller frees).
 * is_available is a cheap probe. */
typedef struct {
    const char *provider_id;
    ca_tool_descriptor_t *(*discover)(void *user, size_t *out_count);
    bool                  (*is_available)(void *user);
    void *user;
} ca_tool_provider_t;

/* ImportFromAsync — drain a provider into the catalog. Returns imported count.
 */
int ca_tool_catalog_import_from(ca_tool_catalog_t *c, const ca_tool_provider_t *provider);

/* IToolExecutor seam: validate + dispatch. Fills *out. */
typedef struct {
    void (*execute)(void *user, const ca_tool_descriptor_t *tool,
                    const char *arguments_json, ca_tool_execution_result_t *out);
    void *user;
} ca_tool_executor_t;

void ca_tool_executor_execute(const ca_tool_executor_t *ex, const ca_tool_descriptor_t *tool,
                              const char *arguments_json, ca_tool_execution_result_t *out);

/* ===========================================================================
 * Generative UI — UiComponent + UiCatalogEntry + JsonRenderParser
 * =========================================================================== */

/* Property value kinds ToManaged emits. */
typedef enum {
    CA_UI_VAL_NULL   = 0,
    CA_UI_VAL_STRING = 1,
    CA_UI_VAL_INT    = 2,
    CA_UI_VAL_DOUBLE = 3,
    CA_UI_VAL_BOOL   = 4
} ca_ui_value_kind_t;

typedef struct {
    char              *key;      /* owned */
    ca_ui_value_kind_t kind;
    char              *s;        /* owned when STRING */
    int64_t            i;        /* when INT */
    double             d;        /* when DOUBLE */
    bool               b;        /* when BOOL */
} ca_ui_property_t;

typedef struct ca_ui_component ca_ui_component_t;
struct ca_ui_component {
    char             *kind;         /* owned */
    ca_ui_property_t *properties;   /* owned */
    size_t            property_count;
    ca_ui_component_t *children;    /* owned array */
    size_t            child_count;
};

void ca_ui_component_free(ca_ui_component_t *c);   /* frees children recursively + the struct */

/* A catalog entry: kind + description + allowed (name->type) props + children. */
typedef struct {
    const char *name;
    const char *type;
} ca_ui_allowed_prop_t;

typedef struct {
    const char                 *kind;
    const char                 *description;
    const ca_ui_allowed_prop_t *allowed_properties;
    size_t                      allowed_property_count;
    bool                        allows_children;
} ca_ui_catalog_entry_t;

/* UiCatalogs.Default (borrowed static). *out_count set. */
const ca_ui_catalog_entry_t *ca_ui_catalog_default(size_t *out_count);

/* JsonRenderParser.Parse. Returns a fresh component tree (caller frees with
 * ca_ui_component_free), or NULL on parse failure / (strict) validation
 * failure. When strict is false, unknown kinds become a textBlock. */
ca_ui_component_t *ca_ui_parse(const char *json,
                               const ca_ui_catalog_entry_t *catalog, size_t catalog_count,
                               bool strict);

/* DescribeCatalogForPrompt — freshly-allocated prompt snippet (caller frees). */
char *ca_ui_describe_catalog_for_prompt(const ca_ui_catalog_entry_t *catalog,
                                        size_t catalog_count);

/* IGenerativeUIRenderer seam + a recording renderer (RecordingGenerativeUIRenderer). */
typedef struct {
    void (*render)(void *user, const ca_ui_component_t *root);
    void *user;
} ca_ui_renderer_t;

typedef struct ca_recording_ui_renderer ca_recording_ui_renderer_t;
ca_recording_ui_renderer_t *ca_recording_ui_renderer_create(void);
void ca_recording_ui_renderer_destroy(ca_recording_ui_renderer_t *r);
ca_ui_renderer_t ca_recording_ui_renderer_as_renderer(ca_recording_ui_renderer_t *r);
int ca_recording_ui_renderer_count(const ca_recording_ui_renderer_t *r);
/* Kind of the last rendered root (borrowed), or NULL. */
const char *ca_recording_ui_renderer_last_kind(const ca_recording_ui_renderer_t *r);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_HOST_TOOLS_UI_H */
