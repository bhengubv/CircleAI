#ifndef CIRCLE_AI_WINDOWS_AUTOMATION_H
#define CIRCLE_AI_WINDOWS_AUTOMATION_H

/*
 * windows_automation.h — CircleAI.WindowsAutomation (C11 port).
 *
 * Ports (from src/CircleAI.WindowsAutomation):
 *   Contracts.cs                 — UiElement record; IUiAutomationDriver seam.
 *   InMemoryWindowsAutomation.cs — UiAutomationEvent record; the in-memory
 *                                  virtual-UIA driver (element dictionary +
 *                                  observer list + click/type/key notify).
 *   NullImplementations.cs       — NullUiAutomationDriver (empty snapshot,
 *                                  no-op click/type/key).
 *   WindowsAutomationHelpers.cs  — UiElementHelpers: ContainsPoint, HitTest,
 *                                  Dump.
 *
 * FULLY PORTABLE. The real Win32-UIA backend is an injected impl in C# (hosts
 * "snap a real Win32-UIA implementation in for production"); it is NOT ported.
 * The in-memory driver is a virtual UI a test drives without touching a desktop:
 *   - Register(UiElement)  — add/replace elements keyed by ElementId (Ordinal).
 *   - Observe(handler)     — attach an observer for click/type/key events.
 *   - Snapshot             — all registered elements (insertion order).
 *   - Click/Type/Key       — mutate nothing; raise a UiAutomationEvent to every
 *                            observer synchronously (a borrowed event, valid for
 *                            the call only). Observer errors are swallowed (there
 *                            is no exception in C — the handler is simply called).
 *
 * The C# async methods (ValueTask) complete synchronously; here they are plain
 * calls returning 0 / -1 (int) instead of throwing:
 *   Click: null/whitespace elementId -> -1 (ArgumentException); unknown element
 *          -> -1 (InvalidOperationException "Unknown element"); else notify -> 0.
 *   Type:  null text -> -1 (ArgumentNullException); else notify -> 0.
 *   Key:   null/whitespace keyName -> -1 (ArgumentException); else notify -> 0.
 *   The Null driver: Click/Type/Key are no-op success (0); Snapshot is empty.
 *
 * Conventions: ca_ prefix, _t types, opaque handles (forward-declared here /
 * defined in the .c), strdup-owning fields with matching *_free / *_free_array,
 * deep-copy getters, errors via NULL / count SIZE_MAX. Linear arrays, no
 * hashtable, no pthreads. Ordinal string comparison == byte compare.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * UiElement record
 * =========================================================================== */

/* UiElement(ElementId, Name, Kind, int X, int Y, int Width, int Height). All
 * three strings are non-null C# strings (owned, empty-coalesced here). */
typedef struct {
    char *element_id;   /* owned, non-null */
    char *name;         /* owned, non-null */
    char *kind;         /* owned, non-null */
    int   x;
    int   y;
    int   width;
    int   height;
} ca_ui_element_t;

/* Deep-free the owned fields of a single element (does not free the struct). */
void ca_ui_element_free(ca_ui_element_t *e);
/* Free an owned array of elements (each field + the block). */
void ca_ui_element_free_array(ca_ui_element_t *arr, size_t count);

/* ===========================================================================
 * UiAutomationEvent record
 * =========================================================================== */

/* UiAutomationEvent(Kind, string? ElementId, string? Payload). Kind is non-null;
 * element_id and payload may be NULL (the C# nullable strings). */
typedef struct {
    char *kind;         /* owned, non-null */
    char *element_id;   /* owned, NULL ok */
    char *payload;      /* owned, NULL ok */
} ca_ui_automation_event_t;

void ca_ui_automation_event_free(ca_ui_automation_event_t *ev);

/* ===========================================================================
 * IUiAutomationDriver — InMemory + Null
 * =========================================================================== */

typedef struct ca_ui_automation_driver ca_ui_automation_driver_t;

/* Observer callback. `ev` is borrowed and valid only for the duration of the
 * call (mirrors Action<UiAutomationEvent>). */
typedef void (*ca_ui_event_handler_fn)(void *ctx,
                                        const ca_ui_automation_event_t *ev);

/* InMemoryUiAutomationDriver() (BackendId "in-memory"). NULL on OOM. */
ca_ui_automation_driver_t *ca_ui_automation_driver_inmemory_create(void);
/* NullUiAutomationDriver (BackendId "null"). NULL on OOM. */
ca_ui_automation_driver_t *ca_ui_automation_driver_null_create(void);
void ca_ui_automation_driver_destroy(ca_ui_automation_driver_t *drv);

/* BackendId ("in-memory" or "null"). */
const char *ca_ui_automation_driver_backend_id(const ca_ui_automation_driver_t *drv);

/* Register(el) — deep-copies; an existing ElementId (Ordinal) is replaced.
 * In-memory only (a no-op reject on the Null driver). 0 / -1 on bad args / OOM. */
int ca_ui_automation_driver_register(ca_ui_automation_driver_t *drv,
                                     const ca_ui_element_t *el);

/* Observe(handler) — attach an observer invoked on every click/type/key. handler
 * required. In-memory only (a no-op reject on the Null driver). 0 / -1. */
int ca_ui_automation_driver_observe(ca_ui_automation_driver_t *drv,
                                    ca_ui_event_handler_fn handler, void *ctx);

/* SnapshotAsync() -> fresh owned array (*out_count) of every element (insertion
 * order). NULL + *out_count 0 when empty (or on the Null driver); NULL +
 * SIZE_MAX on error. Caller frees with ca_ui_element_free_array. */
ca_ui_element_t *ca_ui_automation_driver_snapshot(
    const ca_ui_automation_driver_t *drv, size_t *out_count);

/* ClickAsync(elementId). elementId required (non-null / non-whitespace) and must
 * name a registered element, else -1. On success notifies observers with an
 * event {kind="click", element_id, payload=NULL} and returns 0. On the Null
 * driver this is a no-op returning 0. */
int ca_ui_automation_driver_click(ca_ui_automation_driver_t *drv,
                                  const char *element_id);

/* TypeAsync(text). text required (non-null; whitespace/empty is allowed). On
 * success notifies observers with {kind="type", element_id=NULL, payload=text}
 * and returns 0; text NULL -> -1. No-op returning 0 on the Null driver. */
int ca_ui_automation_driver_type(ca_ui_automation_driver_t *drv,
                                 const char *text);

/* KeyAsync(keyName). keyName required (non-null / non-whitespace). On success
 * notifies observers with {kind="key", element_id=NULL, payload=keyName} and
 * returns 0; else -1. No-op returning 0 on the Null driver. */
int ca_ui_automation_driver_key(ca_ui_automation_driver_t *drv,
                                const char *key_name);

/* ===========================================================================
 * UiElementHelpers (static helpers over UiElement arrays)
 * =========================================================================== */

/* ContainsPoint(el, x, y) => x>=el.X && y>=el.Y && x<el.X+el.Width &&
 * y<el.Y+el.Height. false when el is NULL. */
bool ca_ui_element_contains_point(const ca_ui_element_t *el, int x, int y);

/* HitTest(els[n], x, y) -> fresh owned array (*out_count) of the elements that
 * contain the point (source order). NULL + *out_count 0 when none; NULL +
 * SIZE_MAX on error (els NULL with n>0, or OOM). Caller frees with
 * ca_ui_element_free_array. */
ca_ui_element_t *ca_ui_element_hit_test(const ca_ui_element_t *els, size_t n,
                                        int x, int y, size_t *out_count);

/* Dump(els[n]) -> malloc'd string, one line per element:
 *   <ElementId> "<Name>" <Kind> @ (<X>,<Y>) <Width>x<Height>\n
 * Returns an empty (non-NULL) string when n==0; NULL only on OOM (or els NULL
 * with n>0). Caller frees with free(). */
char *ca_ui_element_dump(const ca_ui_element_t *els, size_t n);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_WINDOWS_AUTOMATION_H */
