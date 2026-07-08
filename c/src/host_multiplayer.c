/*
 * host_multiplayer.c — CircleAI.Hosting.Multiplayer (C11 port). See
 * host_multiplayer.h.
 *
 * MultiplayerHub ported 1:1: per-doc groups, LWW-by-rev edits (AddOrUpdate
 * semantics), live cursors, presence, and the ColourFor hash → HSL. SignalR's
 * static process state becomes hub-instance state; outgoing events go through
 * the injected emit seam.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/host_multiplayer.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>

static char *mp_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static bool mp_blank(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p)
        if (*p != ' ' && *p != '\t' && *p != '\n' && *p != '\r' && *p != '\f' && *p != '\v')
            return false;
    return true;
}

typedef struct { char *data; size_t len, cap; } sb;
static void sb_reserve(sb *b, size_t extra) {
    if (b->len + extra + 1 <= b->cap) return;
    size_t nc = b->cap ? b->cap : 64;
    while (nc < b->len + extra + 1) nc *= 2;
    char *n = (char *)realloc(b->data, nc);
    if (n) { b->data = n; b->cap = nc; }
}
static void sb_add(sb *b, const char *s) { if (!s) return; size_t n = strlen(s); sb_reserve(b, n); memcpy(b->data + b->len, s, n); b->len += n; b->data[b->len] = 0; }
static void sb_addc(sb *b, char c) { sb_reserve(b, 1); b->data[b->len++] = c; b->data[b->len] = 0; }
static void json_escape(sb *b, const char *s) {
    sb_addc(b, '"');
    for (const char *p = s ? s : ""; *p; p++) {
        unsigned char ch = (unsigned char)*p;
        switch (ch) {
            case '"':  sb_add(b, "\\\""); break;
            case '\\': sb_add(b, "\\\\"); break;
            case '\n': sb_add(b, "\\n");  break;
            case '\r': sb_add(b, "\\r");  break;
            case '\t': sb_add(b, "\\t");  break;
            default:
                if (ch < 0x20) { char u[8]; snprintf(u, sizeof(u), "\\u%04x", ch); sb_add(b, u); }
                else sb_addc(b, (char)ch);
        }
    }
    sb_addc(b, '"');
}

/* ===========================================================================
 * PeerState + GuestPeerIdentity + ColourFor
 * =========================================================================== */

void ca_peer_state_free(ca_peer_state_t *p) {
    if (!p) return;
    free(p->connection_id); free(p->display_name); free(p->color); free(p->doc_id);
    p->connection_id = p->display_name = p->color = p->doc_id = NULL;
}
void ca_peer_state_free_array(ca_peer_state_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_peer_state_free(&arr[i]);
    free(arr);
}

static uint64_t g_guest_counter = 1;
static void make_hex(uint64_t counter, char out[33]) {
    uint64_t x = counter * 0x9E3779B97F4A7C15ull + 0x1234567890ABCDEFull;
    uint64_t y = (counter ^ 0xD1B54A32D192ED03ull) * 0xBF58476D1CE4E5B9ull;
    snprintf(out, 33, "%08x%08x%08x%08x",
             (unsigned)(x >> 32), (unsigned)(x & 0xFFFFFFFFu),
             (unsigned)(y >> 32), (unsigned)(y & 0xFFFFFFFFu));
}

void ca_guest_peer_identity_init(ca_guest_peer_identity_t *out,
                                 const char *peer_id, const char *display_name) {
    if (!out) return;
    if (peer_id) out->peer_id = mp_strdup(peer_id);
    else { char id[33]; make_hex(g_guest_counter++, id); out->peer_id = mp_strdup(id); }
    out->display_name = mp_strdup(display_name ? display_name : "Guest");
}
void ca_guest_peer_identity_free(ca_guest_peer_identity_t *g) {
    if (!g) return;
    free(g->peer_id); free(g->display_name);
    g->peer_id = g->display_name = NULL;
}

char *ca_multiplayer_colour_for(const char *peer_id) {
    if (mp_blank(peer_id) || (peer_id && peer_id[0] == '\0'))
        return mp_strdup("#5a4fcf");
    if (!peer_id) return mp_strdup("#5a4fcf");
    /* unchecked int h = 0; foreach c: h = h*31 + c; hue = ((h%360)+360)%360. */
    int32_t h = 0;
    for (const char *p = peer_id; *p; ++p) {
        uint32_t hu = (uint32_t)h;
        hu = hu * 31u + (uint32_t)(unsigned char)*p; /* C# char is UTF-16 unit; ASCII matches */
        h = (int32_t)hu;
    }
    int hue = ((h % 360) + 360) % 360;
    char buf[32];
    snprintf(buf, sizeof(buf), "hsl(%d, 70%%, 55%%)", hue);
    return mp_strdup(buf);
}

/* ===========================================================================
 * MultiplayerHub
 * =========================================================================== */

typedef struct {
    char   *doc_id;
    int64_t rev;
    int64_t updated_at_ms; /* informational */
} doc_rev_t;

typedef struct {
    char *connection_id;
    char *display_name;
    char *color;
    char *doc_id;   /* NULL when not in a doc */
} peer_conn_t;

struct ca_multiplayer_hub {
    ca_multiplayer_emit_fn emit;
    void                  *emit_user;
    doc_rev_t             *revs;
    size_t                 rev_count, rev_cap;
    peer_conn_t           *peers;
    size_t                 peer_count, peer_cap;
    int64_t                clock_ms;
};

ca_multiplayer_hub_t *ca_multiplayer_hub_create(ca_multiplayer_emit_fn emit, void *emit_user) {
    ca_multiplayer_hub_t *h = (ca_multiplayer_hub_t *)calloc(1, sizeof(*h));
    if (!h) return NULL;
    h->emit = emit; h->emit_user = emit_user;
    h->clock_ms = 3000;
    return h;
}
static void hub_clear(ca_multiplayer_hub_t *h) {
    for (size_t i = 0; i < h->rev_count; ++i) free(h->revs[i].doc_id);
    free(h->revs); h->revs = NULL; h->rev_count = h->rev_cap = 0;
    for (size_t i = 0; i < h->peer_count; ++i) {
        free(h->peers[i].connection_id); free(h->peers[i].display_name);
        free(h->peers[i].color); free(h->peers[i].doc_id);
    }
    free(h->peers); h->peers = NULL; h->peer_count = h->peer_cap = 0;
}
void ca_multiplayer_hub_destroy(ca_multiplayer_hub_t *h) {
    if (!h) return;
    hub_clear(h);
    free(h);
}
void ca_multiplayer_hub_reset(ca_multiplayer_hub_t *h) {
    if (!h) return;
    hub_clear(h);
}

static peer_conn_t *find_peer(ca_multiplayer_hub_t *h, const char *conn) {
    for (size_t i = 0; i < h->peer_count; ++i)
        if (h->peers[i].connection_id && strcmp(h->peers[i].connection_id, conn) == 0)
            return &h->peers[i];
    return NULL;
}
static doc_rev_t *find_rev(ca_multiplayer_hub_t *h, const char *doc) {
    for (size_t i = 0; i < h->rev_count; ++i)
        if (h->revs[i].doc_id && strcmp(h->revs[i].doc_id, doc) == 0) return &h->revs[i];
    return NULL;
}

static void emit_event(ca_multiplayer_hub_t *h, const char *event, const char *doc, const char *args_json) {
    if (h->emit) h->emit(h->emit_user, event, doc, args_json);
}

void ca_multiplayer_hub_on_connected(ca_multiplayer_hub_t *h, const char *connection_id,
                                     const ca_peer_identity_t *identity) {
    if (!h || mp_blank(connection_id) || !identity) return;
    peer_conn_t *existing = find_peer(h, connection_id);
    char *color = ca_multiplayer_colour_for(identity->peer_id);
    if (existing) {
        free(existing->display_name); free(existing->color); free(existing->doc_id);
        existing->display_name = mp_strdup(identity->display_name);
        existing->color = color;
        existing->doc_id = NULL;
        return;
    }
    if (h->peer_count == h->peer_cap) {
        size_t nc = h->peer_cap ? h->peer_cap * 2 : 8;
        void *n = realloc(h->peers, nc * sizeof(*h->peers));
        if (!n) { free(color); return; }
        h->peers = (peer_conn_t *)n; h->peer_cap = nc;
    }
    peer_conn_t *p = &h->peers[h->peer_count++];
    p->connection_id = mp_strdup(connection_id);
    p->display_name = mp_strdup(identity->display_name);
    p->color = color;
    p->doc_id = NULL;
}

void ca_multiplayer_hub_on_disconnected(ca_multiplayer_hub_t *h, const char *connection_id) {
    if (!h || mp_blank(connection_id)) return;
    for (size_t i = 0; i < h->peer_count; ++i) {
        if (h->peers[i].connection_id && strcmp(h->peers[i].connection_id, connection_id) == 0) {
            peer_conn_t peer = h->peers[i];
            /* remove first (TryRemove) */
            memmove(&h->peers[i], &h->peers[i + 1], (h->peer_count - i - 1) * sizeof(*h->peers));
            h->peer_count--;
            if (!mp_blank(peer.doc_id)) {
                sb b = {0};
                sb_add(&b, "["); json_escape(&b, peer.doc_id);
                sb_add(&b, ","); json_escape(&b, peer.connection_id);
                sb_add(&b, ","); json_escape(&b, peer.display_name);
                sb_add(&b, "]");
                emit_event(h, "PeerLeft", peer.doc_id, b.data);
                free(b.data);
            }
            free(peer.connection_id); free(peer.display_name); free(peer.color); free(peer.doc_id);
            return;
        }
    }
}

void ca_multiplayer_hub_join_document(ca_multiplayer_hub_t *h, const char *connection_id,
                                      const char *doc_id) {
    if (!h || mp_blank(connection_id) || mp_blank(doc_id)) return;
    peer_conn_t *peer = find_peer(h, connection_id);
    if (!peer) return;
    free(peer->doc_id);
    peer->doc_id = mp_strdup(doc_id);
    sb b = {0};
    sb_add(&b, "["); json_escape(&b, doc_id);
    sb_add(&b, ","); json_escape(&b, peer->connection_id);
    sb_add(&b, ","); json_escape(&b, peer->display_name);
    sb_add(&b, ","); json_escape(&b, peer->color);
    sb_add(&b, "]");
    emit_event(h, "PeerJoined", doc_id, b.data);
    free(b.data);
}

void ca_multiplayer_hub_leave_document(ca_multiplayer_hub_t *h, const char *connection_id,
                                       const char *doc_id) {
    if (!h || mp_blank(connection_id) || mp_blank(doc_id)) return;
    peer_conn_t *peer = find_peer(h, connection_id);
    if (!peer) return;
    free(peer->doc_id);
    peer->doc_id = NULL;
    sb b = {0};
    sb_add(&b, "["); json_escape(&b, doc_id);
    sb_add(&b, ","); json_escape(&b, peer->connection_id);
    sb_add(&b, ","); json_escape(&b, peer->display_name);
    sb_add(&b, "]");
    emit_event(h, "PeerLeft", doc_id, b.data);
    free(b.data);
}

void ca_multiplayer_hub_send_cursor(ca_multiplayer_hub_t *h, const char *connection_id,
                                    const char *doc_id, int line, int ch) {
    if (!h || mp_blank(connection_id)) return;
    peer_conn_t *peer = find_peer(h, connection_id);
    if (!peer) return;
    char nums[64]; snprintf(nums, sizeof(nums), "%d,%d", line, ch);
    sb b = {0};
    sb_add(&b, "["); json_escape(&b, peer->connection_id);
    sb_add(&b, ","); json_escape(&b, peer->display_name);
    sb_add(&b, ","); json_escape(&b, peer->color);
    sb_add(&b, ","); sb_add(&b, nums);
    sb_add(&b, "]");
    emit_event(h, "CursorChanged", doc_id, b.data);
    free(b.data);
}

int64_t ca_multiplayer_hub_send_edit(ca_multiplayer_hub_t *h, const char *connection_id,
                                     const char *doc_id, const char *content, int64_t rev) {
    if (!h || mp_blank(doc_id)) return 0;
    h->clock_ms += 5;
    doc_rev_t *existing = find_rev(h, doc_id);
    int64_t new_rev;
    if (!existing) {
        /* AddOrUpdate add: Math.Max(rev, 1) */
        int64_t r = rev > 1 ? rev : 1;
        if (h->rev_count == h->rev_cap) {
            size_t nc = h->rev_cap ? h->rev_cap * 2 : 8;
            void *n = realloc(h->revs, nc * sizeof(*h->revs));
            if (!n) return rev;
            h->revs = (doc_rev_t *)n; h->rev_cap = nc;
        }
        doc_rev_t *dr = &h->revs[h->rev_count++];
        dr->doc_id = mp_strdup(doc_id);
        dr->rev = r; dr->updated_at_ms = h->clock_ms;
        new_rev = r;
    } else {
        /* update: if rev <= prev keep prev, else take rev */
        if (rev <= existing->rev) new_rev = existing->rev;
        else { existing->rev = rev; existing->updated_at_ms = h->clock_ms; new_rev = rev; }
    }

    if (new_rev != rev) return new_rev; /* rejected — client rebases */

    sb b = {0};
    sb_add(&b, "["); json_escape(&b, doc_id);
    sb_add(&b, ","); json_escape(&b, content ? content : "");
    char num[32]; snprintf(num, sizeof(num), "%lld", (long long)rev);
    sb_add(&b, ","); sb_add(&b, num);
    sb_add(&b, ","); json_escape(&b, connection_id ? connection_id : "");
    sb_add(&b, "]");
    emit_event(h, "EditApplied", doc_id, b.data);
    free(b.data);
    return rev;
}

ca_peer_state_t *ca_multiplayer_hub_peers(ca_multiplayer_hub_t *h, const char *doc_id,
                                          size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!h || !doc_id) return NULL;
    size_t n = 0;
    for (size_t i = 0; i < h->peer_count; ++i)
        if (h->peers[i].doc_id && strcmp(h->peers[i].doc_id, doc_id) == 0) n++;
    if (n == 0) return NULL;
    ca_peer_state_t *res = (ca_peer_state_t *)calloc(n, sizeof(*res));
    if (!res) return NULL;
    size_t k = 0;
    for (size_t i = 0; i < h->peer_count; ++i)
        if (h->peers[i].doc_id && strcmp(h->peers[i].doc_id, doc_id) == 0) {
            res[k].connection_id = mp_strdup(h->peers[i].connection_id);
            res[k].display_name = mp_strdup(h->peers[i].display_name);
            res[k].color = mp_strdup(h->peers[i].color);
            res[k].doc_id = mp_strdup(h->peers[i].doc_id);
            k++;
        }
    if (out_count) *out_count = n;
    return res;
}

int64_t ca_multiplayer_hub_current_rev(ca_multiplayer_hub_t *h, const char *doc_id) {
    if (!h || !doc_id) return 0;
    doc_rev_t *dr = find_rev(h, doc_id);
    return dr ? dr->rev : 0;
}
