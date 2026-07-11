/*
 * collaboration.c — CircleAI.Collaboration (C11 port).
 *
 * Channels keyed by ChannelId, presence keyed by UserId, messages appended per
 * ChannelId (flat list, filtered on Read). Deterministic linear arrays.
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/collaboration.h"
#include "board_common.h"

/* ── Channel ────────────────────────────────────────────────────────────── */

void ca_collab_channel_free(ca_collab_channel_t *c) {
    if (!c) return;
    free(c->channel_id);
    free(c->name);
    free(c->team_id);
    c->channel_id = c->name = c->team_id = NULL;
}
void ca_collab_channel_free_array(ca_collab_channel_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_collab_channel_free(&arr[i]);
    free(arr);
}
static bool channel_copy(ca_collab_channel_t *dst, const ca_collab_channel_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->channel_id = cab_strdup_empty(src->channel_id);
    dst->name       = cab_strdup_empty(src->name);
    dst->team_id    = cab_strdup_empty(src->team_id);
    if (!dst->channel_id || !dst->name || !dst->team_id) {
        ca_collab_channel_free(dst);
        return false;
    }
    return true;
}

/* ── Message ────────────────────────────────────────────────────────────── */

void ca_collab_message_free(ca_collab_message_t *m) {
    if (!m) return;
    free(m->message_id);
    free(m->channel_id);
    free(m->author_id);
    free(m->body);
    m->message_id = m->channel_id = m->author_id = m->body = NULL;
}
void ca_collab_message_free_array(ca_collab_message_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_collab_message_free(&arr[i]);
    free(arr);
}
static bool message_copy(ca_collab_message_t *dst, const ca_collab_message_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->at_utc_ms = src->at_utc_ms;
    dst->message_id = cab_strdup_empty(src->message_id);
    dst->channel_id = cab_strdup_empty(src->channel_id);
    dst->author_id  = cab_strdup_empty(src->author_id);
    dst->body       = cab_strdup_empty(src->body);
    if (!dst->message_id || !dst->channel_id || !dst->author_id || !dst->body) {
        ca_collab_message_free(dst);
        return false;
    }
    return true;
}

/* ── PresenceState ──────────────────────────────────────────────────────── */

void ca_collab_presence_free(ca_collab_presence_t *p) {
    if (!p) return;
    free(p->user_id);
    p->user_id = NULL;
}
static bool presence_copy(ca_collab_presence_t *dst,
                          const ca_collab_presence_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->online = src->online;
    dst->last_seen_utc_ms = src->last_seen_utc_ms;
    dst->user_id = cab_strdup_empty(src->user_id);
    return dst->user_id != NULL;
}

/* ── InMemoryChannelStore ───────────────────────────────────────────────── */

struct ca_collab_channel_store {
    ca_collab_channel_t *items;
    size_t               count, cap;
};

ca_collab_channel_store_t *ca_collab_channel_store_create(void) {
    return (ca_collab_channel_store_t *)calloc(1, sizeof(ca_collab_channel_store_t));
}
void ca_collab_channel_store_destroy(ca_collab_channel_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_collab_channel_free(&s->items[i]);
    free(s->items);
    free(s);
}
const char *ca_collab_channel_store_backend_id(const ca_collab_channel_store_t *s) {
    (void)s; return "in-memory";
}

int ca_collab_channel_store_upsert(ca_collab_channel_store_t *s,
                                   const ca_collab_channel_t *c) {
    if (!s || !c) return -1;
    for (size_t i = 0; i < s->count; ++i) {
        if (cab_ord_eq(s->items[i].channel_id, c->channel_id)) {
            ca_collab_channel_t copy;
            if (!channel_copy(&copy, c)) return -1;
            ca_collab_channel_free(&s->items[i]);
            s->items[i] = copy;
            return 0;
        }
    }
    ca_collab_channel_t copy;
    if (!channel_copy(&copy, c)) return -1;
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->items, nc * sizeof(*s->items));
        if (!n) { ca_collab_channel_free(&copy); return -1; }
        s->items = (ca_collab_channel_t *)n;
        s->cap = nc;
    }
    s->items[s->count++] = copy;
    return 0;
}

bool ca_collab_channel_store_get(const ca_collab_channel_store_t *s,
                                 const char *id, ca_collab_channel_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || cab_is_ws(id) || !out) return false;
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->items[i].channel_id, id))
            return channel_copy(out, &s->items[i]);
    return false;
}

/* Stable ascending sort of indices by Name (ordinal). */
static void channel_sort_name(const ca_collab_channel_store_t *s, size_t *idx,
                              size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 && strcmp(s->items[idx[j - 1]].name, s->items[key].name) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_collab_channel_t *ca_collab_channel_store_list_for_team(
    const ca_collab_channel_store_t *s, const char *team_id, size_t *out_count) {
    if (!out_count) return NULL;
    if (!s || cab_is_ws(team_id)) { *out_count = (size_t)-1; return NULL; }
    if (s->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(s->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->items[i].team_id, team_id)) idx[n++] = i;
    channel_sort_name(s, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_collab_channel_t *out = (ca_collab_channel_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!channel_copy(&out[i], &s->items[idx[i]])) {
            ca_collab_channel_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

const char *ca_collab_null_channel_store_backend_id(void) { return "null"; }

/* ── InMemoryMessageStore ───────────────────────────────────────────────── */

struct ca_collab_message_store {
    ca_collab_message_t *items;
    size_t               count, cap;
};

ca_collab_message_store_t *ca_collab_message_store_create(void) {
    return (ca_collab_message_store_t *)calloc(1, sizeof(ca_collab_message_store_t));
}
void ca_collab_message_store_destroy(ca_collab_message_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_collab_message_free(&s->items[i]);
    free(s->items);
    free(s);
}
const char *ca_collab_message_store_backend_id(const ca_collab_message_store_t *s) {
    (void)s; return "in-memory";
}

int ca_collab_message_store_post(ca_collab_message_store_t *s,
                                 const ca_collab_message_t *msg) {
    if (!s || !msg || cab_is_ws(msg->channel_id)) return -1;
    ca_collab_message_t copy;
    if (!message_copy(&copy, msg)) return -1;
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->items, nc * sizeof(*s->items));
        if (!n) { ca_collab_message_free(&copy); return -1; }
        s->items = (ca_collab_message_t *)n;
        s->cap = nc;
    }
    s->items[s->count++] = copy;
    return 0;
}

/* Stable descending sort of indices by AtUtc. */
static void message_sort_desc(const ca_collab_message_store_t *s, size_t *idx,
                              size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = s->items[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && s->items[idx[j - 1]].at_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_collab_message_t *ca_collab_message_store_read(
    const ca_collab_message_store_t *s, const char *channel_id, int limit,
    size_t *out_count) {
    if (!out_count) return NULL;
    if (!s || cab_is_ws(channel_id)) { *out_count = (size_t)-1; return NULL; }
    if (limit <= 0 || s->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(s->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->items[i].channel_id, channel_id)) idx[n++] = i;
    message_sort_desc(s, idx, n);
    if ((size_t)limit < n) n = (size_t)limit;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_collab_message_t *out = (ca_collab_message_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!message_copy(&out[i], &s->items[idx[i]])) {
            ca_collab_message_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

const char *ca_collab_null_message_store_backend_id(void) { return "null"; }

/* ── InMemoryPresence ───────────────────────────────────────────────────── */

struct ca_collab_presence_store {
    ca_collab_presence_t *items;
    size_t                count, cap;
};

ca_collab_presence_store_t *ca_collab_presence_store_create(void) {
    return (ca_collab_presence_store_t *)calloc(1, sizeof(ca_collab_presence_store_t));
}
void ca_collab_presence_store_destroy(ca_collab_presence_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_collab_presence_free(&s->items[i]);
    free(s->items);
    free(s);
}
const char *ca_collab_presence_store_backend_id(const ca_collab_presence_store_t *s) {
    (void)s; return "in-memory";
}

int ca_collab_presence_store_set(ca_collab_presence_store_t *s,
                                 const ca_collab_presence_t *state) {
    if (!s || !state) return -1;
    for (size_t i = 0; i < s->count; ++i) {
        if (cab_ord_eq(s->items[i].user_id, state->user_id)) {
            ca_collab_presence_t copy;
            if (!presence_copy(&copy, state)) return -1;
            ca_collab_presence_free(&s->items[i]);
            s->items[i] = copy;
            return 0;
        }
    }
    ca_collab_presence_t copy;
    if (!presence_copy(&copy, state)) return -1;
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->items, nc * sizeof(*s->items));
        if (!n) { ca_collab_presence_free(&copy); return -1; }
        s->items = (ca_collab_presence_t *)n;
        s->cap = nc;
    }
    s->items[s->count++] = copy;
    return 0;
}

bool ca_collab_presence_store_get(const ca_collab_presence_store_t *s,
                                  const char *user_id, ca_collab_presence_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || cab_is_ws(user_id) || !out) return false;
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->items[i].user_id, user_id))
            return presence_copy(out, &s->items[i]);
    return false;
}

const char *ca_collab_null_presence_backend_id(void) { return "null"; }
