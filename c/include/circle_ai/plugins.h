#ifndef CIRCLE_AI_PLUGINS_H
#define CIRCLE_AI_PLUGINS_H

/*
 * plugins.h — CircleAI.Plugins (C11 port of IPlugin.cs + PluginContext.cs).
 *
 *   Events  : IPluginEvents -> PluginEvents — Subscribe(name, handler) -> token;
 *               Raise(name, payload) fans out to name's handlers (snapshot first;
 *               a throwing handler cannot corrupt the host — here handlers are
 *               plain callbacks). Dispose the token to unsubscribe.
 *             PluginEventNames constants: "workspace.loaded", "chat.message",
 *               "model.loaded", "model.unloaded".
 *   Context : IPluginContext -> PluginContext(workspacePathAccessor, events,
 *               logger) — WorkspacePath (via accessor, may be null), Events,
 *               Logger (opaque, host-owned).
 *             PermissionedPluginContext(inner, grantedPermissions) — gates
 *               WorkspacePath by "workspace.read"/"workspace.write" (else null)
 *               and Events by "events.subscribe" (else a silent drop-on-floor
 *               bus). Permission strings are matched case-insensitively.
 *   Plugin  : IPlugin (vtable) — Id / DisplayName / Version + Initialize(context)
 *               + Shutdown. The plugin body is host-supplied; this port owns the
 *               event bus + context surface.
 *
 * Payload is opaque (void*); senders + listeners agree on the concrete type per
 * event name (mirrors object?). Logger is an opaque host handle carried through.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free. Errors via NULL. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* PluginEventNames. */
#define CA_PLUGIN_EVENT_WORKSPACE_LOADED "workspace.loaded"
#define CA_PLUGIN_EVENT_CHAT_MESSAGE     "chat.message"
#define CA_PLUGIN_EVENT_MODEL_LOADED     "model.loaded"
#define CA_PLUGIN_EVENT_MODEL_UNLOADED   "model.unloaded"

/* Permission constants (PermissionedPluginContext.Permissions). */
#define CA_PLUGIN_PERM_WORKSPACE_READ   "workspace.read"
#define CA_PLUGIN_PERM_WORKSPACE_WRITE  "workspace.write"
#define CA_PLUGIN_PERM_EVENTS_SUBSCRIBE "events.subscribe"

/* ── IPluginEvents -> PluginEvents ──────────────────────────────────────── */

typedef struct ca_plugin_events ca_plugin_events_t;
typedef struct ca_plugin_subscription ca_plugin_subscription_t;

/* Event handler. Receives the opaque payload (borrowed for the call). */
typedef void (*ca_plugin_handler_fn)(void *ctx, void *payload);

ca_plugin_events_t *ca_plugin_events_create(void); /* NULL on OOM */
void ca_plugin_events_destroy(ca_plugin_events_t *e);

/* Subscribe(eventName, handler) -> owned token (dispose to unsubscribe). NULL
 * on bad args (null/empty name, null handler) or OOM. */
ca_plugin_subscription_t *ca_plugin_events_subscribe(ca_plugin_events_t *e,
                                                     const char *event_name,
                                                     ca_plugin_handler_fn handler,
                                                     void *ctx);
/* Raise(eventName, payload) — fans out to the name's handlers (snapshot first).
 * Returns the number of handlers invoked (0 when the name is unknown). */
int ca_plugin_events_raise(ca_plugin_events_t *e, const char *event_name,
                           void *payload);
/* Dispose a subscription (unsubscribes; idempotent). */
void ca_plugin_events_unsubscribe(ca_plugin_events_t *e,
                                  ca_plugin_subscription_t *sub);

/* ── IPluginContext ─────────────────────────────────────────────────────── */

/* Workspace-path accessor (may return NULL). */
typedef const char *(*ca_plugin_workspace_fn)(void *ctx);

typedef struct ca_plugin_context ca_plugin_context_t;

/* PluginContext(workspacePathAccessor, events, logger). Borrows events + logger
 * (they must outlive the context). accessor NULL -> WorkspacePath always null.
 * events required; logger required (opaque host handle). NULL on bad args/OOM. */
ca_plugin_context_t *ca_plugin_context_create(ca_plugin_workspace_fn accessor,
                                              void *accessor_ctx,
                                              ca_plugin_events_t *events,
                                              void *logger);
void ca_plugin_context_destroy(ca_plugin_context_t *c);

/* WorkspacePath (may be NULL). */
const char *ca_plugin_context_workspace_path(const ca_plugin_context_t *c);
/* Events bus. */
ca_plugin_events_t *ca_plugin_context_events(const ca_plugin_context_t *c);
/* Logger (opaque host handle). */
void *ca_plugin_context_logger(const ca_plugin_context_t *c);

/* PermissionedPluginContext(inner, grantedPermissions[]). Wraps `inner`:
 * WorkspacePath is exposed only when "workspace.read" or "workspace.write" is
 * granted (else null); Events is the inner bus only when "events.subscribe" is
 * granted (else a silent bus that drops raises + returns a no-op token).
 * Permissions matched case-insensitively. Borrows `inner`. NULL on bad args/OOM. */
ca_plugin_context_t *ca_plugin_context_permissioned(
    const ca_plugin_context_t *inner, const char *const *granted,
    size_t granted_count);

/* ── IPlugin (injected vtable) ──────────────────────────────────────────── */

/* Initialize(context) -> 0 on success, -1 on failure. */
typedef int (*ca_plugin_init_fn)(void *ctx, ca_plugin_context_t *context);
/* Shutdown -> 0 on success, -1 on failure. */
typedef int (*ca_plugin_shutdown_fn)(void *ctx);

typedef struct {
    const char           *id;           /* borrowed */
    const char           *display_name; /* borrowed */
    const char           *version;      /* borrowed */
    ca_plugin_init_fn     initialize;
    ca_plugin_shutdown_fn shutdown;
    void                 *ctx;
} ca_plugin_t;

int ca_plugin_initialize(const ca_plugin_t *p, ca_plugin_context_t *context);
int ca_plugin_shutdown(const ca_plugin_t *p);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_PLUGINS_H */
