/*
 * operator.c — CircleAI.Operator (C11 port).
 *
 * InMemoryModelOperator: statuses keyed by "{ns}/{id}" (linear array),
 * observers as owned tokens each with a FIFO cursor of delivered statuses so a
 * handler-less subscriber can drain what it received. ApplyAsync walks the
 * lifecycle machine and notifies on every transition, snapshotting the observer
 * list first so a handler may unsubscribe mid-fan-out.
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/operator.h"
#include "board_common.h"

#include <stdio.h>

/* ── ModelStatus copy / free ────────────────────────────────────────────── */

void ca_op_status_free(ca_op_status_t *s) {
    if (!s) return;
    free(s->model_id);
    free(s->ns);
    free(s->last_error);
    s->model_id = s->ns = s->last_error = NULL;
    s->has_last_error = false;
}

static bool status_copy(ca_op_status_t *dst, const ca_op_status_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->phase          = src->phase;
    dst->ready_replicas = src->ready_replicas;
    dst->model_id = cab_strdup_empty(src->model_id);
    dst->ns       = cab_strdup_empty(src->ns);
    bool ok = dst->model_id && dst->ns;
    if (ok && src->has_last_error) {
        dst->last_error = cab_strdup_empty(src->last_error);
        ok = dst->last_error != NULL;
        dst->has_last_error = ok;
    }
    if (!ok) { ca_op_status_free(dst); return false; }
    return true;
}

/* ── observer token (cursor) ────────────────────────────────────────────── */

struct ca_op_observer_token {
    ca_op_observer_fn handler;
    void             *ctx;
    ca_op_status_t   *buf;   /* FIFO of delivered copies */
    size_t            head, count, cap;
};

static void token_free(ca_op_observer_token_t *t) {
    if (!t) return;
    for (size_t i = 0; i < t->count; ++i)
        ca_op_status_free(&t->buf[(t->head + i) % t->cap]);
    free(t->buf);
    free(t);
}

static bool token_push(ca_op_observer_token_t *t, const ca_op_status_t *s) {
    if (t->count == t->cap) {
        size_t nc = t->cap ? t->cap * 2 : 4;
        ca_op_status_t *nb = (ca_op_status_t *)calloc(nc, sizeof(*nb));
        if (!nb) return false;
        for (size_t i = 0; i < t->count; ++i)
            nb[i] = t->buf[(t->head + i) % t->cap];
        free(t->buf);
        t->buf = nb;
        t->cap = nc;
        t->head = 0;
    }
    ca_op_status_t copy;
    if (!status_copy(&copy, s)) return false;
    t->buf[(t->head + t->count) % t->cap] = copy;
    t->count++;
    return true;
}

bool ca_op_observer_token_next(ca_op_observer_token_t *t, ca_op_status_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!t || !out || t->count == 0) return false;
    *out = t->buf[t->head];
    t->head = (t->head + 1) % t->cap;
    t->count--;
    return true;
}
size_t ca_op_observer_token_pending(const ca_op_observer_token_t *t) {
    return t ? t->count : 0;
}

/* ── operator ───────────────────────────────────────────────────────────── */

typedef struct {
    char          *key;    /* owned "{ns}/{id}" */
    ca_op_status_t status; /* owned */
} op_slot_t;

struct ca_op_operator {
    op_slot_t              *slots;
    size_t                  count, cap;
    ca_op_observer_token_t **obs;    /* owned tokens */
    size_t                  o_count, o_cap;
};

ca_op_operator_t *ca_op_operator_create(void) {
    return (ca_op_operator_t *)calloc(1, sizeof(ca_op_operator_t));
}
void ca_op_operator_destroy(ca_op_operator_t *o) {
    if (!o) return;
    for (size_t i = 0; i < o->count; ++i) {
        free(o->slots[i].key);
        ca_op_status_free(&o->slots[i].status);
    }
    for (size_t i = 0; i < o->o_count; ++i) token_free(o->obs[i]);
    free(o->slots);
    free(o->obs);
    free(o);
}
const char *ca_op_operator_backend_id(const ca_op_operator_t *o) {
    (void)o; return "in-memory";
}

/* Build "{ns}/{id}" into a fresh string. NULL on OOM. */
static char *make_key(const char *ns, const char *id) {
    size_t n = strlen(ns) + 1 + strlen(id) + 1;
    char *k = (char *)malloc(n);
    if (k) snprintf(k, n, "%s/%s", ns, id);
    return k;
}

static op_slot_t *find_slot(ca_op_operator_t *o, const char *key) {
    for (size_t i = 0; i < o->count; ++i)
        if (cab_ord_eq(o->slots[i].key, key)) return &o->slots[i];
    return NULL;
}

/* Store status under key (replace) and notify observers. Returns -1 on OOM. */
static int transition(ca_op_operator_t *o, const char *key,
                      const ca_op_deployment_t *d,
                      ca_op_lifecycle_phase_t phase, int ready_replicas) {
    ca_op_status_t status;
    memset(&status, 0, sizeof(status));
    status.model_id = (char *)d->model_id;
    status.ns       = (char *)d->ns;
    status.phase    = phase;
    status.ready_replicas = ready_replicas;
    status.has_last_error = false; /* LastError: null */

    /* Persist (deep copy). */
    op_slot_t *slot = find_slot(o, key);
    if (slot) {
        ca_op_status_t copy;
        if (!status_copy(&copy, &status)) return -1;
        ca_op_status_free(&slot->status);
        slot->status = copy;
    } else {
        if (o->count == o->cap) {
            size_t nc = o->cap ? o->cap * 2 : 4;
            void *n = realloc(o->slots, nc * sizeof(*o->slots));
            if (!n) return -1;
            o->slots = (op_slot_t *)n;
            o->cap = nc;
        }
        op_slot_t *ns_slot = &o->slots[o->count];
        memset(ns_slot, 0, sizeof(*ns_slot));
        ns_slot->key = cab_strdup(key);
        if (!ns_slot->key) return -1;
        if (!status_copy(&ns_slot->status, &status)) { free(ns_slot->key); return -1; }
        o->count++;
    }

    /* Snapshot observers, then notify (so a handler may unsubscribe safely). */
    size_t sn = o->o_count;
    ca_op_observer_token_t **snap = NULL;
    if (sn > 0) {
        snap = (ca_op_observer_token_t **)malloc(sn * sizeof(*snap));
        if (!snap) return -1;
        memcpy(snap, o->obs, sn * sizeof(*snap));
    }
    for (size_t i = 0; i < sn; ++i) {
        ca_op_observer_token_t *t = snap[i];
        if (t->handler) t->handler(t->ctx, &status);
        token_push(t, &status); /* best-effort buffering */
    }
    free(snap);
    return 0;
}

int ca_op_operator_apply(ca_op_operator_t *o, const ca_op_deployment_t *d) {
    if (!o || !d) return -1;
    if (cab_is_ws(d->model_id) || cab_is_ws(d->ns) || d->replicas < 0) return -1;

    char *key = make_key(d->ns, d->model_id);
    if (!key) return -1;

    int rc = transition(o, key, d, CA_OP_PHASE_PENDING, 0);
    if (rc == 0) rc = transition(o, key, d, CA_OP_PHASE_DOWNLOADING, 0);
    if (rc == 0) rc = transition(o, key, d, CA_OP_PHASE_LOADING, 0);
    if (rc == 0) rc = transition(o, key, d, CA_OP_PHASE_READY, d->replicas);
    free(key);
    return rc;
}

int ca_op_operator_delete(ca_op_operator_t *o, const char *model_id,
                          const char *ns) {
    if (!o || cab_is_ws(model_id) || cab_is_ws(ns)) return -1;
    char *key = make_key(ns, model_id);
    if (!key) return -1;
    for (size_t i = 0; i < o->count; ++i) {
        if (cab_ord_eq(o->slots[i].key, key)) {
            free(o->slots[i].key);
            ca_op_status_free(&o->slots[i].status);
            o->slots[i] = o->slots[o->count - 1];
            o->count--;
            break;
        }
    }
    free(key);
    return 0;
}

bool ca_op_operator_get_status(const ca_op_operator_t *o, const char *model_id,
                               const char *ns, ca_op_status_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!o || cab_is_ws(model_id) || cab_is_ws(ns) || !out) return false;
    char *key = make_key(ns, model_id);
    if (!key) return false;
    bool found = false;
    for (size_t i = 0; i < o->count; ++i) {
        if (cab_ord_eq(o->slots[i].key, key)) {
            found = status_copy(out, &o->slots[i].status);
            break;
        }
    }
    free(key);
    return found;
}

ca_op_observer_token_t *ca_op_operator_subscribe(ca_op_operator_t *o,
                                                 ca_op_observer_fn handler,
                                                 void *ctx) {
    if (!o) return NULL;
    ca_op_observer_token_t *t =
        (ca_op_observer_token_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->handler = handler;
    t->ctx     = ctx;
    if (o->o_count == o->o_cap) {
        size_t nc = o->o_cap ? o->o_cap * 2 : 4;
        void *n = realloc(o->obs, nc * sizeof(*o->obs));
        if (!n) { free(t); return NULL; }
        o->obs = (ca_op_observer_token_t **)n;
        o->o_cap = nc;
    }
    o->obs[o->o_count++] = t;
    return t;
}

void ca_op_operator_unsubscribe(ca_op_operator_t *o,
                                ca_op_observer_token_t *token) {
    if (!o || !token) return;
    for (size_t i = 0; i < o->o_count; ++i) {
        if (o->obs[i] == token) {
            o->obs[i] = o->obs[o->o_count - 1];
            o->o_count--;
            token_free(token);
            return;
        }
    }
}

/* ── Null backends ──────────────────────────────────────────────────────── */

const char *ca_op_null_operator_backend_id(void) { return "null"; }
int  ca_op_null_operator_apply(const ca_op_deployment_t *d) { (void)d; return 0; }
int  ca_op_null_operator_delete(const char *model_id, const char *ns) {
    (void)model_id; (void)ns; return 0;
}
bool ca_op_null_operator_get_status(const char *model_id, const char *ns,
                                    ca_op_status_t *out) {
    (void)model_id; (void)ns;
    if (out) memset(out, 0, sizeof(*out));
    return false;
}
const char *ca_op_null_observer_backend_id(void) { return "null"; }
