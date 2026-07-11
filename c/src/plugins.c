/*
 * plugins.c — CircleAI.Plugins (C11 port).
 *
 * PluginEvents: string-keyed handler lists; Raise snapshots the matching list
 * before invoking so a handler may unsubscribe safely. PluginContext carries a
 * workspace accessor + events + opaque logger. PermissionedPluginContext gates
 * WorkspacePath + Events by a granted-permission set (a silent bus stands in
 * when events are denied).
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/plugins.h"
#include "board_common.h"

/* ── PluginEvents ───────────────────────────────────────────────────────── */

struct ca_plugin_subscription {
    char                *event_name; /* owned */
    ca_plugin_handler_fn handler;
    void                *ctx;
    ca_plugin_events_t  *owner;      /* borrowed */
    bool                 live;
};

struct ca_plugin_events {
    ca_plugin_subscription_t **subs; /* owned tokens */
    size_t                     count, cap;
    bool                       silent; /* SilentEvents: drop raises, noop tokens */
};

static ca_plugin_events_t *events_create_impl(bool silent) {
    ca_plugin_events_t *e =
        (ca_plugin_events_t *)calloc(1, sizeof(ca_plugin_events_t));
    if (e) e->silent = silent;
    return e;
}
ca_plugin_events_t *ca_plugin_events_create(void) {
    return events_create_impl(false);
}
void ca_plugin_events_destroy(ca_plugin_events_t *e) {
    if (!e) return;
    for (size_t i = 0; i < e->count; ++i) {
        free(e->subs[i]->event_name);
        free(e->subs[i]);
    }
    free(e->subs);
    free(e);
}

ca_plugin_subscription_t *ca_plugin_events_subscribe(ca_plugin_events_t *e,
                                                     const char *event_name,
                                                     ca_plugin_handler_fn handler,
                                                     void *ctx) {
    if (!e || cab_is_ws(event_name) || !handler) return NULL;
    if (e->silent) {
        /* SilentEvents.Subscribe -> NoopDisposable. Return a live-but-detached
         * token that unsubscribe treats as a no-op. */
        ca_plugin_subscription_t *nop =
            (ca_plugin_subscription_t *)calloc(1, sizeof(*nop));
        if (!nop) return NULL;
        nop->owner = e;
        nop->live = false; /* not registered */
        nop->event_name = cab_strdup(event_name);
        if (!nop->event_name) { free(nop); return NULL; }
        return nop;
    }
    ca_plugin_subscription_t *sub =
        (ca_plugin_subscription_t *)calloc(1, sizeof(*sub));
    if (!sub) return NULL;
    sub->event_name = cab_strdup(event_name);
    if (!sub->event_name) { free(sub); return NULL; }
    sub->handler = handler;
    sub->ctx = ctx;
    sub->owner = e;
    sub->live = true;
    if (e->count == e->cap) {
        size_t nc = e->cap ? e->cap * 2 : 4;
        void *n = realloc(e->subs, nc * sizeof(*e->subs));
        if (!n) { free(sub->event_name); free(sub); return NULL; }
        e->subs = (ca_plugin_subscription_t **)n;
        e->cap = nc;
    }
    e->subs[e->count++] = sub;
    return sub;
}

int ca_plugin_events_raise(ca_plugin_events_t *e, const char *event_name,
                           void *payload) {
    if (!e || !event_name || e->silent) return 0;
    /* Snapshot matching handlers first. */
    size_t match = 0;
    for (size_t i = 0; i < e->count; ++i)
        if (e->subs[i]->live && cab_ord_eq(e->subs[i]->event_name, event_name))
            match++;
    if (match == 0) return 0;

    ca_plugin_subscription_t **snap =
        (ca_plugin_subscription_t **)malloc(match * sizeof(*snap));
    if (!snap) return 0;
    size_t k = 0;
    for (size_t i = 0; i < e->count; ++i)
        if (e->subs[i]->live && cab_ord_eq(e->subs[i]->event_name, event_name))
            snap[k++] = e->subs[i];
    for (size_t i = 0; i < match; ++i)
        snap[i]->handler(snap[i]->ctx, payload);
    free(snap);
    return (int)match;
}

void ca_plugin_events_unsubscribe(ca_plugin_events_t *e,
                                  ca_plugin_subscription_t *sub) {
    if (!sub) return;
    if (!sub->live) {
        /* detached silent-bus token: just free it */
        free(sub->event_name);
        free(sub);
        return;
    }
    if (!e) return;
    for (size_t i = 0; i < e->count; ++i) {
        if (e->subs[i] == sub) {
            free(sub->event_name);
            free(sub);
            e->subs[i] = e->subs[e->count - 1];
            e->count--;
            return;
        }
    }
}

/* ── IPluginContext ─────────────────────────────────────────────────────── */

struct ca_plugin_context {
    ca_plugin_workspace_fn accessor;
    void                  *accessor_ctx;
    ca_plugin_events_t    *events;      /* borrowed (or owned silent bus) */
    void                  *logger;      /* borrowed opaque */
    bool                   owns_events; /* true when we made a silent bus */
    /* Permissioned gating (for the wrapper). */
    bool                   permissioned;
    bool                   allow_workspace; /* WorkspacePath exposed? */
    const ca_plugin_context_t *inner;    /* borrowed, for the wrapper */
};

ca_plugin_context_t *ca_plugin_context_create(ca_plugin_workspace_fn accessor,
                                              void *accessor_ctx,
                                              ca_plugin_events_t *events,
                                              void *logger) {
    if (!events || !logger) return NULL;
    ca_plugin_context_t *c =
        (ca_plugin_context_t *)calloc(1, sizeof(ca_plugin_context_t));
    if (!c) return NULL;
    c->accessor = accessor;
    c->accessor_ctx = accessor_ctx;
    c->events = events;
    c->logger = logger;
    return c;
}
void ca_plugin_context_destroy(ca_plugin_context_t *c) {
    if (!c) return;
    if (c->owns_events) ca_plugin_events_destroy(c->events);
    free(c);
}

const char *ca_plugin_context_workspace_path(const ca_plugin_context_t *c) {
    if (!c) return NULL;
    if (c->permissioned) {
        if (!c->allow_workspace) return NULL;
        return ca_plugin_context_workspace_path(c->inner);
    }
    if (!c->accessor) return NULL;
    return c->accessor(c->accessor_ctx);
}
ca_plugin_events_t *ca_plugin_context_events(const ca_plugin_context_t *c) {
    return c ? c->events : NULL;
}
void *ca_plugin_context_logger(const ca_plugin_context_t *c) {
    if (!c) return NULL;
    if (c->permissioned) return ca_plugin_context_logger(c->inner);
    return c->logger;
}

/* Case-insensitive membership in the granted set. */
static bool granted_has(const char *const *granted, size_t n, const char *perm) {
    for (size_t i = 0; i < n; ++i)
        if (cab_ci_eq(granted[i], perm)) return true;
    return false;
}

ca_plugin_context_t *ca_plugin_context_permissioned(
    const ca_plugin_context_t *inner, const char *const *granted,
    size_t granted_count) {
    if (!inner) return NULL;
    ca_plugin_context_t *c =
        (ca_plugin_context_t *)calloc(1, sizeof(ca_plugin_context_t));
    if (!c) return NULL;
    c->permissioned = true;
    c->inner = inner;
    c->logger = inner->logger;

    bool events_ok = granted_has(granted, granted_count, CA_PLUGIN_PERM_EVENTS_SUBSCRIBE);
    c->allow_workspace =
        granted_has(granted, granted_count, CA_PLUGIN_PERM_WORKSPACE_READ) ||
        granted_has(granted, granted_count, CA_PLUGIN_PERM_WORKSPACE_WRITE);

    if (events_ok) {
        c->events = inner->events;
        c->owns_events = false;
    } else {
        /* SilentEvents */
        c->events = events_create_impl(true);
        if (!c->events) { free(c); return NULL; }
        c->owns_events = true;
    }
    return c;
}

/* ── IPlugin dispatchers ────────────────────────────────────────────────── */

int ca_plugin_initialize(const ca_plugin_t *p, ca_plugin_context_t *context) {
    if (!p || !p->initialize || !context) return -1;
    return p->initialize(p->ctx, context);
}
int ca_plugin_shutdown(const ca_plugin_t *p) {
    if (!p || !p->shutdown) return -1;
    return p->shutdown(p->ctx);
}
