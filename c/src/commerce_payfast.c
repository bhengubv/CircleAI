/*
 * commerce_payfast.c — CircleAI.Commerce.Integration.PayFast (C11 port of
 * PayFastPrimitives.cs).
 *
 * InMemoryPayFastBoard: a deep-copied config + an appended webhook list.
 * SignatureFor reproduces System.Net.WebUtility.UrlEncode + .Replace("%20","+")
 * byte-for-byte, then MD5s the pre-hash and returns lowercase hex. A small
 * self-contained RFC 1321 MD5 is included (no OpenSSL / platform crypto).
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/commerce_payfast.h"
#include "board_common.h"

#include <stdio.h>

/* ===========================================================================
 * MD5 (RFC 1321) — minimal, self-contained, little-endian-safe.
 * =========================================================================== */

typedef struct {
    uint32_t a, b, c, d;
    uint64_t len_bits;
    uint8_t  buf[64];
    size_t   buf_len;
} md5_ctx_t;

static uint32_t md5_rotl(uint32_t x, int c) { return (x << c) | (x >> (32 - c)); }

static void md5_block(md5_ctx_t *ctx, const uint8_t *p) {
    static const uint32_t K[64] = {
        0xd76aa478,0xe8c7b756,0x242070db,0xc1bdceee,0xf57c0faf,0x4787c62a,
        0xa8304613,0xfd469501,0x698098d8,0x8b44f7af,0xffff5bb1,0x895cd7be,
        0x6b901122,0xfd987193,0xa679438e,0x49b40821,0xf61e2562,0xc040b340,
        0x265e5a51,0xe9b6c7aa,0xd62f105d,0x02441453,0xd8a1e681,0xe7d3fbc8,
        0x21e1cde6,0xc33707d6,0xf4d50d87,0x455a14ed,0xa9e3e905,0xfcefa3f8,
        0x676f02d9,0x8d2a4c8a,0xfffa3942,0x8771f681,0x6d9d6122,0xfde5380c,
        0xa4beea44,0x4bdecfa9,0xf6bb4b60,0xbebfbc70,0x289b7ec6,0xeaa127fa,
        0xd4ef3085,0x04881d05,0xd9d4d039,0xe6db99e5,0x1fa27cf8,0xc4ac5665,
        0xf4292244,0x432aff97,0xab9423a7,0xfc93a039,0x655b59c3,0x8f0ccc92,
        0xffeff47d,0x85845dd1,0x6fa87e4f,0xfe2ce6e0,0xa3014314,0x4e0811a1,
        0xf7537e82,0xbd3af235,0x2ad7d2bb,0xeb86d391 };
    static const int S[64] = {
        7,12,17,22, 7,12,17,22, 7,12,17,22, 7,12,17,22,
        5, 9,14,20, 5, 9,14,20, 5, 9,14,20, 5, 9,14,20,
        4,11,16,23, 4,11,16,23, 4,11,16,23, 4,11,16,23,
        6,10,15,21, 6,10,15,21, 6,10,15,21, 6,10,15,21 };

    uint32_t M[16];
    for (int i = 0; i < 16; ++i)
        M[i] = (uint32_t)p[i*4] | ((uint32_t)p[i*4+1] << 8) |
               ((uint32_t)p[i*4+2] << 16) | ((uint32_t)p[i*4+3] << 24);

    uint32_t A = ctx->a, B = ctx->b, C = ctx->c, D = ctx->d;
    for (int i = 0; i < 64; ++i) {
        uint32_t F;
        int g;
        if (i < 16)      { F = (B & C) | (~B & D);          g = i; }
        else if (i < 32) { F = (D & B) | (~D & C);          g = (5*i + 1) & 15; }
        else if (i < 48) { F = B ^ C ^ D;                    g = (3*i + 5) & 15; }
        else             { F = C ^ (B | ~D);                 g = (7*i) & 15; }
        uint32_t tmp = D;
        D = C;
        C = B;
        B = B + md5_rotl(A + F + K[i] + M[g], S[i]);
        A = tmp;
    }
    ctx->a += A; ctx->b += B; ctx->c += C; ctx->d += D;
}

static void md5_init(md5_ctx_t *ctx) {
    ctx->a = 0x67452301; ctx->b = 0xefcdab89;
    ctx->c = 0x98badcfe; ctx->d = 0x10325476;
    ctx->len_bits = 0;
    ctx->buf_len = 0;
}

static void md5_update(md5_ctx_t *ctx, const uint8_t *data, size_t len) {
    ctx->len_bits += (uint64_t)len * 8;
    while (len > 0) {
        size_t take = 64 - ctx->buf_len;
        if (take > len) take = len;
        memcpy(ctx->buf + ctx->buf_len, data, take);
        ctx->buf_len += take;
        data += take;
        len  -= take;
        if (ctx->buf_len == 64) {
            md5_block(ctx, ctx->buf);
            ctx->buf_len = 0;
        }
    }
}

static void md5_final(md5_ctx_t *ctx, uint8_t out[16]) {
    uint64_t bits = ctx->len_bits;
    uint8_t pad = 0x80;
    md5_update(ctx, &pad, 1);
    uint8_t zero = 0x00;
    while (ctx->buf_len != 56) md5_update(ctx, &zero, 1);
    uint8_t lenbytes[8];
    for (int i = 0; i < 8; ++i) lenbytes[i] = (uint8_t)(bits >> (8 * i));
    md5_update(ctx, lenbytes, 8);
    uint32_t v[4] = { ctx->a, ctx->b, ctx->c, ctx->d };
    for (int i = 0; i < 4; ++i) {
        out[i*4]   = (uint8_t)(v[i]);
        out[i*4+1] = (uint8_t)(v[i] >> 8);
        out[i*4+2] = (uint8_t)(v[i] >> 16);
        out[i*4+3] = (uint8_t)(v[i] >> 24);
    }
}

/* ===========================================================================
 * WebUtility.UrlEncode (+ .Replace("%20","+")) — append the encoding of `s` to
 * a growable byte buffer. Unreserved set: A-Z a-z 0-9 ! ( ) * - . _ pass through,
 * space -> '+', every other byte -> %XX (UPPERCASE). Operates on raw UTF-8 bytes.
 * =========================================================================== */

typedef struct { char *p; size_t len, cap; } strbuf_t;

static bool sb_reserve(strbuf_t *b, size_t extra) {
    if (b->len + extra + 1 <= b->cap) return true;
    size_t nc = b->cap ? b->cap * 2 : 64;
    while (nc < b->len + extra + 1) nc *= 2;
    char *n = (char *)realloc(b->p, nc);
    if (!n) return false;
    b->p = n;
    b->cap = nc;
    return true;
}
static bool sb_putc(strbuf_t *b, char c) {
    if (!sb_reserve(b, 1)) return false;
    b->p[b->len++] = c;
    b->p[b->len] = '\0';
    return true;
}
static bool sb_puts(strbuf_t *b, const char *s) {
    for (; *s; ++s) if (!sb_putc(b, *s)) return false;
    return true;
}

static bool pf_is_unreserved(unsigned char c) {
    if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
        return true;
    switch (c) {
        case '!': case '(': case ')': case '*':
        case '-': case '.': case '_':
            return true;
        default:
            return false;
    }
}

static bool pf_url_encode_append(strbuf_t *b, const char *s) {
    static const char HEX[] = "0123456789ABCDEF";
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p) {
        unsigned char c = *p;
        if (c == ' ') {            /* space -> '+' (and .Replace("%20","+") is a no-op) */
            if (!sb_putc(b, '+')) return false;
        } else if (pf_is_unreserved(c)) {
            if (!sb_putc(b, (char)c)) return false;
        } else {
            char esc[3] = { '%', HEX[(c >> 4) & 0xF], HEX[c & 0xF] };
            if (!sb_putc(b, esc[0]) || !sb_putc(b, esc[1]) || !sb_putc(b, esc[2]))
                return false;
        }
    }
    return true;
}

/* ===========================================================================
 * Records
 * =========================================================================== */

void ca_payfast_config_free(ca_payfast_config_t *c) {
    if (!c) return;
    free(c->merchant_id);
    free(c->merchant_key);
    free(c->passphrase);
    c->merchant_id = c->merchant_key = c->passphrase = NULL;
}

static bool config_copy(ca_payfast_config_t *dst,
                        const ca_payfast_config_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->merchant_id  = cab_strdup_empty(src->merchant_id);
    dst->merchant_key = cab_strdup_empty(src->merchant_key);
    dst->passphrase   = cab_strdup_empty(src->passphrase);
    dst->sandbox      = src->sandbox;
    if (!dst->merchant_id || !dst->merchant_key || !dst->passphrase) {
        ca_payfast_config_free(dst);
        return false;
    }
    return true;
}

void ca_payfast_itn_free(ca_payfast_itn_t *p) {
    if (!p) return;
    free(p->merchant_id);
    free(p->payment_id);
    free(p->payment_status);
    free(p->m_payment_id);
    free(p->signature);
    p->merchant_id = p->payment_id = p->payment_status = NULL;
    p->m_payment_id = p->signature = NULL;
}
void ca_payfast_itn_free_array(ca_payfast_itn_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_payfast_itn_free(&arr[i]);
    free(arr);
}

static bool itn_copy(ca_payfast_itn_t *dst, const ca_payfast_itn_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->merchant_id    = cab_strdup_empty(src->merchant_id);
    dst->payment_id     = cab_strdup_empty(src->payment_id);
    dst->payment_status = cab_strdup_empty(src->payment_status);
    dst->m_payment_id   = cab_strdup_empty(src->m_payment_id);
    dst->signature      = cab_strdup_empty(src->signature);
    dst->amount         = src->amount;
    if (!dst->merchant_id || !dst->payment_id || !dst->payment_status ||
        !dst->m_payment_id || !dst->signature) {
        ca_payfast_itn_free(dst);
        return false;
    }
    return true;
}

/* ===========================================================================
 * Board
 * =========================================================================== */

struct ca_payfast_board {
    ca_payfast_config_t config;
    ca_payfast_itn_t   *webhooks;
    size_t              count, cap;
};

ca_payfast_board_t *ca_payfast_board_create(const ca_payfast_config_t *config) {
    if (!config) return NULL;   /* ArgumentNullException(cfg) */
    ca_payfast_board_t *b = (ca_payfast_board_t *)calloc(1, sizeof(*b));
    if (!b) return NULL;
    if (!config_copy(&b->config, config)) { free(b); return NULL; }
    return b;
}
void ca_payfast_board_destroy(ca_payfast_board_t *b) {
    if (!b) return;
    ca_payfast_config_free(&b->config);
    for (size_t i = 0; i < b->count; ++i) ca_payfast_itn_free(&b->webhooks[i]);
    free(b->webhooks);
    free(b);
}

bool ca_payfast_board_config(const ca_payfast_board_t *b,
                             ca_payfast_config_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !out) return false;
    return config_copy(out, &b->config);
}

char *ca_payfast_board_signature_for(const ca_payfast_board_t *b,
                                     const ca_payfast_field_t *fields,
                                     size_t count) {
    if (!b) return NULL;
    if (count > 0 && !fields) return NULL;

    strbuf_t sb = { NULL, 0, 0 };
    if (!sb_reserve(&sb, 0)) return NULL;   /* allocate the initial NUL slot */

    for (size_t i = 0; i < count; ++i) {
        const char *k = fields[i].key ? fields[i].key : "";
        const char *v = fields[i].value ? fields[i].value : "";
        if (!sb_puts(&sb, k) || !sb_putc(&sb, '=') ||
            !pf_url_encode_append(&sb, v) || !sb_putc(&sb, '&')) {
            free(sb.p);
            return NULL;
        }
    }

    if (b->config.passphrase[0] != '\0') {
        if (!sb_puts(&sb, "passphrase=") ||
            !pf_url_encode_append(&sb, b->config.passphrase)) {
            free(sb.p);
            return NULL;
        }
    } else if (sb.len > 0 && sb.p[sb.len - 1] == '&') {
        sb.p[--sb.len] = '\0';   /* strip trailing '&' */
    }

    md5_ctx_t ctx;
    md5_init(&ctx);
    md5_update(&ctx, (const uint8_t *)sb.p, sb.len);
    uint8_t digest[16];
    md5_final(&ctx, digest);
    free(sb.p);

    char *hex = (char *)malloc(33);
    if (!hex) return NULL;
    static const char L[] = "0123456789abcdef";
    for (int i = 0; i < 16; ++i) {
        hex[i*2]   = L[(digest[i] >> 4) & 0xF];
        hex[i*2+1] = L[digest[i] & 0xF];
    }
    hex[32] = '\0';
    return hex;
}

bool ca_payfast_board_verify_itn(const ca_payfast_board_t *b,
                                 const ca_payfast_itn_t *p) {
    if (!b || !p) return false;
    return cab_ord_eq(p->merchant_id, b->config.merchant_id);
}

int ca_payfast_board_record_webhook(ca_payfast_board_t *b,
                                    const ca_payfast_itn_t *p) {
    if (!b || !p) return -1;
    ca_payfast_itn_t copy;
    if (!itn_copy(&copy, p)) return -1;
    if (b->count == b->cap) {
        size_t nc = b->cap ? b->cap * 2 : 4;
        void *n = realloc(b->webhooks, nc * sizeof(*b->webhooks));
        if (!n) { ca_payfast_itn_free(&copy); return -1; }
        b->webhooks = (ca_payfast_itn_t *)n;
        b->cap = nc;
    }
    b->webhooks[b->count++] = copy;
    return 0;
}

ca_payfast_itn_t *ca_payfast_board_recent_webhooks(const ca_payfast_board_t *b,
                                                   int limit, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || limit < 0) { *out_count = (size_t)-1; return NULL; }
    if (b->count == 0 || limit == 0) { *out_count = 0; return NULL; }

    /* AsEnumerable().Reverse().Take(limit): most-recent (last-inserted) first. */
    size_t n = b->count;
    if (n > (size_t)limit) n = (size_t)limit;
    ca_payfast_itn_t *out = (ca_payfast_itn_t *)calloc(n, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        const ca_payfast_itn_t *src = &b->webhooks[b->count - 1 - i];
        if (!itn_copy(&out[i], src)) {
            ca_payfast_itn_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = n;
    return out;
}
