/*
 * tools_catalog.c — CircleAI.Tools.Catalog (C11 port).
 *
 * Records + five contracts:
 *   IProviderCatalog     — InMemory (scored substring search) + Null.
 *   ICredentialStore     — Encrypted (encrypt/decrypt SEAMs over a self-contained
 *                          bundle serializer) + plain InMemory + Null.
 *   IOAuth2FlowDriver    — OAuth2 (authorize-URL builder + host client-id SEAM +
 *                          token-exchange SEAM) + Null.
 *   IQuotaGuard          — SlidingWindow (explicit-clock per-minute/daily/
 *                          concurrent caps) + Null.
 *   IToolNamespaceStore  — InMemory + Null.
 *
 * See tools_catalog.h for the CredentialBundle byte format and the clock /
 * state-token decisions. Pure C11 + libc. Linear arrays, no hashtable, no
 * pthreads.
 */

#include "circle_ai/tools_catalog.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>

/* ── per-file helpers (mirrors media.c md_*) ────────────────────────────────── */

static char *tc_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *tc_strdup_empty(const char *s) { return tc_strdup(s ? s : ""); }

/* string.IsNullOrWhiteSpace. */
static bool tc_is_ws(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p)
        if (!isspace((unsigned char)*p)) return false;
    return true;
}

/* StringComparer.Ordinal equality (byte compare). */
static bool tc_ord_eq(const char *a, const char *b) {
    if (a == b) return true;
    if (!a || !b) return false;
    return strcmp(a, b) == 0;
}

/* OrdinalIgnoreCase full-string comparison (ASCII case-fold). */
static int tc_ci_cmp(const char *a, const char *b) {
    const unsigned char *x = (const unsigned char *)a;
    const unsigned char *y = (const unsigned char *)b;
    for (;; ++x, ++y) {
        int ca = tolower(*x), cb = tolower(*y);
        if (ca != cb) return ca - cb;
        if (ca == 0) return 0;
    }
}
static bool tc_ci_eq(const char *a, const char *b) {
    if (a == b) return true;
    if (!a || !b) return false;
    return tc_ci_cmp(a, b) == 0;
}

/* OrdinalIgnoreCase substring test: does needle occur in hay (ASCII CI)? An
 * empty needle matches (string.Contains("") is always true in C#). */
static bool tc_ci_contains(const char *hay, const char *needle) {
    if (!hay || !needle) return false;
    if (*needle == '\0') return true;
    size_t nl = strlen(needle);
    for (const char *h = hay; *h; ++h) {
        size_t k = 0;
        while (k < nl && h[k] &&
               tolower((unsigned char)h[k]) == tolower((unsigned char)needle[k]))
            k++;
        if (k == nl) return true;
    }
    return false;
}

/* Free an owned string array (each element + the block). */
static void tc_strv_free(char **v, size_t n) {
    if (!v) return;
    for (size_t i = 0; i < n; ++i) free(v[i]);
    free(v);
}
/* Deep-copy an owned string array (each element empty-coalesced). *out set to a
 * fresh block (NULL when n==0). false on OOM (leaving *out NULL). */
static bool tc_strv_copy(char ***out, char *const *src, size_t n) {
    *out = NULL;
    if (n == 0) return true;
    char **v = (char **)calloc(n, sizeof(char *));
    if (!v) return false;
    for (size_t i = 0; i < n; ++i) {
        v[i] = tc_strdup_empty(src ? src[i] : NULL);
        if (!v[i]) { tc_strv_free(v, i); return false; }
    }
    *out = v;
    return true;
}
/* Any element contains q (OrdinalIgnoreCase)? */
static bool tc_strv_ci_any_contains(char *const *v, size_t n, const char *q) {
    if (!v) return false;
    for (size_t i = 0; i < n; ++i)
        if (tc_ci_contains(v[i], q)) return true;
    return false;
}

/* Build "<a>/<b>" into a fresh string. NULL on OOM. */
static char *tc_key2(const char *a, const char *b) {
    size_t la = strlen(a), lb = strlen(b);
    char *k = (char *)malloc(la + 1 + lb + 1);
    if (!k) return NULL;
    memcpy(k, a, la);
    k[la] = '/';
    memcpy(k + la + 1, b, lb + 1);
    return k;
}

/* ===========================================================================
 * OAuth2Descriptor
 * =========================================================================== */

static void oauth2_descriptor_free(ca_oauth2_descriptor_t *d) {
    if (!d) return;
    free(d->authorize_url);
    free(d->token_url);
    tc_strv_free(d->scopes, d->scopes_count);
    free(d->user_info_url);
    free(d);
}

/* Deep-copy src (owned by the caller) into a fresh heap OAuth2Descriptor. NULL on
 * OOM. */
static ca_oauth2_descriptor_t *oauth2_descriptor_dup(
    const ca_oauth2_descriptor_t *src) {
    ca_oauth2_descriptor_t *d =
        (ca_oauth2_descriptor_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->authorize_url = tc_strdup_empty(src->authorize_url);
    d->token_url     = tc_strdup_empty(src->token_url);
    d->user_info_url = src->user_info_url ? tc_strdup(src->user_info_url) : NULL;
    if (!d->authorize_url || !d->token_url ||
        (src->user_info_url && !d->user_info_url)) {
        oauth2_descriptor_free(d);
        return NULL;
    }
    if (!tc_strv_copy(&d->scopes, src->scopes, src->scopes_count)) {
        oauth2_descriptor_free(d);
        return NULL;
    }
    d->scopes_count = src->scopes_count;
    return d;
}

/* ===========================================================================
 * ProviderDescriptor
 * =========================================================================== */

void ca_provider_descriptor_free(ca_provider_descriptor_t *p) {
    if (!p) return;
    free(p->provider_id);
    free(p->display_name);
    free(p->description);
    free(p->homepage);
    tc_strv_free(p->tags, p->tags_count);
    tc_strv_free(p->capabilities, p->capabilities_count);
    oauth2_descriptor_free(p->oauth2);
    memset(p, 0, sizeof(*p));
}
void ca_provider_descriptor_free_array(ca_provider_descriptor_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_provider_descriptor_free(&arr[i]);
    free(arr);
}

static bool provider_copy(ca_provider_descriptor_t *dst,
                          const ca_provider_descriptor_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->provider_id  = tc_strdup_empty(src->provider_id);
    dst->display_name = tc_strdup_empty(src->display_name);
    dst->description  = tc_strdup_empty(src->description);
    dst->homepage     = src->homepage ? tc_strdup(src->homepage) : NULL;
    dst->auth         = src->auth;
    if (!dst->provider_id || !dst->display_name || !dst->description ||
        (src->homepage && !dst->homepage)) {
        ca_provider_descriptor_free(dst);
        return false;
    }
    if (!tc_strv_copy(&dst->tags, src->tags, src->tags_count) ||
        !tc_strv_copy(&dst->capabilities, src->capabilities,
                      src->capabilities_count)) {
        ca_provider_descriptor_free(dst);
        return false;
    }
    dst->tags_count         = src->tags_count;
    dst->capabilities_count = src->capabilities_count;
    if (src->oauth2) {
        dst->oauth2 = oauth2_descriptor_dup(src->oauth2);
        if (!dst->oauth2) { ca_provider_descriptor_free(dst); return false; }
    }
    return true;
}

/* ===========================================================================
 * CredentialBundle
 * =========================================================================== */

void ca_credential_bundle_free(ca_credential_bundle_t *b) {
    if (!b) return;
    free(b->provider_id);
    free(b->user_id);
    tc_strv_free(b->field_keys, b->field_count);
    tc_strv_free(b->field_values, b->field_count);
    memset(b, 0, sizeof(*b));
}

/* ── CredentialBundle serializer (v1 little-endian; see header) ─────────────── */

static void put_u32(uint8_t **p, uint32_t v) {
    (*p)[0] = (uint8_t)(v);
    (*p)[1] = (uint8_t)(v >> 8);
    (*p)[2] = (uint8_t)(v >> 16);
    (*p)[3] = (uint8_t)(v >> 24);
    *p += 4;
}
static void put_i64(uint8_t **p, int64_t sv) {
    uint64_t v = (uint64_t)sv;
    for (int i = 0; i < 8; ++i) { (*p)[i] = (uint8_t)(v >> (i * 8)); }
    *p += 8;
}
static void put_bytes(uint8_t **p, const void *b, size_t n) {
    if (n) memcpy(*p, b, n);
    *p += n;
}
static void put_str(uint8_t **p, const char *s) {
    size_t n = s ? strlen(s) : 0;
    put_u32(p, (uint32_t)n);
    put_bytes(p, s, n);
}

/* Serialize `b` into a fresh malloc'd buffer (out + out_len by pointer). false OOM. */
static bool bundle_serialize(const ca_credential_bundle_t *b,
                             uint8_t **out, size_t *out_len) {
    size_t len = 1 /*version*/ + 1 /*has_expires*/ + 8 /*expires*/;
    len += 4 + strlen(b->provider_id);
    len += 4 + strlen(b->user_id);
    len += 4; /* field_count */
    for (size_t i = 0; i < b->field_count; ++i) {
        len += 4 + (b->field_keys[i] ? strlen(b->field_keys[i]) : 0);
        len += 4 + (b->field_values[i] ? strlen(b->field_values[i]) : 0);
    }
    uint8_t *buf = (uint8_t *)malloc(len ? len : 1);
    if (!buf) return false;
    uint8_t *p = buf;
    *p++ = 1;                                     /* version */
    *p++ = b->has_expires ? 1 : 0;
    put_i64(&p, b->has_expires ? b->expires_at_utc_ms : 0);
    put_str(&p, b->provider_id);
    put_str(&p, b->user_id);
    put_u32(&p, (uint32_t)b->field_count);
    for (size_t i = 0; i < b->field_count; ++i) {
        put_str(&p, b->field_keys[i]);
        put_str(&p, b->field_values[i]);
    }
    *out = buf;
    *out_len = len;
    return true;
}

/* Bounds-checked reader cursor over the serialized buffer. */
typedef struct { const uint8_t *p, *end; bool ok; } rdr_t;

static uint32_t get_u32(rdr_t *r) {
    if (!r->ok || (size_t)(r->end - r->p) < 4) { r->ok = false; return 0; }
    uint32_t v = (uint32_t)r->p[0] | ((uint32_t)r->p[1] << 8) |
                 ((uint32_t)r->p[2] << 16) | ((uint32_t)r->p[3] << 24);
    r->p += 4;
    return v;
}
static int64_t get_i64(rdr_t *r) {
    if (!r->ok || (size_t)(r->end - r->p) < 8) { r->ok = false; return 0; }
    uint64_t v = 0;
    for (int i = 0; i < 8; ++i) v |= ((uint64_t)r->p[i]) << (i * 8);
    r->p += 8;
    return (int64_t)v;
}
static uint8_t get_u8(rdr_t *r) {
    if (!r->ok || r->p >= r->end) { r->ok = false; return 0; }
    return *r->p++;
}
/* Read a u32-length-prefixed string into a fresh NUL-terminated buffer. NULL on
 * malformed/OOM (and clears r->ok on malformed). */
static char *get_str(rdr_t *r) {
    uint32_t n = get_u32(r);
    if (!r->ok || (size_t)(r->end - r->p) < n) { r->ok = false; return NULL; }
    char *s = (char *)malloc((size_t)n + 1);
    if (!s) { r->ok = false; return NULL; }
    if (n) memcpy(s, r->p, n);
    s[n] = '\0';
    r->p += n;
    return s;
}

/* Deserialize a serialized buffer into *out. false on malformed / OOM. */
static bool bundle_deserialize(const uint8_t *buf, size_t len,
                               ca_credential_bundle_t *out) {
    memset(out, 0, sizeof(*out));
    rdr_t r = { buf, buf + len, true };
    uint8_t version = get_u8(&r);
    if (!r.ok || version != 1) return false;
    uint8_t has_expires = get_u8(&r);
    int64_t expires = get_i64(&r);
    out->provider_id = get_str(&r);
    out->user_id     = get_str(&r);
    uint32_t fc = get_u32(&r);
    if (!r.ok) { ca_credential_bundle_free(out); return false; }
    if (fc) {
        out->field_keys   = (char **)calloc(fc, sizeof(char *));
        out->field_values = (char **)calloc(fc, sizeof(char *));
        if (!out->field_keys || !out->field_values) {
            ca_credential_bundle_free(out);
            return false;
        }
        for (uint32_t i = 0; i < fc; ++i) {
            out->field_keys[i]   = get_str(&r);
            out->field_values[i] = get_str(&r);
            out->field_count = i + 1;   /* track so free covers partial rows */
            if (!r.ok) { ca_credential_bundle_free(out); return false; }
        }
    }
    out->field_count       = fc;
    out->has_expires       = has_expires != 0;
    out->expires_at_utc_ms = has_expires ? expires : 0;
    if (!r.ok) { ca_credential_bundle_free(out); return false; }
    return true;
}

/* ===========================================================================
 * QuotaPolicy
 * =========================================================================== */

void ca_quota_policy_free(ca_quota_policy_t *p) {
    if (!p) return;
    free(p->provider_id);
    free(p->user_id);
    memset(p, 0, sizeof(*p));
}

static bool policy_copy(ca_quota_policy_t *dst, const ca_quota_policy_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->provider_id = tc_strdup_empty(src->provider_id);
    dst->user_id     = tc_strdup_empty(src->user_id);
    dst->daily_call_budget = src->daily_call_budget;
    dst->max_concurrent    = src->max_concurrent;
    dst->per_minute_cap    = src->per_minute_cap;
    if (!dst->provider_id || !dst->user_id) {
        ca_quota_policy_free(dst);
        return false;
    }
    return true;
}

/* ===========================================================================
 * ToolNamespace
 * =========================================================================== */

void ca_tool_namespace_free(ca_tool_namespace_t *ns) {
    if (!ns) return;
    free(ns->namespace_id);
    free(ns->owner_user_id);
    tc_strv_free(ns->provider_ids, ns->provider_ids_count);
    memset(ns, 0, sizeof(*ns));
}
void ca_tool_namespace_free_array(ca_tool_namespace_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_tool_namespace_free(&arr[i]);
    free(arr);
}

static bool namespace_copy(ca_tool_namespace_t *dst,
                           const ca_tool_namespace_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->namespace_id  = tc_strdup_empty(src->namespace_id);
    dst->owner_user_id = tc_strdup_empty(src->owner_user_id);
    if (!dst->namespace_id || !dst->owner_user_id) {
        ca_tool_namespace_free(dst);
        return false;
    }
    if (!tc_strv_copy(&dst->provider_ids, src->provider_ids,
                      src->provider_ids_count)) {
        ca_tool_namespace_free(dst);
        return false;
    }
    dst->provider_ids_count = src->provider_ids_count;
    return true;
}

/* ===========================================================================
 * IProviderCatalog — InMemory + Null
 * =========================================================================== */

struct ca_provider_catalog {
    bool                      is_null;
    ca_provider_descriptor_t *items;
    size_t                    count, cap;
};

ca_provider_catalog_t *ca_provider_catalog_inmemory_create(void) {
    return (ca_provider_catalog_t *)calloc(1, sizeof(ca_provider_catalog_t));
}
ca_provider_catalog_t *ca_provider_catalog_null_create(void) {
    ca_provider_catalog_t *c =
        (ca_provider_catalog_t *)calloc(1, sizeof(*c));
    if (c) c->is_null = true;
    return c;
}
void ca_provider_catalog_destroy(ca_provider_catalog_t *cat) {
    if (!cat) return;
    for (size_t i = 0; i < cat->count; ++i)
        ca_provider_descriptor_free(&cat->items[i]);
    free(cat->items);
    free(cat);
}
const char *ca_provider_catalog_backend_id(const ca_provider_catalog_t *cat) {
    if (!cat) return NULL;
    return cat->is_null ? "null" : "in-memory";
}

/* Index of a provider by ProviderId (OrdinalIgnoreCase key). SIZE_MAX absent. */
static size_t provider_index_of(const ca_provider_catalog_t *cat,
                                const char *id) {
    for (size_t i = 0; i < cat->count; ++i)
        if (tc_ci_eq(cat->items[i].provider_id, id)) return i;
    return (size_t)-1;
}

int ca_provider_catalog_register(ca_provider_catalog_t *cat,
                                 const ca_provider_descriptor_t *p) {
    if (!cat || !p || cat->is_null) return -1;
    if (!p->provider_id) return -1;   /* ProviderId keys the dictionary */

    size_t idx = provider_index_of(cat, p->provider_id);
    ca_provider_descriptor_t copy;
    if (!provider_copy(&copy, p)) return -1;
    if (idx != (size_t)-1) {
        ca_provider_descriptor_free(&cat->items[idx]);
        cat->items[idx] = copy;
        return 0;
    }
    if (cat->count == cat->cap) {
        size_t nc = cat->cap ? cat->cap * 2 : 4;
        void *n = realloc(cat->items, nc * sizeof(*cat->items));
        if (!n) { ca_provider_descriptor_free(&copy); return -1; }
        cat->items = (ca_provider_descriptor_t *)n;
        cat->cap = nc;
    }
    cat->items[cat->count++] = copy;
    return 0;
}

/* Stable ascending sort of an index array by ProviderId (Ordinal). */
static void sort_by_provider_id(const ca_provider_catalog_t *cat, size_t *idx,
                                size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        const char *kid = cat->items[key].provider_id;
        size_t j = i;
        while (j > 0 && strcmp(cat->items[idx[j - 1]].provider_id, kid) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

/* Materialise selected indices [0,n) into a fresh owned descriptor array. */
static ca_provider_descriptor_t *provider_materialise(
    const ca_provider_catalog_t *cat, const size_t *idx, size_t n,
    size_t *out_count) {
    if (n == 0) { *out_count = 0; return NULL; }
    ca_provider_descriptor_t *out =
        (ca_provider_descriptor_t *)calloc(n, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!provider_copy(&out[i], &cat->items[idx[i]])) {
            ca_provider_descriptor_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = n;
    return out;
}

ca_provider_descriptor_t *ca_provider_catalog_list(
    const ca_provider_catalog_t *cat, size_t *out_count) {
    if (!out_count) return NULL;
    if (!cat) { *out_count = (size_t)-1; return NULL; }
    if (cat->is_null || cat->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(cat->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < cat->count; ++i) idx[i] = i;
    sort_by_provider_id(cat, idx, cat->count);
    ca_provider_descriptor_t *out =
        provider_materialise(cat, idx, cat->count, out_count);
    free(idx);
    return out;
}

bool ca_provider_catalog_get(const ca_provider_catalog_t *cat, const char *id,
                             ca_provider_descriptor_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!cat || !out) return false;
    if (cat->is_null) return false;             /* NullProviderCatalog -> null */
    if (tc_is_ws(id)) return false;             /* ArgumentException */
    size_t idx = provider_index_of(cat, id);
    if (idx == (size_t)-1) return false;
    return provider_copy(out, &cat->items[idx]);
}

/* Score = +3 DisplayName / +1 Description / +2 any Tag / +2 any Capability. */
static int provider_score(const ca_provider_descriptor_t *p, const char *q) {
    int s = 0;
    if (tc_ci_contains(p->display_name, q)) s += 3;
    if (tc_ci_contains(p->description, q)) s += 1;
    if (tc_strv_ci_any_contains(p->tags, p->tags_count, q)) s += 2;
    if (tc_strv_ci_any_contains(p->capabilities, p->capabilities_count, q))
        s += 2;
    return s;
}

/* Stable descending sort of an index array by precomputed score. */
static void sort_by_score_desc(size_t *idx, const int *score, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int ks = score[key];
        size_t j = i;
        while (j > 0 && score[idx[j - 1]] < ks) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_provider_descriptor_t *ca_provider_catalog_search(
    const ca_provider_catalog_t *cat, const char *query, int top_k,
    size_t *out_count) {
    if (!out_count) return NULL;
    if (!cat) { *out_count = (size_t)-1; return NULL; }
    /* NullProviderCatalog.SearchAsync -> Array.Empty (no validation). Only the
     * in-memory catalog throws on a null query / topK <= 0. */
    if (cat->is_null) { *out_count = 0; return NULL; }
    if (!query || top_k <= 0) { *out_count = (size_t)-1; return NULL; }
    if (cat->count == 0) { *out_count = 0; return NULL; }

    int *score = (int *)malloc(cat->count * sizeof(int));
    size_t *idx = (size_t *)malloc(cat->count * sizeof(size_t));
    if (!score || !idx) { free(score); free(idx); *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < cat->count; ++i) {
        int s = provider_score(&cat->items[i], query);
        score[i] = s;
        if (s > 0) idx[n++] = i;
    }
    sort_by_score_desc(idx, score, n);
    if (n > (size_t)top_k) n = (size_t)top_k;
    ca_provider_descriptor_t *out = provider_materialise(cat, idx, n, out_count);
    free(score);
    free(idx);
    return out;
}

/* ===========================================================================
 * ICredentialStore — Encrypted (SEAM) + plain InMemory + Null
 * =========================================================================== */

typedef enum { CRED_INMEMORY, CRED_ENCRYPTED, CRED_NULL } cred_mode_t;

typedef struct {
    char    *key;     /* owned "<provider>/<user>" */
    uint8_t *blob;    /* owned stored bytes (serialized, or ciphertext) */
    size_t   blob_len;
} cred_slot_t;

struct ca_credential_store {
    cred_mode_t        mode;
    ca_cred_encrypt_fn enc;
    ca_cred_decrypt_fn dec;
    void              *ctx;
    cred_slot_t       *slots;
    size_t             count, cap;
};

ca_credential_store_t *ca_credential_store_encrypted_create(
    ca_cred_encrypt_fn enc, ca_cred_decrypt_fn dec, void *ctx) {
    if (!enc || !dec) return NULL;
    ca_credential_store_t *s =
        (ca_credential_store_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->mode = CRED_ENCRYPTED;
    s->enc = enc;
    s->dec = dec;
    s->ctx = ctx;
    return s;
}
ca_credential_store_t *ca_credential_store_inmemory_create(void) {
    ca_credential_store_t *s =
        (ca_credential_store_t *)calloc(1, sizeof(*s));
    if (s) s->mode = CRED_INMEMORY;
    return s;
}
ca_credential_store_t *ca_credential_store_null_create(void) {
    ca_credential_store_t *s =
        (ca_credential_store_t *)calloc(1, sizeof(*s));
    if (s) s->mode = CRED_NULL;
    return s;
}
void ca_credential_store_destroy(ca_credential_store_t *store) {
    if (!store) return;
    for (size_t i = 0; i < store->count; ++i) {
        free(store->slots[i].key);
        free(store->slots[i].blob);
    }
    free(store->slots);
    free(store);
}
const char *ca_credential_store_backend_id(const ca_credential_store_t *store) {
    if (!store) return NULL;
    switch (store->mode) {
        case CRED_ENCRYPTED: return "encrypted";
        case CRED_NULL:      return "null";
        default:             return "in-memory";
    }
}

static size_t cred_index_of(const ca_credential_store_t *s, const char *key) {
    for (size_t i = 0; i < s->count; ++i)
        if (tc_ord_eq(s->slots[i].key, key)) return i;
    return (size_t)-1;
}

/* Store `blob` under `key`, replacing any prior entry. Takes ownership of both
 * on success; on OOM frees neither (caller frees). false on OOM. */
static bool cred_store_put(ca_credential_store_t *s, char *key,
                           uint8_t *blob, size_t blob_len) {
    size_t idx = cred_index_of(s, key);
    if (idx != (size_t)-1) {
        free(s->slots[idx].blob);
        free(key);
        s->slots[idx].blob = blob;
        s->slots[idx].blob_len = blob_len;
        return true;
    }
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->slots, nc * sizeof(*s->slots));
        if (!n) return false;
        s->slots = (cred_slot_t *)n;
        s->cap = nc;
    }
    s->slots[s->count].key = key;
    s->slots[s->count].blob = blob;
    s->slots[s->count].blob_len = blob_len;
    s->count++;
    return true;
}

int ca_credential_store_upsert(ca_credential_store_t *store,
                               const ca_credential_bundle_t *bundle) {
    if (!store) return -1;
    if (store->mode == CRED_NULL) return 0;      /* no-op */
    if (!bundle || !bundle->provider_id || !bundle->user_id) return -1;

    uint8_t *plain = NULL;
    size_t plain_len = 0;
    if (!bundle_serialize(bundle, &plain, &plain_len)) return -1;

    uint8_t *blob = plain;
    size_t   blob_len = plain_len;
    if (store->mode == CRED_ENCRYPTED) {
        uint8_t *cipher = NULL;
        size_t   cipher_len = 0;
        int rc = store->enc(store->ctx, plain, plain_len, &cipher, &cipher_len);
        free(plain);
        if (rc != 0) return -1;                  /* encrypt-seam failure */
        blob = cipher;
        blob_len = cipher_len;
    }

    char *key = tc_key2(bundle->provider_id, bundle->user_id);
    if (!key) { free(blob); return -1; }
    if (!cred_store_put(store, key, blob, blob_len)) {
        free(key);
        free(blob);
        return -1;
    }
    return 0;
}

bool ca_credential_store_get(const ca_credential_store_t *store,
                             const char *provider_id, const char *user_id,
                             ca_credential_bundle_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!store || !out) return false;
    if (store->mode == CRED_NULL) return false;  /* always null */
    if (tc_is_ws(provider_id) || tc_is_ws(user_id)) return false;

    char *key = tc_key2(provider_id, user_id);
    if (!key) return false;
    size_t idx = cred_index_of(store, key);
    free(key);
    if (idx == (size_t)-1) return false;

    const uint8_t *blob = store->slots[idx].blob;
    size_t blob_len = store->slots[idx].blob_len;

    if (store->mode == CRED_ENCRYPTED) {
        uint8_t *plain = NULL;
        size_t   plain_len = 0;
        int rc = store->dec(store->ctx, blob, blob_len, &plain, &plain_len);
        if (rc != 0) return false;               /* decrypt-seam failure -> null */
        bool ok = bundle_deserialize(plain, plain_len, out);
        free(plain);
        return ok;
    }
    return bundle_deserialize(blob, blob_len, out);
}

int ca_credential_store_delete(ca_credential_store_t *store,
                               const char *provider_id, const char *user_id) {
    if (!store) return -1;
    if (store->mode == CRED_NULL) return 0;
    if (tc_is_ws(provider_id) || tc_is_ws(user_id)) return -1;

    char *key = tc_key2(provider_id, user_id);
    if (!key) return -1;
    size_t idx = cred_index_of(store, key);
    free(key);
    if (idx != (size_t)-1) {
        free(store->slots[idx].key);
        free(store->slots[idx].blob);
        store->slots[idx] = store->slots[--store->count];
    }
    return 0;
}

/* ===========================================================================
 * IOAuth2FlowDriver — OAuth2 (SEAMs) + Null
 * =========================================================================== */

struct ca_oauth2_flow_driver {
    bool                   is_null;
    ca_provider_catalog_t *catalog;      /* borrowed */
    ca_oauth2_client_id_fn client_id_for;
    ca_oauth2_exchange_fn  exchange;
    void                  *ctx;
    uint64_t               state_counter;
};

ca_oauth2_flow_driver_t *ca_oauth2_flow_driver_create(
    ca_provider_catalog_t *catalog, ca_oauth2_client_id_fn client_id_for,
    ca_oauth2_exchange_fn exchange, void *ctx) {
    if (!catalog || !client_id_for || !exchange) return NULL;
    ca_oauth2_flow_driver_t *d =
        (ca_oauth2_flow_driver_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->catalog = catalog;
    d->client_id_for = client_id_for;
    d->exchange = exchange;
    d->ctx = ctx;
    return d;
}
ca_oauth2_flow_driver_t *ca_oauth2_flow_driver_null_create(void) {
    ca_oauth2_flow_driver_t *d =
        (ca_oauth2_flow_driver_t *)calloc(1, sizeof(*d));
    if (d) d->is_null = true;
    return d;
}
void ca_oauth2_flow_driver_destroy(ca_oauth2_flow_driver_t *drv) {
    free(drv);
}
const char *ca_oauth2_flow_driver_backend_id(const ca_oauth2_flow_driver_t *drv) {
    if (!drv) return NULL;
    return drv->is_null ? "null" : "oauth2";
}

/* WebUtility.UrlEncode-ish: percent-encode everything except the unreserved set
 * (A-Z a-z 0-9 - _ . ~). Returns a fresh string; NULL on OOM. */
static char *url_encode(const char *s) {
    if (!s) s = "";
    static const char *hex = "0123456789ABCDEF";
    size_t sl = strlen(s);
    char *out = (char *)malloc(sl * 3 + 1);   /* worst case every byte -> %XX */
    if (!out) return NULL;
    char *o = out;
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p) {
        unsigned char c = *p;
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
            (c >= '0' && c <= '9') ||
            c == '-' || c == '_' || c == '.' || c == '~') {
            *o++ = (char)c;
        } else {
            *o++ = '%';
            *o++ = hex[c >> 4];
            *o++ = hex[c & 0x0F];
        }
    }
    *o = '\0';
    return out;
}

/* Base64url alphabet (no padding). Sized [65] so the string literal's NUL fits;
 * only indices 0..63 are ever read. */
static const char B64URL[65] =
    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

/* Encode `n` bytes as base64url without padding into a fresh string. NULL OOM. */
static char *base64url_no_pad(const uint8_t *data, size_t n) {
    size_t out_len = (n * 4 + 2) / 3;           /* ceil(4n/3) sans padding */
    char *out = (char *)malloc(out_len + 1);
    if (!out) return NULL;
    size_t o = 0, i = 0;
    while (i + 3 <= n) {
        uint32_t v = ((uint32_t)data[i] << 16) | ((uint32_t)data[i + 1] << 8) |
                     data[i + 2];
        out[o++] = B64URL[(v >> 18) & 0x3F];
        out[o++] = B64URL[(v >> 12) & 0x3F];
        out[o++] = B64URL[(v >> 6) & 0x3F];
        out[o++] = B64URL[v & 0x3F];
        i += 3;
    }
    size_t rem = n - i;
    if (rem == 1) {
        uint32_t v = (uint32_t)data[i] << 16;
        out[o++] = B64URL[(v >> 18) & 0x3F];
        out[o++] = B64URL[(v >> 12) & 0x3F];
    } else if (rem == 2) {
        uint32_t v = ((uint32_t)data[i] << 16) | ((uint32_t)data[i + 1] << 8);
        out[o++] = B64URL[(v >> 18) & 0x3F];
        out[o++] = B64URL[(v >> 12) & 0x3F];
        out[o++] = B64URL[(v >> 6) & 0x3F];
    }
    out[o] = '\0';
    return out;
}

/* Derive a 16-byte state token from the driver's monotonic counter mixed with
 * its address (unique per call; not a security RNG — see header). */
static char *make_state_token(ca_oauth2_flow_driver_t *drv) {
    uint64_t counter = ++drv->state_counter;
    uint64_t addr = (uint64_t)(uintptr_t)drv;
    uint8_t raw[16];
    for (int i = 0; i < 8; ++i) raw[i] = (uint8_t)(counter >> (i * 8));
    for (int i = 0; i < 8; ++i) raw[8 + i] = (uint8_t)((addr >> (i * 8)) ^ counter);
    return base64url_no_pad(raw, sizeof(raw));
}

/* Join owned scope strings with single spaces into a fresh string. NULL OOM. */
static char *join_scopes(char *const *scopes, size_t n) {
    size_t total = 0;
    for (size_t i = 0; i < n; ++i)
        total += (scopes[i] ? strlen(scopes[i]) : 0) + (i ? 1 : 0);
    char *out = (char *)malloc(total + 1);
    if (!out) return NULL;
    char *o = out;
    for (size_t i = 0; i < n; ++i) {
        if (i) *o++ = ' ';
        const char *s = scopes[i] ? scopes[i] : "";
        size_t sl = strlen(s);
        memcpy(o, s, sl);
        o += sl;
    }
    *o = '\0';
    return out;
}

char *ca_oauth2_flow_driver_start(ca_oauth2_flow_driver_t *drv,
                                  const char *provider_id, const char *user_id,
                                  const char *redirect_uri) {
    if (!drv) return NULL;
    if (drv->is_null) return tc_strdup("about:blank");
    if (tc_is_ws(provider_id) || tc_is_ws(user_id) || tc_is_ws(redirect_uri))
        return NULL;

    ca_provider_descriptor_t provider;
    if (!ca_provider_catalog_get(drv->catalog, provider_id, &provider))
        return NULL;                             /* Unknown provider */
    if (!provider.oauth2) {                       /* not OAuth2 */
        ca_provider_descriptor_free(&provider);
        return NULL;
    }

    char *state = make_state_token(drv);
    char *scopes = join_scopes(provider.oauth2->scopes,
                               provider.oauth2->scopes_count);
    char *client_id = drv->client_id_for(drv->ctx, provider_id);
    char *enc_client = url_encode(client_id ? client_id : "");
    char *enc_redirect = url_encode(redirect_uri);
    char *enc_scope = state && scopes ? url_encode(scopes) : NULL;
    char *enc_state = state ? url_encode(state) : NULL;

    char *url = NULL;
    if (state && scopes && enc_client && enc_redirect && enc_scope && enc_state) {
        const char *authorize = provider.oauth2->authorize_url
                                    ? provider.oauth2->authorize_url : "";
        /* "<authorize>?response_type=code&client_id=..&redirect_uri=..&scope=..&state=.." */
        size_t len = strlen(authorize) + strlen("?response_type=code") +
                     strlen("&client_id=") + strlen(enc_client) +
                     strlen("&redirect_uri=") + strlen(enc_redirect) +
                     strlen("&scope=") + strlen(enc_scope) +
                     strlen("&state=") + strlen(enc_state) + 1;
        url = (char *)malloc(len);
        if (url) {
            snprintf(url, len,
                     "%s?response_type=code&client_id=%s&redirect_uri=%s"
                     "&scope=%s&state=%s",
                     authorize, enc_client, enc_redirect, enc_scope, enc_state);
        }
    }

    free(state);
    free(scopes);
    free(client_id);
    free(enc_client);
    free(enc_redirect);
    free(enc_scope);
    free(enc_state);
    ca_provider_descriptor_free(&provider);
    return url;
}

bool ca_oauth2_flow_driver_complete(ca_oauth2_flow_driver_t *drv,
                                    const char *provider_id, const char *user_id,
                                    const char *code, const char *redirect_uri,
                                    ca_credential_bundle_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!drv || !out) return false;
    if (drv->is_null) return false;              /* no real provider wired */
    if (tc_is_ws(provider_id) || tc_is_ws(user_id) ||
        tc_is_ws(code) || tc_is_ws(redirect_uri))
        return false;
    int rc = drv->exchange(drv->ctx, provider_id, user_id, code, redirect_uri,
                           out);
    if (rc != 0) { ca_credential_bundle_free(out); return false; }
    return true;
}

/* ===========================================================================
 * IQuotaGuard — SlidingWindow + Null
 * =========================================================================== */

typedef struct {
    char             *key;          /* owned "<provider>/<user>" */
    ca_quota_policy_t policy;       /* owned; valid when has_policy */
    bool              has_policy;
    int64_t          *calls;        /* owned call timestamps (Unix ms) */
    size_t            call_count, call_cap;
    int               inflight;
} quota_slot_t;

struct ca_quota_guard {
    bool          is_null;
    quota_slot_t *slots;
    size_t        count, cap;
};

ca_quota_guard_t *ca_quota_guard_slidingwindow_create(void) {
    return (ca_quota_guard_t *)calloc(1, sizeof(ca_quota_guard_t));
}
ca_quota_guard_t *ca_quota_guard_null_create(void) {
    ca_quota_guard_t *g = (ca_quota_guard_t *)calloc(1, sizeof(*g));
    if (g) g->is_null = true;
    return g;
}
void ca_quota_guard_destroy(ca_quota_guard_t *g) {
    if (!g) return;
    for (size_t i = 0; i < g->count; ++i) {
        free(g->slots[i].key);
        if (g->slots[i].has_policy) ca_quota_policy_free(&g->slots[i].policy);
        free(g->slots[i].calls);
    }
    free(g->slots);
    free(g);
}
const char *ca_quota_guard_backend_id(const ca_quota_guard_t *g) {
    if (!g) return NULL;
    return g->is_null ? "null" : "sliding-window";
}

static size_t quota_index_of(const ca_quota_guard_t *g, const char *key) {
    for (size_t i = 0; i < g->count; ++i)
        if (tc_ord_eq(g->slots[i].key, key)) return i;
    return (size_t)-1;
}

/* Find-or-create a slot for `key` (takes a copy of the key). NULL on OOM. */
static quota_slot_t *quota_get_or_add(ca_quota_guard_t *g, const char *key) {
    size_t idx = quota_index_of(g, key);
    if (idx != (size_t)-1) return &g->slots[idx];
    if (g->count == g->cap) {
        size_t nc = g->cap ? g->cap * 2 : 4;
        void *n = realloc(g->slots, nc * sizeof(*g->slots));
        if (!n) return NULL;
        g->slots = (quota_slot_t *)n;
        g->cap = nc;
    }
    quota_slot_t *s = &g->slots[g->count];
    memset(s, 0, sizeof(*s));
    s->key = tc_strdup(key);
    if (!s->key) return NULL;
    g->count++;
    return s;
}

#define QUOTA_MINUTE_MS 60000LL
#define QUOTA_DAY_MS    86400000LL

bool ca_quota_guard_try_acquire(ca_quota_guard_t *g, const char *provider_id,
                                const char *user_id, int64_t now_ms) {
    if (!g || g->is_null) return false;          /* NullQuotaGuard -> false */
    if (!provider_id || !user_id) return false;

    char *key = tc_key2(provider_id, user_id);
    if (!key) return false;
    size_t idx = quota_index_of(g, key);
    /* No policy for the key => unlimited (true). A slot may exist without a
     * policy (from a prior Release) — treat that as no policy too. */
    if (idx == (size_t)-1 || !g->slots[idx].has_policy) { free(key); return true; }

    quota_slot_t *s = &g->slots[idx];
    free(key);
    const ca_quota_policy_t *pol = &s->policy;

    /* Prune call timestamps older than now-60000ms (list.RemoveAll(...)). */
    size_t w = 0;
    for (size_t i = 0; i < s->call_count; ++i)
        if (s->calls[i] >= now_ms - QUOTA_MINUTE_MS) s->calls[w++] = s->calls[i];
    s->call_count = w;

    /* Per-minute cap (all remaining timestamps are within the last minute). */
    if ((int)s->call_count >= pol->per_minute_cap) return false;

    /* Daily budget: timestamps within the last 24h. After minute-pruning every
     * remaining timestamp is inside the day window, but count explicitly to
     * mirror list.Count(t => t >= now.AddDays(-1)). */
    size_t within_day = 0;
    for (size_t i = 0; i < s->call_count; ++i)
        if (s->calls[i] >= now_ms - QUOTA_DAY_MS) within_day++;
    if ((int)within_day >= pol->daily_call_budget) return false;

    /* Concurrency. */
    if (s->inflight >= pol->max_concurrent) return false;

    /* Record the call + take an in-flight slot. */
    if (s->call_count == s->call_cap) {
        size_t nc = s->call_cap ? s->call_cap * 2 : 4;
        void *n = realloc(s->calls, nc * sizeof(*s->calls));
        if (!n) return false;
        s->calls = (int64_t *)n;
        s->call_cap = nc;
    }
    s->calls[s->call_count++] = now_ms;
    s->inflight++;
    return true;
}

void ca_quota_guard_release(ca_quota_guard_t *g, const char *provider_id,
                            const char *user_id) {
    if (!g || g->is_null || !provider_id || !user_id) return;
    char *key = tc_key2(provider_id, user_id);
    if (!key) return;
    size_t idx = quota_index_of(g, key);
    free(key);
    if (idx != (size_t)-1 && g->slots[idx].inflight > 0)
        g->slots[idx].inflight--;
}

int ca_quota_guard_set_policy(ca_quota_guard_t *g,
                              const ca_quota_policy_t *policy) {
    if (!g) return -1;
    if (g->is_null) return 0;                    /* no-op */
    if (!policy || !policy->provider_id || !policy->user_id) return -1;

    char *key = tc_key2(policy->provider_id, policy->user_id);
    if (!key) return -1;
    quota_slot_t *s = quota_get_or_add(g, key);
    free(key);
    if (!s) return -1;

    ca_quota_policy_t copy;
    if (!policy_copy(&copy, policy)) return -1;
    if (s->has_policy) ca_quota_policy_free(&s->policy);
    s->policy = copy;
    s->has_policy = true;
    return 0;
}

bool ca_quota_guard_get_policy(const ca_quota_guard_t *g, const char *provider_id,
                               const char *user_id, ca_quota_policy_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!g || !out || g->is_null) return false;
    if (!provider_id || !user_id) return false;
    char *key = tc_key2(provider_id, user_id);
    if (!key) return false;
    size_t idx = quota_index_of(g, key);
    free(key);
    if (idx == (size_t)-1 || !g->slots[idx].has_policy) return false;
    return policy_copy(out, &g->slots[idx].policy);
}

/* ===========================================================================
 * IToolNamespaceStore — InMemory + Null
 * =========================================================================== */

struct ca_tool_namespace_store {
    bool                 is_null;
    ca_tool_namespace_t *items;
    size_t               count, cap;
};

ca_tool_namespace_store_t *ca_tool_namespace_store_inmemory_create(void) {
    return (ca_tool_namespace_store_t *)calloc(1, sizeof(ca_tool_namespace_store_t));
}
ca_tool_namespace_store_t *ca_tool_namespace_store_null_create(void) {
    ca_tool_namespace_store_t *s =
        (ca_tool_namespace_store_t *)calloc(1, sizeof(*s));
    if (s) s->is_null = true;
    return s;
}
void ca_tool_namespace_store_destroy(ca_tool_namespace_store_t *store) {
    if (!store) return;
    for (size_t i = 0; i < store->count; ++i)
        ca_tool_namespace_free(&store->items[i]);
    free(store->items);
    free(store);
}
const char *ca_tool_namespace_store_backend_id(const ca_tool_namespace_store_t *store) {
    if (!store) return NULL;
    return store->is_null ? "null" : "in-memory";
}

static size_t namespace_index_of(const ca_tool_namespace_store_t *s,
                                 const char *id) {
    for (size_t i = 0; i < s->count; ++i)
        if (tc_ord_eq(s->items[i].namespace_id, id)) return i;
    return (size_t)-1;
}

int ca_tool_namespace_store_upsert(ca_tool_namespace_store_t *store,
                                   const ca_tool_namespace_t *ns) {
    if (!store) return -1;
    if (store->is_null) return 0;                /* no-op */
    if (!ns || tc_is_ws(ns->namespace_id)) return -1;

    size_t idx = namespace_index_of(store, ns->namespace_id);
    ca_tool_namespace_t copy;
    if (!namespace_copy(&copy, ns)) return -1;
    if (idx != (size_t)-1) {
        ca_tool_namespace_free(&store->items[idx]);
        store->items[idx] = copy;
        return 0;
    }
    if (store->count == store->cap) {
        size_t nc = store->cap ? store->cap * 2 : 4;
        void *n = realloc(store->items, nc * sizeof(*store->items));
        if (!n) { ca_tool_namespace_free(&copy); return -1; }
        store->items = (ca_tool_namespace_t *)n;
        store->cap = nc;
    }
    store->items[store->count++] = copy;
    return 0;
}

bool ca_tool_namespace_store_get(const ca_tool_namespace_store_t *store,
                                 const char *namespace_id,
                                 ca_tool_namespace_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!store || !out || store->is_null) return false;
    if (tc_is_ws(namespace_id)) return false;
    size_t idx = namespace_index_of(store, namespace_id);
    if (idx == (size_t)-1) return false;
    return namespace_copy(out, &store->items[idx]);
}

ca_tool_namespace_t *ca_tool_namespace_store_list_for_user(
    const ca_tool_namespace_store_t *store, const char *user_id,
    size_t *out_count) {
    if (!out_count) return NULL;
    if (!store) { *out_count = (size_t)-1; return NULL; }
    /* NullToolNamespaceStore -> empty; the in-memory one throws on a bad userId,
     * so treat null/whitespace as an empty (0) result rather than an error. */
    if (store->is_null || tc_is_ws(user_id) || store->count == 0) {
        *out_count = 0;
        return NULL;
    }

    size_t *idx = (size_t *)malloc(store->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < store->count; ++i)
        if (tc_ord_eq(store->items[i].owner_user_id, user_id)) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_tool_namespace_t *out =
        (ca_tool_namespace_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!namespace_copy(&out[i], &store->items[idx[i]])) {
            ca_tool_namespace_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
