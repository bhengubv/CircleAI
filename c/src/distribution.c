/*
 * distribution.c — CircleAI.Distribution (C11 port of the four scoped rails).
 *
 * DefaultAppStoreSubmitter: records accepted packages keyed by
 * "{StoreName}/{Version}", rejecting unknown stores. DefaultSignedDeltaUpdater:
 * per-channel version pointer advanced only when the HMAC-SHA256 signature over
 * "Channel|FromVersion|ToVersion|" + Payload verifies (constant-time). The OEM
 * and carrier catalogues are static string arrays.
 *
 * A self-contained SHA-256 + HMAC-SHA256 lives here so the module has no
 * external crypto dependency and builds cleanly with gcc/Ninja on any platform.
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/distribution.h"
#include "board_common.h"

#include <stdio.h>

/* ═══════════════════════════ SHA-256 + HMAC ═══════════════════════════════ */

typedef struct {
    uint32_t state[8];
    uint64_t bitlen;
    uint8_t  data[64];
    size_t   datalen;
} sha256_ctx;

#define ROTR(x, n) (((x) >> (n)) | ((x) << (32 - (n))))
#define CH(x, y, z)  (((x) & (y)) ^ (~(x) & (z)))
#define MAJ(x, y, z) (((x) & (y)) ^ ((x) & (z)) ^ ((y) & (z)))
#define EP0(x)  (ROTR(x, 2) ^ ROTR(x, 13) ^ ROTR(x, 22))
#define EP1(x)  (ROTR(x, 6) ^ ROTR(x, 11) ^ ROTR(x, 25))
#define SIG0(x) (ROTR(x, 7) ^ ROTR(x, 18) ^ ((x) >> 3))
#define SIG1(x) (ROTR(x, 17) ^ ROTR(x, 19) ^ ((x) >> 10))

static const uint32_t sha256_k[64] = {
    0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
    0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
    0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
    0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
    0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
    0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
    0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
    0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2
};

static void sha256_init(sha256_ctx *c) {
    c->datalen = 0; c->bitlen = 0;
    c->state[0]=0x6a09e667; c->state[1]=0xbb67ae85; c->state[2]=0x3c6ef372; c->state[3]=0xa54ff53a;
    c->state[4]=0x510e527f; c->state[5]=0x9b05688c; c->state[6]=0x1f83d9ab; c->state[7]=0x5be0cd19;
}
static void sha256_transform(sha256_ctx *c, const uint8_t *d) {
    uint32_t a,b,e,f,g,h,i,t1,t2,m[64],cc;
    for (i=0;i<16;++i) m[i] = ((uint32_t)d[i*4]<<24)|((uint32_t)d[i*4+1]<<16)|((uint32_t)d[i*4+2]<<8)|((uint32_t)d[i*4+3]);
    for (;i<64;++i) m[i] = SIG1(m[i-2]) + m[i-7] + SIG0(m[i-15]) + m[i-16];
    a=c->state[0]; b=c->state[1]; cc=c->state[2]; e=c->state[3];
    f=c->state[4]; g=c->state[5]; h=c->state[6]; t2=c->state[7];
    {
        uint32_t A=a,B=b,C=cc,D=e,E=f,F=g,G=h,H=t2;
        for (i=0;i<64;++i) {
            t1 = H + EP1(E) + CH(E,F,G) + sha256_k[i] + m[i];
            t2 = EP0(A) + MAJ(A,B,C);
            H=G; G=F; F=E; E=D+t1; D=C; C=B; B=A; A=t1+t2;
        }
        c->state[0]+=A; c->state[1]+=B; c->state[2]+=C; c->state[3]+=D;
        c->state[4]+=E; c->state[5]+=F; c->state[6]+=G; c->state[7]+=H;
    }
}
static void sha256_update(sha256_ctx *c, const uint8_t *d, size_t len) {
    for (size_t i=0;i<len;++i) {
        c->data[c->datalen++] = d[i];
        if (c->datalen == 64) { sha256_transform(c, c->data); c->bitlen += 512; c->datalen = 0; }
    }
}
static void sha256_final(sha256_ctx *c, uint8_t *hash) {
    size_t i = c->datalen;
    c->data[i++] = 0x80;
    if (c->datalen < 56) { while (i < 56) c->data[i++] = 0; }
    else { while (i < 64) c->data[i++] = 0; sha256_transform(c, c->data); memset(c->data, 0, 56); }
    c->bitlen += (uint64_t)c->datalen * 8;
    for (int b=0;b<8;++b) c->data[63-b] = (uint8_t)(c->bitlen >> (b*8));
    sha256_transform(c, c->data);
    for (i=0;i<4;++i)
        for (int j=0;j<8;++j)
            hash[i + j*4] = (uint8_t)((c->state[j] >> (24 - i*8)) & 0xFF);
}

/* HMAC-SHA256(key, msg) -> out[32]. */
static void hmac_sha256(const uint8_t *key, size_t key_len,
                        const uint8_t *msg, size_t msg_len, uint8_t out[32]) {
    uint8_t k[64], ipad[64], opad[64], inner[32];
    memset(k, 0, sizeof(k));
    if (key_len > 64) {
        sha256_ctx kc; sha256_init(&kc); sha256_update(&kc, key, key_len); sha256_final(&kc, k);
    } else {
        memcpy(k, key, key_len);
    }
    for (int i=0;i<64;++i) { ipad[i] = k[i]^0x36; opad[i] = k[i]^0x5c; }
    sha256_ctx ic; sha256_init(&ic); sha256_update(&ic, ipad, 64);
    sha256_update(&ic, msg, msg_len); sha256_final(&ic, inner);
    sha256_ctx oc; sha256_init(&oc); sha256_update(&oc, opad, 64);
    sha256_update(&oc, inner, 32); sha256_final(&oc, out);
}

/* Constant-time compare (FixedTimeEquals). */
static bool ct_equal(const uint8_t *a, size_t alen, const uint8_t *b, size_t blen) {
    if (alen != blen) return false;
    uint8_t diff = 0;
    for (size_t i = 0; i < alen; ++i) diff |= (uint8_t)(a[i] ^ b[i]);
    return diff == 0;
}

/* ═══════════════════════════ records ══════════════════════════════════════ */

void ca_dist_app_package_free(ca_dist_app_package_t *p) {
    if (!p) return;
    free(p->store_name);
    free(p->package_path);
    free(p->version);
    cab_strv_free(p->meta_keys, p->meta_count);
    cab_strv_free(p->meta_values, p->meta_count);
    memset(p, 0, sizeof(*p));
}
void ca_dist_app_package_free_array(ca_dist_app_package_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_dist_app_package_free(&arr[i]);
    free(arr);
}
static bool package_copy(ca_dist_app_package_t *dst,
                         const ca_dist_app_package_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->store_name   = cab_strdup_empty(src->store_name);
    dst->package_path = cab_strdup_empty(src->package_path);
    dst->version      = cab_strdup_empty(src->version);
    if (!dst->store_name || !dst->package_path || !dst->version) {
        ca_dist_app_package_free(dst);
        return false;
    }
    if (!cab_strv_copy(&dst->meta_keys, src->meta_keys, src->meta_count) ||
        !cab_strv_copy(&dst->meta_values, src->meta_values, src->meta_count)) {
        ca_dist_app_package_free(dst);
        return false;
    }
    dst->meta_count = src->meta_count;
    return true;
}

void ca_dist_delta_update_free(ca_dist_delta_update_t *u) {
    if (!u) return;
    free(u->channel);
    free(u->from_version);
    free(u->to_version);
    free(u->payload);
    free(u->signature);
    memset(u, 0, sizeof(*u));
}

/* ═══════════════════════ DefaultAppStoreSubmitter ═════════════════════════ */

static const char *const KNOWN_STORES[] = {
    "PlayStore", "AppStore", "Galaxy Store", "Huawei AppGallery",
    "Microsoft Store", "F-Droid"
};
#define KNOWN_STORE_COUNT (sizeof(KNOWN_STORES) / sizeof(KNOWN_STORES[0]))

static bool store_known(const char *name) {
    for (size_t i = 0; i < KNOWN_STORE_COUNT; ++i)
        if (cab_ci_eq(KNOWN_STORES[i], name)) return true;
    return false;
}

typedef struct {
    char                 *key;     /* owned "{StoreName}/{Version}" */
    ca_dist_app_package_t package; /* owned */
} submit_slot_t;

struct ca_dist_app_submitter {
    submit_slot_t *slots;
    size_t         count, cap;
};

ca_dist_app_submitter_t *ca_dist_app_submitter_create(void) {
    return (ca_dist_app_submitter_t *)calloc(1, sizeof(ca_dist_app_submitter_t));
}
void ca_dist_app_submitter_destroy(ca_dist_app_submitter_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) {
        free(s->slots[i].key);
        ca_dist_app_package_free(&s->slots[i].package);
    }
    free(s->slots);
    free(s);
}

int ca_dist_app_submitter_submit(ca_dist_app_submitter_t *s,
                                 const ca_dist_app_package_t *package,
                                 bool *accepted) {
    if (accepted) *accepted = false;
    if (!s || !package || !accepted) return -1;
    if (cab_is_ws(package->store_name) || cab_is_ws(package->package_path) ||
        cab_is_ws(package->version))
        return -1;
    if (!store_known(package->store_name)) { *accepted = false; return 0; }

    size_t klen = strlen(package->store_name) + 1 + strlen(package->version) + 1;
    char *key = (char *)malloc(klen);
    if (!key) return -1;
    snprintf(key, klen, "%s/%s", package->store_name, package->version);

    /* replace by key */
    for (size_t i = 0; i < s->count; ++i) {
        if (cab_ord_eq(s->slots[i].key, key)) {
            ca_dist_app_package_t copy;
            if (!package_copy(&copy, package)) { free(key); return -1; }
            ca_dist_app_package_free(&s->slots[i].package);
            s->slots[i].package = copy;
            free(key);
            *accepted = true;
            return 0;
        }
    }
    ca_dist_app_package_t copy;
    if (!package_copy(&copy, package)) { free(key); return -1; }
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->slots, nc * sizeof(*s->slots));
        if (!n) { ca_dist_app_package_free(&copy); free(key); return -1; }
        s->slots = (submit_slot_t *)n;
        s->cap = nc;
    }
    s->slots[s->count].key = key;
    s->slots[s->count].package = copy;
    s->count++;
    *accepted = true;
    return 0;
}

ca_dist_app_package_t *ca_dist_app_submitter_submitted(
    const ca_dist_app_submitter_t *s, size_t *out_count) {
    if (!out_count) return NULL;
    if (!s) { *out_count = (size_t)-1; return NULL; }
    if (s->count == 0) { *out_count = 0; return NULL; }
    ca_dist_app_package_t *out =
        (ca_dist_app_package_t *)calloc(s->count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < s->count; ++i) {
        if (!package_copy(&out[i], &s->slots[i].package)) {
            ca_dist_app_package_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = s->count;
    return out;
}

/* ═══════════════════════ DefaultSignedDeltaUpdater ════════════════════════ */

typedef struct {
    char *channel;  /* owned */
    char *version;  /* owned (current ToVersion) */
} channel_slot_t;

struct ca_dist_delta_updater {
    uint8_t        *hmac_key;
    size_t          hmac_key_len;
    channel_slot_t *channels;
    size_t          count, cap;
};

ca_dist_delta_updater_t *ca_dist_delta_updater_create(const uint8_t *hmac_key,
                                                      size_t hmac_key_len) {
    if (!hmac_key || hmac_key_len < 16) return NULL;
    ca_dist_delta_updater_t *u =
        (ca_dist_delta_updater_t *)calloc(1, sizeof(*u));
    if (!u) return NULL;
    u->hmac_key = (uint8_t *)malloc(hmac_key_len);
    if (!u->hmac_key) { free(u); return NULL; }
    memcpy(u->hmac_key, hmac_key, hmac_key_len);
    u->hmac_key_len = hmac_key_len;
    return u;
}
void ca_dist_delta_updater_destroy(ca_dist_delta_updater_t *u) {
    if (!u) return;
    for (size_t i = 0; i < u->count; ++i) {
        free(u->channels[i].channel);
        free(u->channels[i].version);
    }
    free(u->channels);
    free(u->hmac_key);
    free(u);
}

static channel_slot_t *channel_find(ca_dist_delta_updater_t *u,
                                    const char *channel) {
    for (size_t i = 0; i < u->count; ++i)
        if (cab_ord_eq(u->channels[i].channel, channel))
            return &u->channels[i];
    return NULL;
}

int ca_dist_delta_updater_apply(ca_dist_delta_updater_t *u,
                                const ca_dist_delta_update_t *update,
                                bool *applied) {
    if (applied) *applied = false;
    if (!u || !update || !applied) return -1;
    if (cab_is_ws(update->channel) || cab_is_ws(update->to_version)) {
        *applied = false;
        return 0;
    }
    channel_slot_t *slot = channel_find(u, update->channel);
    if (slot && !cab_ord_eq(slot->version, update->from_version)) {
        *applied = false;
        return 0;
    }

    /* msg = "Channel|FromVersion|ToVersion|" + Payload */
    const char *ch = update->channel;
    const char *fv = update->from_version ? update->from_version : "";
    const char *tv = update->to_version;
    size_t prefix_len = strlen(ch) + 1 + strlen(fv) + 1 + strlen(tv) + 1;
    size_t msg_len = prefix_len + update->payload_len;
    uint8_t *msg = (uint8_t *)malloc(msg_len ? msg_len : 1);
    if (!msg) return -1;
    int off = snprintf((char *)msg, prefix_len + 1, "%s|%s|%s|", ch, fv, tv);
    if (off < 0) { free(msg); return -1; }
    if (update->payload_len)
        memcpy(msg + prefix_len, update->payload, update->payload_len);

    uint8_t expected[32];
    hmac_sha256(u->hmac_key, u->hmac_key_len, msg, msg_len, expected);
    free(msg);

    if (!ct_equal(expected, 32, update->signature, update->signature_len)) {
        *applied = false;
        return 0;
    }

    /* advance channel to ToVersion */
    if (slot) {
        char *nv = cab_strdup(update->to_version);
        if (!nv) return -1;
        free(slot->version);
        slot->version = nv;
    } else {
        if (u->count == u->cap) {
            size_t nc = u->cap ? u->cap * 2 : 4;
            void *n = realloc(u->channels, nc * sizeof(*u->channels));
            if (!n) return -1;
            u->channels = (channel_slot_t *)n;
            u->cap = nc;
        }
        channel_slot_t *ns = &u->channels[u->count];
        ns->channel = cab_strdup(update->channel);
        ns->version = cab_strdup(update->to_version);
        if (!ns->channel || !ns->version) {
            free(ns->channel); free(ns->version);
            return -1;
        }
        u->count++;
    }
    *applied = true;
    return 0;
}

const char *ca_dist_delta_updater_current_version(
    const ca_dist_delta_updater_t *u, const char *channel) {
    if (!u || !channel) return NULL;
    channel_slot_t *slot = channel_find((ca_dist_delta_updater_t *)u, channel);
    return slot ? slot->version : NULL;
}

/* ═══════════════════════ OEM / Carrier catalogues ═════════════════════════ */

static const char *const OEM_PARTNERS[] = {
    "Tecno", "Itel", "Samsung mid-tier", "Xiaomi", "Huawei"
};
static const char *const CARRIERS[] = {
    "MTN", "Vodacom", "Cell C", "Telkom", "Safaricom", "Airtel"
};

const char *const *ca_dist_oem_partners(size_t *out_count) {
    if (out_count) *out_count = sizeof(OEM_PARTNERS) / sizeof(OEM_PARTNERS[0]);
    return OEM_PARTNERS;
}
const char *const *ca_dist_carrier_carriers(size_t *out_count) {
    if (out_count) *out_count = sizeof(CARRIERS) / sizeof(CARRIERS[0]);
    return CARRIERS;
}

/* ═══════════════ DefaultAbusiveEnvironmentMode.SafetyPhrase ════════════════ */

/* FNV-1a 32-bit over the raw UTF-8 bytes — deterministic and identical across
 * all language ports (unlike a host string hash, which .NET randomises per
 * process). uint32_t arithmetic wraps mod 2^32 naturally. */
static uint32_t fnv1a32(const char *s) {
    uint32_t h = 2166136261u; /* FNV offset basis */
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p)
        h = (h ^ (uint32_t)*p) * 16777619u; /* XOR byte, * FNV prime */
    return h;
}

char *ca_dist_abusive_env_safety_phrase(const char *owner_id) {
    if (cab_is_ws(owner_id)) return NULL; /* ArgumentException: ownerId required */

    /* 8-word benign vocabulary; phrase = "the {a} {b} is {c}". */
    static const char *const VOCAB[8] = {
        "thunder", "river", "amber", "field", "rain", "stone", "harbor", "linen"
    };
    uint32_t h = fnv1a32(owner_id);
    const char *a = VOCAB[h & 7u];
    const char *b = VOCAB[(h >> 8) & 7u];
    const char *c = VOCAB[(h >> 16) & 7u];

    size_t len = strlen("the ") + strlen(a) + 1 + strlen(b) +
                 strlen(" is ") + strlen(c) + 1;
    char *out = (char *)malloc(len);
    if (!out) return NULL;
    snprintf(out, len, "the %s %s is %s", a, b, c);
    return out;
}
