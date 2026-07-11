/*
 * windows_automation.c — CircleAI.WindowsAutomation (C11 port).
 *
 * UiElement + UiAutomationEvent records; InMemory + Null UiAutomationDriver;
 * UiElementHelpers (ContainsPoint / HitTest / Dump).
 *
 * The in-memory driver holds an element list keyed by ElementId (Ordinal,
 * replace on dup) and an observer list. Click/Type/Key raise a UiAutomationEvent
 * to a snapshot of the observer list (so a handler that unsubscribes mid-notify
 * — not exposed here, but harmless — cannot corrupt the walk); the event is
 * borrowed for the call only. Observer "exceptions" have no analogue in C, so
 * the handler is simply invoked (the C# try/catch that logs and continues).
 *
 * Pure C11 + libc. Linear arrays (no hashtable), no pthreads.
 */

#include "circle_ai/windows_automation.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>

/* ── per-file helpers (mirrors media.c md_*) ────────────────────────────────── */

static char *wa_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *wa_strdup_empty(const char *s) { return wa_strdup(s ? s : ""); }

/* string.IsNullOrWhiteSpace. */
static bool wa_is_ws(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p)
        if (!isspace((unsigned char)*p)) return false;
    return true;
}

/* ===========================================================================
 * UiElement record
 * =========================================================================== */

void ca_ui_element_free(ca_ui_element_t *e) {
    if (!e) return;
    free(e->element_id);
    free(e->name);
    free(e->kind);
    e->element_id = e->name = e->kind = NULL;
}
void ca_ui_element_free_array(ca_ui_element_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_ui_element_free(&arr[i]);
    free(arr);
}

/* Deep-copy src into dst (dst assumed uninitialised). false on OOM. */
static bool ui_element_copy(ca_ui_element_t *dst, const ca_ui_element_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->element_id = wa_strdup_empty(src->element_id);
    dst->name       = wa_strdup_empty(src->name);
    dst->kind       = wa_strdup_empty(src->kind);
    dst->x      = src->x;
    dst->y      = src->y;
    dst->width  = src->width;
    dst->height = src->height;
    if (!dst->element_id || !dst->name || !dst->kind) {
        ca_ui_element_free(dst);
        return false;
    }
    return true;
}

/* ===========================================================================
 * UiAutomationEvent record
 * =========================================================================== */

void ca_ui_automation_event_free(ca_ui_automation_event_t *ev) {
    if (!ev) return;
    free(ev->kind);
    free(ev->element_id);
    free(ev->payload);
    ev->kind = ev->element_id = ev->payload = NULL;
}

/* ===========================================================================
 * IUiAutomationDriver — InMemory + Null
 * =========================================================================== */

typedef struct {
    ca_ui_event_handler_fn handler;
    void                  *ctx;
} observer_t;

struct ca_ui_automation_driver {
    bool             is_null;
    ca_ui_element_t *elements;
    size_t           el_count, el_cap;
    observer_t      *observers;
    size_t           obs_count, obs_cap;
};

ca_ui_automation_driver_t *ca_ui_automation_driver_inmemory_create(void) {
    return (ca_ui_automation_driver_t *)calloc(1, sizeof(ca_ui_automation_driver_t));
}
ca_ui_automation_driver_t *ca_ui_automation_driver_null_create(void) {
    ca_ui_automation_driver_t *d =
        (ca_ui_automation_driver_t *)calloc(1, sizeof(*d));
    if (d) d->is_null = true;
    return d;
}
void ca_ui_automation_driver_destroy(ca_ui_automation_driver_t *drv) {
    if (!drv) return;
    for (size_t i = 0; i < drv->el_count; ++i) ca_ui_element_free(&drv->elements[i]);
    free(drv->elements);
    free(drv->observers);
    free(drv);
}
const char *ca_ui_automation_driver_backend_id(const ca_ui_automation_driver_t *drv) {
    if (!drv) return NULL;
    return drv->is_null ? "null" : "in-memory";
}

/* Find index of an element by ElementId (Ordinal). SIZE_MAX if absent. */
static size_t ui_index_of(const ca_ui_automation_driver_t *drv, const char *id) {
    for (size_t i = 0; i < drv->el_count; ++i)
        if (strcmp(drv->elements[i].element_id, id) == 0) return i;
    return (size_t)-1;
}

int ca_ui_automation_driver_register(ca_ui_automation_driver_t *drv,
                                     const ca_ui_element_t *el) {
    if (!drv || !el || drv->is_null) return -1;
    /* ElementId keys the dictionary; a real UiElement always has one. */
    if (!el->element_id) return -1;

    size_t idx = ui_index_of(drv, el->element_id);
    ca_ui_element_t copy;
    if (!ui_element_copy(&copy, el)) return -1;
    if (idx != (size_t)-1) {
        /* Dictionary set: replace in place. */
        ca_ui_element_free(&drv->elements[idx]);
        drv->elements[idx] = copy;
        return 0;
    }
    if (drv->el_count == drv->el_cap) {
        size_t nc = drv->el_cap ? drv->el_cap * 2 : 4;
        void *n = realloc(drv->elements, nc * sizeof(*drv->elements));
        if (!n) { ca_ui_element_free(&copy); return -1; }
        drv->elements = (ca_ui_element_t *)n;
        drv->el_cap = nc;
    }
    drv->elements[drv->el_count++] = copy;
    return 0;
}

int ca_ui_automation_driver_observe(ca_ui_automation_driver_t *drv,
                                    ca_ui_event_handler_fn handler, void *ctx) {
    if (!drv || !handler || drv->is_null) return -1;
    if (drv->obs_count == drv->obs_cap) {
        size_t nc = drv->obs_cap ? drv->obs_cap * 2 : 4;
        void *n = realloc(drv->observers, nc * sizeof(*drv->observers));
        if (!n) return -1;
        drv->observers = (observer_t *)n;
        drv->obs_cap = nc;
    }
    drv->observers[drv->obs_count].handler = handler;
    drv->observers[drv->obs_count].ctx     = ctx;
    drv->obs_count++;
    return 0;
}

ca_ui_element_t *ca_ui_automation_driver_snapshot(
    const ca_ui_automation_driver_t *drv, size_t *out_count) {
    if (!out_count) return NULL;
    if (!drv) { *out_count = (size_t)-1; return NULL; }
    /* NullUiAutomationDriver.SnapshotAsync -> Array.Empty. */
    if (drv->is_null || drv->el_count == 0) { *out_count = 0; return NULL; }

    ca_ui_element_t *out =
        (ca_ui_element_t *)calloc(drv->el_count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < drv->el_count; ++i) {
        if (!ui_element_copy(&out[i], &drv->elements[i])) {
            ca_ui_element_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = drv->el_count;
    return out;
}

/* Notify: snapshot the observer list, invoke each with a borrowed event.
 * Mirrors InMemoryUiAutomationDriver.Notify (_observers.ToArray() then a
 * try/catch-per-observer loop). Observer errors have no C analogue. */
static void ui_notify(ca_ui_automation_driver_t *drv,
                      const char *kind, const char *element_id,
                      const char *payload) {
    if (drv->obs_count == 0) return;
    observer_t *snap = (observer_t *)malloc(drv->obs_count * sizeof(*snap));
    if (!snap) return;   /* cannot notify without a snapshot; drop (no throw) */
    memcpy(snap, drv->observers, drv->obs_count * sizeof(*snap));
    size_t n = drv->obs_count;

    /* A borrowed event: casts away const only so handlers get non-owning views;
     * we never free these fields (they point at caller/literal storage). */
    ca_ui_automation_event_t ev;
    ev.kind       = (char *)kind;
    ev.element_id = (char *)element_id;
    ev.payload    = (char *)payload;
    for (size_t i = 0; i < n; ++i)
        if (snap[i].handler) snap[i].handler(snap[i].ctx, &ev);
    free(snap);
}

int ca_ui_automation_driver_click(ca_ui_automation_driver_t *drv,
                                  const char *element_id) {
    if (!drv) return -1;
    if (drv->is_null) return 0;                 /* Null -> CompletedTask */
    if (wa_is_ws(element_id)) return -1;         /* ArgumentException */
    if (ui_index_of(drv, element_id) == (size_t)-1) return -1; /* Unknown element */
    ui_notify(drv, "click", element_id, NULL);
    return 0;
}

int ca_ui_automation_driver_type(ca_ui_automation_driver_t *drv,
                                 const char *text) {
    if (!drv) return -1;
    if (drv->is_null) return 0;
    if (!text) return -1;                        /* ArgumentNullException(text) */
    ui_notify(drv, "type", NULL, text);
    return 0;
}

int ca_ui_automation_driver_key(ca_ui_automation_driver_t *drv,
                                const char *key_name) {
    if (!drv) return -1;
    if (drv->is_null) return 0;
    if (wa_is_ws(key_name)) return -1;           /* ArgumentException */
    ui_notify(drv, "key", NULL, key_name);
    return 0;
}

/* ===========================================================================
 * UiElementHelpers
 * =========================================================================== */

bool ca_ui_element_contains_point(const ca_ui_element_t *el, int x, int y) {
    if (!el) return false;
    return x >= el->x && y >= el->y &&
           x < el->x + el->width && y < el->y + el->height;
}

ca_ui_element_t *ca_ui_element_hit_test(const ca_ui_element_t *els, size_t n,
                                        int x, int y, size_t *out_count) {
    if (!out_count) return NULL;
    if (n == 0) { *out_count = 0; return NULL; }
    if (!els) { *out_count = (size_t)-1; return NULL; }

    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t hits = 0;
    for (size_t i = 0; i < n; ++i)
        if (ca_ui_element_contains_point(&els[i], x, y)) idx[hits++] = i;

    if (hits == 0) { free(idx); *out_count = 0; return NULL; }
    ca_ui_element_t *out = (ca_ui_element_t *)calloc(hits, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < hits; ++i) {
        if (!ui_element_copy(&out[i], &els[idx[i]])) {
            ca_ui_element_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = hits;
    return out;
}

/* Grow-or-fail append into a heap buffer (buf/len/cap by pointer). false on OOM. */
static bool dump_append(char **buf, size_t *len, size_t *cap, const char *s) {
    size_t sl = strlen(s);
    if (*len + sl + 1 > *cap) {
        size_t nc = *cap ? *cap : 64;
        while (*len + sl + 1 > nc) nc *= 2;
        void *n = realloc(*buf, nc);
        if (!n) return false;
        *buf = (char *)n;
        *cap = nc;
    }
    memcpy(*buf + *len, s, sl + 1);
    *len += sl;
    return true;
}

char *ca_ui_element_dump(const ca_ui_element_t *els, size_t n) {
    if (n > 0 && !els) return NULL;
    size_t cap = 128, len = 0;
    char *buf = (char *)malloc(cap);
    if (!buf) return NULL;
    buf[0] = '\0';
    for (size_t i = 0; i < n; ++i) {
        const ca_ui_element_t *e = &els[i];
        /* <ElementId> "<Name>" <Kind> @ (<X>,<Y>) <Width>x<Height>\n
         * The int coordinates go through a fixed scratch; the strings append
         * directly so we never truncate a long ElementId/Name/Kind. */
        char coords[128];
        if (!dump_append(&buf, &len, &cap, e->element_id ? e->element_id : "") ||
            !dump_append(&buf, &len, &cap, " \"") ||
            !dump_append(&buf, &len, &cap, e->name ? e->name : "") ||
            !dump_append(&buf, &len, &cap, "\" ") ||
            !dump_append(&buf, &len, &cap, e->kind ? e->kind : "")) {
            free(buf);
            return NULL;
        }
        snprintf(coords, sizeof(coords), " @ (%d,%d) %dx%d\n",
                 e->x, e->y, e->width, e->height);
        if (!dump_append(&buf, &len, &cap, coords)) { free(buf); return NULL; }
    }
    return buf;
}
