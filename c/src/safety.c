/*
 * safety.c — CircleAI.Safety domain primitives (C11 port).
 *
 * Ports SafetyPrimitives.cs: Incident / Hazard / EmergencyContact records and
 * InMemorySafetyBoard. Incidents + contacts are stored in insertion order;
 * hazards are keyed by HazardId (last-write-wins, ConcurrentDictionary in C#).
 * Active / AtOrAboveSeverity / Hazards return descending-by-timestamp copies.
 *
 * The C# uses a stable OrderByDescending — for equal timestamps LINQ preserves
 * source order. We reproduce that with a stable descending sort.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/safety.h"

#include <stdlib.h>
#include <string.h>

static char *sf_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

/* ── records ────────────────────────────────────────────────────────────── */

void ca_incident_free(ca_incident_t *i) {
    if (!i) return;
    free(i->incident_id);
    free(i->description);
    i->incident_id = i->description = NULL;
}
void ca_incident_free_array(ca_incident_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_incident_free(&arr[i]);
    free(arr);
}
ca_incident_t *ca_incident_copy(ca_incident_t *dst, const ca_incident_t *src) {
    if (!dst || !src) return dst;
    dst->incident_id   = sf_strdup(src->incident_id);
    dst->severity      = src->severity;
    dst->description   = sf_strdup(src->description);
    dst->has_latitude  = src->has_latitude;
    dst->latitude      = src->latitude;
    dst->has_longitude = src->has_longitude;
    dst->longitude     = src->longitude;
    dst->at_utc_ms     = src->at_utc_ms;
    return dst;
}

void ca_hazard_free(ca_hazard_t *h) {
    if (!h) return;
    free(h->hazard_id);
    free(h->description);
    free(h->category);
    h->hazard_id = h->description = h->category = NULL;
}
void ca_hazard_free_array(ca_hazard_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_hazard_free(&arr[i]);
    free(arr);
}
ca_hazard_t *ca_hazard_copy(ca_hazard_t *dst, const ca_hazard_t *src) {
    if (!dst || !src) return dst;
    dst->hazard_id    = sf_strdup(src->hazard_id);
    dst->description  = sf_strdup(src->description);
    dst->category     = sf_strdup(src->category);
    dst->noted_utc_ms = src->noted_utc_ms;
    return dst;
}

void ca_emergency_contact_free(ca_emergency_contact_t *c) {
    if (!c) return;
    free(c->contact_id);
    free(c->name);
    free(c->phone);
    free(c->relationship);
    c->contact_id = c->name = c->phone = c->relationship = NULL;
}
void ca_emergency_contact_free_array(ca_emergency_contact_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_emergency_contact_free(&arr[i]);
    free(arr);
}
ca_emergency_contact_t *ca_emergency_contact_copy(ca_emergency_contact_t *dst,
                                                  const ca_emergency_contact_t *src) {
    if (!dst || !src) return dst;
    dst->contact_id   = sf_strdup(src->contact_id);
    dst->name         = sf_strdup(src->name);
    dst->phone        = sf_strdup(src->phone);
    dst->relationship = sf_strdup(src->relationship);
    return dst;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_safety_board {
    ca_incident_t          *incidents;
    size_t                  inc_count, inc_cap;
    ca_hazard_t            *hazards;      /* keyed by hazard_id */
    size_t                  haz_count, haz_cap;
    ca_emergency_contact_t *contacts;
    size_t                  con_count, con_cap;
};

ca_safety_board_t *ca_safety_board_create(void) {
    return (ca_safety_board_t *)calloc(1, sizeof(ca_safety_board_t));
}
void ca_safety_board_destroy(ca_safety_board_t *board) {
    if (!board) return;
    for (size_t i = 0; i < board->inc_count; ++i) ca_incident_free(&board->incidents[i]);
    free(board->incidents);
    for (size_t i = 0; i < board->haz_count; ++i) ca_hazard_free(&board->hazards[i]);
    free(board->hazards);
    for (size_t i = 0; i < board->con_count; ++i) ca_emergency_contact_free(&board->contacts[i]);
    free(board->contacts);
    free(board);
}

bool ca_safety_board_log(ca_safety_board_t *board, const ca_incident_t *i) {
    if (!board || !i) return false;
    if (board->inc_count == board->inc_cap) {
        size_t nc = board->inc_cap ? board->inc_cap * 2 : 8;
        void *n = realloc(board->incidents, nc * sizeof(*board->incidents));
        if (!n) return false;
        board->incidents = n; board->inc_cap = nc;
    }
    ca_incident_copy(&board->incidents[board->inc_count], i);
    board->inc_count++;
    return true;
}

/* Stable descending sort of an index array by the incident timestamps: LINQ
 * OrderByDescending is stable, so equal keys keep source order. We build an
 * index list already in source order and insertion-sort by timestamp desc. */
static size_t *sf_order_desc_incidents(const ca_incident_t *arr, const size_t *pick,
                                       size_t n) {
    size_t *idx = (size_t *)malloc(n ? n * sizeof(size_t) : 1);
    if (!idx) return NULL;
    for (size_t i = 0; i < n; ++i) idx[i] = pick[i];
    for (size_t i = 1; i < n; ++i) {
        size_t cur = idx[i];
        int64_t key = arr[cur].at_utc_ms;
        size_t j = i;
        /* move left while predecessor has a strictly smaller timestamp (so a
         * larger timestamp bubbles earlier); equal timestamps do not swap →
         * stable. */
        while (j > 0 && arr[idx[j - 1]].at_utc_ms < key) { idx[j] = idx[j - 1]; --j; }
        idx[j] = cur;
    }
    return idx;
}

static ca_incident_t *sf_collect_incidents(ca_safety_board_t *board,
                                           bool filter_sev, ca_incident_severity_t minimum,
                                           size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!board) { if (out_count) *out_count = SIZE_MAX; return NULL; }

    size_t *pick = (size_t *)malloc(board->inc_count ? board->inc_count * sizeof(size_t) : 1);
    if (!pick) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < board->inc_count; ++i) {
        if (filter_sev && (int)board->incidents[i].severity < (int)minimum) continue;
        pick[n++] = i;
    }
    if (n == 0) { free(pick); return NULL; }

    size_t *idx = sf_order_desc_incidents(board->incidents, pick, n);
    free(pick);
    if (!idx) { if (out_count) *out_count = SIZE_MAX; return NULL; }

    ca_incident_t *res = (ca_incident_t *)calloc(n, sizeof(*res));
    if (!res) { free(idx); if (out_count) *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < n; ++i) ca_incident_copy(&res[i], &board->incidents[idx[i]]);
    free(idx);
    if (out_count) *out_count = n;
    return res;
}

ca_incident_t *ca_safety_board_active(ca_safety_board_t *board, size_t *out_count) {
    return sf_collect_incidents(board, false, CA_INCIDENT_SEVERITY_INFO, out_count);
}
ca_incident_t *ca_safety_board_at_or_above_severity(ca_safety_board_t *board,
                                                    ca_incident_severity_t minimum,
                                                    size_t *out_count) {
    return sf_collect_incidents(board, true, minimum, out_count);
}

bool ca_safety_board_note_hazard(ca_safety_board_t *board, const ca_hazard_t *h) {
    if (!board || !h) return false;
    /* last-write-wins by hazard_id */
    for (size_t i = 0; i < board->haz_count; ++i) {
        if (board->hazards[i].hazard_id && h->hazard_id &&
            strcmp(board->hazards[i].hazard_id, h->hazard_id) == 0) {
            ca_hazard_t copy; memset(&copy, 0, sizeof(copy));
            ca_hazard_copy(&copy, h);
            ca_hazard_free(&board->hazards[i]);
            board->hazards[i] = copy;
            return true;
        }
    }
    if (board->haz_count == board->haz_cap) {
        size_t nc = board->haz_cap ? board->haz_cap * 2 : 8;
        void *n = realloc(board->hazards, nc * sizeof(*board->hazards));
        if (!n) return false;
        board->hazards = n; board->haz_cap = nc;
    }
    ca_hazard_copy(&board->hazards[board->haz_count], h);
    board->haz_count++;
    return true;
}

ca_hazard_t *ca_safety_board_hazards(ca_safety_board_t *board, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!board) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    size_t n = board->haz_count;
    if (n == 0) return NULL;
    /* descending by noted_utc_ms, stable (source order = dictionary Values;
     * ConcurrentDictionary enumeration order is unspecified in C# — insertion
     * order is the closest deterministic analogue and what our linear store
     * yields). */
    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < n; ++i) idx[i] = i;
    for (size_t i = 1; i < n; ++i) {
        size_t cur = idx[i];
        int64_t key = board->hazards[cur].noted_utc_ms;
        size_t j = i;
        while (j > 0 && board->hazards[idx[j - 1]].noted_utc_ms < key) { idx[j] = idx[j - 1]; --j; }
        idx[j] = cur;
    }
    ca_hazard_t *res = (ca_hazard_t *)calloc(n, sizeof(*res));
    if (!res) { free(idx); if (out_count) *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < n; ++i) ca_hazard_copy(&res[i], &board->hazards[idx[i]]);
    free(idx);
    if (out_count) *out_count = n;
    return res;
}

bool ca_safety_board_add_contact(ca_safety_board_t *board,
                                 const ca_emergency_contact_t *c) {
    if (!board || !c) return false;
    if (board->con_count == board->con_cap) {
        size_t nc = board->con_cap ? board->con_cap * 2 : 8;
        void *n = realloc(board->contacts, nc * sizeof(*board->contacts));
        if (!n) return false;
        board->contacts = n; board->con_cap = nc;
    }
    ca_emergency_contact_copy(&board->contacts[board->con_count], c);
    board->con_count++;
    return true;
}

bool ca_safety_board_first_contact(ca_safety_board_t *board,
                                   ca_emergency_contact_t *out) {
    if (!board || !out || board->con_count == 0) return false;
    ca_emergency_contact_copy(out, &board->contacts[0]);
    return true;
}

ca_emergency_contact_t *ca_safety_board_contacts(ca_safety_board_t *board,
                                                 size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!board) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    size_t n = board->con_count;
    if (n == 0) return NULL;
    ca_emergency_contact_t *res = (ca_emergency_contact_t *)calloc(n, sizeof(*res));
    if (!res) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < n; ++i) ca_emergency_contact_copy(&res[i], &board->contacts[i]);
    if (out_count) *out_count = n;
    return res;
}
