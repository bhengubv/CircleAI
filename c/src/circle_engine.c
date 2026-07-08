/*
 * circle_engine.c — CircleEngine facade + module bag (C11 port).
 *
 * See circle_engine.h. Ports CircleAI.Core.CircleEngine: a model-loader holder,
 * an embedding-service slot, and a type-keyed module bag (string keys stand in
 * for C#'s Type keys). In-memory only; pure C11 + libc.
 */

#include "circle_ai/circle_engine.h"

#include <stdlib.h>
#include <string.h>

static char *ce_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

typedef struct {
    char *key;      /* owned */
    void *instance; /* borrowed */
} module_slot_t;

struct ca_circle_engine {
    ca_model_loader_t *model_loader;   /* borrowed */
    void              *embedding_service; /* borrowed */
    module_slot_t     *modules;
    size_t             count;
    size_t             cap;
};

ca_circle_engine_t *ca_circle_engine_create(ca_model_loader_t *model_loader) {
    if (!model_loader) return NULL; /* ArgumentNullException analogue */
    ca_circle_engine_t *e = (ca_circle_engine_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    e->model_loader = model_loader;
    return e;
}

void ca_circle_engine_destroy(ca_circle_engine_t *engine) {
    if (!engine) return;
    for (size_t i = 0; i < engine->count; i++) free(engine->modules[i].key);
    free(engine->modules);
    free(engine);
}

ca_model_loader_t *ca_circle_engine_model_loader(const ca_circle_engine_t *engine) {
    return engine ? engine->model_loader : NULL;
}

void ca_circle_engine_set_embedding_service(ca_circle_engine_t *engine, void *service) {
    if (engine) engine->embedding_service = service;
}

void *ca_circle_engine_get_embedding_service(const ca_circle_engine_t *engine) {
    return engine ? engine->embedding_service : NULL;
}

static module_slot_t *find_slot(const ca_circle_engine_t *engine, const char *key) {
    for (size_t i = 0; i < engine->count; i++) {
        if (strcmp(engine->modules[i].key, key) == 0) return &engine->modules[i];
    }
    return NULL;
}

ca_circle_engine_t *ca_circle_engine_register_module(ca_circle_engine_t *engine,
                                                     const char *type_key, void *module) {
    if (!engine || !type_key || !module) return NULL;
    module_slot_t *existing = find_slot(engine, type_key);
    if (existing) { existing->instance = module; return engine; }

    if (engine->count >= engine->cap) {
        size_t nc = engine->cap == 0 ? 4 : engine->cap * 2;
        module_slot_t *g = (module_slot_t *)realloc(engine->modules, nc * sizeof(module_slot_t));
        if (!g) return NULL;
        engine->modules = g; engine->cap = nc;
    }
    engine->modules[engine->count].key = ce_strdup(type_key);
    if (!engine->modules[engine->count].key) return NULL;
    engine->modules[engine->count].instance = module;
    engine->count++;
    return engine;
}

void *ca_circle_engine_get_module(const ca_circle_engine_t *engine, const char *type_key) {
    if (!engine || !type_key) return NULL;
    module_slot_t *s = find_slot(engine, type_key);
    return s ? s->instance : NULL;
}

bool ca_circle_engine_has_module(const ca_circle_engine_t *engine, const char *type_key) {
    if (!engine || !type_key) return false;
    return find_slot(engine, type_key) != NULL;
}
