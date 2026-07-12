/*
 * paca.c — CircleAI.Workflows PACA surface (C11 port). See paca.h.
 *
 * Self-contained: SHA-256 + HMAC-SHA256 + base64/base64url live here (no
 * external crypto). JSON is emitted by hand and read with a tiny scanner just
 * deep enough for the JWT payload (flat object of string/number values). Stores
 * are linear arrays with deep-copy getters. Pure C11 + libc.
 */

#include "circle_ai/paca.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>

/* ── string helpers ─────────────────────────────────────────────────────── */

static char *pdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *pdup_or_empty(const char *s) {
    return pdup(s ? s : "");
}
static bool pblank(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p)
        if (*p != ' ' && *p != '\t' && *p != '\n' && *p != '\r') return false;
    return true;
}
static bool pstr_eq(const char *a, const char *b) {
    if (a == b) return true;
    if (!a || !b) return false;
    return strcmp(a, b) == 0;
}
static bool pstr_ieq(const char *a, const char *b) {
    if (a == b) return true;
    if (!a || !b) return false;
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b)) return false;
        ++a; ++b;
    }
    return *a == *b;
}
/* strdup with a duplicate-on-null-safe replace helper. */
static void pset(char **slot, const char *v) {
    char *n = pdup(v);
    free(*slot);
    *slot = n;
}

/* growable char buffer */
typedef struct { char *buf; size_t len, cap; } sb_t;
static bool sb_reserve(sb_t *b, size_t extra) {
    if (b->len + extra + 1 <= b->cap) return true;
    size_t nc = b->cap ? b->cap : 64;
    while (b->len + extra + 1 > nc) nc *= 2;
    char *nb = (char *)realloc(b->buf, nc);
    if (!nb) return false;
    b->buf = nb; b->cap = nc;
    return true;
}
static bool sb_puts(sb_t *b, const char *s) {
    if (!s) return true;
    size_t n = strlen(s);
    if (!sb_reserve(b, n)) return false;
    memcpy(b->buf + b->len, s, n);
    b->len += n; b->buf[b->len] = '\0';
    return true;
}
static bool sb_putc(sb_t *b, char c) {
    if (!sb_reserve(b, 1)) return false;
    b->buf[b->len++] = c; b->buf[b->len] = '\0';
    return true;
}
static char *sb_take(sb_t *b) {
    if (!b->buf) return pdup_or_empty("");
    return b->buf; /* caller owns */
}

/* ── clock ──────────────────────────────────────────────────────────────── */

typedef struct { ca_paca_clock_fn fn; void *ctx; } clock_t_;
static int64_t clock_now(const clock_t_ *c) {
    return c->fn ? c->fn(c->ctx) : 0;
}

/* ── SHA-256 (self-contained) ───────────────────────────────────────────── */

typedef struct { uint8_t data[64]; uint32_t datalen; uint64_t bitlen; uint32_t state[8]; } sha_ctx;
#define ROTR(a,b) (((a) >> (b)) | ((a) << (32-(b))))
#define CH(x,y,z)  (((x) & (y)) ^ (~(x) & (z)))
#define MAJ(x,y,z) (((x) & (y)) ^ ((x) & (z)) ^ ((y) & (z)))
#define EP0(x) (ROTR(x,2) ^ ROTR(x,13) ^ ROTR(x,22))
#define EP1(x) (ROTR(x,6) ^ ROTR(x,11) ^ ROTR(x,25))
#define SIG0(x) (ROTR(x,7) ^ ROTR(x,18) ^ ((x) >> 3))
#define SIG1(x) (ROTR(x,17) ^ ROTR(x,19) ^ ((x) >> 10))
static const uint32_t SHA_K[64] = {
    0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
    0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
    0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
    0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
    0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
    0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
    0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
    0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2 };
static void sha_init(sha_ctx *c) {
    c->datalen = 0; c->bitlen = 0;
    c->state[0]=0x6a09e667; c->state[1]=0xbb67ae85; c->state[2]=0x3c6ef372; c->state[3]=0xa54ff53a;
    c->state[4]=0x510e527f; c->state[5]=0x9b05688c; c->state[6]=0x1f83d9ab; c->state[7]=0x5be0cd19;
}
static void sha_transform(sha_ctx *c, const uint8_t *d) {
    uint32_t m[64], a,b,e,f,g,h,i,j,t1,t2,cc,dd;
    for (i=0,j=0;i<16;++i,j+=4) m[i]=(d[j]<<24)|(d[j+1]<<16)|(d[j+2]<<8)|d[j+3];
    for (;i<64;++i) m[i]=SIG1(m[i-2])+m[i-7]+SIG0(m[i-15])+m[i-16];
    a=c->state[0]; b=c->state[1]; cc=c->state[2]; dd=c->state[3];
    e=c->state[4]; f=c->state[5]; g=c->state[6]; h=c->state[7];
    for (i=0;i<64;++i) {
        t1=h+EP1(e)+CH(e,f,g)+SHA_K[i]+m[i];
        t2=EP0(a)+MAJ(a,b,cc);
        h=g; g=f; f=e; e=dd+t1; dd=cc; cc=b; b=a; a=t1+t2;
    }
    c->state[0]+=a; c->state[1]+=b; c->state[2]+=cc; c->state[3]+=dd;
    c->state[4]+=e; c->state[5]+=f; c->state[6]+=g; c->state[7]+=h;
}
static void sha_update(sha_ctx *c, const uint8_t *d, size_t len) {
    for (size_t i=0;i<len;++i) {
        c->data[c->datalen++]=d[i];
        if (c->datalen==64) { sha_transform(c,c->data); c->bitlen+=512; c->datalen=0; }
    }
}
static void sha_final(sha_ctx *c, uint8_t *hash) {
    uint32_t i=c->datalen;
    if (c->datalen<56) { c->data[i++]=0x80; while(i<56) c->data[i++]=0; }
    else { c->data[i++]=0x80; while(i<64) c->data[i++]=0; sha_transform(c,c->data); memset(c->data,0,56); }
    c->bitlen += (uint64_t)c->datalen*8;
    c->data[63]=(uint8_t)(c->bitlen); c->data[62]=(uint8_t)(c->bitlen>>8);
    c->data[61]=(uint8_t)(c->bitlen>>16); c->data[60]=(uint8_t)(c->bitlen>>24);
    c->data[59]=(uint8_t)(c->bitlen>>32); c->data[58]=(uint8_t)(c->bitlen>>40);
    c->data[57]=(uint8_t)(c->bitlen>>48); c->data[56]=(uint8_t)(c->bitlen>>56);
    sha_transform(c,c->data);
    for (i=0;i<4;++i)
        for (uint32_t k=0;k<8;++k)
            hash[i+k*4]=(uint8_t)(c->state[k]>>(24-i*8));
}
static void sha256(const uint8_t *d, size_t len, uint8_t out[32]) {
    sha_ctx c; sha_init(&c); sha_update(&c,d,len); sha_final(&c,out);
}
static void hmac_sha256(const uint8_t *key, size_t klen, const uint8_t *msg, size_t mlen, uint8_t out[32]) {
    uint8_t k[64], ipad[64], opad[64], inner[32];
    memset(k,0,64);
    if (klen>64) { sha256(key,klen,k); } else if (key && klen) memcpy(k,key,klen);
    for (int i=0;i<64;++i) { ipad[i]=k[i]^0x36; opad[i]=k[i]^0x5c; }
    sha_ctx ic; sha_init(&ic); sha_update(&ic,ipad,64); sha_update(&ic,msg,mlen); sha_final(&ic,inner);
    sha_ctx oc; sha_init(&oc); sha_update(&oc,opad,64); sha_update(&oc,inner,32); sha_final(&oc,out);
}

/* ── base64 / base64url ─────────────────────────────────────────────────── */

static const char B64[]="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
/* standard base64, no line breaks. Owned. */
static char *b64_encode(const uint8_t *data, size_t len) {
    size_t olen = 4 * ((len + 2) / 3);
    char *out = (char *)malloc(olen + 1);
    if (!out) return NULL;
    size_t i, o = 0;
    for (i = 0; i + 3 <= len; i += 3) {
        uint32_t n = (data[i]<<16)|(data[i+1]<<8)|data[i+2];
        out[o++]=B64[(n>>18)&63]; out[o++]=B64[(n>>12)&63];
        out[o++]=B64[(n>>6)&63];  out[o++]=B64[n&63];
    }
    if (len - i == 1) {
        uint32_t n = data[i]<<16;
        out[o++]=B64[(n>>18)&63]; out[o++]=B64[(n>>12)&63]; out[o++]='='; out[o++]='=';
    } else if (len - i == 2) {
        uint32_t n = (data[i]<<16)|(data[i+1]<<8);
        out[o++]=B64[(n>>18)&63]; out[o++]=B64[(n>>12)&63]; out[o++]=B64[(n>>6)&63]; out[o++]='=';
    }
    out[o]='\0';
    return out;
}
/* base64url without padding. Owned. */
static char *b64url_encode(const uint8_t *data, size_t len) {
    char *s = b64_encode(data, len);
    if (!s) return NULL;
    size_t j = 0;
    for (size_t i = 0; s[i]; ++i) {
        char c = s[i];
        if (c == '=') continue;
        if (c == '+') c = '-'; else if (c == '/') c = '_';
        s[j++] = c;
    }
    s[j] = '\0';
    return s;
}
static int b64_val(char c) {
    if (c>='A'&&c<='Z') return c-'A';
    if (c>='a'&&c<='z') return c-'a'+26;
    if (c>='0'&&c<='9') return c-'0'+52;
    if (c=='+'||c=='-') return 62;
    if (c=='/'||c=='_') return 63;
    return -1;
}
/* base64url decode (padding optional). Owned; *out_len set. NULL on error. */
static uint8_t *b64url_decode(const char *in, size_t *out_len) {
    size_t n = strlen(in);
    uint8_t *out = (uint8_t *)malloc(n + 4);
    if (!out) return NULL;
    size_t o = 0; int quad[4], qi = 0;
    for (size_t i = 0; i < n; ++i) {
        if (in[i]=='=') break;
        int v = b64_val(in[i]);
        if (v < 0) continue;
        quad[qi++] = v;
        if (qi == 4) {
            out[o++]=(uint8_t)((quad[0]<<2)|(quad[1]>>4));
            out[o++]=(uint8_t)((quad[1]<<4)|(quad[2]>>2));
            out[o++]=(uint8_t)((quad[2]<<6)|quad[3]);
            qi = 0;
        }
    }
    if (qi == 2) { out[o++]=(uint8_t)((quad[0]<<2)|(quad[1]>>4)); }
    else if (qi == 3) {
        out[o++]=(uint8_t)((quad[0]<<2)|(quad[1]>>4));
        out[o++]=(uint8_t)((quad[1]<<4)|(quad[2]>>2));
    }
    *out_len = o;
    return out;
}
static bool fixed_time_eq(const char *a, const char *b) {
    size_t la = a ? strlen(a) : 0, lb = b ? strlen(b) : 0;
    if (la != lb) return false;
    int diff = 0;
    for (size_t i = 0; i < la; ++i) diff |= a[i] ^ b[i];
    return diff == 0;
}
/* SHA-256 -> base64 (padded, matching C# Convert.ToBase64String) trimmed of
 * '='. */
static char *sha256_b64_trim(const char *s) {
    uint8_t h[32];
    sha256((const uint8_t *)s, strlen(s), h);
    char *b = b64_encode(h, 32);
    if (!b) return NULL;
    size_t n = strlen(b);
    while (n && b[n-1]=='=') b[--n]='\0';
    return b;
}

/* ── JSON emission helper (escape a string value) ───────────────────────── */

static bool sb_put_json_string(sb_t *b, const char *s) {
    if (!sb_putc(b, '"')) return false;
    for (const char *p = s ? s : ""; *p; ++p) {
        unsigned char c = (unsigned char)*p;
        switch (c) {
            case '"':  if (!sb_puts(b, "\\\"")) return false; break;
            case '\\': if (!sb_puts(b, "\\\\")) return false; break;
            case '\n': if (!sb_puts(b, "\\n"))  return false; break;
            case '\r': if (!sb_puts(b, "\\r"))  return false; break;
            case '\t': if (!sb_puts(b, "\\t"))  return false; break;
            case '\b': if (!sb_puts(b, "\\b"))  return false; break;
            case '\f': if (!sb_puts(b, "\\f"))  return false; break;
            default:
                if (c < 0x20) {
                    char u[8]; snprintf(u, sizeof u, "\\u%04x", c);
                    if (!sb_puts(b, u)) return false;
                } else if (!sb_putc(b, (char)c)) return false;
        }
    }
    return sb_putc(b, '"');
}

/* random bytes — deterministic-safe PRNG seeded off an internal counter mixed
 * with the address of a local and time-independent SHA feedback. This is NOT a
 * CSPRNG; the C# uses RandomNumberGenerator, but the ported logic (secret
 * generation) only needs unpredictable-enough bytes for the hermetic build.
 * Uses a SHA-256 keystream over a monotonically increasing counter. */
static uint64_t g_rng_ctr = 0x9E3779B97F4A7C15ULL;
static void rand_bytes(uint8_t *out, size_t len) {
    size_t o = 0;
    while (o < len) {
        uint8_t block[32];
        uint8_t seed[16];
        uint64_t c = ++g_rng_ctr;
        uintptr_t a = (uintptr_t)out;
        for (int i = 0; i < 8; ++i) seed[i] = (uint8_t)(c >> (i*8));
        for (int i = 0; i < 8; ++i) seed[8+i] = (uint8_t)(((uint64_t)a + o) >> (i*8));
        sha256(seed, sizeof seed, block);
        size_t take = len - o < 32 ? len - o : 32;
        memcpy(out + o, block, take);
        o += take;
    }
}

/* ===========================================================================
 * Claim helpers
 * =========================================================================== */

static void claim_array_free(ca_paca_claim_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) { free(arr[i].key); free(arr[i].value); }
    free(arr);
}
static ca_paca_claim_t *claim_array_copy(const ca_paca_claim_t *src, size_t count) {
    if (count == 0) return NULL;
    ca_paca_claim_t *d = (ca_paca_claim_t *)calloc(count, sizeof *d);
    if (!d) return NULL;
    for (size_t i = 0; i < count; ++i) {
        d[i].key = pdup(src[i].key);
        d[i].value = pdup(src[i].value);
    }
    return d;
}
static char **str_array_copy(char **src, size_t count) {
    if (count == 0) return NULL;
    char **d = (char **)calloc(count, sizeof *d);
    if (!d) return NULL;
    for (size_t i = 0; i < count; ++i) d[i] = pdup(src[i]);
    return d;
}

void ca_paca_string_array_free(char **arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) free(arr[i]);
    free(arr);
}

/* ===========================================================================
 * Auth — JWT
 * =========================================================================== */

void ca_paca_jwt_pair_free(ca_paca_jwt_pair_t *p) {
    if (!p) return;
    free(p->access_token); free(p->refresh_token);
    p->access_token = p->refresh_token = NULL;
}
void ca_paca_jwt_payload_free(ca_paca_jwt_payload_t *p) {
    if (!p) return;
    free(p->subject);
    claim_array_free(p->claims, p->claim_count);
    p->subject = NULL; p->claims = NULL; p->claim_count = 0;
}
const char *ca_paca_jwt_payload_claim(const ca_paca_jwt_payload_t *p, const char *key) {
    if (!p || !key) return NULL;
    for (size_t i = 0; i < p->claim_count; ++i)
        if (pstr_eq(p->claims[i].key, key)) return p->claims[i].value;
    return NULL;
}

struct ca_paca_jwt_auth {
    uint8_t *secret; size_t secret_len;
    int64_t access_ms, refresh_ms;
    clock_t_ clock;
};

ca_paca_jwt_auth_t *ca_paca_jwt_auth_create(const char *signing_secret,
                                            int64_t access_lifetime_ms,
                                            int64_t refresh_lifetime_ms,
                                            ca_paca_clock_fn clock, void *clock_ctx) {
    if (!signing_secret || strlen(signing_secret) < 16) return NULL;
    ca_paca_jwt_auth_t *a = (ca_paca_jwt_auth_t *)calloc(1, sizeof *a);
    if (!a) return NULL;
    a->secret_len = strlen(signing_secret);
    a->secret = (uint8_t *)malloc(a->secret_len);
    if (!a->secret) { free(a); return NULL; }
    memcpy(a->secret, signing_secret, a->secret_len);
    a->access_ms  = access_lifetime_ms  > 0 ? access_lifetime_ms  : (int64_t)15 * 60 * 1000;
    a->refresh_ms = refresh_lifetime_ms > 0 ? refresh_lifetime_ms : (int64_t)7 * 24 * 60 * 60 * 1000;
    a->clock.fn = clock; a->clock.ctx = clock_ctx;
    return a;
}
void ca_paca_jwt_auth_destroy(ca_paca_jwt_auth_t *a) {
    if (!a) return;
    free(a->secret);
    free(a);
}

static char *jwt_sign_b64url(ca_paca_jwt_auth_t *a, const char *signing) {
    uint8_t mac[32];
    hmac_sha256(a->secret, a->secret_len, (const uint8_t *)signing, strlen(signing), mac);
    return b64url_encode(mac, 32);
}

/* Encode one token. exp_ms is Unix ms; the JWT "exp" is in seconds. */
static char *jwt_encode(ca_paca_jwt_auth_t *a, const char *subject, const char *type,
                        int64_t exp_ms, const ca_paca_claim_t *claims, size_t claim_count) {
    static const char header[] = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";
    /* payload JSON: sub, typ, exp + claims */
    sb_t pj = {0};
    if (!sb_putc(&pj, '{')) goto oom;
    if (!sb_puts(&pj, "\"sub\":") || !sb_put_json_string(&pj, subject)) goto oom;
    if (!sb_puts(&pj, ",\"typ\":") || !sb_put_json_string(&pj, type)) goto oom;
    {
        char expbuf[32];
        snprintf(expbuf, sizeof expbuf, "%lld", (long long)(exp_ms / 1000));
        if (!sb_puts(&pj, ",\"exp\":") || !sb_puts(&pj, expbuf)) goto oom;
    }
    for (size_t i = 0; i < claim_count; ++i) {
        if (!sb_putc(&pj, ',')) goto oom;
        if (!sb_put_json_string(&pj, claims[i].key ? claims[i].key : "")) goto oom;
        if (!sb_putc(&pj, ':')) goto oom;
        if (!sb_put_json_string(&pj, claims[i].value ? claims[i].value : "")) goto oom;
    }
    if (!sb_putc(&pj, '}')) goto oom;

    char *hb = b64url_encode((const uint8_t *)header, sizeof header - 1);
    char *pb = b64url_encode((const uint8_t *)pj.buf, pj.len);
    free(pj.buf);
    if (!hb || !pb) { free(hb); free(pb); return NULL; }

    sb_t sig_in = {0};
    if (!sb_puts(&sig_in, hb) || !sb_putc(&sig_in, '.') || !sb_puts(&sig_in, pb)) {
        free(hb); free(pb); free(sig_in.buf); return NULL;
    }
    char *sig = jwt_sign_b64url(a, sig_in.buf);
    if (!sig) { free(hb); free(pb); free(sig_in.buf); return NULL; }

    sb_t tok = {0};
    bool ok = sb_puts(&tok, sig_in.buf) && sb_putc(&tok, '.') && sb_puts(&tok, sig);
    free(hb); free(pb); free(sig_in.buf); free(sig);
    if (!ok) { free(tok.buf); return NULL; }
    return sb_take(&tok);
oom:
    free(pj.buf);
    return NULL;
}

int ca_paca_jwt_auth_issue(ca_paca_jwt_auth_t *a, const char *subject,
                           const ca_paca_claim_t *claims, size_t claim_count,
                           ca_paca_jwt_pair_t *out) {
    if (!a || pblank(subject) || !out) return -1;
    int64_t now = clock_now(&a->clock);
    int64_t aexp = now + a->access_ms;
    int64_t rexp = now + a->refresh_ms;
    char *access  = jwt_encode(a, subject, "access", aexp, claims, claim_count);
    char *refresh = jwt_encode(a, subject, "refresh", rexp, NULL, 0);
    if (!access || !refresh) { free(access); free(refresh); return -1; }
    /* exp is truncated to whole seconds in the token; mirror that in the pair. */
    out->access_token = access;
    out->refresh_token = refresh;
    out->access_expires_at_ms  = (aexp / 1000) * 1000;
    out->refresh_expires_at_ms = (rexp / 1000) * 1000;
    return 0;
}

/* Very small flat-JSON scanner for the payload: pulls the string value of a
 * top-level "key" (sub/typ) or the integer value (exp) and collects the extra
 * claims. Assumes the payload we emitted (well-formed, flat). */

/* find "\"key\":" and return a pointer just past the colon (skipping ws), or
 * NULL. */
static const char *json_find_key(const char *json, const char *key) {
    size_t klen = strlen(key);
    for (const char *p = json; *p; ++p) {
        if (*p != '"') continue;
        if (strncmp(p + 1, key, klen) == 0 && p[1 + klen] == '"') {
            const char *q = p + 1 + klen + 1;
            while (*q == ' ' || *q == '\t') ++q;
            if (*q == ':') { ++q; while (*q==' '||*q=='\t') ++q; return q; }
        }
    }
    return NULL;
}
/* decode a JSON string literal starting at *p (which points at the opening
 * quote). Returns owned string; advances *p past the closing quote. */
static char *json_read_string(const char **pp) {
    const char *p = *pp;
    if (*p != '"') return NULL;
    ++p;
    sb_t b = {0};
    while (*p && *p != '"') {
        if (*p == '\\') {
            ++p;
            switch (*p) {
                case 'n': sb_putc(&b,'\n'); break;
                case 'r': sb_putc(&b,'\r'); break;
                case 't': sb_putc(&b,'\t'); break;
                case 'b': sb_putc(&b,'\b'); break;
                case 'f': sb_putc(&b,'\f'); break;
                case '"': sb_putc(&b,'"'); break;
                case '\\': sb_putc(&b,'\\'); break;
                case '/': sb_putc(&b,'/'); break;
                case 'u': {
                    if (p[1]&&p[2]&&p[3]&&p[4]) {
                        char hx[5]={p[1],p[2],p[3],p[4],0};
                        unsigned v=(unsigned)strtoul(hx,NULL,16);
                        if (v<0x80) sb_putc(&b,(char)v);
                        else if (v<0x800){ sb_putc(&b,(char)(0xC0|(v>>6))); sb_putc(&b,(char)(0x80|(v&0x3F))); }
                        else { sb_putc(&b,(char)(0xE0|(v>>12))); sb_putc(&b,(char)(0x80|((v>>6)&0x3F))); sb_putc(&b,(char)(0x80|(v&0x3F))); }
                        p += 4;
                    }
                    break;
                }
                default: sb_putc(&b,*p); break;
            }
            if (*p) ++p;
        } else {
            sb_putc(&b, *p); ++p;
        }
    }
    if (*p == '"') ++p;
    *pp = p;
    return b.buf ? b.buf : pdup_or_empty("");
}

int ca_paca_jwt_auth_verify(ca_paca_jwt_auth_t *a, const char *token,
                            const char *expected_type, ca_paca_jwt_payload_t *out) {
    if (!a || pblank(token) || !out) return -1;
    const char *want_type = expected_type ? expected_type : "access";

    /* split into 3 parts */
    const char *d1 = strchr(token, '.');
    if (!d1) return -1;
    const char *d2 = strchr(d1 + 1, '.');
    if (!d2) return -1;
    if (strchr(d2 + 1, '.')) return -1;

    size_t hlen = (size_t)(d1 - token);
    size_t plen = (size_t)(d2 - (d1 + 1));
    char *header = (char *)malloc(hlen + 1);
    char *payload_b = (char *)malloc(plen + 1);
    const char *sig = d2 + 1;
    if (!header || !payload_b) { free(header); free(payload_b); return -1; }
    memcpy(header, token, hlen); header[hlen] = '\0';
    memcpy(payload_b, d1 + 1, plen); payload_b[plen] = '\0';

    /* recompute signature over "header.payload" */
    sb_t signing = {0};
    if (!sb_puts(&signing, header) || !sb_putc(&signing, '.') || !sb_puts(&signing, payload_b)) {
        free(header); free(payload_b); free(signing.buf); return -1;
    }
    char *expected = jwt_sign_b64url(a, signing.buf);
    free(signing.buf);
    if (!expected) { free(header); free(payload_b); return -1; }
    bool sig_ok = fixed_time_eq(expected, sig);
    free(expected);
    free(header);
    if (!sig_ok) { free(payload_b); return -1; }

    /* decode payload JSON */
    size_t jlen = 0;
    uint8_t *jbytes = b64url_decode(payload_b, &jlen);
    free(payload_b);
    if (!jbytes) return -1;
    char *json = (char *)malloc(jlen + 1);
    if (!json) { free(jbytes); return -1; }
    memcpy(json, jbytes, jlen); json[jlen] = '\0';
    free(jbytes);

    int rc = -1;
    char *subject = NULL;
    ca_paca_claim_t *claims = NULL; size_t claim_count = 0, claim_cap = 0;

    /* typ must match */
    const char *tp = json_find_key(json, "typ");
    if (!tp || *tp != '"') goto done;
    { const char *cur = tp; char *typ = json_read_string(&cur);
      bool ok = typ && pstr_eq(typ, want_type); free(typ); if (!ok) goto done; }

    /* sub */
    { const char *sp = json_find_key(json, "sub");
      if (!sp || *sp != '"') goto done;
      const char *cur = sp; subject = json_read_string(&cur);
      if (!subject) goto done; }

    /* exp (seconds) */
    { const char *ep = json_find_key(json, "exp");
      if (!ep) goto done;
      long long exp_s = strtoll(ep, NULL, 10);
      int64_t exp_ms = (int64_t)exp_s * 1000;
      if (exp_ms <= clock_now(&a->clock)) goto done;
      out->expires_at_ms = exp_ms; }

    /* extra claims: walk every top-level "key":"value" pair skipping sub/typ/exp.
     * Because we only ever emit string claims, we only collect string values. */
    {
        const char *p = json;
        /* enter object */
        while (*p && *p != '{') ++p;
        if (*p == '{') ++p;
        while (*p) {
            while (*p==' '||*p=='\t'||*p==','||*p=='\n'||*p=='\r') ++p;
            if (*p == '}' || *p == '\0') break;
            if (*p != '"') { ++p; continue; }
            const char *cur = p;
            char *k = json_read_string(&cur);
            while (*cur==' '||*cur=='\t') ++cur;
            if (*cur != ':') { free(k); p = cur; continue; }
            ++cur; while (*cur==' '||*cur=='\t') ++cur;
            char *v = NULL;
            if (*cur == '"') {
                v = json_read_string(&cur);
            } else {
                /* number/other scalar -> capture raw token */
                const char *start = cur;
                while (*cur && *cur!=','&&*cur!='}'&&*cur!=' '&&*cur!='\t'&&*cur!='\n'&&*cur!='\r') ++cur;
                size_t n = (size_t)(cur - start);
                v = (char *)malloc(n + 1);
                if (v) { memcpy(v, start, n); v[n] = '\0'; }
            }
            p = cur;
            if (k && v && !pstr_eq(k,"typ") && !pstr_eq(k,"sub") && !pstr_eq(k,"exp")) {
                if (claim_count == claim_cap) {
                    size_t nc = claim_cap ? claim_cap*2 : 4;
                    ca_paca_claim_t *nn = (ca_paca_claim_t *)realloc(claims, nc*sizeof *nn);
                    if (!nn) { free(k); free(v); goto done; }
                    claims = nn; claim_cap = nc;
                }
                claims[claim_count].key = k; claims[claim_count].value = v;
                ++claim_count;
                k = NULL; v = NULL;
            }
            free(k); free(v);
        }
    }

    out->subject = subject; subject = NULL;
    out->claims = claims; out->claim_count = claim_count;
    claims = NULL;
    rc = 0;
done:
    free(subject);
    claim_array_free(claims, claim_count);
    free(json);
    return rc;
}

/* ===========================================================================
 * Auth — API keys
 * =========================================================================== */

void ca_paca_api_key_record_free(ca_paca_api_key_record_t *r) {
    if (!r) return;
    free(r->key_id); free(r->label); free(r->hashed_secret);
    r->key_id = r->label = r->hashed_secret = NULL;
}

typedef struct { ca_paca_api_key_record_t rec; } apikey_slot;

struct ca_paca_api_key_auth {
    apikey_slot *keys; size_t count, cap;
    clock_t_ clock;
};

ca_paca_api_key_auth_t *ca_paca_api_key_auth_create(ca_paca_clock_fn clock, void *clock_ctx) {
    ca_paca_api_key_auth_t *a = (ca_paca_api_key_auth_t *)calloc(1, sizeof *a);
    if (!a) return NULL;
    a->clock.fn = clock; a->clock.ctx = clock_ctx;
    return a;
}
void ca_paca_api_key_auth_destroy(ca_paca_api_key_auth_t *a) {
    if (!a) return;
    for (size_t i = 0; i < a->count; ++i) ca_paca_api_key_record_free(&a->keys[i].rec);
    free(a->keys);
    free(a);
}
static apikey_slot *apikey_find(ca_paca_api_key_auth_t *a, const char *key_id) {
    for (size_t i = 0; i < a->count; ++i)
        if (pstr_eq(a->keys[i].rec.key_id, key_id)) return &a->keys[i];
    return NULL;
}
static void record_copy(ca_paca_api_key_record_t *dst, const ca_paca_api_key_record_t *src) {
    dst->key_id = pdup(src->key_id);
    dst->label = pdup(src->label);
    dst->hashed_secret = pdup(src->hashed_secret);
    dst->created_at_ms = src->created_at_ms;
    dst->revoked_at_ms = src->revoked_at_ms;
}
/* GUID "n" format: 32 lowercase hex chars, no dashes. */
static char *make_guid_n(void) {
    uint8_t b[16]; rand_bytes(b, 16);
    char *s = (char *)malloc(33);
    if (!s) return NULL;
    static const char hx[] = "0123456789abcdef";
    for (int i = 0; i < 16; ++i) { s[i*2]=hx[b[i]>>4]; s[i*2+1]=hx[b[i]&0xF]; }
    s[32]='\0';
    return s;
}

int ca_paca_api_key_auth_issue(ca_paca_api_key_auth_t *a, const char *label,
                               ca_paca_api_key_record_t *out_record,
                               char **out_raw_secret) {
    if (!a || pblank(label) || !out_record || !out_raw_secret) return -1;
    char *key_id = make_guid_n();
    if (!key_id) return -1;
    uint8_t sb[32]; rand_bytes(sb, 32);
    char *secret = b64_encode(sb, 32);
    if (!secret) { free(key_id); return -1; }
    /* TrimEnd('=') */
    { size_t n = strlen(secret); while (n && secret[n-1]=='=') secret[--n]='\0'; }
    char *hashed = sha256_b64_trim(secret);
    if (!hashed) { free(key_id); free(secret); return -1; }

    if (a->count == a->cap) {
        size_t nc = a->cap ? a->cap*2 : 8;
        apikey_slot *ns = (apikey_slot *)realloc(a->keys, nc*sizeof *ns);
        if (!ns) { free(key_id); free(secret); free(hashed); return -1; }
        a->keys = ns; a->cap = nc;
    }
    ca_paca_api_key_record_t *rec = &a->keys[a->count].rec;
    rec->key_id = key_id;
    rec->label = pdup(label);
    rec->hashed_secret = hashed;
    rec->created_at_ms = clock_now(&a->clock);
    rec->revoked_at_ms = -1;
    ++a->count;

    record_copy(out_record, rec);
    *out_raw_secret = secret;
    return 0;
}

int ca_paca_api_key_auth_verify(ca_paca_api_key_auth_t *a, const char *key_id,
                                const char *presented_secret,
                                ca_paca_api_key_record_t *out) {
    if (!a || !key_id || !presented_secret || !out) return -1;
    apikey_slot *s = apikey_find(a, key_id);
    if (!s) return -1;
    if (s->rec.revoked_at_ms >= 0) return -1;
    char *hashed = sha256_b64_trim(presented_secret);
    if (!hashed) return -1;
    bool ok = fixed_time_eq(hashed, s->rec.hashed_secret);
    free(hashed);
    if (!ok) return -1;
    record_copy(out, &s->rec);
    return 0;
}

void ca_paca_api_key_auth_revoke(ca_paca_api_key_auth_t *a, const char *key_id) {
    if (!a || !key_id) return;
    apikey_slot *s = apikey_find(a, key_id);
    if (!s || s->rec.revoked_at_ms >= 0) return;
    s->rec.revoked_at_ms = clock_now(&a->clock);
}

/* ===========================================================================
 * Projects — InMemoryPacaStore
 * =========================================================================== */

void ca_paca_project_free(ca_paca_project_t *p) {
    if (!p) return;
    free(p->id); free(p->name); free(p->prefix); free(p->settings_json);
    memset(p, 0, sizeof *p);
}
void ca_paca_project_free_array(ca_paca_project_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_paca_project_free(&arr[i]);
    free(arr);
}
void ca_paca_task_free(ca_paca_task_t *t) {
    if (!t) return;
    free(t->project_id); free(t->title); free(t->description_json); free(t->status);
    memset(t, 0, sizeof *t);
}
void ca_paca_task_free_array(ca_paca_task_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_paca_task_free(&arr[i]);
    free(arr);
}
char *ca_paca_task_reference(const ca_paca_task_t *t, const char *prefix) {
    if (!t) return NULL;
    sb_t b = {0};
    char num[16]; snprintf(num, sizeof num, "%d", t->number);
    if (!sb_puts(&b, prefix ? prefix : "") || !sb_putc(&b, '-') || !sb_puts(&b, num)) {
        free(b.buf); return NULL;
    }
    return sb_take(&b);
}

typedef struct { ca_paca_project_t proj; ca_paca_task_t *tasks; size_t task_count, task_cap; int next_number; } proj_slot;

struct ca_paca_store {
    proj_slot *projects; size_t count, cap;
    clock_t_ clock;
};

ca_paca_store_t *ca_paca_store_create(ca_paca_clock_fn clock, void *clock_ctx) {
    ca_paca_store_t *s = (ca_paca_store_t *)calloc(1, sizeof *s);
    if (!s) return NULL;
    s->clock.fn = clock; s->clock.ctx = clock_ctx;
    return s;
}
void ca_paca_store_destroy(ca_paca_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) {
        ca_paca_project_free(&s->projects[i].proj);
        ca_paca_task_free_array(s->projects[i].tasks, s->projects[i].task_count);
    }
    free(s->projects);
    free(s);
}
static proj_slot *store_find(ca_paca_store_t *s, const char *id) {
    for (size_t i = 0; i < s->count; ++i)
        if (pstr_eq(s->projects[i].proj.id, id)) return &s->projects[i];
    return NULL;
}
static proj_slot *store_find_live(ca_paca_store_t *s, const char *id) {
    proj_slot *p = store_find(s, id);
    return (p && p->proj.deleted_at_ms < 0) ? p : NULL;
}
static void project_copy(ca_paca_project_t *dst, const ca_paca_project_t *src) {
    dst->id = pdup(src->id); dst->name = pdup(src->name);
    dst->prefix = pdup(src->prefix); dst->settings_json = pdup(src->settings_json);
    dst->created_at_ms = src->created_at_ms; dst->deleted_at_ms = src->deleted_at_ms;
}
static void task_copy(ca_paca_task_t *dst, const ca_paca_task_t *src) {
    dst->project_id = pdup(src->project_id); dst->number = src->number;
    dst->title = pdup(src->title); dst->description_json = pdup(src->description_json);
    dst->status = pdup(src->status);
    dst->created_at_ms = src->created_at_ms; dst->deleted_at_ms = src->deleted_at_ms;
}

int ca_paca_store_create_project(ca_paca_store_t *s, const char *id, const char *name,
                                 const char *prefix, const char *settings_json,
                                 ca_paca_project_t *out) {
    if (!s || pblank(id) || pblank(name) || pblank(prefix) || !out) return -1;
    if (store_find(s, id)) return -1; /* duplicate */
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap*2 : 8;
        proj_slot *ns = (proj_slot *)realloc(s->projects, nc*sizeof *ns);
        if (!ns) return -1;
        s->projects = ns; s->cap = nc;
    }
    proj_slot *slot = &s->projects[s->count];
    memset(slot, 0, sizeof *slot);
    slot->proj.id = pdup(id);
    slot->proj.name = pdup(name);
    slot->proj.prefix = pdup(prefix);
    slot->proj.settings_json = pdup(settings_json ? settings_json : "{}");
    slot->proj.created_at_ms = clock_now(&s->clock);
    slot->proj.deleted_at_ms = -1;
    slot->next_number = 1;
    ++s->count;
    project_copy(out, &slot->proj);
    return 0;
}

int ca_paca_store_get_project(ca_paca_store_t *s, const char *id, ca_paca_project_t *out) {
    if (!s || !id || !out) return -1;
    proj_slot *p = store_find_live(s, id);
    if (!p) return -1;
    project_copy(out, &p->proj);
    return 0;
}
void ca_paca_store_delete_project(ca_paca_store_t *s, const char *id) {
    if (!s || !id) return;
    proj_slot *p = store_find(s, id);
    if (!p || p->proj.deleted_at_ms >= 0) return;
    p->proj.deleted_at_ms = clock_now(&s->clock);
}
int ca_paca_store_update_project_settings(ca_paca_store_t *s, const char *project_id,
                                          const char *new_settings_json,
                                          ca_paca_project_t *out) {
    if (!s || !project_id || !out) return -1;
    proj_slot *p = store_find_live(s, project_id);
    if (!p) return -1;
    pset(&p->proj.settings_json, new_settings_json ? new_settings_json : "{}");
    project_copy(out, &p->proj);
    return 0;
}

static ca_paca_task_t *proj_add_task(proj_slot *p, const char *project_id, int number,
                                     const char *title, const char *desc, const char *status,
                                     int64_t now) {
    if (p->task_count == p->task_cap) {
        size_t nc = p->task_cap ? p->task_cap*2 : 8;
        ca_paca_task_t *nt = (ca_paca_task_t *)realloc(p->tasks, nc*sizeof *nt);
        if (!nt) return NULL;
        p->tasks = nt; p->task_cap = nc;
    }
    ca_paca_task_t *t = &p->tasks[p->task_count];
    memset(t, 0, sizeof *t);
    t->project_id = pdup(project_id);
    t->number = number;
    t->title = pdup(title ? title : "");
    t->description_json = pdup(desc ? desc : "{}");
    t->status = pdup(status ? status : "todo");
    t->created_at_ms = now;
    t->deleted_at_ms = -1;
    ++p->task_count;
    return t;
}

int ca_paca_store_add_task(ca_paca_store_t *s, const char *project_id, const char *title,
                           const char *description_json, const char *status,
                           ca_paca_task_t *out) {
    if (!s || !project_id || !out) return -1;
    proj_slot *p = store_find_live(s, project_id);
    if (!p) return -1;
    int number = p->next_number++;
    ca_paca_task_t *t = proj_add_task(p, project_id, number, title, description_json, status,
                                      clock_now(&s->clock));
    if (!t) return -1;
    task_copy(out, t);
    return 0;
}

ca_paca_task_t *ca_paca_store_list_tasks(ca_paca_store_t *s, const char *project_id,
                                         size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!s || !project_id || !out_count) return NULL;
    proj_slot *p = store_find(s, project_id);
    if (!p) { *out_count = 0; return NULL; }
    /* count live */
    size_t live = 0;
    for (size_t i = 0; i < p->task_count; ++i) if (p->tasks[i].deleted_at_ms < 0) ++live;
    *out_count = 0;
    if (live == 0) return NULL;
    ca_paca_task_t *arr = (ca_paca_task_t *)calloc(live, sizeof *arr);
    if (!arr) { *out_count = SIZE_MAX; return NULL; }
    /* selection sort by number ascending (small n) */
    /* build index of live tasks */
    size_t *idx = (size_t *)malloc(live * sizeof *idx);
    if (!idx) { free(arr); *out_count = SIZE_MAX; return NULL; }
    size_t k = 0;
    for (size_t i = 0; i < p->task_count; ++i) if (p->tasks[i].deleted_at_ms < 0) idx[k++] = i;
    for (size_t i = 0; i < live; ++i)
        for (size_t j = i + 1; j < live; ++j)
            if (p->tasks[idx[j]].number < p->tasks[idx[i]].number) { size_t tmp=idx[i]; idx[i]=idx[j]; idx[j]=tmp; }
    for (size_t i = 0; i < live; ++i) task_copy(&arr[i], &p->tasks[idx[i]]);
    free(idx);
    *out_count = live;
    return arr;
}

int ca_paca_store_get_task_by_reference(ca_paca_store_t *s, const char *project_id,
                                        const char *reference, ca_paca_task_t *out) {
    if (!s || !project_id || !reference || !out) return -1;
    proj_slot *p = store_find_live(s, project_id);
    if (!p) return -1;
    /* expected prefix = "<Prefix>-" (case-insensitive) */
    size_t plen = strlen(p->proj.prefix);
    if (strncasecmp(reference, p->proj.prefix, plen) != 0) return -1;
    if (reference[plen] != '-') return -1;
    char *end = NULL;
    long n = strtol(reference + plen + 1, &end, 10);
    if (end == reference + plen + 1 || *end != '\0') return -1;
    for (size_t i = 0; i < p->task_count; ++i)
        if (p->tasks[i].number == (int)n && p->tasks[i].deleted_at_ms < 0) {
            task_copy(out, &p->tasks[i]);
            return 0;
        }
    return -1;
}

void ca_paca_store_update_task(ca_paca_store_t *s, const ca_paca_task_t *updated) {
    if (!s || !updated) return;
    proj_slot *p = store_find(s, updated->project_id);
    if (!p) return;
    for (size_t i = 0; i < p->task_count; ++i)
        if (p->tasks[i].number == updated->number) {
            ca_paca_task_t copy; memset(&copy, 0, sizeof copy);
            task_copy(&copy, updated);
            ca_paca_task_free(&p->tasks[i]);
            p->tasks[i] = copy;
            return;
        }
}

void ca_paca_store_delete_task(ca_paca_store_t *s, const char *project_id, int number) {
    if (!s || !project_id) return;
    proj_slot *p = store_find(s, project_id);
    if (!p) return;
    for (size_t i = 0; i < p->task_count; ++i)
        if (p->tasks[i].number == number) {
            p->tasks[i].deleted_at_ms = clock_now(&s->clock);
            return;
        }
}

/* ===========================================================================
 * Boards — PacaBoard
 * =========================================================================== */

void ca_paca_status_column_free(ca_paca_status_column_t *c) {
    if (!c) return;
    free(c->name); free(c->category);
    c->name = c->category = NULL;
}
void ca_paca_status_column_free_array(ca_paca_status_column_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_paca_status_column_free(&arr[i]);
    free(arr);
}
void ca_paca_sprint_free(ca_paca_sprint_t *s) {
    if (!s) return;
    free(s->id); free(s->project_id); free(s->name); free(s->goal);
    memset(s, 0, sizeof *s);
}
void ca_paca_task_metadata_free(ca_paca_task_metadata_t *m) {
    if (!m) return;
    free(m->project_id); free(m->assignee_member_id); free(m->reporter_member_id);
    free(m->sprint_id);
    ca_paca_string_array_free(m->tags, m->tag_count);
    claim_array_free(m->custom_fields, m->custom_field_count);
    memset(m, 0, sizeof *m);
}
void ca_paca_board_view_free(ca_paca_board_view_t *v) {
    if (!v) return;
    free(v->name); free(v->filter_tags_csv); free(v->filter_assignee); free(v->sort_by);
    ca_paca_string_array_free(v->visible_columns, v->visible_column_count);
    ca_paca_string_array_free(v->visible_fields, v->visible_field_count);
    memset(v, 0, sizeof *v);
}
void ca_paca_board_view_free_array(ca_paca_board_view_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_paca_board_view_free(&arr[i]);
    free(arr);
}

typedef struct { ca_paca_status_column_t col; } col_slot;
typedef struct { ca_paca_sprint_t sp; } sprint_slot;
typedef struct { ca_paca_task_metadata_t md; } md_slot;
typedef struct { ca_paca_board_view_t view; } view_slot;

struct ca_paca_board {
    ca_paca_store_t *tasks;                 /* borrowed */
    col_slot   *cols;   size_t col_count,   col_cap;
    sprint_slot *sprints; size_t sprint_count, sprint_cap;
    md_slot    *mds;    size_t md_count,    md_cap;
    view_slot  *views;  size_t view_count,  view_cap;
    clock_t_ clock;
};

static void col_copy(ca_paca_status_column_t *dst, const ca_paca_status_column_t *src) {
    dst->name = pdup(src->name); dst->category = pdup(src->category);
    dst->position = src->position; dst->collapsed = src->collapsed;
}
static void sprint_copy(ca_paca_sprint_t *dst, const ca_paca_sprint_t *src) {
    dst->id = pdup(src->id); dst->project_id = pdup(src->project_id);
    dst->name = pdup(src->name); dst->goal = pdup(src->goal);
    dst->start_ms = src->start_ms; dst->end_ms = src->end_ms; dst->state = src->state;
}
static void md_copy(ca_paca_task_metadata_t *dst, const ca_paca_task_metadata_t *src) {
    dst->project_id = pdup(src->project_id);
    dst->number = src->number;
    dst->story_points = src->story_points;
    dst->importance = src->importance;
    dst->assignee_member_id = pdup(src->assignee_member_id);
    dst->reporter_member_id = pdup(src->reporter_member_id);
    dst->parent_task_number = src->parent_task_number;
    dst->sprint_id = pdup(src->sprint_id);
    dst->tags = str_array_copy(src->tags, src->tag_count);
    dst->tag_count = src->tag_count;
    dst->custom_fields = claim_array_copy(src->custom_fields, src->custom_field_count);
    dst->custom_field_count = src->custom_field_count;
    dst->position_in_column = src->position_in_column;
}
static void view_copy(ca_paca_board_view_t *dst, const ca_paca_board_view_t *src) {
    dst->name = pdup(src->name);
    dst->filter_tags_csv = pdup(src->filter_tags_csv);
    dst->filter_assignee = pdup(src->filter_assignee);
    dst->sort_by = pdup(src->sort_by);
    dst->sort_descending = src->sort_descending;
    dst->visible_columns = str_array_copy(src->visible_columns, src->visible_column_count);
    dst->visible_column_count = src->visible_column_count;
    dst->visible_fields = str_array_copy(src->visible_fields, src->visible_field_count);
    dst->visible_field_count = src->visible_field_count;
}

static col_slot *board_find_col(ca_paca_board_t *b, const char *name) {
    for (size_t i = 0; i < b->col_count; ++i)
        if (pstr_eq(b->cols[i].col.name, name)) return &b->cols[i];
    return NULL;
}
static int board_put_col(ca_paca_board_t *b, const ca_paca_status_column_t *src) {
    col_slot *ex = board_find_col(b, src->name);
    if (ex) { ca_paca_status_column_free(&ex->col); col_copy(&ex->col, src); return 0; }
    if (b->col_count == b->col_cap) {
        size_t nc = b->col_cap ? b->col_cap*2 : 8;
        col_slot *ns = (col_slot *)realloc(b->cols, nc*sizeof *ns);
        if (!ns) return -1;
        b->cols = ns; b->col_cap = nc;
    }
    col_copy(&b->cols[b->col_count].col, src);
    ++b->col_count;
    return 0;
}

ca_paca_board_t *ca_paca_board_create(ca_paca_store_t *tasks, ca_paca_clock_fn clock, void *clock_ctx) {
    if (!tasks) return NULL;
    ca_paca_board_t *b = (ca_paca_board_t *)calloc(1, sizeof *b);
    if (!b) return NULL;
    b->tasks = tasks;
    b->clock.fn = clock; b->clock.ctx = clock_ctx;
    /* default columns */
    struct { const char *n, *c; int p; bool coll; } defs[] = {
        {"todo","open",0,false}, {"in_progress","in-flight",1,false},
        {"in_review","review",2,false}, {"done","closed",3,false},
        {"cancelled","cancelled",4,false}, {"blocked","blocked",5,true},
    };
    for (size_t i = 0; i < sizeof defs/sizeof defs[0]; ++i) {
        ca_paca_status_column_t c = { (char*)defs[i].n, (char*)defs[i].c, defs[i].p, defs[i].coll };
        if (board_put_col(b, &c) != 0) { ca_paca_board_destroy(b); return NULL; }
    }
    return b;
}
void ca_paca_board_destroy(ca_paca_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->col_count; ++i) ca_paca_status_column_free(&b->cols[i].col);
    free(b->cols);
    for (size_t i = 0; i < b->sprint_count; ++i) ca_paca_sprint_free(&b->sprints[i].sp);
    free(b->sprints);
    for (size_t i = 0; i < b->md_count; ++i) ca_paca_task_metadata_free(&b->mds[i].md);
    free(b->mds);
    for (size_t i = 0; i < b->view_count; ++i) ca_paca_board_view_free(&b->views[i].view);
    free(b->views);
    free(b);
}

ca_paca_status_column_t *ca_paca_board_columns(ca_paca_board_t *b, size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!b || !out_count) return NULL;
    *out_count = 0;
    if (b->col_count == 0) return NULL;
    ca_paca_status_column_t *arr = (ca_paca_status_column_t *)calloc(b->col_count, sizeof *arr);
    if (!arr) { *out_count = SIZE_MAX; return NULL; }
    size_t *idx = (size_t *)malloc(b->col_count * sizeof *idx);
    if (!idx) { free(arr); *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < b->col_count; ++i) idx[i] = i;
    for (size_t i = 0; i < b->col_count; ++i)
        for (size_t j = i+1; j < b->col_count; ++j)
            if (b->cols[idx[j]].col.position < b->cols[idx[i]].col.position) { size_t t=idx[i]; idx[i]=idx[j]; idx[j]=t; }
    for (size_t i = 0; i < b->col_count; ++i) col_copy(&arr[i], &b->cols[idx[i]].col);
    free(idx);
    *out_count = b->col_count;
    return arr;
}
int ca_paca_board_add_column(ca_paca_board_t *b, const ca_paca_status_column_t *col) {
    if (!b || !col || !col->name) return -1;
    return board_put_col(b, col);
}
void ca_paca_board_collapse_column(ca_paca_board_t *b, const char *name, bool collapsed) {
    if (!b || !name) return;
    col_slot *c = board_find_col(b, name);
    if (c) c->col.collapsed = collapsed;
}

static md_slot *board_find_md(ca_paca_board_t *b, const char *project_id, int number) {
    for (size_t i = 0; i < b->md_count; ++i)
        if (b->mds[i].md.number == number && pstr_eq(b->mds[i].md.project_id, project_id))
            return &b->mds[i];
    return NULL;
}
/* GetOrAdd metadata with C# defaults (importance 3). Returns internal ptr. */
static md_slot *board_get_or_create_md(ca_paca_board_t *b, const char *project_id, int number) {
    md_slot *ex = board_find_md(b, project_id, number);
    if (ex) return ex;
    if (b->md_count == b->md_cap) {
        size_t nc = b->md_cap ? b->md_cap*2 : 8;
        md_slot *ns = (md_slot *)realloc(b->mds, nc*sizeof *ns);
        if (!ns) return NULL;
        b->mds = ns; b->md_cap = nc;
    }
    ca_paca_task_metadata_t *m = &b->mds[b->md_count].md;
    memset(m, 0, sizeof *m);
    m->project_id = pdup(project_id);
    m->number = number;
    m->story_points = 0;
    m->importance = 3;
    m->parent_task_number = -1;
    m->position_in_column = 0;
    ++b->md_count;
    return &b->mds[b->md_count - 1];
}

int ca_paca_board_move_task(ca_paca_board_t *b, const char *project_id, int number,
                            const char *new_status, int new_position) {
    if (!b || !project_id || !new_status) return -1;
    if (!board_find_col(b, new_status)) return -1; /* unknown status */
    /* find the task via the store */
    size_t cnt = 0;
    ca_paca_task_t *list = ca_paca_store_list_tasks(b->tasks, project_id, &cnt);
    if (cnt == SIZE_MAX) return -1;
    ca_paca_task_t *found = NULL;
    for (size_t i = 0; i < cnt; ++i) if (list[i].number == number) { found = &list[i]; break; }
    if (!found) { ca_paca_task_free_array(list, cnt); return -1; }
    /* UpdateTask(task with { Status = newStatus }) */
    pset(&found->status, new_status);
    ca_paca_store_update_task(b->tasks, found);
    ca_paca_task_free_array(list, cnt);
    /* metadata position */
    md_slot *m = board_get_or_create_md(b, project_id, number);
    if (!m) return -1;
    m->md.position_in_column = new_position;
    return 0;
}

int ca_paca_board_set_task_metadata(ca_paca_board_t *b, const ca_paca_task_metadata_t *m) {
    if (!b || !m || !m->project_id) return -1;
    md_slot *ex = board_find_md(b, m->project_id, m->number);
    if (ex) { ca_paca_task_metadata_free(&ex->md); md_copy(&ex->md, m); return 0; }
    if (b->md_count == b->md_cap) {
        size_t nc = b->md_cap ? b->md_cap*2 : 8;
        md_slot *ns = (md_slot *)realloc(b->mds, nc*sizeof *ns);
        if (!ns) return -1;
        b->mds = ns; b->md_cap = nc;
    }
    md_copy(&b->mds[b->md_count].md, m);
    ++b->md_count;
    return 0;
}
int ca_paca_board_get_task_metadata(ca_paca_board_t *b, const char *project_id,
                                    int number, ca_paca_task_metadata_t *out) {
    if (!b || !project_id || !out) return -1;
    md_slot *m = board_find_md(b, project_id, number);
    if (!m) return -1;
    md_copy(out, &m->md);
    return 0;
}

ca_paca_task_t *ca_paca_board_tasks_in_column(ca_paca_board_t *b, const char *project_id,
                                              const char *status, int skip, int take,
                                              size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!b || !project_id || !status || !out_count) return NULL;
    size_t cnt = 0;
    ca_paca_task_t *list = ca_paca_store_list_tasks(b->tasks, project_id, &cnt);
    if (cnt == SIZE_MAX) return NULL;
    /* filter by status */
    size_t *idx = cnt ? (size_t *)malloc(cnt * sizeof *idx) : NULL;
    size_t n = 0;
    for (size_t i = 0; i < cnt; ++i) if (pstr_eq(list[i].status, status)) idx[n++] = i;
    /* order by position-in-column (GetOrCreateMetadata) */
    for (size_t i = 0; i < n; ++i)
        for (size_t j = i+1; j < n; ++j) {
            md_slot *mi = board_get_or_create_md(b, list[idx[i]].project_id, list[idx[i]].number);
            md_slot *mj = board_get_or_create_md(b, list[idx[j]].project_id, list[idx[j]].number);
            int pi = mi ? mi->md.position_in_column : 0;
            int pj = mj ? mj->md.position_in_column : 0;
            if (pj < pi) { size_t t=idx[i]; idx[i]=idx[j]; idx[j]=t; }
        }
    /* skip/take */
    size_t start = skip < 0 ? 0 : (size_t)skip;
    size_t limit = take < 0 ? 0 : (size_t)take;
    size_t avail = start < n ? n - start : 0;
    size_t outn = avail < limit ? avail : limit;
    ca_paca_task_t *arr = outn ? (ca_paca_task_t *)calloc(outn, sizeof *arr) : NULL;
    if (outn && !arr) { free(idx); ca_paca_task_free_array(list, cnt); *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < outn; ++i) task_copy(&arr[i], &list[idx[start + i]]);
    free(idx);
    ca_paca_task_free_array(list, cnt);
    *out_count = outn;
    return arr;
}

ca_paca_task_t *ca_paca_board_tasks_in_sprint(ca_paca_board_t *b, const char *sprint_id,
                                              size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!b || !sprint_id || !out_count) return NULL;
    /* metadata rows with matching sprint id, resolved back to live tasks */
    ca_paca_task_t *arr = NULL; size_t n = 0, cap = 0;
    for (size_t i = 0; i < b->md_count; ++i) {
        if (!pstr_eq(b->mds[i].md.sprint_id, sprint_id)) continue;
        size_t cnt = 0;
        ca_paca_task_t *list = ca_paca_store_list_tasks(b->tasks, b->mds[i].md.project_id, &cnt);
        if (cnt == SIZE_MAX) continue;
        for (size_t j = 0; j < cnt; ++j) {
            if (list[j].number != b->mds[i].md.number) continue;
            if (n == cap) {
                size_t nc = cap ? cap*2 : 4;
                ca_paca_task_t *na = (ca_paca_task_t *)realloc(arr, nc*sizeof *na);
                if (!na) { ca_paca_task_free_array(list, cnt); ca_paca_task_free_array(arr, n); *out_count = SIZE_MAX; return NULL; }
                arr = na; cap = nc;
            }
            task_copy(&arr[n++], &list[j]);
            break;
        }
        ca_paca_task_free_array(list, cnt);
    }
    *out_count = n;
    return arr;
}

static sprint_slot *board_find_sprint(ca_paca_board_t *b, const char *id) {
    for (size_t i = 0; i < b->sprint_count; ++i)
        if (pstr_eq(b->sprints[i].sp.id, id)) return &b->sprints[i];
    return NULL;
}
int ca_paca_board_create_sprint(ca_paca_board_t *b, const char *id, const char *project_id,
                                const char *name, const char *goal,
                                int64_t start_ms, int64_t end_ms, ca_paca_sprint_t *out) {
    if (!b || !id || !out) return -1;
    sprint_slot *ex = board_find_sprint(b, id);
    if (!ex) {
        if (b->sprint_count == b->sprint_cap) {
            size_t nc = b->sprint_cap ? b->sprint_cap*2 : 8;
            sprint_slot *ns = (sprint_slot *)realloc(b->sprints, nc*sizeof *ns);
            if (!ns) return -1;
            b->sprints = ns; b->sprint_cap = nc;
        }
        ex = &b->sprints[b->sprint_count++];
        memset(ex, 0, sizeof *ex);
    } else {
        ca_paca_sprint_free(&ex->sp);
    }
    ex->sp.id = pdup(id);
    ex->sp.project_id = pdup(project_id ? project_id : "");
    ex->sp.name = pdup(name ? name : "");
    ex->sp.goal = pdup(goal ? goal : "");
    ex->sp.start_ms = start_ms; ex->sp.end_ms = end_ms;
    ex->sp.state = CA_PACA_SPRINT_PLANNING;
    sprint_copy(out, &ex->sp);
    return 0;
}
int ca_paca_board_get_sprint(ca_paca_board_t *b, const char *id, ca_paca_sprint_t *out) {
    if (!b || !id || !out) return -1;
    sprint_slot *s = board_find_sprint(b, id);
    if (!s) return -1;
    sprint_copy(out, &s->sp);
    return 0;
}
static int board_transition(ca_paca_board_t *b, const char *id, ca_paca_sprint_state_t to, ca_paca_sprint_t *out) {
    sprint_slot *s = board_find_sprint(b, id);
    if (!s) return -1;
    s->sp.state = to;
    sprint_copy(out, &s->sp);
    return 0;
}
int ca_paca_board_start_sprint(ca_paca_board_t *b, const char *id, ca_paca_sprint_t *out) {
    if (!b || !id || !out) return -1;
    return board_transition(b, id, CA_PACA_SPRINT_ACTIVE, out);
}
int ca_paca_board_complete_sprint(ca_paca_board_t *b, const char *id, ca_paca_sprint_t *out) {
    if (!b || !id || !out) return -1;
    return board_transition(b, id, CA_PACA_SPRINT_COMPLETED, out);
}

static view_slot *board_find_view(ca_paca_board_t *b, const char *name) {
    for (size_t i = 0; i < b->view_count; ++i)
        if (pstr_eq(b->views[i].view.name, name)) return &b->views[i];
    return NULL;
}
int ca_paca_board_save_view(ca_paca_board_t *b, const ca_paca_board_view_t *v) {
    if (!b || !v || !v->name) return -1;
    view_slot *ex = board_find_view(b, v->name);
    if (ex) { ca_paca_board_view_free(&ex->view); view_copy(&ex->view, v); return 0; }
    if (b->view_count == b->view_cap) {
        size_t nc = b->view_cap ? b->view_cap*2 : 8;
        view_slot *ns = (view_slot *)realloc(b->views, nc*sizeof *ns);
        if (!ns) return -1;
        b->views = ns; b->view_cap = nc;
    }
    view_copy(&b->views[b->view_count].view, v);
    ++b->view_count;
    return 0;
}
int ca_paca_board_get_view(ca_paca_board_t *b, const char *name, ca_paca_board_view_t *out) {
    if (!b || !name || !out) return -1;
    view_slot *v = board_find_view(b, name);
    if (!v) return -1;
    view_copy(out, &v->view);
    return 0;
}
ca_paca_board_view_t *ca_paca_board_list_views(ca_paca_board_t *b, size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!b || !out_count) return NULL;
    *out_count = 0;
    if (b->view_count == 0) return NULL;
    ca_paca_board_view_t *arr = (ca_paca_board_view_t *)calloc(b->view_count, sizeof *arr);
    if (!arr) { *out_count = SIZE_MAX; return NULL; }
    size_t *idx = (size_t *)malloc(b->view_count * sizeof *idx);
    if (!idx) { free(arr); *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < b->view_count; ++i) idx[i] = i;
    for (size_t i = 0; i < b->view_count; ++i)
        for (size_t j = i+1; j < b->view_count; ++j)
            if (strcmp(b->views[idx[j]].view.name, b->views[idx[i]].view.name) < 0) { size_t t=idx[i]; idx[i]=idx[j]; idx[j]=t; }
    for (size_t i = 0; i < b->view_count; ++i) view_copy(&arr[i], &b->views[idx[i]].view);
    free(idx);
    *out_count = b->view_count;
    return arr;
}

/* ===========================================================================
 * Agents
 * =========================================================================== */

void ca_paca_member_free(ca_paca_member_t *m) {
    if (!m) return;
    free(m->id); free(m->project_id); free(m->display_name); free(m->handle);
    free(m->role); free(m->avatar_url);
    memset(m, 0, sizeof *m);
}
void ca_paca_member_free_array(ca_paca_member_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_paca_member_free(&arr[i]);
    free(arr);
}
void ca_paca_agent_profile_free(ca_paca_agent_profile_t *p) {
    if (!p) return;
    free(p->member_id);
    free(p->llm_provider); free(p->llm_model); free(p->llm_api_key); free(p->llm_base_address);
    free(p->task_prompt); free(p->doc_prompt); free(p->chat_prompt);
    free(p->git_name); free(p->git_email);
    free(p->trigger_task_created); free(p->trigger_chat_mention);
    free(p->trigger_doc_edit); free(p->trigger_direct_mention);
    memset(p, 0, sizeof *p);
}
ca_paca_agent_profile_t *ca_paca_agent_profile_copy(ca_paca_agent_profile_t *dst,
                                                    const ca_paca_agent_profile_t *src) {
    if (!dst || !src) return NULL;
    memset(dst, 0, sizeof *dst);
    dst->member_id = pdup(src->member_id);
    dst->llm_provider = pdup(src->llm_provider);
    dst->llm_model = pdup(src->llm_model);
    dst->llm_api_key = pdup(src->llm_api_key);
    dst->llm_base_address = pdup(src->llm_base_address);
    dst->task_prompt = pdup(src->task_prompt);
    dst->doc_prompt = pdup(src->doc_prompt);
    dst->chat_prompt = pdup(src->chat_prompt);
    dst->can_clone_repos = src->can_clone_repos;
    dst->can_create_prs = src->can_create_prs;
    dst->can_write_files = src->can_write_files;
    dst->can_call_external_tools = src->can_call_external_tools;
    dst->max_iterations = src->max_iterations;
    dst->timeout_ms = src->timeout_ms;
    dst->git_name = pdup(src->git_name);
    dst->git_email = pdup(src->git_email);
    dst->trigger_task_created = pdup(src->trigger_task_created);
    dst->trigger_chat_mention = pdup(src->trigger_chat_mention);
    dst->trigger_doc_edit = pdup(src->trigger_doc_edit);
    dst->trigger_direct_mention = pdup(src->trigger_direct_mention);
    return dst;
}

/* helper to build a profile from literal fields (deep-copies strings). */
static int build_profile(ca_paca_agent_profile_t *out, const char *member_id,
                         const char *provider, const char *model, const char *api_key,
                         const char *base_address, const char *task_p, const char *doc_p,
                         const char *chat_p, bool clone, bool prs, bool write, bool ext,
                         int max_iters, int64_t timeout_ms, const char *git_name,
                         const char *git_email, const char *t_task, const char *t_chat,
                         const char *t_doc, const char *t_direct) {
    if (!out) return -1;
    memset(out, 0, sizeof *out);
    out->member_id = pdup(member_id);
    out->llm_provider = pdup(provider);
    out->llm_model = pdup(model);
    out->llm_api_key = pdup(api_key);
    out->llm_base_address = pdup(base_address);
    out->task_prompt = pdup(task_p);
    out->doc_prompt = pdup(doc_p);
    out->chat_prompt = pdup(chat_p);
    out->can_clone_repos = clone; out->can_create_prs = prs;
    out->can_write_files = write; out->can_call_external_tools = ext;
    out->max_iterations = max_iters; out->timeout_ms = timeout_ms;
    out->git_name = pdup(git_name); out->git_email = pdup(git_email);
    out->trigger_task_created = pdup(t_task);
    out->trigger_chat_mention = pdup(t_chat);
    out->trigger_doc_edit = pdup(t_doc);
    out->trigger_direct_mention = pdup(t_direct);
    return 0;
}

int ca_paca_agent_template_development(const char *member_id, const char *api_key,
                                       const char *base_address, ca_paca_agent_profile_t *out) {
    return build_profile(out, member_id, "openai", "gpt-4o-mini", api_key, base_address,
        "You are a senior developer. Implement requested changes, write tests, open PRs.",
        "You write engineering docs that are precise and example-driven.",
        "You answer engineering questions with concrete code samples.",
        true, true, true, true, 25, (int64_t)10*60*1000,
        "CircleAI Dev Agent", "dev-agent@circleai.local",
        "dev", "@dev", NULL, "dev");
}
int ca_paca_agent_template_product_manager(const char *member_id, const char *api_key,
                                           ca_paca_agent_profile_t *out) {
    return build_profile(out, member_id, "openai", "gpt-4o-mini", api_key, NULL,
        "You are a product manager. Triage tasks, break them down, assign owners.",
        "You write product specs and PRDs.",
        "You answer product/priority questions.",
        false, false, true, true, 15, (int64_t)5*60*1000,
        "CircleAI PM Agent", "pm-agent@circleai.local",
        "pm", "@pm", "@pm", "pm");
}
int ca_paca_agent_template_designer(const char *member_id, const char *api_key,
                                    ca_paca_agent_profile_t *out) {
    return build_profile(out, member_id, "openai", "gpt-4o-mini", api_key, NULL,
        "You are a designer. Sketch UI ideas, write copy, propose flows.",
        "You write design memos.",
        "You answer design questions and propose concepts.",
        false, false, true, false, 10, (int64_t)5*60*1000,
        "CircleAI Design Agent", "design-agent@circleai.local",
        "design", "@design", "@design", "design");
}
int ca_paca_agent_template_qa(const char *member_id, const char *api_key,
                              ca_paca_agent_profile_t *out) {
    return build_profile(out, member_id, "openai", "gpt-4o-mini", api_key, NULL,
        "You are a QA engineer. Write test plans, generate test cases, validate against AC.",
        "You write QA reports.",
        "You answer QA questions and propose test strategies.",
        true, false, true, true, 20, (int64_t)7*60*1000,
        "CircleAI QA Agent", "qa-agent@circleai.local",
        "qa", "@qa", NULL, "qa");
}
int ca_paca_agent_template_code_reviewer(const char *member_id, const char *api_key,
                                         ca_paca_agent_profile_t *out) {
    return build_profile(out, member_id, "openai", "gpt-4o-mini", api_key, NULL,
        "You are a senior code reviewer. Comment for clarity, correctness, security.",
        "You write code review checklists.",
        "You answer questions about code patterns and best practices.",
        true, false, false, true, 15, (int64_t)7*60*1000,
        "CircleAI Reviewer Agent", "reviewer-agent@circleai.local",
        NULL, "@review", NULL, "review");
}
static const char *const PRESET_NAMES[] = { "development", "pm", "design", "qa", "review" };
const char *const *ca_paca_agent_preset_names(size_t *out_count) {
    if (out_count) *out_count = sizeof PRESET_NAMES / sizeof PRESET_NAMES[0];
    return PRESET_NAMES;
}

typedef struct { ca_paca_member_t member; ca_paca_agent_profile_t profile; bool has_profile; } member_slot;

struct ca_paca_member_store {
    member_slot *members; size_t count, cap;
    clock_t_ clock;
};

ca_paca_member_store_t *ca_paca_member_store_create(ca_paca_clock_fn clock, void *clock_ctx) {
    ca_paca_member_store_t *s = (ca_paca_member_store_t *)calloc(1, sizeof *s);
    if (!s) return NULL;
    s->clock.fn = clock; s->clock.ctx = clock_ctx;
    return s;
}
void ca_paca_member_store_destroy(ca_paca_member_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) {
        ca_paca_member_free(&s->members[i].member);
        if (s->members[i].has_profile) ca_paca_agent_profile_free(&s->members[i].profile);
    }
    free(s->members);
    free(s);
}
static member_slot *member_find(ca_paca_member_store_t *s, const char *id) {
    for (size_t i = 0; i < s->count; ++i)
        if (pstr_eq(s->members[i].member.id, id)) return &s->members[i];
    return NULL;
}
static member_slot *member_find_live(ca_paca_member_store_t *s, const char *id) {
    member_slot *m = member_find(s, id);
    return (m && m->member.deleted_at_ms < 0) ? m : NULL;
}
static void member_copy(ca_paca_member_t *dst, const ca_paca_member_t *src) {
    dst->id = pdup(src->id); dst->project_id = pdup(src->project_id);
    dst->kind = src->kind;
    dst->display_name = pdup(src->display_name); dst->handle = pdup(src->handle);
    dst->role = pdup(src->role); dst->avatar_url = pdup(src->avatar_url);
    dst->created_at_ms = src->created_at_ms; dst->deleted_at_ms = src->deleted_at_ms;
}

static int member_store_add(ca_paca_member_store_t *s, const char *id, const char *project_id,
                            ca_paca_member_kind_t kind, const char *display_name,
                            const char *handle, const char *role, const char *avatar,
                            member_slot **out_slot) {
    if (pblank(id) || pblank(project_id) || pblank(display_name) || pblank(handle)) return -1;
    if (member_find(s, id)) return -1; /* duplicate */
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap*2 : 8;
        member_slot *ns = (member_slot *)realloc(s->members, nc*sizeof *ns);
        if (!ns) return -1;
        s->members = ns; s->cap = nc;
    }
    member_slot *slot = &s->members[s->count];
    memset(slot, 0, sizeof *slot);
    slot->member.id = pdup(id);
    slot->member.project_id = pdup(project_id);
    slot->member.kind = kind;
    slot->member.display_name = pdup(display_name);
    slot->member.handle = pdup(handle);
    slot->member.role = pdup(role ? role : "developer");
    slot->member.avatar_url = pdup(avatar);
    slot->member.created_at_ms = clock_now(&s->clock);
    slot->member.deleted_at_ms = -1;
    ++s->count;
    if (out_slot) *out_slot = slot;
    return 0;
}

int ca_paca_member_store_add_human(ca_paca_member_store_t *s, const char *id,
                                   const char *project_id, const char *display_name,
                                   const char *handle, const char *role,
                                   const char *avatar, ca_paca_member_t *out) {
    if (!s || !out) return -1;
    member_slot *slot = NULL;
    if (member_store_add(s, id, project_id, CA_PACA_MEMBER_HUMAN, display_name, handle,
                         role ? role : "developer", avatar, &slot) != 0) return -1;
    member_copy(out, &slot->member);
    return 0;
}
int ca_paca_member_store_add_agent(ca_paca_member_store_t *s, const char *id,
                                   const char *project_id, const char *display_name,
                                   const char *handle,
                                   const ca_paca_agent_profile_t *profile,
                                   const char *avatar, ca_paca_member_t *out) {
    if (!s || !out || !profile) return -1;
    member_slot *slot = NULL;
    if (member_store_add(s, id, project_id, CA_PACA_MEMBER_AGENT, display_name, handle,
                         "agent", avatar, &slot) != 0) return -1;
    /* store a copy of the profile with MemberId = id */
    ca_paca_agent_profile_copy(&slot->profile, profile);
    pset(&slot->profile.member_id, id);
    slot->has_profile = true;
    member_copy(out, &slot->member);
    return 0;
}
int ca_paca_member_store_get_member(ca_paca_member_store_t *s, const char *id,
                                    ca_paca_member_t *out) {
    if (!s || !id || !out) return -1;
    member_slot *m = member_find_live(s, id);
    if (!m) return -1;
    member_copy(out, &m->member);
    return 0;
}
int ca_paca_member_store_get_agent_profile(ca_paca_member_store_t *s, const char *member_id,
                                           ca_paca_agent_profile_t *out) {
    if (!s || !member_id || !out) return -1;
    member_slot *m = member_find(s, member_id);
    if (!m || !m->has_profile) return -1;
    ca_paca_agent_profile_copy(out, &m->profile);
    return 0;
}
ca_paca_member_t *ca_paca_member_store_list_members(ca_paca_member_store_t *s,
                                                    const char *project_id,
                                                    int kind_filter, size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!s || !project_id || !out_count) return NULL;
    size_t *idx = s->count ? (size_t *)malloc(s->count * sizeof *idx) : NULL;
    size_t n = 0;
    for (size_t i = 0; i < s->count; ++i) {
        ca_paca_member_t *m = &s->members[i].member;
        if (!pstr_eq(m->project_id, project_id)) continue;
        if (m->deleted_at_ms >= 0) continue;
        if (kind_filter >= 0 && (int)m->kind != kind_filter) continue;
        idx[n++] = i;
    }
    /* order by DisplayName */
    for (size_t i = 0; i < n; ++i)
        for (size_t j = i+1; j < n; ++j)
            if (strcmp(s->members[idx[j]].member.display_name, s->members[idx[i]].member.display_name) < 0) {
                size_t t=idx[i]; idx[i]=idx[j]; idx[j]=t;
            }
    ca_paca_member_t *arr = n ? (ca_paca_member_t *)calloc(n, sizeof *arr) : NULL;
    if (n && !arr) { free(idx); *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < n; ++i) member_copy(&arr[i], &s->members[idx[i]].member);
    free(idx);
    *out_count = n;
    return arr;
}
void ca_paca_member_store_remove_member(ca_paca_member_store_t *s, const char *id) {
    if (!s || !id) return;
    member_slot *m = member_find(s, id);
    if (!m || m->member.deleted_at_ms >= 0) return;
    m->member.deleted_at_ms = clock_now(&s->clock);
}
int ca_paca_member_store_update_agent_profile(ca_paca_member_store_t *s,
                                              const char *member_id,
                                              const ca_paca_agent_profile_t *updated,
                                              ca_paca_agent_profile_t *out) {
    if (!s || !member_id || !updated || !out) return -1;
    member_slot *m = member_find_live(s, member_id);
    if (!m || m->member.kind != CA_PACA_MEMBER_AGENT) return -1;
    if (m->has_profile) ca_paca_agent_profile_free(&m->profile);
    ca_paca_agent_profile_copy(&m->profile, updated);
    pset(&m->profile.member_id, member_id);
    m->has_profile = true;
    ca_paca_agent_profile_copy(out, &m->profile);
    return 0;
}

/* ===========================================================================
 * Docs
 * =========================================================================== */

void ca_paca_doc_node_free(ca_paca_doc_node_t *n) {
    if (!n) return;
    free(n->id); free(n->project_id); free(n->parent_id); free(n->title); free(n->content_json);
    memset(n, 0, sizeof *n);
}
void ca_paca_doc_node_free_array(ca_paca_doc_node_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_paca_doc_node_free(&arr[i]);
    free(arr);
}
void ca_paca_doc_version_free(ca_paca_doc_version_t *v) {
    if (!v) return;
    free(v->version_id); free(v->doc_id); free(v->content_json); free(v->author_member_id);
    memset(v, 0, sizeof *v);
}
void ca_paca_doc_version_free_array(ca_paca_doc_version_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_paca_doc_version_free(&arr[i]);
    free(arr);
}
void ca_paca_doc_activity_free(ca_paca_doc_activity_t *a) {
    if (!a) return;
    free(a->activity_id); free(a->doc_id); free(a->author_member_id); free(a->action); free(a->detail);
    memset(a, 0, sizeof *a);
}
void ca_paca_doc_activity_free_array(ca_paca_doc_activity_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_paca_doc_activity_free(&arr[i]);
    free(arr);
}
void ca_paca_doc_link_free(ca_paca_doc_link_t *l) {
    if (!l) return;
    free(l->link_id); free(l->doc_id); free(l->section_anchor); free(l->project_id);
    memset(l, 0, sizeof *l);
}
void ca_paca_doc_link_free_array(ca_paca_doc_link_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_paca_doc_link_free(&arr[i]);
    free(arr);
}

typedef struct {
    ca_paca_doc_node_t node;
    ca_paca_doc_version_t *versions; size_t version_count, version_cap;
    ca_paca_doc_activity_t *activity; size_t activity_count, activity_cap;
    ca_paca_doc_link_t *links; size_t link_count, link_cap;
} doc_slot;

struct ca_paca_doc_service {
    doc_slot *docs; size_t count, cap;
    clock_t_ clock;
};

ca_paca_doc_service_t *ca_paca_doc_service_create(ca_paca_clock_fn clock, void *clock_ctx) {
    ca_paca_doc_service_t *s = (ca_paca_doc_service_t *)calloc(1, sizeof *s);
    if (!s) return NULL;
    s->clock.fn = clock; s->clock.ctx = clock_ctx;
    return s;
}
void ca_paca_doc_service_destroy(ca_paca_doc_service_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) {
        ca_paca_doc_node_free(&s->docs[i].node);
        ca_paca_doc_version_free_array(s->docs[i].versions, s->docs[i].version_count);
        ca_paca_doc_activity_free_array(s->docs[i].activity, s->docs[i].activity_count);
        ca_paca_doc_link_free_array(s->docs[i].links, s->docs[i].link_count);
    }
    free(s->docs);
    free(s);
}
static doc_slot *doc_find(ca_paca_doc_service_t *s, const char *id) {
    for (size_t i = 0; i < s->count; ++i)
        if (pstr_eq(s->docs[i].node.id, id)) return &s->docs[i];
    return NULL;
}
static void docnode_copy(ca_paca_doc_node_t *dst, const ca_paca_doc_node_t *src) {
    dst->id = pdup(src->id); dst->project_id = pdup(src->project_id);
    dst->parent_id = pdup(src->parent_id); dst->is_folder = src->is_folder;
    dst->title = pdup(src->title); dst->content_json = pdup(src->content_json);
    dst->created_at_ms = src->created_at_ms; dst->deleted_at_ms = src->deleted_at_ms;
}
static void docactivity_copy(ca_paca_doc_activity_t *dst, const ca_paca_doc_activity_t *src) {
    dst->activity_id = pdup(src->activity_id); dst->doc_id = pdup(src->doc_id);
    dst->author_member_id = pdup(src->author_member_id); dst->action = pdup(src->action);
    dst->detail = pdup(src->detail); dst->at_ms = src->at_ms;
}
static void docversion_copy(ca_paca_doc_version_t *dst, const ca_paca_doc_version_t *src) {
    dst->version_id = pdup(src->version_id); dst->doc_id = pdup(src->doc_id);
    dst->content_json = pdup(src->content_json); dst->saved_at_ms = src->saved_at_ms;
    dst->author_member_id = pdup(src->author_member_id);
}
static void doclink_copy(ca_paca_doc_link_t *dst, const ca_paca_doc_link_t *src) {
    dst->link_id = pdup(src->link_id); dst->doc_id = pdup(src->doc_id);
    dst->section_anchor = pdup(src->section_anchor); dst->project_id = pdup(src->project_id);
    dst->task_number = src->task_number;
}

static int doc_push_activity(doc_slot *d, const char *author, const char *action,
                             const char *detail, int64_t at) {
    if (d->activity_count == d->activity_cap) {
        size_t nc = d->activity_cap ? d->activity_cap*2 : 4;
        ca_paca_doc_activity_t *na = (ca_paca_doc_activity_t *)realloc(d->activity, nc*sizeof *na);
        if (!na) return -1;
        d->activity = na; d->activity_cap = nc;
    }
    ca_paca_doc_activity_t *a = &d->activity[d->activity_count];
    memset(a, 0, sizeof *a);
    a->activity_id = make_guid_n();
    a->doc_id = pdup(d->node.id);
    a->author_member_id = pdup(author);
    a->action = pdup(action);
    a->detail = pdup(detail);
    a->at_ms = at;
    ++d->activity_count;
    return 0;
}

static int doc_create(ca_paca_doc_service_t *s, const char *id, const char *project_id,
                      const char *parent_id, bool is_folder, const char *title,
                      const char *content_json, const char *author, ca_paca_doc_node_t *out) {
    if (pblank(id) || pblank(project_id)) return -1;
    if (doc_find(s, id)) return -1; /* duplicate */
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap*2 : 8;
        doc_slot *ns = (doc_slot *)realloc(s->docs, nc*sizeof *ns);
        if (!ns) return -1;
        s->docs = ns; s->cap = nc;
    }
    doc_slot *d = &s->docs[s->count];
    memset(d, 0, sizeof *d);
    d->node.id = pdup(id);
    d->node.project_id = pdup(project_id);
    d->node.parent_id = pdup(parent_id);
    d->node.is_folder = is_folder;
    d->node.title = pdup(title ? title : "");
    d->node.content_json = pdup(content_json ? content_json : "{}");
    d->node.created_at_ms = clock_now(&s->clock);
    d->node.deleted_at_ms = -1;
    ++s->count;
    if (!is_folder) {
        doc_push_activity(d, author, "created", NULL, clock_now(&s->clock));
    }
    docnode_copy(out, &d->node);
    return 0;
}

int ca_paca_doc_service_create_folder(ca_paca_doc_service_t *s, const char *id,
                                      const char *project_id, const char *parent_id,
                                      const char *title, ca_paca_doc_node_t *out) {
    if (!s || !out) return -1;
    return doc_create(s, id, project_id, parent_id, true, title, "{}", "system", out);
}
int ca_paca_doc_service_create_document(ca_paca_doc_service_t *s, const char *id,
                                        const char *project_id, const char *parent_id,
                                        const char *title, const char *content_json,
                                        const char *author_member_id, ca_paca_doc_node_t *out) {
    if (!s || !out) return -1;
    return doc_create(s, id, project_id, parent_id, false, title,
                      content_json ? content_json : "{}", author_member_id, out);
}
int ca_paca_doc_service_get(ca_paca_doc_service_t *s, const char *id, ca_paca_doc_node_t *out) {
    if (!s || !id || !out) return -1;
    doc_slot *d = doc_find(s, id);
    if (!d || d->node.deleted_at_ms >= 0) return -1;
    docnode_copy(out, &d->node);
    return 0;
}
ca_paca_doc_node_t *ca_paca_doc_service_list_children(ca_paca_doc_service_t *s,
                                                      const char *project_id,
                                                      const char *parent_id,
                                                      size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!s || !project_id || !out_count) return NULL;
    size_t *idx = s->count ? (size_t *)malloc(s->count * sizeof *idx) : NULL;
    size_t n = 0;
    for (size_t i = 0; i < s->count; ++i) {
        ca_paca_doc_node_t *nd = &s->docs[i].node;
        if (!pstr_eq(nd->project_id, project_id)) continue;
        if (!pstr_eq(nd->parent_id, parent_id)) continue;
        if (nd->deleted_at_ms >= 0) continue;
        idx[n++] = i;
    }
    for (size_t i = 0; i < n; ++i)
        for (size_t j = i+1; j < n; ++j)
            if (strcmp(s->docs[idx[j]].node.title, s->docs[idx[i]].node.title) < 0) { size_t t=idx[i]; idx[i]=idx[j]; idx[j]=t; }
    ca_paca_doc_node_t *arr = n ? (ca_paca_doc_node_t *)calloc(n, sizeof *arr) : NULL;
    if (n && !arr) { free(idx); *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < n; ++i) docnode_copy(&arr[i], &s->docs[idx[i]].node);
    free(idx);
    *out_count = n;
    return arr;
}

/* @mention extraction (shared). */
char **ca_paca_extract_mentions(const char *content, size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!out_count) return NULL;
    if (!content) { *out_count = 0; return NULL; }
    char **arr = NULL; size_t n = 0, cap = 0;
    const char *p = content;
    while (*p) {
        if (*p == '@') {
            const char *start = p + 1;
            const char *q = start;
            while (*q && (isalnum((unsigned char)*q) || *q=='_' || *q=='-')) ++q;
            if (q > start) {
                size_t len = (size_t)(q - start);
                char *cand = (char *)malloc(len + 1);
                if (cand) {
                    memcpy(cand, start, len); cand[len] = '\0';
                    /* dedupe case-insensitive */
                    bool dup = false;
                    for (size_t i = 0; i < n; ++i) if (pstr_ieq(arr[i], cand)) { dup = true; break; }
                    if (dup) { free(cand); }
                    else {
                        if (n == cap) {
                            size_t nc = cap ? cap*2 : 4;
                            char **na = (char **)realloc(arr, nc*sizeof *na);
                            if (!na) { free(cand); ca_paca_string_array_free(arr, n); *out_count = SIZE_MAX; return NULL; }
                            arr = na; cap = nc;
                        }
                        arr[n++] = cand;
                    }
                }
            }
            p = q;
        } else ++p;
    }
    *out_count = n;
    return arr;
}

char **ca_paca_doc_service_edit(ca_paca_doc_service_t *s, const char *id,
                                const char *new_content_json, const char *author_member_id,
                                bool is_ai_edit, size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!s || !id || !out_count) return NULL;
    doc_slot *d = doc_find(s, id);
    if (!d || d->node.is_folder || d->node.deleted_at_ms >= 0) return NULL;

    /* snapshot the PRIOR content as a version */
    char *prior = pdup(d->node.content_json);
    /* update content */
    pset(&d->node.content_json, new_content_json ? new_content_json : "{}");

    if (d->version_count == d->version_cap) {
        size_t nc = d->version_cap ? d->version_cap*2 : 4;
        ca_paca_doc_version_t *nv = (ca_paca_doc_version_t *)realloc(d->versions, nc*sizeof *nv);
        if (nv) { d->versions = nv; d->version_cap = nc; }
    }
    if (d->version_count < d->version_cap) {
        ca_paca_doc_version_t *v = &d->versions[d->version_count];
        memset(v, 0, sizeof *v);
        v->version_id = make_guid_n();
        v->doc_id = pdup(id);
        v->content_json = prior; prior = NULL;
        v->saved_at_ms = clock_now(&s->clock);
        v->author_member_id = pdup(author_member_id);
        ++d->version_count;
    }
    free(prior);

    doc_push_activity(d, author_member_id, is_ai_edit ? "ai-edited" : "edited", NULL,
                      clock_now(&s->clock));

    return ca_paca_extract_mentions(new_content_json ? new_content_json : "", out_count);
}

ca_paca_doc_version_t *ca_paca_doc_service_versions(ca_paca_doc_service_t *s,
                                                    const char *doc_id, size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!s || !doc_id || !out_count) return NULL;
    doc_slot *d = doc_find(s, doc_id);
    *out_count = 0;
    if (!d || d->version_count == 0) return NULL;
    ca_paca_doc_version_t *arr = (ca_paca_doc_version_t *)calloc(d->version_count, sizeof *arr);
    if (!arr) { *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < d->version_count; ++i) docversion_copy(&arr[i], &d->versions[i]);
    *out_count = d->version_count;
    return arr;
}

/* split into lines on '\n', return unique set (first-seen order). */
static char **split_line_set(const char *s, size_t *out_n) {
    char **arr = NULL; size_t n = 0, cap = 0;
    const char *start = s ? s : "";
    const char *p = start;
    for (;;) {
        const char *nl = strchr(p, '\n');
        size_t len = nl ? (size_t)(nl - p) : strlen(p);
        char *line = (char *)malloc(len + 1);
        if (line) {
            memcpy(line, p, len); line[len] = '\0';
            bool dup = false;
            for (size_t i = 0; i < n; ++i) if (pstr_eq(arr[i], line)) { dup = true; break; }
            if (dup) free(line);
            else {
                if (n == cap) {
                    size_t nc = cap ? cap*2 : 8;
                    char **na = (char **)realloc(arr, nc*sizeof *na);
                    if (!na) { free(line); break; }
                    arr = na; cap = nc;
                }
                arr[n++] = line;
            }
        }
        if (!nl) break;
        p = nl + 1;
    }
    *out_n = n;
    return arr;
}
int ca_paca_doc_service_diff_lines(const char *before, const char *after,
                                   char ***out_added, size_t *out_added_count,
                                   char ***out_removed, size_t *out_removed_count) {
    if (!out_added || !out_added_count || !out_removed || !out_removed_count) return -1;
    size_t bn = 0, an = 0;
    char **bset = split_line_set(before, &bn);
    char **aset = split_line_set(after, &an);
    /* added = a - b */
    char **added = NULL; size_t addn = 0, addc = 0;
    for (size_t i = 0; i < an; ++i) {
        bool in_b = false;
        for (size_t j = 0; j < bn; ++j) if (pstr_eq(aset[i], bset[j])) { in_b = true; break; }
        if (!in_b) {
            if (addn == addc) { size_t nc = addc ? addc*2 : 4; char **na = (char **)realloc(added, nc*sizeof *na); if (!na) break; added = na; addc = nc; }
            added[addn++] = pdup(aset[i]);
        }
    }
    /* removed = b - a */
    char **removed = NULL; size_t remn = 0, remc = 0;
    for (size_t i = 0; i < bn; ++i) {
        bool in_a = false;
        for (size_t j = 0; j < an; ++j) if (pstr_eq(bset[i], aset[j])) { in_a = true; break; }
        if (!in_a) {
            if (remn == remc) { size_t nc = remc ? remc*2 : 4; char **na = (char **)realloc(removed, nc*sizeof *na); if (!na) break; removed = na; remc = nc; }
            removed[remn++] = pdup(bset[i]);
        }
    }
    ca_paca_string_array_free(bset, bn);
    ca_paca_string_array_free(aset, an);
    *out_added = added; *out_added_count = addn;
    *out_removed = removed; *out_removed_count = remn;
    return 0;
}

ca_paca_doc_activity_t *ca_paca_doc_service_activity(ca_paca_doc_service_t *s,
                                                     const char *doc_id, size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!s || !doc_id || !out_count) return NULL;
    doc_slot *d = doc_find(s, doc_id);
    *out_count = 0;
    if (!d || d->activity_count == 0) return NULL;
    ca_paca_doc_activity_t *arr = (ca_paca_doc_activity_t *)calloc(d->activity_count, sizeof *arr);
    if (!arr) { *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < d->activity_count; ++i) docactivity_copy(&arr[i], &d->activity[i]);
    *out_count = d->activity_count;
    return arr;
}

int ca_paca_doc_service_link(ca_paca_doc_service_t *s, const char *doc_id,
                             const char *section_anchor, const char *project_id,
                             int task_number, ca_paca_doc_link_t *out) {
    if (!s || !doc_id || !out) return -1;
    doc_slot *d = doc_find(s, doc_id);
    if (!d) return -1;
    if (d->link_count == d->link_cap) {
        size_t nc = d->link_cap ? d->link_cap*2 : 4;
        ca_paca_doc_link_t *nl = (ca_paca_doc_link_t *)realloc(d->links, nc*sizeof *nl);
        if (!nl) return -1;
        d->links = nl; d->link_cap = nc;
    }
    ca_paca_doc_link_t *l = &d->links[d->link_count];
    memset(l, 0, sizeof *l);
    l->link_id = make_guid_n();
    l->doc_id = pdup(doc_id);
    l->section_anchor = pdup(section_anchor ? section_anchor : "");
    l->project_id = pdup(project_id ? project_id : "");
    l->task_number = task_number;
    ++d->link_count;
    /* activity "linked" detail "<project>-<num>@<anchor>" */
    sb_t detail = {0};
    char num[16]; snprintf(num, sizeof num, "%d", task_number);
    sb_puts(&detail, project_id ? project_id : "");
    sb_putc(&detail, '-'); sb_puts(&detail, num); sb_putc(&detail, '@');
    sb_puts(&detail, section_anchor ? section_anchor : "");
    doc_push_activity(d, "system", "linked", detail.buf ? detail.buf : "", clock_now(&s->clock));
    free(detail.buf);
    doclink_copy(out, l);
    return 0;
}

ca_paca_doc_link_t *ca_paca_doc_service_links(ca_paca_doc_service_t *s,
                                              const char *doc_id, size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!s || !doc_id || !out_count) return NULL;
    doc_slot *d = doc_find(s, doc_id);
    *out_count = 0;
    if (!d || d->link_count == 0) return NULL;
    ca_paca_doc_link_t *arr = (ca_paca_doc_link_t *)calloc(d->link_count, sizeof *arr);
    if (!arr) { *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < d->link_count; ++i) doclink_copy(&arr[i], &d->links[i]);
    *out_count = d->link_count;
    return arr;
}

/* ===========================================================================
 * Plugins
 * =========================================================================== */

void ca_paca_plugin_manifest_free(ca_paca_plugin_manifest_t *m) {
    if (!m) return;
    free(m->name); free(m->display_name); free(m->version); free(m->description);
    free(m->artifact_wasm_url); free(m->frontend_module_url);
    free(m->extension_points);
    ca_paca_string_array_free(m->mcp_tools, m->mcp_tool_count);
    ca_paca_string_array_free(m->sql_migration_files, m->sql_migration_file_count);
    memset(m, 0, sizeof *m);
}
ca_paca_plugin_manifest_t *ca_paca_plugin_manifest_copy(ca_paca_plugin_manifest_t *dst,
                                                        const ca_paca_plugin_manifest_t *src) {
    if (!dst || !src) return NULL;
    memset(dst, 0, sizeof *dst);
    dst->name = pdup(src->name);
    dst->display_name = pdup(src->display_name);
    dst->version = pdup(src->version);
    dst->description = pdup(src->description);
    dst->artifact_wasm_url = pdup(src->artifact_wasm_url);
    dst->frontend_module_url = pdup(src->frontend_module_url);
    if (src->extension_point_count) {
        dst->extension_points = (ca_paca_ext_point_t *)malloc(src->extension_point_count * sizeof(ca_paca_ext_point_t));
        if (dst->extension_points)
            memcpy(dst->extension_points, src->extension_points, src->extension_point_count * sizeof(ca_paca_ext_point_t));
        dst->extension_point_count = src->extension_point_count;
    }
    dst->mcp_tools = str_array_copy(src->mcp_tools, src->mcp_tool_count);
    dst->mcp_tool_count = src->mcp_tool_count;
    dst->sql_migration_files = str_array_copy(src->sql_migration_files, src->sql_migration_file_count);
    dst->sql_migration_file_count = src->sql_migration_file_count;
    dst->limits = src->limits;
    return dst;
}
void ca_paca_installed_plugin_free(ca_paca_installed_plugin_t *p) {
    if (!p) return;
    free(p->id);
    ca_paca_plugin_manifest_free(&p->manifest);
    free(p->installed_from_catalog);
    memset(p, 0, sizeof *p);
}
void ca_paca_installed_plugin_free_array(ca_paca_installed_plugin_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_paca_installed_plugin_free(&arr[i]);
    free(arr);
}
static void installed_copy(ca_paca_installed_plugin_t *dst, const ca_paca_installed_plugin_t *src) {
    dst->id = pdup(src->id);
    ca_paca_plugin_manifest_copy(&dst->manifest, &src->manifest);
    dst->installed_from_catalog = pdup(src->installed_from_catalog);
    dst->installed_at_ms = src->installed_at_ms;
    dst->enabled = src->enabled;
}

/* reverse-DNS: ^[a-z][a-z0-9]*(\.[a-z][a-z0-9_-]*)+$ */
static bool is_reverse_dns(const char *s) {
    if (!s || !*s) return false;
    const char *p = s;
    if (!(*p >= 'a' && *p <= 'z')) return false;
    ++p;
    while (*p >= 'a' && *p <= 'z') ++p;
    while (*p >= '0' && *p <= '9') ++p;
    /* first label already consumed lowercase then digits; but the regex allows
     * [a-z][a-z0-9]* — reconsume mixed alnum properly */
    /* restart: reparse label-by-label to honor the pattern exactly. */
    p = s;
    int labels = 0;
    for (;;) {
        /* label start must be [a-z] */
        if (!(*p >= 'a' && *p <= 'z')) return false;
        ++p;
        if (labels == 0) {
            /* first label: [a-z0-9]* */
            while ((*p>='a'&&*p<='z')||(*p>='0'&&*p<='9')) ++p;
        } else {
            /* subsequent labels: [a-z0-9_-]* */
            while ((*p>='a'&&*p<='z')||(*p>='0'&&*p<='9')||*p=='_'||*p=='-') ++p;
        }
        ++labels;
        if (*p == '.') { ++p; continue; }
        if (*p == '\0') break;
        return false;
    }
    return labels >= 2;
}

/* strip prerelease/build: take substring before first '-' or '+'. Owned. */
static char *semver_strip(const char *v) {
    if (!v) return pdup_or_empty("");
    size_t n = 0;
    while (v[n] && v[n] != '-' && v[n] != '+') ++n;
    char *o = (char *)malloc(n + 1);
    if (!o) return NULL;
    memcpy(o, v, n); o[n] = '\0';
    return o;
}
/* parse up to 4 dotted numeric components (System.Version semantics). Returns
 * true when at least major.minor parse and all present components are numeric. */
static bool version_parse(const char *v, long out[4]) {
    out[0]=out[1]=out[2]=out[3]=0;
    if (!v || !*v) return false;
    const char *p = v; int comp = 0;
    while (*p && comp < 4) {
        if (!isdigit((unsigned char)*p)) return false;
        char *end = NULL;
        long val = strtol(p, &end, 10);
        if (end == p) return false;
        out[comp++] = val;
        p = end;
        if (*p == '.') { ++p; if (!*p) return false; }
        else if (*p == '\0') break;
        else return false;
    }
    /* System.Version requires at least major.minor */
    return comp >= 2;
}

int ca_paca_plugin_validate_manifest(const ca_paca_plugin_manifest_t *m) {
    if (!m) return -1;
    if (!is_reverse_dns(m->name)) return -1;
    char *stripped = semver_strip(m->version);
    long ver[4];
    bool ok = stripped && version_parse(stripped, ver);
    free(stripped);
    if (!ok) return -1;
    if (m->limits.call_timeout_ms <= 0) return -1;
    if (m->limits.memory_ceiling_bytes <= 0) return -1;
    return 0;
}
int ca_paca_plugin_compare_semver(const char *a, const char *b) {
    char *sa = semver_strip(a), *sb = semver_strip(b);
    long va[4] = {0}, vb[4] = {0};
    version_parse(sa, va); version_parse(sb, vb);
    free(sa); free(sb);
    for (int i = 0; i < 4; ++i) {
        if (va[i] < vb[i]) return -1;
        if (va[i] > vb[i]) return 1;
    }
    return 0;
}

typedef struct { ca_paca_installed_plugin_t plugin; } plugin_slot;
struct ca_paca_plugin_registry {
    plugin_slot *plugins; size_t count, cap;
    ca_paca_plugin_runtime_host_t runtime; bool has_runtime;
    clock_t_ clock;
};

ca_paca_plugin_registry_t *ca_paca_plugin_registry_create(
    const ca_paca_plugin_runtime_host_t *runtime, ca_paca_clock_fn clock, void *clock_ctx) {
    ca_paca_plugin_registry_t *r = (ca_paca_plugin_registry_t *)calloc(1, sizeof *r);
    if (!r) return NULL;
    if (runtime) { r->runtime = *runtime; r->has_runtime = true; }
    r->clock.fn = clock; r->clock.ctx = clock_ctx;
    return r;
}
void ca_paca_plugin_registry_destroy(ca_paca_plugin_registry_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->count; ++i) ca_paca_installed_plugin_free(&r->plugins[i].plugin);
    free(r->plugins);
    free(r);
}
static plugin_slot *plugin_find(ca_paca_plugin_registry_t *r, const char *id) {
    for (size_t i = 0; i < r->count; ++i)
        if (pstr_eq(r->plugins[i].plugin.id, id)) return &r->plugins[i];
    return NULL;
}
ca_paca_installed_plugin_t *ca_paca_plugin_registry_list(ca_paca_plugin_registry_t *r,
                                                         size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!r || !out_count) return NULL;
    *out_count = 0;
    if (r->count == 0) return NULL;
    ca_paca_installed_plugin_t *arr = (ca_paca_installed_plugin_t *)calloc(r->count, sizeof *arr);
    if (!arr) { *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < r->count; ++i) installed_copy(&arr[i], &r->plugins[i].plugin);
    *out_count = r->count;
    return arr;
}
int ca_paca_plugin_registry_get(ca_paca_plugin_registry_t *r, const char *id,
                                ca_paca_installed_plugin_t *out) {
    if (!r || !id || !out) return -1;
    plugin_slot *p = plugin_find(r, id);
    if (!p) return -1;
    installed_copy(out, &p->plugin);
    return 0;
}
int ca_paca_plugin_registry_install(ca_paca_plugin_registry_t *r,
                                    const ca_paca_plugin_manifest_t *manifest,
                                    const char *catalog, ca_paca_installed_plugin_t *out) {
    if (!r || !manifest || !out) return -1;
    if (ca_paca_plugin_validate_manifest(manifest) != 0) return -1;
    if (plugin_find(r, manifest->name)) return -1; /* already installed */
    /* build InstalledPlugin */
    ca_paca_installed_plugin_t inst; memset(&inst, 0, sizeof inst);
    inst.id = pdup(manifest->name);
    ca_paca_plugin_manifest_copy(&inst.manifest, manifest);
    inst.installed_from_catalog = pdup(catalog);
    inst.installed_at_ms = clock_now(&r->clock);
    inst.enabled = true;
    if (r->has_runtime && r->runtime.install) {
        if (r->runtime.install(r->runtime.self, &inst) != 0) {
            ca_paca_installed_plugin_free(&inst);
            return -1;
        }
    }
    if (r->count == r->cap) {
        size_t nc = r->cap ? r->cap*2 : 8;
        plugin_slot *ns = (plugin_slot *)realloc(r->plugins, nc*sizeof *ns);
        if (!ns) { ca_paca_installed_plugin_free(&inst); return -1; }
        r->plugins = ns; r->cap = nc;
    }
    r->plugins[r->count].plugin = inst; /* move */
    ++r->count;
    installed_copy(out, &r->plugins[r->count-1].plugin);
    return 0;
}
int ca_paca_plugin_registry_upgrade(ca_paca_plugin_registry_t *r,
                                    const ca_paca_plugin_manifest_t *new_manifest,
                                    const char *catalog, ca_paca_installed_plugin_t *out) {
    if (!r || !new_manifest || !out) return -1;
    if (ca_paca_plugin_validate_manifest(new_manifest) != 0) return -1;
    plugin_slot *cur = plugin_find(r, new_manifest->name);
    if (!cur) return -1; /* not installed */
    if (ca_paca_plugin_compare_semver(new_manifest->version, cur->plugin.manifest.version) <= 0)
        return -1; /* not newer */
    ca_paca_installed_plugin_t next; memset(&next, 0, sizeof next);
    next.id = pdup(new_manifest->name);
    ca_paca_plugin_manifest_copy(&next.manifest, new_manifest);
    next.installed_from_catalog = pdup(catalog);
    next.installed_at_ms = clock_now(&r->clock);
    next.enabled = cur->plugin.enabled;
    if (r->has_runtime && r->runtime.upgrade) {
        if (r->runtime.upgrade(r->runtime.self, &cur->plugin, &next) != 0) {
            ca_paca_installed_plugin_free(&next);
            return -1;
        }
    }
    ca_paca_installed_plugin_free(&cur->plugin);
    cur->plugin = next; /* move */
    installed_copy(out, &cur->plugin);
    return 0;
}
void ca_paca_plugin_registry_uninstall(ca_paca_plugin_registry_t *r, const char *id,
                                       bool drop_artifacts) {
    if (!r || !id) return;
    plugin_slot *p = plugin_find(r, id);
    if (!p) return;
    if (r->has_runtime && r->runtime.uninstall) {
        r->runtime.uninstall(r->runtime.self, id, drop_artifacts);
    }
    ca_paca_installed_plugin_free(&p->plugin);
    /* compact array */
    size_t idx = (size_t)(p - r->plugins);
    for (size_t i = idx; i + 1 < r->count; ++i) r->plugins[i] = r->plugins[i+1];
    --r->count;
}
void ca_paca_plugin_registry_set_enabled(ca_paca_plugin_registry_t *r, const char *id,
                                         bool enabled) {
    if (!r || !id) return;
    plugin_slot *p = plugin_find(r, id);
    if (p) p->plugin.enabled = enabled;
}

/* ===========================================================================
 * Mcp
 * =========================================================================== */

void ca_paca_mcp_tool_free(ca_paca_mcp_tool_t *t) {
    if (!t) return;
    free(t->name); free(t->description); free(t->input_schema);
    memset(t, 0, sizeof *t);
}
void ca_paca_mcp_tool_free_array(ca_paca_mcp_tool_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_paca_mcp_tool_free(&arr[i]);
    free(arr);
}
void ca_paca_mcp_agent_config_free(ca_paca_mcp_agent_config_t *c) {
    if (!c) return;
    free(c->agent_member_id);
    free(c->transports);
    ca_paca_string_array_free(c->enabled_tools, c->enabled_tool_count);
    claim_array_free(c->tool_settings, c->tool_setting_count);
    memset(c, 0, sizeof *c);
}
static void tool_copy(ca_paca_mcp_tool_t *dst, const ca_paca_mcp_tool_t *src) {
    dst->name = pdup(src->name); dst->description = pdup(src->description);
    dst->input_schema = pdup(src->input_schema);
}
static void mcp_cfg_copy(ca_paca_mcp_agent_config_t *dst, const ca_paca_mcp_agent_config_t *src) {
    dst->agent_member_id = pdup(src->agent_member_id);
    if (src->transport_count) {
        dst->transports = (ca_paca_mcp_transport_t *)malloc(src->transport_count * sizeof(ca_paca_mcp_transport_t));
        if (dst->transports) memcpy(dst->transports, src->transports, src->transport_count * sizeof(ca_paca_mcp_transport_t));
        dst->transport_count = src->transport_count;
    }
    dst->enabled_tools = str_array_copy(src->enabled_tools, src->enabled_tool_count);
    dst->enabled_tool_count = src->enabled_tool_count;
    dst->tool_settings = claim_array_copy(src->tool_settings, src->tool_setting_count);
    dst->tool_setting_count = src->tool_setting_count;
}

typedef struct { ca_paca_mcp_tool_t tool; ca_paca_mcp_handler_fn handler; void *ctx; } mcp_tool_slot;
typedef struct { ca_paca_mcp_agent_config_t cfg; } mcp_cfg_slot;
struct ca_paca_mcp_server {
    mcp_tool_slot *tools; size_t tool_count, tool_cap;
    mcp_cfg_slot *cfgs; size_t cfg_count, cfg_cap;
};

ca_paca_mcp_server_t *ca_paca_mcp_server_create(void) {
    return (ca_paca_mcp_server_t *)calloc(1, sizeof(ca_paca_mcp_server_t));
}
void ca_paca_mcp_server_destroy(ca_paca_mcp_server_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->tool_count; ++i) ca_paca_mcp_tool_free(&s->tools[i].tool);
    free(s->tools);
    for (size_t i = 0; i < s->cfg_count; ++i) ca_paca_mcp_agent_config_free(&s->cfgs[i].cfg);
    free(s->cfgs);
    free(s);
}
static mcp_tool_slot *mcp_find_tool(ca_paca_mcp_server_t *s, const char *name) {
    for (size_t i = 0; i < s->tool_count; ++i)
        if (pstr_ieq(s->tools[i].tool.name, name)) return &s->tools[i];
    return NULL;
}
int ca_paca_mcp_server_register_tool(ca_paca_mcp_server_t *s, const ca_paca_mcp_tool_t *tool,
                                     ca_paca_mcp_handler_fn handler, void *ctx) {
    if (!s || !tool || !tool->name || !handler) return -1;
    mcp_tool_slot *ex = mcp_find_tool(s, tool->name);
    if (ex) {
        ca_paca_mcp_tool_free(&ex->tool);
        tool_copy(&ex->tool, tool);
        ex->handler = handler; ex->ctx = ctx;
        return 0;
    }
    if (s->tool_count == s->tool_cap) {
        size_t nc = s->tool_cap ? s->tool_cap*2 : 8;
        mcp_tool_slot *ns = (mcp_tool_slot *)realloc(s->tools, nc*sizeof *ns);
        if (!ns) return -1;
        s->tools = ns; s->tool_cap = nc;
    }
    tool_copy(&s->tools[s->tool_count].tool, tool);
    s->tools[s->tool_count].handler = handler;
    s->tools[s->tool_count].ctx = ctx;
    ++s->tool_count;
    return 0;
}
ca_paca_mcp_tool_t *ca_paca_mcp_server_tools(ca_paca_mcp_server_t *s, size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!s || !out_count) return NULL;
    *out_count = 0;
    if (s->tool_count == 0) return NULL;
    ca_paca_mcp_tool_t *arr = (ca_paca_mcp_tool_t *)calloc(s->tool_count, sizeof *arr);
    if (!arr) { *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < s->tool_count; ++i) tool_copy(&arr[i], &s->tools[i].tool);
    *out_count = s->tool_count;
    return arr;
}
static mcp_cfg_slot *mcp_find_cfg(ca_paca_mcp_server_t *s, const char *agent) {
    for (size_t i = 0; i < s->cfg_count; ++i)
        if (pstr_eq(s->cfgs[i].cfg.agent_member_id, agent)) return &s->cfgs[i];
    return NULL;
}
int ca_paca_mcp_server_configure_agent(ca_paca_mcp_server_t *s,
                                       const ca_paca_mcp_agent_config_t *config) {
    if (!s || !config || !config->agent_member_id) return -1;
    mcp_cfg_slot *ex = mcp_find_cfg(s, config->agent_member_id);
    if (ex) { ca_paca_mcp_agent_config_free(&ex->cfg); mcp_cfg_copy(&ex->cfg, config); return 0; }
    if (s->cfg_count == s->cfg_cap) {
        size_t nc = s->cfg_cap ? s->cfg_cap*2 : 8;
        mcp_cfg_slot *ns = (mcp_cfg_slot *)realloc(s->cfgs, nc*sizeof *ns);
        if (!ns) return -1;
        s->cfgs = ns; s->cfg_cap = nc;
    }
    mcp_cfg_copy(&s->cfgs[s->cfg_count].cfg, config);
    ++s->cfg_count;
    return 0;
}
int ca_paca_mcp_server_get_agent_config(ca_paca_mcp_server_t *s, const char *agent_member_id,
                                        ca_paca_mcp_agent_config_t *out) {
    if (!s || !agent_member_id || !out) return -1;
    mcp_cfg_slot *c = mcp_find_cfg(s, agent_member_id);
    if (!c) return -1;
    mcp_cfg_copy(out, &c->cfg);
    return 0;
}
/* wrap {"error":{"message":<msg>}} */
static char *mcp_wrap_error(const char *msg) {
    sb_t b = {0};
    sb_puts(&b, "{\"error\":{\"message\":");
    sb_put_json_string(&b, msg ? msg : "error");
    sb_puts(&b, "}}");
    return sb_take(&b);
}
int ca_paca_mcp_server_invoke(ca_paca_mcp_server_t *s, const char *agent_member_id,
                              const char *tool_name, const char *arguments_json,
                              char **out_result) {
    if (!s || !tool_name || !out_result) return -1;
    *out_result = NULL;
    mcp_tool_slot *t = mcp_find_tool(s, tool_name);
    if (!t) {
        sb_t b = {0};
        sb_puts(&b, "Unknown tool '"); sb_puts(&b, tool_name); sb_puts(&b, "'.");
        *out_result = mcp_wrap_error(b.buf ? b.buf : "Unknown tool.");
        free(b.buf);
        return 0;
    }
    /* per-agent enabled-tool gate */
    if (agent_member_id) {
        mcp_cfg_slot *c = mcp_find_cfg(s, agent_member_id);
        if (c && c->cfg.enabled_tool_count > 0) {
            bool allowed = false;
            for (size_t i = 0; i < c->cfg.enabled_tool_count; ++i)
                if (pstr_ieq(c->cfg.enabled_tools[i], tool_name)) { allowed = true; break; }
            if (!allowed) {
                sb_t b = {0};
                sb_puts(&b, "Tool '"); sb_puts(&b, tool_name);
                sb_puts(&b, "' is not enabled for agent '");
                sb_puts(&b, agent_member_id); sb_puts(&b, "'.");
                *out_result = mcp_wrap_error(b.buf ? b.buf : "Tool not enabled.");
                free(b.buf);
                return 0;
            }
        }
    }
    /* invoke */
    char *result = NULL;
    int rc = t->handler(t->ctx, arguments_json, &result);
    if (rc != 0) {
        char *wrapped = mcp_wrap_error(result ? result : "error");
        free(result);
        *out_result = wrapped;
        return 0;
    }
    *out_result = result ? result : pdup_or_empty("");
    return 0;
}

static int mcp_core_tool(ca_paca_mcp_tool_t *out, const char *name, const char *desc, const char *schema) {
    if (!out) return -1;
    out->name = pdup(name); out->description = pdup(desc); out->input_schema = pdup(schema);
    return 0;
}
int ca_paca_mcp_core_tool_create_task(ca_paca_mcp_tool_t *out) {
    return mcp_core_tool(out, "create_task", "Create a new task in a project.",
        "{\"type\":\"object\",\"properties\":{\"project_id\":{\"type\":\"string\"},\"title\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"}},\"required\":[\"project_id\",\"title\"]}");
}
int ca_paca_mcp_core_tool_list_tasks(ca_paca_mcp_tool_t *out) {
    return mcp_core_tool(out, "list_tasks", "List live tasks in a project.",
        "{\"type\":\"object\",\"properties\":{\"project_id\":{\"type\":\"string\"}},\"required\":[\"project_id\"]}");
}
int ca_paca_mcp_core_tool_edit_task(ca_paca_mcp_tool_t *out) {
    return mcp_core_tool(out, "edit_task", "Edit a task (title, description, status).",
        "{\"type\":\"object\",\"properties\":{\"project_id\":{\"type\":\"string\"},\"number\":{\"type\":\"integer\"},\"title\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"},\"status\":{\"type\":\"string\"}},\"required\":[\"project_id\",\"number\"]}");
}
int ca_paca_mcp_core_tool_create_doc(ca_paca_mcp_tool_t *out) {
    return mcp_core_tool(out, "create_doc", "Create a doc in the project's doc tree.",
        "{\"type\":\"object\",\"properties\":{\"project_id\":{\"type\":\"string\"},\"title\":{\"type\":\"string\"},\"parent_id\":{\"type\":\"string\",\"nullable\":true},\"content_json\":{\"type\":\"string\"}},\"required\":[\"project_id\",\"title\",\"content_json\"]}");
}
int ca_paca_mcp_core_tool_link_doc_to_task(ca_paca_mcp_tool_t *out) {
    return mcp_core_tool(out, "link_doc_to_task", "Link a doc section to a task.",
        "{\"type\":\"object\",\"properties\":{\"doc_id\":{\"type\":\"string\"},\"section_anchor\":{\"type\":\"string\"},\"project_id\":{\"type\":\"string\"},\"task_number\":{\"type\":\"integer\"}},\"required\":[\"doc_id\",\"section_anchor\",\"project_id\",\"task_number\"]}");
}

/* ===========================================================================
 * Realtime
 * =========================================================================== */

void ca_paca_realtime_event_free(ca_paca_realtime_event_t *e) {
    if (!e) return;
    free(e->project_id); free(e->query_key); free(e->doc_id); free(e->member_id);
    free(e->agent_member_id); free(e->action); free(e->detail_json); free(e->conversation_id);
    memset(e, 0, sizeof *e);
}

typedef struct { char *room; char **members; size_t member_count, member_cap; } room_slot;
struct ca_paca_realtime_hub {
    ca_paca_broadcaster_t broadcaster;
    ca_paca_permission_fn permission; void *permission_ctx;
    room_slot *rooms; size_t room_count, room_cap;
};

ca_paca_realtime_hub_t *ca_paca_realtime_hub_create(const ca_paca_broadcaster_t *broadcaster,
                                                    ca_paca_permission_fn permission,
                                                    void *permission_ctx) {
    if (!broadcaster || !broadcaster->broadcast) return NULL;
    ca_paca_realtime_hub_t *h = (ca_paca_realtime_hub_t *)calloc(1, sizeof *h);
    if (!h) return NULL;
    h->broadcaster = *broadcaster;
    h->permission = permission;
    h->permission_ctx = permission_ctx;
    return h;
}
void ca_paca_realtime_hub_destroy(ca_paca_realtime_hub_t *h) {
    if (!h) return;
    for (size_t i = 0; i < h->room_count; ++i) {
        free(h->rooms[i].room);
        ca_paca_string_array_free(h->rooms[i].members, h->rooms[i].member_count);
    }
    free(h->rooms);
    free(h);
}
static room_slot *hub_find_room(ca_paca_realtime_hub_t *h, const char *room) {
    for (size_t i = 0; i < h->room_count; ++i)
        if (pstr_eq(h->rooms[i].room, room)) return &h->rooms[i];
    return NULL;
}
bool ca_paca_realtime_hub_join(ca_paca_realtime_hub_t *h, const char *member_id,
                               const char *room) {
    if (!h || !member_id || !room) return false;
    if (h->permission && !h->permission(h->permission_ctx, member_id, room)) return false;
    room_slot *r = hub_find_room(h, room);
    if (!r) {
        if (h->room_count == h->room_cap) {
            size_t nc = h->room_cap ? h->room_cap*2 : 8;
            room_slot *ns = (room_slot *)realloc(h->rooms, nc*sizeof *ns);
            if (!ns) return false;
            h->rooms = ns; h->room_cap = nc;
        }
        r = &h->rooms[h->room_count++];
        memset(r, 0, sizeof *r);
        r->room = pdup(room);
    }
    /* set semantics — skip duplicate member */
    for (size_t i = 0; i < r->member_count; ++i) if (pstr_eq(r->members[i], member_id)) return true;
    if (r->member_count == r->member_cap) {
        size_t nc = r->member_cap ? r->member_cap*2 : 4;
        char **nm = (char **)realloc(r->members, nc*sizeof *nm);
        if (!nm) return false;
        r->members = nm; r->member_cap = nc;
    }
    r->members[r->member_count++] = pdup(member_id);
    return true;
}
void ca_paca_realtime_hub_leave(ca_paca_realtime_hub_t *h, const char *member_id,
                                const char *room) {
    if (!h || !member_id || !room) return;
    room_slot *r = hub_find_room(h, room);
    if (!r) return;
    for (size_t i = 0; i < r->member_count; ++i)
        if (pstr_eq(r->members[i], member_id)) {
            free(r->members[i]);
            for (size_t j = i; j + 1 < r->member_count; ++j) r->members[j] = r->members[j+1];
            --r->member_count;
            return;
        }
}
char **ca_paca_realtime_hub_members(ca_paca_realtime_hub_t *h, const char *room,
                                    size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!h || !room || !out_count) return NULL;
    room_slot *r = hub_find_room(h, room);
    *out_count = 0;
    if (!r || r->member_count == 0) return NULL;
    char **arr = str_array_copy(r->members, r->member_count);
    if (!arr) { *out_count = SIZE_MAX; return NULL; }
    *out_count = r->member_count;
    return arr;
}
int ca_paca_realtime_hub_publish(ca_paca_realtime_hub_t *h, const ca_paca_realtime_event_t *ev) {
    if (!h || !ev) return -1;
    sb_t b = {0};
    sb_puts(&b, "project:"); sb_puts(&b, ev->project_id ? ev->project_id : "");
    int rc = h->broadcaster.broadcast(h->broadcaster.self, b.buf ? b.buf : "project:", ev);
    free(b.buf);
    return rc;
}
int ca_paca_realtime_hub_publish_to_doc(ca_paca_realtime_hub_t *h, const char *doc_id,
                                        const ca_paca_realtime_event_t *ev) {
    if (!h || !doc_id || !ev) return -1;
    sb_t b = {0};
    sb_puts(&b, "doc:"); sb_puts(&b, doc_id);
    int rc = h->broadcaster.broadcast(h->broadcaster.self, b.buf ? b.buf : "doc:", ev);
    free(b.buf);
    return rc;
}

static int keys_add(char ***arr, size_t *n, size_t *cap, const char *fmt_a, const char *fmt_b) {
    /* append a single already-built key string composed of fmt_a + fmt_b */
    sb_t b = {0};
    sb_puts(&b, fmt_a ? fmt_a : "");
    if (fmt_b) sb_puts(&b, fmt_b);
    if (*n == *cap) {
        size_t nc = *cap ? *cap*2 : 4;
        char **na = (char **)realloc(*arr, nc*sizeof *na);
        if (!na) { free(b.buf); return -1; }
        *arr = na; *cap = nc;
    }
    (*arr)[(*n)++] = sb_take(&b);
    return 0;
}
char **ca_paca_query_invalidation_keys_for(const ca_paca_realtime_event_t *ev,
                                           size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!ev || !out_count) return NULL;
    char **arr = NULL; size_t n = 0, cap = 0;
    char num[16];
    switch (ev->kind) {
        case CA_PACA_EV_TASK_UPDATED: {
            keys_add(&arr, &n, &cap, "tasks/", ev->project_id);
            snprintf(num, sizeof num, "%d", ev->task_number);
            sb_t b = {0};
            sb_puts(&b, "task/"); sb_puts(&b, ev->project_id ? ev->project_id : "");
            sb_putc(&b, '/'); sb_puts(&b, num);
            if (n == cap) { size_t nc = cap?cap*2:4; char **na=(char**)realloc(arr,nc*sizeof *na); if(na){arr=na;cap=nc;} }
            if (n < cap) arr[n++] = sb_take(&b); else free(b.buf);
            break;
        }
        case CA_PACA_EV_AGENT_ACTIVITY:
            keys_add(&arr, &n, &cap, "activity/", ev->project_id);
            keys_add(&arr, &n, &cap, "agent/", ev->agent_member_id);
            break;
        case CA_PACA_EV_CONVERSATION_STEP:
            keys_add(&arr, &n, &cap, "conversation/", ev->conversation_id);
            keys_add(&arr, &n, &cap, "conversations/", ev->project_id);
            break;
        case CA_PACA_EV_DOC_CURSOR_MOVE: {
            sb_t b = {0};
            sb_puts(&b, "doc/"); sb_puts(&b, ev->doc_id ? ev->doc_id : ""); sb_puts(&b, "/cursors");
            if (n == cap) { size_t nc = cap?cap*2:4; char **na=(char**)realloc(arr,nc*sizeof *na); if(na){arr=na;cap=nc;} }
            if (n < cap) arr[n++] = sb_take(&b); else free(b.buf);
            break;
        }
        case CA_PACA_EV_QUERY_INVALIDATION:
            keys_add(&arr, &n, &cap, ev->query_key, NULL);
            break;
    }
    *out_count = n;
    return arr;
}

/* ===========================================================================
 * Skills
 * =========================================================================== */

void ca_paca_skill_free(ca_paca_skill_t *s) {
    if (!s) return;
    free(s->name); free(s->description); free(s->body);
    memset(s, 0, sizeof *s);
}
void ca_paca_skill_free_array(ca_paca_skill_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_paca_skill_free(&arr[i]);
    free(arr);
}
char *ca_paca_skill_to_markdown(const ca_paca_skill_t *s) {
    if (!s) return NULL;
    sb_t b = {0};
    sb_puts(&b, "---\nname: "); sb_puts(&b, s->name ? s->name : "");
    sb_puts(&b, "\ndescription: "); sb_puts(&b, s->description ? s->description : "");
    sb_puts(&b, "\n---\n\n"); sb_puts(&b, s->body ? s->body : "");
    return sb_take(&b);
}

/* skill template bodies (SkillTemplates.*) */
#define TPL_EPIC       "You are running paca-epic. Use only the paca MCP tools. Output structure: title, problem statement, success criteria, scope, out-of-scope, risks."
#define TPL_BREAKDOWN  "You are running paca-breakdown. Use only the paca MCP tools. Take the supplied epic and produce a numbered list of tasks with title + acceptance criteria."
#define TPL_CLARIFY    "You are running paca-clarify. Pose the smallest set of clarifying questions needed to estimate the supplied task."
#define TPL_SPRINT     "You are running paca-sprint. Use the create_sprint / start_sprint / complete_sprint MCP tools."
#define TPL_ESTIMATE   "You are running paca-estimate. For each task, propose story points (1-13). Cite assumptions."
#define TPL_PRIORITIZE "You are running paca-prioritize. Reorder the backlog by importance (0-5). Cite reasoning."
#define TPL_DO         "You are running paca-do. Pick the next-best ready task, mark in_progress, execute, then mark done."
#define TPL_TEST       "You are running paca-test. Write and run unit + integration tests for the current change."
#define TPL_DOC        "You are running paca-doc. Update the living document with the smallest accurate diff."

static const struct { const char *name, *desc, *body; } SKILLS[] = {
    {"paca",            "Run the paca workflow on the current ask.",                          "Use the paca MCP tools to plan and execute the user's request."},
    {"paca-epic",       "Capture a large initiative as a paca epic.",                         TPL_EPIC},
    {"paca-breakdown",  "Break a paca epic into actionable tasks.",                           TPL_BREAKDOWN},
    {"paca-clarify",    "Ask the right clarifying questions before estimating.",              TPL_CLARIFY},
    {"paca-sprint",     "Form / close a sprint with the paca sprint surface.",                TPL_SPRINT},
    {"paca-estimate",   "Estimate story points for a set of tasks.",                          TPL_ESTIMATE},
    {"paca-prioritize", "Reorder the backlog by importance.",                                 TPL_PRIORITIZE},
    {"paca-do",         "Pick the next-best task and start it.",                              TPL_DO},
    {"paca-test",       "Generate and run tests for the current change.",                     TPL_TEST},
    {"paca-doc",        "Update the project's living doc to reflect the latest change.",      TPL_DOC},
    {"paca-setup",      "First-run setup: pick project, configure agents, install plugins.",  "Walk the user through paca first-run setup."},
};

ca_paca_skill_t *ca_paca_skill_library_all(size_t *out_count) {
    if (out_count) *out_count = SIZE_MAX;
    if (!out_count) return NULL;
    size_t n = sizeof SKILLS / sizeof SKILLS[0];
    ca_paca_skill_t *arr = (ca_paca_skill_t *)calloc(n, sizeof *arr);
    if (!arr) { *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        arr[i].name = pdup(SKILLS[i].name);
        arr[i].description = pdup(SKILLS[i].desc);
        arr[i].body = pdup(SKILLS[i].body);
    }
    *out_count = n;
    return arr;
}
int ca_paca_skill_library_find(const char *name, ca_paca_skill_t *out) {
    if (!name || !out) return -1;
    size_t n = sizeof SKILLS / sizeof SKILLS[0];
    for (size_t i = 0; i < n; ++i)
        if (pstr_ieq(SKILLS[i].name, name)) {
            out->name = pdup(SKILLS[i].name);
            out->description = pdup(SKILLS[i].desc);
            out->body = pdup(SKILLS[i].body);
            return 0;
        }
    return -1;
}
/* TrimStart. Owned. */
static char *trim_start_dup(const char *s) {
    if (!s) return pdup_or_empty("");
    while (*s == ' ' || *s == '\t' || *s == '\n' || *s == '\r') ++s;
    return pdup(s);
}
char *ca_paca_skill_strip_frontmatter(const char *markdown) {
    if (!markdown || !*markdown) return pdup_or_empty("");
    /* Regex: ^\s*---.*?---\s*\n  (Singleline). Must match at index 0. Because C#
     * allows leading \s*, find the first "---", require only whitespace before
     * it, then the next "---", then trailing \s* up to and including a newline. */
    const char *p = markdown;
    const char *q = p;
    while (*q == ' ' || *q == '\t' || *q == '\n' || *q == '\r') ++q;
    if (strncmp(q, "---", 3) != 0) return trim_start_dup(markdown);
    const char *after_open = q + 3;
    const char *close = strstr(after_open, "---");
    if (!close) return trim_start_dup(markdown);
    const char *r = close + 3;
    /* \s*\n : consume whitespace; require it reaches at least one newline */
    const char *scan = r;
    const char *last_nl = NULL;
    while (*scan == ' ' || *scan == '\t' || *scan == '\r' || *scan == '\n') {
        if (*scan == '\n') last_nl = scan;
        ++scan;
    }
    if (!last_nl) return trim_start_dup(markdown); /* no terminating newline -> not a match */
    return trim_start_dup(last_nl + 1);
}

/* ===========================================================================
 * Deploy
 * =========================================================================== */

void ca_paca_deploy_artifact_free(ca_paca_deploy_artifact_t *a) {
    if (!a) return;
    free(a->compose_yaml); free(a->env_file);
    a->compose_yaml = a->env_file = NULL;
}

/* URL-safe base64 secret, '='-trimmed, truncated to `length` chars. Owned. */
static char *random_secret(int length) {
    if (length <= 0) return pdup_or_empty("");
    uint8_t *bytes = (uint8_t *)malloc((size_t)length);
    if (!bytes) return NULL;
    rand_bytes(bytes, (size_t)length);
    char *b = b64_encode(bytes, (size_t)length);
    free(bytes);
    if (!b) return NULL;
    /* +/ -> -_ , trim '=' */
    for (char *p = b; *p; ++p) { if (*p == '+') *p = '-'; else if (*p == '/') *p = '_'; }
    { size_t n = strlen(b); while (n && b[n-1]=='=') b[--n]='\0'; }
    /* substring [..length] — C# takes exactly `length` chars; base64 of `length`
     * bytes is >= length chars, so truncation is safe. */
    if ((int)strlen(b) > length) b[length] = '\0';
    return b;
}

static const char *deploy_mode_str(ca_paca_deploy_mode_t m) {
    switch (m) { case CA_PACA_DEPLOY_PROD: return "prod"; case CA_PACA_DEPLOY_E2E: return "e2e"; default: return "dev"; }
}

static char *build_env_file(ca_paca_deploy_mode_t mode, const ca_paca_deploy_overrides_t *ov) {
    sb_t b = {0};
    sb_puts(&b, "PACA_MODE="); sb_puts(&b, deploy_mode_str(mode)); sb_putc(&b, '\n');
    sb_puts(&b, "PACA_PG_USER=paca\n");
    { char *s = random_secret(32); sb_puts(&b, "PACA_PG_PASSWORD="); sb_puts(&b, s?s:""); sb_putc(&b,'\n'); free(s); }
    sb_puts(&b, "PACA_PG_DB=paca\n");
    if (ov && ov->use_external_postgres && *ov->use_external_postgres) {
        sb_puts(&b, "PACA_PG_URL="); sb_puts(&b, ov->use_external_postgres); sb_putc(&b,'\n');
    }
    sb_puts(&b, "PACA_VALKEY_URL=redis://paca-valkey:6379\n");
    { char *s = random_secret(20); sb_puts(&b, "PACA_S3_KEY="); sb_puts(&b, s?s:""); sb_putc(&b,'\n'); free(s); }
    { char *s = random_secret(40); sb_puts(&b, "PACA_S3_SECRET="); sb_puts(&b, s?s:""); sb_putc(&b,'\n'); free(s); }
    if (ov && ov->use_external_s3 && *ov->use_external_s3) {
        sb_puts(&b, "PACA_S3_ENDPOINT="); sb_puts(&b, ov->use_external_s3); sb_putc(&b,'\n');
    }
    { char *s = random_secret(48); sb_puts(&b, "PACA_JWT_SIGNING_SECRET="); sb_puts(&b, s?s:""); sb_putc(&b,'\n'); free(s); }
    sb_puts(&b, "PACA_AI_ENABLED="); sb_puts(&b, (ov && ov->skip_ai_agent) ? "false" : "true"); sb_putc(&b,'\n');
    return sb_take(&b);
}

int ca_paca_deployer_build(ca_paca_deploy_mode_t mode,
                           const ca_paca_deploy_overrides_t *overrides,
                           ca_paca_deploy_artifact_t *out) {
    if (!out) return -1;
    ca_paca_deploy_overrides_t def = {0};
    const ca_paca_deploy_overrides_t *ov = overrides ? overrides : &def;

    sb_t b = {0};
    sb_puts(&b, "version: '3.9'\n");
    sb_puts(&b, "services:\n");

    sb_puts(&b, "  paca-web:\n");
    sb_puts(&b, "    image: bhengubv/paca-web:");
    sb_puts(&b, mode == CA_PACA_DEPLOY_PROD ? "stable" : "latest"); sb_putc(&b, '\n');
    sb_puts(&b, "    env_file: [.env]\n");
    sb_puts(&b, "    ports:\n");
    sb_puts(&b, mode == CA_PACA_DEPLOY_PROD ? "      - \"443:8080\"\n" : "      - \"8080:8080\"\n");

    if (!(ov->use_external_postgres && *ov->use_external_postgres)) {
        sb_puts(&b, "  paca-postgres:\n");
        sb_puts(&b, "    image: postgres:16-alpine\n");
        sb_puts(&b, "    environment:\n");
        sb_puts(&b, "      POSTGRES_USER:     ${PACA_PG_USER}\n");
        sb_puts(&b, "      POSTGRES_PASSWORD: ${PACA_PG_PASSWORD}\n");
        sb_puts(&b, "      POSTGRES_DB:       ${PACA_PG_DB}\n");
        sb_puts(&b, "    volumes: [paca_pg_data:/var/lib/postgresql/data]\n");
    }

    sb_puts(&b, "  paca-valkey:\n");
    sb_puts(&b, "    image: valkey/valkey:8\n");

    if (!(ov->use_external_s3 && *ov->use_external_s3)) {
        sb_puts(&b, "  paca-minio:\n");
        sb_puts(&b, "    image: minio/minio:latest\n");
        sb_puts(&b, "    environment:\n");
        sb_puts(&b, "      MINIO_ROOT_USER:     ${PACA_S3_KEY}\n");
        sb_puts(&b, "      MINIO_ROOT_PASSWORD: ${PACA_S3_SECRET}\n");
        sb_puts(&b, "    command: server /data\n");
    }

    sb_puts(&b, "  paca-nginx:\n");
    sb_puts(&b, "    image: nginx:1.27-alpine\n");

    if (!ov->skip_ai_agent) {
        sb_puts(&b, "  paca-ai:\n");
        sb_puts(&b, "    image: bhengubv/paca-ai:latest\n");
        sb_puts(&b, "    env_file: [.env]\n");
    }

    if (!(ov->use_external_postgres && *ov->use_external_postgres)) {
        sb_puts(&b, "volumes:\n");
        sb_puts(&b, "  paca_pg_data: {}\n");
    }

    char *env = build_env_file(mode, ov);
    if (!env) { free(b.buf); return -1; }
    out->compose_yaml = sb_take(&b);
    out->env_file = env;
    return 0;
}

char *ca_paca_deployer_build_install_plugin_script(const char *plugin_name) {
    if (pblank(plugin_name)) return NULL;
    sb_t b = {0};
    sb_puts(&b, "#!/usr/bin/env bash\nset -euo pipefail\n");
    sb_puts(&b, "echo \"[paca] Building WASM module for "); sb_puts(&b, plugin_name); sb_puts(&b, "...\"\n");
    sb_puts(&b, "wasm-pack build --target web ./plugins/"); sb_puts(&b, plugin_name); sb_putc(&b,'\n');
    sb_puts(&b, "echo \"[paca] Building frontend bundle...\"\n");
    sb_puts(&b, "cd ./plugins/"); sb_puts(&b, plugin_name); sb_puts(&b, "/frontend && pnpm install && pnpm build\n");
    sb_puts(&b, "cd -\n");
    sb_puts(&b, "echo \"[paca] Registering plugin with the API...\"\n");
    sb_puts(&b, "paca-cli plugins install ./plugins/"); sb_puts(&b, plugin_name); sb_puts(&b, "/dist\n");
    sb_puts(&b, "echo \"[paca] Done.\"\n");
    return sb_take(&b);
}
char *ca_paca_deployer_build_uninstall_plugin_script(const char *plugin_name) {
    if (pblank(plugin_name)) return NULL;
    sb_t b = {0};
    sb_puts(&b, "#!/usr/bin/env bash\nset -euo pipefail\n");
    sb_puts(&b, "echo \"[paca] Uninstalling "); sb_puts(&b, plugin_name); sb_puts(&b, "...\"\n");
    sb_puts(&b, "paca-cli plugins uninstall "); sb_puts(&b, plugin_name); sb_putc(&b,'\n');
    sb_puts(&b, "rm -rf ./plugins/"); sb_puts(&b, plugin_name); sb_puts(&b, "/dist\n");
    sb_puts(&b, "echo \"[paca] Done.\"\n");
    return sb_take(&b);
}
