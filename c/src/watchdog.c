/*
 * watchdog.c — CircleAI local runtime immune system (C11 port).
 *
 * See watchdog.h. Ports SecurityResponse, SecurityCheckpoint, UhidKeyRing,
 * ISecurityWatchdog + DefaultSecurityWatchdog, IAnomalyEventDispatcher +
 * DefaultAnomalyEventDispatcher, and the RedactedEvidenceJsonConverter helper.
 *
 * In-memory, deterministic, single-threaded. Reuses ca_mr_sha256 (model
 * runtime) for all hashing and ca_uuid_v4 (security.c) for identifiers.
 */

#include "circle_ai/watchdog.h"
#include "circle_ai/model_runtime.h" /* ca_mr_sha256, ca_mr_sha256_hex */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---------------------------------------------------------------------------
 * libc helpers
 * --------------------------------------------------------------------------- */

static char *wd_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static bool wd_blank(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; p++)
        if (*p != ' ' && *p != '\t' && *p != '\n' && *p != '\r' &&
            *p != '\v' && *p != '\f')
            return false;
    return true;
}

/* Shared clock. Mirrors DateTimeOffset.UtcNow at ms resolution. */
extern void ca_uuid_v4(char out_buf[CA_UUID_STR_LEN]); /* from security.c */

#if defined(_WIN32)
#  include <windows.h>
static int64_t wd_now_ms(void) {
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    uint64_t t = ((uint64_t)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
    t -= 116444736000000000ULL;
    return (int64_t)(t / 10000ULL);
}
#else
#  include <time.h>
static int64_t wd_now_ms(void) {
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    return (int64_t)ts.tv_sec * 1000LL + (int64_t)(ts.tv_nsec / 1000000L);
}
#endif

/* Round a fraction in [0,1] to nearest integer percent — matches "{v:P0}". */
static int wd_pct(double v) {
    double p = v * 100.0;
    /* .NET P0 uses round-half-to-even on the banker's default, but for the
     * strings we produce (diagnostic text) round-half-up is indistinguishable
     * in practice and matches the common case. */
    return (int)(p + 0.5);
}

/* ===========================================================================
 * SecurityCheckpoint
 * =========================================================================== */

struct ca_security_checkpoint {
    char    *id;              /* UUID */
    char    *uhid;            /* owned */
    char    *module;          /* owned */
    uint8_t *payload;         /* owned; may be NULL when len==0 */
    size_t   payload_len;
    uint8_t  hash[32];        /* SHA-256 at creation */
    int64_t  created_at_ms;
};

ca_security_checkpoint_t *ca_security_checkpoint_create(
    const char *uhid_identity_id, const char *module_label,
    const uint8_t *payload, size_t payload_len) {
    if (wd_blank(uhid_identity_id) || wd_blank(module_label)) return NULL;
    if (!payload && payload_len != 0) return NULL;

    ca_security_checkpoint_t *cp = (ca_security_checkpoint_t *)calloc(1, sizeof(*cp));
    if (!cp) return NULL;

    char uuid[CA_UUID_STR_LEN];
    ca_uuid_v4(uuid);
    cp->id     = wd_strdup(uuid);
    cp->uhid   = wd_strdup(uhid_identity_id);
    cp->module = wd_strdup(module_label);
    if (!cp->id || !cp->uhid || !cp->module) { ca_security_checkpoint_destroy(cp); return NULL; }

    if (payload_len > 0) {
        cp->payload = (uint8_t *)malloc(payload_len);
        if (!cp->payload) { ca_security_checkpoint_destroy(cp); return NULL; }
        memcpy(cp->payload, payload, payload_len);
    }
    cp->payload_len = payload_len;

    /* SHA-256 over the payload (empty payload hashes the empty message). */
    ca_mr_sha256(cp->payload ? cp->payload : (const uint8_t *)"", cp->payload_len, cp->hash);
    cp->created_at_ms = wd_now_ms();
    return cp;
}

void ca_security_checkpoint_destroy(ca_security_checkpoint_t *cp) {
    if (!cp) return;
    free(cp->id);
    free(cp->uhid);
    free(cp->module);
    free(cp->payload);
    free(cp);
}

ca_security_checkpoint_t *ca_security_checkpoint_copy(
    const ca_security_checkpoint_t *cp) {
    if (!cp) return NULL;
    ca_security_checkpoint_t *dst = (ca_security_checkpoint_t *)calloc(1, sizeof(*dst));
    if (!dst) return NULL;
    dst->id     = wd_strdup(cp->id);
    dst->uhid   = wd_strdup(cp->uhid);
    dst->module = wd_strdup(cp->module);
    if ((cp->id && !dst->id) || (cp->uhid && !dst->uhid) || (cp->module && !dst->module)) {
        ca_security_checkpoint_destroy(dst); return NULL;
    }
    if (cp->payload_len > 0) {
        dst->payload = (uint8_t *)malloc(cp->payload_len);
        if (!dst->payload) { ca_security_checkpoint_destroy(dst); return NULL; }
        memcpy(dst->payload, cp->payload, cp->payload_len);
    }
    dst->payload_len  = cp->payload_len;
    memcpy(dst->hash, cp->hash, 32);
    dst->created_at_ms = cp->created_at_ms;
    return dst;
}

bool ca_security_checkpoint_verify(const ca_security_checkpoint_t *cp) {
    if (!cp) return false;
    uint8_t current[32];
    ca_mr_sha256(cp->payload ? cp->payload : (const uint8_t *)"", cp->payload_len, current);
    /* Constant-time compare (FixedTimeEquals analogue). */
    uint8_t diff = 0;
    for (int i = 0; i < 32; i++) diff |= (uint8_t)(current[i] ^ cp->hash[i]);
    return diff == 0;
}

const char *ca_security_checkpoint_id(const ca_security_checkpoint_t *cp) {
    return cp ? cp->id : NULL;
}
const char *ca_security_checkpoint_uhid(const ca_security_checkpoint_t *cp) {
    return cp ? cp->uhid : NULL;
}
const char *ca_security_checkpoint_module(const ca_security_checkpoint_t *cp) {
    return cp ? cp->module : NULL;
}
const uint8_t *ca_security_checkpoint_payload(const ca_security_checkpoint_t *cp,
                                              size_t *out_len) {
    if (out_len) *out_len = cp ? cp->payload_len : 0;
    return cp ? cp->payload : NULL;
}
const uint8_t *ca_security_checkpoint_payload_hash(
    const ca_security_checkpoint_t *cp) {
    return cp ? cp->hash : NULL;
}
int64_t ca_security_checkpoint_created_at_ms(const ca_security_checkpoint_t *cp) {
    return cp ? cp->created_at_ms : 0;
}

int ca_security_checkpoint_to_string(const ca_security_checkpoint_t *cp,
                                     char *buf, size_t buf_size) {
    if (!buf || buf_size == 0) return -1;
    if (!cp) { buf[0] = '\0'; return 0; }

    /* First 8 hash bytes as UPPER hex (Convert.ToHexString default), or the
     * "(empty)" sentinel when the payload is shorter than 8 bytes hashed —
     * the C# guard checks PayloadHash.Length >= 8, which is always true for
     * SHA-256, so we always emit the 16-hex prefix. */
    char hp[17];
    static const char *HEX = "0123456789ABCDEF";
    for (int i = 0; i < 8; i++) {
        hp[i * 2]     = HEX[(cp->hash[i] >> 4) & 0xF];
        hp[i * 2 + 1] = HEX[cp->hash[i] & 0xF];
    }
    hp[16] = '\0';

    int n = snprintf(buf, buf_size,
        "SecurityCheckpoint(Id=%s, Module=%s, Uhid=%s, PayloadSha256=%s..., "
        "PayloadBytes=%zu, CreatedAt=%lld)",
        cp->id ? cp->id : "", cp->module ? cp->module : "",
        cp->uhid ? cp->uhid : "", hp, cp->payload_len,
        (long long)cp->created_at_ms);
    if (n < 0) { buf[0] = '\0'; return 0; }
    if ((size_t)n >= buf_size) return (int)(buf_size - 1);
    return n;
}

/* ===========================================================================
 * SecurityResponse
 * =========================================================================== */

struct ca_security_response {
    char                        *signal_id;      /* owned */
    ca_security_response_kind_t  kind;
    ca_security_response_kind_t *applied;        /* owned array */
    size_t                       applied_count;
    char                        *description;    /* owned */
    ca_security_checkpoint_t    *restored;       /* owned; may be NULL */
    int64_t                      responded_at_ms;
};

static ca_security_response_t *response_new(
    const char *signal_id, ca_security_response_kind_t kind,
    const ca_security_response_kind_t *actions, size_t action_count,
    const char *description, const ca_security_checkpoint_t *restored) {
    ca_security_response_t *r = (ca_security_response_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->signal_id   = wd_strdup(signal_id);
    r->description = wd_strdup(description);
    if ((signal_id && !r->signal_id) || (description && !r->description)) {
        ca_security_response_destroy(r); return NULL;
    }
    r->kind = kind;
    if (action_count > 0 && actions) {
        r->applied = (ca_security_response_kind_t *)malloc(
            action_count * sizeof(*r->applied));
        if (!r->applied) { ca_security_response_destroy(r); return NULL; }
        memcpy(r->applied, actions, action_count * sizeof(*r->applied));
        r->applied_count = action_count;
    }
    if (restored) {
        r->restored = ca_security_checkpoint_copy(restored);
        if (!r->restored) { ca_security_response_destroy(r); return NULL; }
    }
    r->responded_at_ms = wd_now_ms();
    return r;
}

ca_security_response_t *ca_security_response_no_action(
    const char *signal_id, const char *reason) {
    return response_new(signal_id, CA_RESPONSE_NO_ACTION, NULL, 0, reason, NULL);
}

ca_security_response_t *ca_security_response_for_key_rotation(
    const char *signal_id, const char *description) {
    return response_new(signal_id, CA_RESPONSE_KEY_ROTATION, NULL, 0,
                        description, NULL);
}

ca_security_response_t *ca_security_response_for_rollback(
    const char *signal_id, const ca_security_checkpoint_t *restored) {
    if (!restored) return NULL;
    char desc[128 + CA_UUID_STR_LEN];
    snprintf(desc, sizeof(desc), "State rolled back to checkpoint %s (%s).",
             ca_security_checkpoint_id(restored),
             ca_security_checkpoint_module(restored));
    return response_new(signal_id, CA_RESPONSE_STATE_ROLLBACK, NULL, 0, desc,
                        restored);
}

ca_security_response_t *ca_security_response_composite(
    const char *signal_id, const ca_security_response_kind_t *actions,
    size_t action_count, const char *description,
    const ca_security_checkpoint_t *restored_checkpoint) {
    return response_new(signal_id, CA_RESPONSE_COMPOSITE, actions, action_count,
                        description, restored_checkpoint);
}

void ca_security_response_destroy(ca_security_response_t *r) {
    if (!r) return;
    free(r->signal_id);
    free(r->applied);
    free(r->description);
    ca_security_checkpoint_destroy(r->restored);
    free(r);
}

ca_security_response_t *ca_security_response_copy(const ca_security_response_t *r) {
    if (!r) return NULL;
    return response_new(r->signal_id, r->kind, r->applied, r->applied_count,
                        r->description, r->restored);
}

const char *ca_security_response_signal_id(const ca_security_response_t *r) {
    return r ? r->signal_id : NULL;
}
ca_security_response_kind_t ca_security_response_kind(
    const ca_security_response_t *r) {
    return r ? r->kind : CA_RESPONSE_NO_ACTION;
}
const ca_security_response_kind_t *ca_security_response_applied_actions(
    const ca_security_response_t *r, size_t *out_count) {
    if (out_count) *out_count = r ? r->applied_count : 0;
    return r ? r->applied : NULL;
}
const char *ca_security_response_description(const ca_security_response_t *r) {
    return r ? r->description : NULL;
}
const ca_security_checkpoint_t *ca_security_response_restored_checkpoint(
    const ca_security_response_t *r) {
    return r ? r->restored : NULL;
}
int64_t ca_security_response_responded_at_ms(const ca_security_response_t *r) {
    return r ? r->responded_at_ms : 0;
}

/* ===========================================================================
 * UhidKeyRing — deterministic HMAC-SHA256 signing (contract-parity for ECDSA).
 *
 * A ring holds a 32-byte random secret + a 32-byte public "key" derived as
 * SHA-256("uhid-keyring-public" || secret). Signing = HMAC-SHA256(secret, data)
 * where HMAC is built on the ca_mr_sha256 primitive. This is fully in-memory,
 * deterministic per secret, and preserves every observable behaviour of the
 * C# ring (rotate/revoke/verify-after-revoke/public-key export).
 * =========================================================================== */

#define UHID_SECRET_LEN 32
#define UHID_PUBLIC_LEN 32

struct ca_uhid_key_ring {
    char    *id;       /* UUID; changes on regenerate */
    char    *uhid;     /* owned */
    bool     revoked;
    int64_t  generated_at_ms;
    int64_t  revoked_at_ms;               /* -1 when active */
    uint8_t  secret[UHID_SECRET_LEN];
    uint8_t  public_key[UHID_PUBLIC_LEN];
};

/* HMAC-SHA256 over the ca_mr_sha256 primitive (RFC 2104, block size 64). */
static void uhid_hmac_sha256(const uint8_t *key, size_t key_len,
                             const uint8_t *msg, size_t msg_len,
                             uint8_t out[32]) {
    uint8_t k0[64];
    memset(k0, 0, sizeof(k0));
    if (key_len > 64) {
        ca_mr_sha256(key, key_len, k0); /* keys > block: hash first */
    } else {
        memcpy(k0, key, key_len);
    }

    uint8_t ipad[64], opad[64];
    for (int i = 0; i < 64; i++) {
        ipad[i] = (uint8_t)(k0[i] ^ 0x36);
        opad[i] = (uint8_t)(k0[i] ^ 0x5c);
    }

    /* inner = SHA256(ipad || msg) */
    uint8_t *inner_buf = (uint8_t *)malloc(64 + msg_len);
    uint8_t inner[32];
    if (inner_buf) {
        memcpy(inner_buf, ipad, 64);
        if (msg_len) memcpy(inner_buf + 64, msg, msg_len);
        ca_mr_sha256(inner_buf, 64 + msg_len, inner);
        free(inner_buf);
    } else {
        /* Degenerate OOM path: hash ipad alone so we still produce output. */
        ca_mr_sha256(ipad, 64, inner);
    }

    /* out = SHA256(opad || inner) */
    uint8_t outer_buf[64 + 32];
    memcpy(outer_buf, opad, 64);
    memcpy(outer_buf + 64, inner, 32);
    ca_mr_sha256(outer_buf, sizeof(outer_buf), out);
}

/* (Re)seed secret/public/id/timestamps — RegenerateKey analogue. */
static bool uhid_regenerate(ca_uhid_key_ring_t *ring) {
    /* Reuse the platform RNG via ca_uuid_v4 as an entropy carrier: derive a
     * fresh secret from two UUIDs hashed together. UUID v4 draws from
     * BCryptGenRandom / /dev/urandom, so this inherits crypto-grade entropy on
     * supported targets and stays deterministic-per-run on exotic ones. */
    char u1[CA_UUID_STR_LEN], u2[CA_UUID_STR_LEN];
    ca_uuid_v4(u1);
    ca_uuid_v4(u2);
    uint8_t seed[CA_UUID_STR_LEN * 2];
    memcpy(seed, u1, CA_UUID_STR_LEN);
    memcpy(seed + CA_UUID_STR_LEN, u2, CA_UUID_STR_LEN);
    ca_mr_sha256(seed, sizeof(seed), ring->secret);

    /* public = SHA256("uhid-keyring-public" || secret) */
    static const char TAG[] = "uhid-keyring-public";
    uint8_t pbuf[sizeof(TAG) - 1 + UHID_SECRET_LEN];
    memcpy(pbuf, TAG, sizeof(TAG) - 1);
    memcpy(pbuf + sizeof(TAG) - 1, ring->secret, UHID_SECRET_LEN);
    ca_mr_sha256(pbuf, sizeof(pbuf), ring->public_key);

    char uuid[CA_UUID_STR_LEN];
    ca_uuid_v4(uuid);
    char *new_id = wd_strdup(uuid);
    if (!new_id) return false;
    free(ring->id);
    ring->id = new_id;

    ring->generated_at_ms = wd_now_ms();
    ring->revoked_at_ms   = -1;
    ring->revoked         = false;
    return true;
}

ca_uhid_key_ring_t *ca_uhid_key_ring_generate_fresh(const char *uhid_identity_id) {
    if (wd_blank(uhid_identity_id)) return NULL;
    ca_uhid_key_ring_t *ring = (ca_uhid_key_ring_t *)calloc(1, sizeof(*ring));
    if (!ring) return NULL;
    ring->uhid = wd_strdup(uhid_identity_id);
    if (!ring->uhid) { free(ring); return NULL; }
    if (!uhid_regenerate(ring)) { free(ring->uhid); free(ring); return NULL; }
    return ring;
}

void ca_uhid_key_ring_destroy(ca_uhid_key_ring_t *ring) {
    if (!ring) return;
    /* Best-effort secret wipe. */
    memset(ring->secret, 0, sizeof(ring->secret));
    free(ring->id);
    free(ring->uhid);
    free(ring);
}

ca_uhid_key_ring_t *ca_uhid_key_ring_rotate(ca_uhid_key_ring_t *ring) {
    if (!ring) return NULL;
    ca_uhid_key_ring_revoke(ring);
    return ca_uhid_key_ring_generate_fresh(ring->uhid);
}

void ca_uhid_key_ring_revoke(ca_uhid_key_ring_t *ring) {
    if (!ring || ring->revoked) return;
    ring->revoked       = true;
    ring->revoked_at_ms = wd_now_ms();
}

int ca_uhid_key_ring_sign(ca_uhid_key_ring_t *ring,
                          const uint8_t *data, size_t len, uint8_t out_sig[32]) {
    if (!ring || !out_sig || (!data && len != 0)) return -1;
    if (ring->revoked) return -2;
    uhid_hmac_sha256(ring->secret, UHID_SECRET_LEN,
                     data ? data : (const uint8_t *)"", len, out_sig);
    return 0;
}

bool ca_uhid_key_ring_verify(const ca_uhid_key_ring_t *ring,
                             const uint8_t *data, size_t len,
                             const uint8_t *signature, size_t sig_len) {
    if (!ring || !signature || sig_len != 32 || (!data && len != 0)) return false;
    uint8_t expected[32];
    uhid_hmac_sha256(ring->secret, UHID_SECRET_LEN,
                     data ? data : (const uint8_t *)"", len, expected);
    uint8_t diff = 0;
    for (int i = 0; i < 32; i++) diff |= (uint8_t)(expected[i] ^ signature[i]);
    return diff == 0;
}

const char *ca_uhid_key_ring_id(const ca_uhid_key_ring_t *ring) {
    return ring ? ring->id : NULL;
}
const char *ca_uhid_key_ring_uhid(const ca_uhid_key_ring_t *ring) {
    return ring ? ring->uhid : NULL;
}
bool ca_uhid_key_ring_is_revoked(const ca_uhid_key_ring_t *ring) {
    return ring ? ring->revoked : true;
}
int64_t ca_uhid_key_ring_generated_at_ms(const ca_uhid_key_ring_t *ring) {
    return ring ? ring->generated_at_ms : 0;
}
int64_t ca_uhid_key_ring_revoked_at_ms(const ca_uhid_key_ring_t *ring) {
    return ring ? ring->revoked_at_ms : -1;
}
const uint8_t *ca_uhid_key_ring_public_key(const ca_uhid_key_ring_t *ring,
                                           size_t *out_len) {
    if (out_len) *out_len = ring ? UHID_PUBLIC_LEN : 0;
    return ring ? ring->public_key : NULL;
}

/* ===========================================================================
 * RedactedEvidenceJsonConverter
 * =========================================================================== */

int ca_redacted_evidence_value(const char *raw, char *buf, size_t buf_size) {
    if (!buf || buf_size < CA_REDACTED_VALUE_LEN) return -1;
    if (!raw || raw[0] == '\0') {
        memcpy(buf, "sha256:", 8); /* includes NUL */
        return 7;
    }
    uint8_t digest[32];
    ca_mr_sha256((const uint8_t *)raw, strlen(raw), digest);
    char hex[65];
    ca_mr_sha256_hex(digest, hex); /* already lowercase */
    memcpy(buf, "sha256:", 7);
    memcpy(buf + 7, hex, 65); /* 64 hex + NUL */
    return 7 + 64;
}

/* Append s to *buf (dynamic), growing as needed. Returns false on OOM. */
static bool sb_append(char **buf, size_t *len, size_t *cap, const char *s) {
    size_t sl = strlen(s);
    if (*len + sl + 1 > *cap) {
        size_t ncap = (*cap == 0) ? 64 : *cap;
        while (*len + sl + 1 > ncap) ncap *= 2;
        char *nb = (char *)realloc(*buf, ncap);
        if (!nb) return false;
        *buf = nb;
        *cap = ncap;
    }
    memcpy(*buf + *len, s, sl + 1);
    *len += sl;
    return true;
}

/* Append a JSON-escaped string literal (with surrounding quotes). */
static bool sb_append_json_str(char **buf, size_t *len, size_t *cap, const char *s) {
    if (!sb_append(buf, len, cap, "\"")) return false;
    char esc[8];
    for (const unsigned char *p = (const unsigned char *)s; *p; p++) {
        switch (*p) {
            case '"':  if (!sb_append(buf, len, cap, "\\\"")) return false; break;
            case '\\': if (!sb_append(buf, len, cap, "\\\\")) return false; break;
            case '\b': if (!sb_append(buf, len, cap, "\\b"))  return false; break;
            case '\f': if (!sb_append(buf, len, cap, "\\f"))  return false; break;
            case '\n': if (!sb_append(buf, len, cap, "\\n"))  return false; break;
            case '\r': if (!sb_append(buf, len, cap, "\\r"))  return false; break;
            case '\t': if (!sb_append(buf, len, cap, "\\t"))  return false; break;
            default:
                if (*p < 0x20) {
                    snprintf(esc, sizeof(esc), "\\u%04x", *p);
                    if (!sb_append(buf, len, cap, esc)) return false;
                } else {
                    char one[2] = { (char)*p, '\0' };
                    if (!sb_append(buf, len, cap, one)) return false;
                }
        }
    }
    return sb_append(buf, len, cap, "\"");
}

char *ca_redacted_evidence_to_json(const char *const *keys,
                                   const char *const *values, size_t count) {
    char  *buf = NULL;
    size_t len = 0, cap = 0;
    if (!sb_append(&buf, &len, &cap, "{")) { free(buf); return NULL; }
    for (size_t i = 0; i < count; i++) {
        if (i > 0 && !sb_append(&buf, &len, &cap, ",")) { free(buf); return NULL; }
        const char *k = keys && keys[i] ? keys[i] : "";
        if (!sb_append_json_str(&buf, &len, &cap, k)) { free(buf); return NULL; }
        if (!sb_append(&buf, &len, &cap, ":")) { free(buf); return NULL; }
        char red[CA_REDACTED_VALUE_LEN];
        ca_redacted_evidence_value(values ? values[i] : NULL, red, sizeof(red));
        if (!sb_append_json_str(&buf, &len, &cap, red)) { free(buf); return NULL; }
    }
    if (!sb_append(&buf, &len, &cap, "}")) { free(buf); return NULL; }
    return buf;
}

/* ===========================================================================
 * DefaultSecurityWatchdog
 * =========================================================================== */

/* Unbounded growable signal log (mirrors unbounded Channel<AnomalySignal>). */
struct ca_default_security_watchdog {
    ca_anomaly_signal_t *log;   /* dynamic array of every signal, in order */
    size_t               count;
    size_t               cap;
};

struct ca_watchdog_signal_reader {
    const ca_default_security_watchdog_t *w;
    size_t                                cursor;
};

ca_default_security_watchdog_t *ca_default_security_watchdog_create(void) {
    return (ca_default_security_watchdog_t *)calloc(1, sizeof(ca_default_security_watchdog_t));
}

void ca_default_security_watchdog_destroy(ca_default_security_watchdog_t *w) {
    if (!w) return;
    free(w->log);
    free(w);
}

static bool wd_log_push(ca_default_security_watchdog_t *w,
                        const ca_anomaly_signal_t *signal) {
    if (w->count == w->cap) {
        size_t ncap = w->cap == 0 ? 8 : w->cap * 2;
        ca_anomaly_signal_t *nl = (ca_anomaly_signal_t *)realloc(
            w->log, ncap * sizeof(*nl));
        if (!nl) return false;
        w->log = nl;
        w->cap = ncap;
    }
    w->log[w->count++] = *signal; /* value copy — signal is a flat struct */
    return true;
}

#define WD_ROTATION_THRESHOLD  0.30
#define WD_COMPOSITE_THRESHOLD 0.60

ca_security_response_t *ca_default_security_watchdog_on_anomaly(
    ca_default_security_watchdog_t *w,
    const ca_anomaly_signal_t      *signal,
    const ca_security_checkpoint_t *checkpoint) {
    if (!w || !signal) return NULL;

    /* Broadcast to stream readers first (WriteAsync). */
    if (!wd_log_push(w, signal)) return NULL;

    double conf = (double)signal->confidence;

    char desc[CA_DESC_LEN + CA_MODULE_NAME_LEN + 128];

    if (conf < WD_ROTATION_THRESHOLD) {
        snprintf(desc, sizeof(desc),
            "Confidence %d%% below rotation threshold — monitoring only.",
            wd_pct(conf));
        return ca_security_response_no_action(signal->id, desc);
    }

    bool high_severity =
        signal->vector == CA_THREAT_CONTROL_FLOW_DRIFT ||
        signal->vector == CA_THREAT_PRIVILEGE_ESCALATION ||
        signal->vector == CA_THREAT_NETWORK_PIVOT ||
        signal->vector == CA_THREAT_STATE_CORRUPTION;

    if (conf > WD_COMPOSITE_THRESHOLD) {
        ca_security_response_kind_t actions[3];
        size_t n = 0;
        actions[n++] = CA_RESPONSE_KEY_ROTATION;
        actions[n++] = CA_RESPONSE_MESH_ISOLATION_SIGNAL;

        const ca_security_checkpoint_t *restored = NULL;
        if (checkpoint && high_severity && ca_security_checkpoint_verify(checkpoint)) {
            actions[n++] = CA_RESPONSE_STATE_ROLLBACK;
            restored = checkpoint;
        }
        snprintf(desc, sizeof(desc),
            "Composite response for vector %d (confidence %d%%) in %s.",
            (int)signal->vector, wd_pct(conf), signal->affected_module);
        return ca_security_response_composite(signal->id, actions, n, desc, restored);
    }

    snprintf(desc, sizeof(desc),
        "Key rotation triggered for vector %d (confidence %d%%) in %s.",
        (int)signal->vector, wd_pct(conf), signal->affected_module);
    return ca_security_response_for_key_rotation(signal->id, desc);
}

/* vtable adapter */
static ca_security_response_t *wd_iface_on_anomaly(
    void *self, const ca_anomaly_signal_t *signal,
    const ca_security_checkpoint_t *checkpoint) {
    return ca_default_security_watchdog_on_anomaly(
        (ca_default_security_watchdog_t *)self, signal, checkpoint);
}

ca_security_watchdog_t ca_default_security_watchdog_as_interface(
    ca_default_security_watchdog_t *w) {
    ca_security_watchdog_t v;
    v.self = w;
    v.on_anomaly_detected = wd_iface_on_anomaly;
    return v;
}

ca_watchdog_signal_reader_t *ca_default_security_watchdog_stream(
    ca_default_security_watchdog_t *w) {
    if (!w) return NULL;
    ca_watchdog_signal_reader_t *r =
        (ca_watchdog_signal_reader_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->w = w;
    r->cursor = 0; /* replays the full log from the start */
    return r;
}

void ca_watchdog_signal_reader_destroy(ca_watchdog_signal_reader_t *r) {
    free(r);
}

bool ca_watchdog_signal_reader_next(ca_watchdog_signal_reader_t *r,
                                    ca_anomaly_signal_t *out) {
    if (!r || !out || !r->w) return false;
    if (r->cursor >= r->w->count) return false;
    *out = r->w->log[r->cursor++];
    return true;
}

size_t ca_default_security_watchdog_signal_count(
    const ca_default_security_watchdog_t *w) {
    return w ? w->count : 0;
}

/* ===========================================================================
 * DefaultAnomalyEventDispatcher
 * =========================================================================== */

struct ca_default_anomaly_dispatcher {
    ca_security_watchdog_t watchdog;
    double                 min_confidence;
    char                 **seen;   /* owned array of signal-id strings */
    size_t                 seen_count;
    size_t                 seen_cap;
};

static double wd_clamp01(double v) {
    if (v < 0.0) return 0.0;
    if (v > 1.0) return 1.0;
    return v;
}

ca_default_anomaly_dispatcher_t *ca_default_anomaly_dispatcher_create(
    ca_security_watchdog_t watchdog, double minimum_confidence) {
    ca_default_anomaly_dispatcher_t *d =
        (ca_default_anomaly_dispatcher_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->watchdog = watchdog;
    d->min_confidence = wd_clamp01(minimum_confidence);
    return d;
}

void ca_default_anomaly_dispatcher_destroy(ca_default_anomaly_dispatcher_t *d) {
    if (!d) return;
    for (size_t i = 0; i < d->seen_count; i++) free(d->seen[i]);
    free(d->seen);
    free(d);
}

/* TryAdd analogue: returns true if newly added, false if id already present. */
static bool dispatcher_try_add(ca_default_anomaly_dispatcher_t *d, const char *id) {
    for (size_t i = 0; i < d->seen_count; i++)
        if (strcmp(d->seen[i], id) == 0) return false;
    if (d->seen_count == d->seen_cap) {
        size_t ncap = d->seen_cap == 0 ? 8 : d->seen_cap * 2;
        char **ns = (char **)realloc(d->seen, ncap * sizeof(*ns));
        if (!ns) return false; /* OOM: treat as not-added upstream */
        d->seen = ns;
        d->seen_cap = ncap;
    }
    char *dup = wd_strdup(id);
    if (!dup) return false;
    d->seen[d->seen_count++] = dup;
    return true;
}

ca_anomaly_dispatch_outcome_t ca_default_anomaly_dispatcher_dispatch(
    ca_default_anomaly_dispatcher_t *d,
    const ca_anomaly_signal_t       *signal,
    const ca_security_checkpoint_t  *checkpoint,
    bool                             cancelled,
    ca_security_response_t         **out_response) {
    if (out_response) *out_response = NULL;
    if (!d || !signal) return CA_DISPATCH_UNVERIFIED;

    if (cancelled) return CA_DISPATCH_CANCELLED;

    if ((double)signal->confidence < d->min_confidence)
        return CA_DISPATCH_BELOW_THRESHOLD;

    /* Distinguish duplicate from OOM: pre-check membership. */
    for (size_t i = 0; i < d->seen_count; i++)
        if (strcmp(d->seen[i], signal->id) == 0) return CA_DISPATCH_DUPLICATE;

    if (!dispatcher_try_add(d, signal->id))
        return CA_DISPATCH_UNVERIFIED; /* OOM */

    if (!d->watchdog.on_anomaly_detected)
        return CA_DISPATCH_UNVERIFIED;

    ca_security_response_t *resp =
        d->watchdog.on_anomaly_detected(d->watchdog.self, signal, checkpoint);
    if (!resp) return CA_DISPATCH_UNVERIFIED;

    if (out_response) *out_response = resp;
    else ca_security_response_destroy(resp);
    return CA_DISPATCH_DISPATCHED;
}
