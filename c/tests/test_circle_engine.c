/*
 * test_circle_engine.c — CircleEngine facade + module bag (C11).
 *
 * Mirrors CircleEngine: null-loader guard, loader accessor, embedding-service
 * slot, RegisterModule/GetModule/HasModule (type-keyed via string keys).
 */

#include "circle_ai/circle_engine.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>

/* ── a trivial IModelLoader impl ── */
typedef struct { int disposed; } fake_loader_state_t;

static bool fl_download(void *u, const char *n, char **out) {
    (void)u;
    char *p = (char *)malloc(strlen(n) + 6);
    sprintf(p, "path/%s", n);
    *out = p;
    return true;
}
static bool fl_get_path(void *u, const char *n, char **out) { return fl_download(u, n, out); }
static bool fl_exists(void *u, const char *n) { (void)u; (void)n; return true; }
static bool fl_critical(void *u) { (void)u; return false; }
static void fl_dispose(void *u) { ((fake_loader_state_t *)u)->disposed = 1; }

/* ── a couple of module instances ── */
typedef struct { const char *name; int loaded; } fake_module_t;

int main(void) {
    /* null loader rejected */
    assert(ca_circle_engine_create(NULL) == NULL);

    fake_loader_state_t st = {0};
    ca_model_loader_t loader = {
        fl_download, fl_get_path, fl_exists, fl_critical, fl_dispose, &st
    };

    ca_circle_engine_t *e = ca_circle_engine_create(&loader);
    assert(e);

    /* loader accessor + a call through the vtable */
    ca_model_loader_t *got = ca_circle_engine_model_loader(e);
    assert(got == &loader);
    char *p = NULL;
    assert(got->download_model(got->user, "qwen", &p));
    assert(strcmp(p, "path/qwen") == 0);
    free(p);

    /* embedding-service slot */
    assert(ca_circle_engine_get_embedding_service(e) == NULL);
    int embed_marker = 42;
    ca_circle_engine_set_embedding_service(e, &embed_marker);
    assert(ca_circle_engine_get_embedding_service(e) == &embed_marker);

    /* module bag */
    fake_module_t search = { "search", 1 };
    fake_module_t tools  = { "tools", 0 };
    assert(!ca_circle_engine_has_module(e, "Search"));
    assert(ca_circle_engine_get_module(e, "Search") == NULL);

    assert(ca_circle_engine_register_module(e, "Search", &search) == e);
    assert(ca_circle_engine_register_module(e, "Tools", &tools) == e);
    assert(ca_circle_engine_has_module(e, "Search"));
    assert(ca_circle_engine_has_module(e, "Tools"));
    assert(ca_circle_engine_get_module(e, "Search") == &search);
    assert(ca_circle_engine_get_module(e, "Tools") == &tools);
    assert(ca_circle_engine_get_module(e, "Missing") == NULL);

    /* re-register replaces */
    fake_module_t search2 = { "search2", 0 };
    assert(ca_circle_engine_register_module(e, "Search", &search2) == e);
    assert(ca_circle_engine_get_module(e, "Search") == &search2);

    /* null module rejected */
    assert(ca_circle_engine_register_module(e, "X", NULL) == NULL);

    ca_circle_engine_destroy(e); /* does not dispose the borrowed loader */
    assert(st.disposed == 0);

    printf("test_circle_engine: all assertions passed\n");
    return 0;
}
