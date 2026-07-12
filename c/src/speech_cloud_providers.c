/*
 * speech_cloud_providers.c — CircleAI.Speech.Cloud provider recognizers +
 * synthesizers (C11 port). See speech_cloud_providers.h.
 *
 * Real ported logic: WAV envelope construction, multipart/form-data assembly,
 * JSON request-body emission, base64 audio encode/decode, response JSON parsing,
 * PCM sample→duration maths, and the fail-soft "not configured -> empty result"
 * behaviour. The single HTTP call is the injected ca_speech_http_t seam.
 *
 * Self-contained: base64 + a tiny read-only JSON scanner live here. Pure C11 +
 * libc + libm.
 */

#include "circle_ai/speech_cloud_providers.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>
#include <math.h>

/* ── string helpers ─────────────────────────────────────────────────────── */

static char *sdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *sdup_def(const char *s, const char *def) {
    return sdup(s ? s : def);
}
static bool sblank(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p)
        if (*p != ' ' && *p != '\t' && *p != '\n' && *p != '\r') return false;
    return true;
}

/* growable byte buffer */
typedef struct { uint8_t *buf; size_t len, cap; } bb_t;
static bool bb_reserve(bb_t *b, size_t extra) {
    if (b->len + extra <= b->cap) return true;
    size_t nc = b->cap ? b->cap : 64;
    while (b->len + extra > nc) nc *= 2;
    uint8_t *nb = (uint8_t *)realloc(b->buf, nc);
    if (!nb) return false;
    b->buf = nb; b->cap = nc;
    return true;
}
static bool bb_bytes(bb_t *b, const void *p, size_t n) {
    if (!n) return true;
    if (!bb_reserve(b, n)) return false;
    memcpy(b->buf + b->len, p, n);
    b->len += n;
    return true;
}
static bool bb_str(bb_t *b, const char *s) { return bb_bytes(b, s, strlen(s)); }

/* growable char string */
typedef struct { char *buf; size_t len, cap; } cb_t;
static bool cb_reserve(cb_t *b, size_t extra) {
    if (b->len + extra + 1 <= b->cap) return true;
    size_t nc = b->cap ? b->cap : 64;
    while (b->len + extra + 1 > nc) nc *= 2;
    char *nb = (char *)realloc(b->buf, nc);
    if (!nb) return false;
    b->buf = nb; b->cap = nc;
    return true;
}
static bool cb_str(cb_t *b, const char *s) {
    if (!s) return true;
    size_t n = strlen(s);
    if (!cb_reserve(b, n)) return false;
    memcpy(b->buf + b->len, s, n); b->len += n; b->buf[b->len] = '\0';
    return true;
}
static bool cb_ch(cb_t *b, char c) {
    if (!cb_reserve(b, 1)) return false;
    b->buf[b->len++] = c; b->buf[b->len] = '\0';
    return true;
}
static char *cb_take(cb_t *b) { return b->buf ? b->buf : sdup(""); }

/* JSON string escaping into a char buffer. */
static bool cb_json_str(cb_t *b, const char *s) {
    if (!cb_ch(b, '"')) return false;
    for (const char *p = s ? s : ""; *p; ++p) {
        unsigned char c = (unsigned char)*p;
        switch (c) {
            case '"':  if (!cb_str(b, "\\\"")) return false; break;
            case '\\': if (!cb_str(b, "\\\\")) return false; break;
            case '\n': if (!cb_str(b, "\\n"))  return false; break;
            case '\r': if (!cb_str(b, "\\r"))  return false; break;
            case '\t': if (!cb_str(b, "\\t"))  return false; break;
            case '\b': if (!cb_str(b, "\\b"))  return false; break;
            case '\f': if (!cb_str(b, "\\f"))  return false; break;
            default:
                if (c < 0x20) { char u[8]; snprintf(u, sizeof u, "\\u%04x", c); if (!cb_str(b, u)) return false; }
                else if (!cb_ch(b, (char)c)) return false;
        }
    }
    return cb_ch(b, '"');
}

/* ── base64 ─────────────────────────────────────────────────────────────── */

static const char B64[]="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
static char *b64_encode(const uint8_t *data, size_t len) {
    size_t olen = 4 * ((len + 2) / 3);
    char *out = (char *)malloc(olen + 1);
    if (!out) return NULL;
    size_t i, o = 0;
    for (i = 0; i + 3 <= len; i += 3) {
        uint32_t n=(data[i]<<16)|(data[i+1]<<8)|data[i+2];
        out[o++]=B64[(n>>18)&63]; out[o++]=B64[(n>>12)&63]; out[o++]=B64[(n>>6)&63]; out[o++]=B64[n&63];
    }
    if (len-i==1){ uint32_t n=data[i]<<16; out[o++]=B64[(n>>18)&63]; out[o++]=B64[(n>>12)&63]; out[o++]='='; out[o++]='='; }
    else if (len-i==2){ uint32_t n=(data[i]<<16)|(data[i+1]<<8); out[o++]=B64[(n>>18)&63]; out[o++]=B64[(n>>12)&63]; out[o++]=B64[(n>>6)&63]; out[o++]='='; }
    out[o]='\0';
    return out;
}
static int b64_val(char c) {
    if (c>='A'&&c<='Z') return c-'A';
    if (c>='a'&&c<='z') return c-'a'+26;
    if (c>='0'&&c<='9') return c-'0'+52;
    if (c=='+') return 62; if (c=='/') return 63;
    return -1;
}
static uint8_t *b64_decode(const char *in, size_t *out_len) {
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
    if (qi == 2) out[o++]=(uint8_t)((quad[0]<<2)|(quad[1]>>4));
    else if (qi == 3) { out[o++]=(uint8_t)((quad[0]<<2)|(quad[1]>>4)); out[o++]=(uint8_t)((quad[1]<<4)|(quad[2]>>2)); }
    *out_len = o;
    return out;
}

/* ── URL escape (Uri.EscapeDataString subset — RFC 3986 unreserved kept) ── */
static char *url_escape(const char *s) {
    if (!s) return sdup("");
    static const char hex[] = "0123456789ABCDEF";
    size_t cap = strlen(s) * 3 + 1;
    char *out = (char *)malloc(cap);
    if (!out) return NULL;
    size_t j = 0;
    for (const char *p = s; *p; ++p) {
        unsigned char c = (unsigned char)*p;
        if ((c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='-'||c=='_'||c=='.'||c=='~')
            out[j++] = (char)c;
        else { out[j++]='%'; out[j++]=hex[c>>4]; out[j++]=hex[c&0xF]; }
    }
    out[j]='\0';
    return out;
}

/* ── WAV envelope (44-byte header, 16-bit mono PCM) ─────────────────────── */
static void put_u32le(uint8_t *p, uint32_t v){ p[0]=(uint8_t)v; p[1]=(uint8_t)(v>>8); p[2]=(uint8_t)(v>>16); p[3]=(uint8_t)(v>>24); }
static void put_u16le(uint8_t *p, uint16_t v){ p[0]=(uint8_t)v; p[1]=(uint8_t)(v>>8); }
static uint8_t *wrap_pcm_as_wav(const uint8_t *pcm, size_t len, int sample_rate, size_t *out_len) {
    const int channels = 1, bits = 16;
    uint32_t byte_rate = (uint32_t)sample_rate * channels * (bits/8);
    uint16_t block_align = channels * (bits/8);
    uint32_t data_size = (uint32_t)len;
    uint32_t chunk_size = 36 + data_size;
    uint8_t *buf = (uint8_t *)malloc(44 + len);
    if (!buf) return NULL;
    memcpy(buf, "RIFF", 4);
    put_u32le(buf+4, chunk_size);
    memcpy(buf+8, "WAVE", 4);
    memcpy(buf+12, "fmt ", 4);
    put_u32le(buf+16, 16);
    put_u16le(buf+20, 1);              /* PCM */
    put_u16le(buf+22, (uint16_t)channels);
    put_u32le(buf+24, (uint32_t)sample_rate);
    put_u32le(buf+28, byte_rate);
    put_u16le(buf+32, block_align);
    put_u16le(buf+34, (uint16_t)bits);
    memcpy(buf+36, "data", 4);
    put_u32le(buf+40, data_size);
    if (len) memcpy(buf+44, pcm, len);
    *out_len = 44 + len;
    return buf;
}
/* strip a 44-byte "RIFF" header if present. Returns owned copy of the PCM. */
static uint8_t *strip_wav_header(const uint8_t *data, size_t len, size_t *out_len) {
    if (len > 44 && data[0]=='R'&&data[1]=='I'&&data[2]=='F'&&data[3]=='F') {
        size_t n = len - 44;
        uint8_t *o = (uint8_t *)malloc(n ? n : 1);
        if (!o) return NULL;
        if (n) memcpy(o, data + 44, n);
        *out_len = n;
        return o;
    }
    uint8_t *o = (uint8_t *)malloc(len ? len : 1);
    if (!o) return NULL;
    if (len) memcpy(o, data, len);
    *out_len = len;
    return o;
}

/* ── minimal read-only JSON scanner ─────────────────────────────────────────
 * Enough to walk the provider responses: find a property by key inside the
 * nearest enclosing object, extract a string/number/bool, index arrays. Works
 * on a NUL-terminated buffer; borrows into it (string values are decoded into
 * fresh buffers on demand). */

typedef struct { const char *s; } json_t; /* cursor at a value start */

static const char *json_skip_ws(const char *p) {
    while (*p==' '||*p=='\t'||*p=='\n'||*p=='\r') ++p;
    return p;
}
/* advance past one complete JSON value starting at p; returns the char after. */
static const char *json_skip_value(const char *p) {
    p = json_skip_ws(p);
    if (*p == '"') {
        ++p;
        while (*p && *p != '"') { if (*p=='\\' && p[1]) p += 2; else ++p; }
        if (*p == '"') ++p;
        return p;
    }
    if (*p == '{' || *p == '[') {
        char open = *p, close = (open=='{') ? '}' : ']';
        int depth = 0;
        for (;;) {
            if (!*p) return p;
            if (*p == '"') { p = json_skip_value(p); continue; }
            if (*p == open) ++depth;
            else if (*p == close) { --depth; if (depth == 0) { ++p; return p; } }
            ++p;
        }
    }
    /* scalar: number/true/false/null */
    while (*p && *p!=','&&*p!='}'&&*p!=']'&&*p!=' '&&*p!='\t'&&*p!='\n'&&*p!='\r') ++p;
    return p;
}
/* Within the object at obj (pointing at '{'), find "key" and return a cursor at
 * its value; NULL if not present / not an object. Only searches the immediate
 * object level. */
static const char *json_obj_get(const char *obj, const char *key) {
    const char *p = json_skip_ws(obj);
    if (*p != '{') return NULL;
    ++p;
    size_t klen = strlen(key);
    for (;;) {
        p = json_skip_ws(p);
        if (*p == '}' || *p == '\0') return NULL;
        if (*p != '"') { /* malformed; bail */ return NULL; }
        const char *kstart = p + 1;
        const char *kend = kstart;
        while (*kend && *kend != '"') { if (*kend=='\\' && kend[1]) kend += 2; else ++kend; }
        bool match = ((size_t)(kend - kstart) == klen) && strncmp(kstart, key, klen) == 0;
        p = (*kend == '"') ? kend + 1 : kend;
        p = json_skip_ws(p);
        if (*p == ':') ++p;
        p = json_skip_ws(p);
        if (match) return p;
        p = json_skip_value(p);
        p = json_skip_ws(p);
        if (*p == ',') ++p;
    }
}
static bool json_is_string(const char *v) { v = json_skip_ws(v); return *v == '"'; }
static bool json_is_number(const char *v) { v = json_skip_ws(v); return (*v=='-'||(*v>='0'&&*v<='9')); }
static bool json_is_array(const char *v)  { v = json_skip_ws(v); return *v == '['; }
/* decode a JSON string value at v into a fresh owned C string. NULL if not a
 * string. */
static char *json_read_string(const char *v) {
    v = json_skip_ws(v);
    if (*v != '"') return NULL;
    ++v;
    cb_t b = {0};
    while (*v && *v != '"') {
        if (*v == '\\') {
            ++v;
            switch (*v) {
                case 'n': cb_ch(&b,'\n'); break;
                case 'r': cb_ch(&b,'\r'); break;
                case 't': cb_ch(&b,'\t'); break;
                case 'b': cb_ch(&b,'\b'); break;
                case 'f': cb_ch(&b,'\f'); break;
                case '"': cb_ch(&b,'"'); break;
                case '\\': cb_ch(&b,'\\'); break;
                case '/': cb_ch(&b,'/'); break;
                case 'u': {
                    if (v[1]&&v[2]&&v[3]&&v[4]) {
                        char hx[5]={v[1],v[2],v[3],v[4],0};
                        unsigned c=(unsigned)strtoul(hx,NULL,16);
                        if (c<0x80) cb_ch(&b,(char)c);
                        else if (c<0x800){ cb_ch(&b,(char)(0xC0|(c>>6))); cb_ch(&b,(char)(0x80|(c&0x3F))); }
                        else { cb_ch(&b,(char)(0xE0|(c>>12))); cb_ch(&b,(char)(0x80|((c>>6)&0x3F))); cb_ch(&b,(char)(0x80|(c&0x3F))); }
                        v += 4;
                    }
                    break;
                }
                default: cb_ch(&b,*v); break;
            }
            if (*v) ++v;
        } else { cb_ch(&b,*v); ++v; }
    }
    return b.buf ? b.buf : sdup("");
}
static double json_read_double(const char *v) {
    v = json_skip_ws(v);
    return strtod(v, NULL);
}
static long long json_read_int64(const char *v) {
    v = json_skip_ws(v);
    return strtoll(v, NULL, 10);
}
/* array length at v (pointing at '['). */
static size_t json_array_len(const char *v) {
    v = json_skip_ws(v);
    if (*v != '[') return 0;
    ++v; v = json_skip_ws(v);
    if (*v == ']') return 0;
    size_t n = 0;
    for (;;) {
        ++n;
        v = json_skip_value(v);
        v = json_skip_ws(v);
        if (*v == ',') { ++v; v = json_skip_ws(v); continue; }
        break;
    }
    return n;
}
/* array element i at v (pointing at '['); cursor at that value, or NULL. */
static const char *json_array_at(const char *v, size_t i) {
    v = json_skip_ws(v);
    if (*v != '[') return NULL;
    ++v; v = json_skip_ws(v);
    if (*v == ']') return NULL;
    size_t idx = 0;
    for (;;) {
        if (idx == i) return v;
        v = json_skip_value(v);
        v = json_skip_ws(v);
        if (*v == ',') { ++v; v = json_skip_ws(v); ++idx; continue; }
        return NULL;
    }
}

/* ── result-result construction helpers (speech.h types) ─────────────────── */

static void empty_transcription(ca_transcription_result_t *out, const char *language) {
    memset(out, 0, sizeof *out);
    out->text = sdup("");
    out->language = language ? sdup(language) : NULL;
    out->segments = NULL;
    out->segment_count = 0;
    out->total_duration_ms = 0;
}
static void empty_synthesis(ca_synthesis_result_t *out, int sample_rate) {
    memset(out, 0, sizeof *out);
    out->audio_pcm16_mono = NULL;
    out->audio_len = 0;
    out->sample_rate_hz = sample_rate;
    out->duration_ms = 0;
}
/* PCM-16 mono: samples = bytes/2; duration_ms = samples * 1000 / rate. */
static int64_t pcm_duration_ms(size_t bytes, int rate) {
    if (rate <= 0) return 0;
    size_t samples = bytes / 2;
    return (int64_t)((double)samples / (double)rate * 1000.0);
}

/* growable segment list */
typedef struct { ca_transcribed_segment_t *arr; size_t n, cap; } seglist_t;
static bool seg_push(seglist_t *l, const char *text, int64_t offset_ms, int64_t dur_ms,
                     const char *language, float confidence) {
    if (l->n == l->cap) {
        size_t nc = l->cap ? l->cap*2 : 8;
        ca_transcribed_segment_t *na = (ca_transcribed_segment_t *)realloc(l->arr, nc*sizeof *na);
        if (!na) return false;
        l->arr = na; l->cap = nc;
    }
    ca_transcribed_segment_t *s = &l->arr[l->n];
    memset(s, 0, sizeof *s);
    s->text = sdup(text ? text : "");
    s->offset_ms = offset_ms;
    s->duration_ms = dur_ms;
    s->language = language ? sdup(language) : NULL;
    s->confidence = confidence;
    ++l->n;
    return true;
}

/* ── HTTP invocation helper: issue via the seam, return owned body bytes ─── */
typedef struct {
    int      status;
    uint8_t *body;   /* owned (may be NULL) */
    size_t   body_len;
    bool     ok;     /* transport returned (not -1) */
} http_resp_t;

static http_resp_t http_send(const ca_speech_http_t *http, const char *method,
                             const char *path, const ca_speech_http_header_t *headers,
                             size_t header_count, const uint8_t *body, size_t body_len) {
    http_resp_t r; memset(&r, 0, sizeof r);
    uint8_t *out_body = NULL; size_t out_len = 0; int status = 0;
    int rc = http->request(http->self, method, path, headers, header_count,
                           body, body_len, &status, &out_body, &out_len);
    if (rc != 0) { r.ok = false; return r; }
    r.ok = true; r.status = status; r.body = out_body; r.body_len = out_len;
    return r;
}
static void http_resp_free(http_resp_t *r) { if (r->body) { free(r->body); r->body = NULL; } }
static bool http_status_ok(int status) { return status >= 200 && status < 300; }
/* NUL-terminate a response body for JSON parsing (returns owned string). */
static char *body_to_cstr(const http_resp_t *r) {
    char *s = (char *)malloc(r->body_len + 1);
    if (!s) return NULL;
    if (r->body_len) memcpy(s, r->body, r->body_len);
    s[r->body_len] = '\0';
    return s;
}

/* ── multipart/form-data assembly ───────────────────────────────────────── */
/* Builds a multipart body with text fields + one binary file part. Returns the
 * body bytes (owned) and sets *content_type to an owned "multipart/form-data;
 * boundary=..." string. */
typedef struct { const char *name; const char *value; } form_field_t;
static uint8_t *build_multipart(const form_field_t *fields, size_t field_count,
                                const char *file_field, const char *file_name,
                                const char *file_content_type,
                                const uint8_t *file_bytes, size_t file_len,
                                size_t *out_len, char **out_content_type) {
    static const char boundary[] = "----CircleAISpeechBoundary7MA4YWxkTrZu0gW";
    bb_t b = {0};
    bool ok = true;
    for (size_t i = 0; i < field_count && ok; ++i) {
        ok = ok && bb_str(&b, "--") && bb_str(&b, boundary) && bb_str(&b, "\r\n");
        ok = ok && bb_str(&b, "Content-Disposition: form-data; name=\"");
        ok = ok && bb_str(&b, fields[i].name) && bb_str(&b, "\"\r\n\r\n");
        ok = ok && bb_str(&b, fields[i].value ? fields[i].value : "") && bb_str(&b, "\r\n");
    }
    if (file_field) {
        ok = ok && bb_str(&b, "--") && bb_str(&b, boundary) && bb_str(&b, "\r\n");
        ok = ok && bb_str(&b, "Content-Disposition: form-data; name=\"");
        ok = ok && bb_str(&b, file_field) && bb_str(&b, "\"; filename=\"");
        ok = ok && bb_str(&b, file_name ? file_name : "file") && bb_str(&b, "\"\r\n");
        ok = ok && bb_str(&b, "Content-Type: ") && bb_str(&b, file_content_type ? file_content_type : "application/octet-stream");
        ok = ok && bb_str(&b, "\r\n\r\n");
        ok = ok && bb_bytes(&b, file_bytes, file_len) && bb_str(&b, "\r\n");
    }
    ok = ok && bb_str(&b, "--") && bb_str(&b, boundary) && bb_str(&b, "--\r\n");
    if (!ok) { free(b.buf); return NULL; }
    cb_t ct = {0};
    if (!cb_str(&ct, "multipart/form-data; boundary=") || !cb_str(&ct, boundary)) {
        free(b.buf); free(ct.buf); return NULL;
    }
    *out_len = b.len;
    *out_content_type = cb_take(&ct);
    return b.buf ? b.buf : (uint8_t *)calloc(1,1);
}

/* ===========================================================================
 * OpenAI Whisper recognizer
 * =========================================================================== */

struct ca_speech_openai_recognizer {
    ca_speech_http_t http;
    char *base_address, *api_key, *transcription_model, *speech_model, *default_voice;
    int   pcm_sample_rate_hz;
};

static void openai_opts_apply(struct ca_speech_openai_recognizer *r, const ca_speech_openai_options_t *o) {
    r->base_address = sdup_def(o ? o->base_address : NULL, "https://api.openai.com");
    r->api_key = sdup(o ? o->api_key : NULL);
    r->transcription_model = sdup_def(o ? o->transcription_model : NULL, "whisper-1");
    r->speech_model = sdup_def(o ? o->speech_model : NULL, "tts-1");
    r->default_voice = sdup_def(o ? o->default_voice : NULL, "alloy");
    r->pcm_sample_rate_hz = (o && o->pcm_sample_rate_hz > 0) ? o->pcm_sample_rate_hz : 24000;
}

ca_speech_openai_recognizer_t *ca_speech_openai_recognizer_create(
    const ca_speech_http_t *http, const ca_speech_openai_options_t *options) {
    if (!http || !http->request) return NULL;
    ca_speech_openai_recognizer_t *r = (ca_speech_openai_recognizer_t *)calloc(1, sizeof *r);
    if (!r) return NULL;
    r->http = *http;
    openai_opts_apply(r, options);
    return r;
}
void ca_speech_openai_recognizer_destroy(ca_speech_openai_recognizer_t *r) {
    if (!r) return;
    free(r->base_address); free(r->api_key); free(r->transcription_model);
    free(r->speech_model); free(r->default_voice);
    free(r);
}
bool ca_speech_openai_recognizer_is_configured(const ca_speech_openai_recognizer_t *r) {
    return r && !sblank(r->api_key);
}
static const char *openai_recognizer_backend_id(void *self) { (void)self; return "openai-whisper"; }
static int openai_recognizer_transcribe(void *self, const uint8_t *audio, size_t len,
                                        int rate, const char *hint, ca_transcription_result_t *out) {
    ca_speech_openai_recognizer_t *r = (ca_speech_openai_recognizer_t *)self;
    if (!out) return -1;
    if (!ca_speech_openai_recognizer_is_configured(r)) { empty_transcription(out, NULL); return 0; }

    size_t wav_len = 0;
    uint8_t *wav = wrap_pcm_as_wav(audio, len, rate, &wav_len);
    if (!wav) return -1;

    /* multipart: file(audio.wav) + model + response_format[+language] */
    form_field_t fields[3]; size_t fc = 0;
    fields[fc].name = "model"; fields[fc].value = r->transcription_model; ++fc;
    fields[fc].name = "response_format"; fields[fc].value = "verbose_json"; ++fc;
    if (!sblank(hint)) { fields[fc].name = "language"; fields[fc].value = hint; ++fc; }
    size_t body_len = 0; char *content_type = NULL;
    uint8_t *body = build_multipart(fields, fc, "file", "audio.wav", "audio/wav",
                                    wav, wav_len, &body_len, &content_type);
    free(wav);
    if (!body || !content_type) { free(body); free(content_type); return -1; }

    cb_t auth = {0}; cb_str(&auth, "Bearer "); cb_str(&auth, r->api_key);
    ca_speech_http_header_t headers[2] = {
        { "Authorization", auth.buf ? auth.buf : "Bearer " },
        { "Content-Type", content_type },
    };
    http_resp_t resp = http_send(&r->http, "POST", "/v1/audio/transcriptions", headers, 2, body, body_len);
    free(auth.buf); free(content_type); free(body);

    if (!resp.ok || !http_status_ok(resp.status)) { http_resp_free(&resp); empty_transcription(out, NULL); return 0; }
    char *json = body_to_cstr(&resp);
    http_resp_free(&resp);
    if (!json) return -1;

    const char *root = json;
    char *text = NULL, *language = NULL;
    const char *tv = json_obj_get(root, "text");
    if (tv && json_is_string(tv)) text = json_read_string(tv);
    const char *lv = json_obj_get(root, "language");
    if (lv && json_is_string(lv)) language = json_read_string(lv);
    int64_t dur_ms = 0;
    const char *dv = json_obj_get(root, "duration");
    if (dv && json_is_number(dv)) dur_ms = (int64_t)(json_read_double(dv) * 1000.0);

    seglist_t segs = {0};
    const char *sv = json_obj_get(root, "segments");
    if (sv && json_is_array(sv)) {
        size_t n = json_array_len(sv);
        for (size_t i = 0; i < n; ++i) {
            const char *el = json_array_at(sv, i);
            if (!el) break;
            char *st = NULL; const char *stv = json_obj_get(el, "text");
            if (stv && json_is_string(stv)) st = json_read_string(stv);
            double ss = 0, se = 0;
            const char *ssv = json_obj_get(el, "start"); if (ssv) ss = json_read_double(ssv);
            const char *sev = json_obj_get(el, "end");   if (sev) se = json_read_double(sev); else se = ss;
            double d = se - ss; if (d < 0) d = 0;
            seg_push(&segs, st ? st : "", (int64_t)(ss*1000.0), (int64_t)(d*1000.0), language, 0.0f);
            free(st);
        }
    }

    memset(out, 0, sizeof *out);
    out->text = text ? text : sdup("");
    out->language = language;
    out->segments = segs.arr;
    out->segment_count = segs.n;
    out->total_duration_ms = dur_ms;
    free(json);
    return 0;
}
ca_speech_recognizer_t ca_speech_openai_recognizer_as_recognizer(ca_speech_openai_recognizer_t *r) {
    ca_speech_recognizer_t v; v.self = r; v.backend_id = openai_recognizer_backend_id;
    v.transcribe = openai_recognizer_transcribe; return v;
}

/* ===========================================================================
 * OpenAI TTS synthesizer
 * =========================================================================== */

struct ca_speech_openai_synthesizer {
    ca_speech_http_t http;
    char *base_address, *api_key, *speech_model, *default_voice;
    int   pcm_sample_rate_hz;
};

ca_speech_openai_synthesizer_t *ca_speech_openai_synthesizer_create(
    const ca_speech_http_t *http, const ca_speech_openai_options_t *o) {
    if (!http || !http->request) return NULL;
    ca_speech_openai_synthesizer_t *s = (ca_speech_openai_synthesizer_t *)calloc(1, sizeof *s);
    if (!s) return NULL;
    s->http = *http;
    s->base_address = sdup_def(o ? o->base_address : NULL, "https://api.openai.com");
    s->api_key = sdup(o ? o->api_key : NULL);
    s->speech_model = sdup_def(o ? o->speech_model : NULL, "tts-1");
    s->default_voice = sdup_def(o ? o->default_voice : NULL, "alloy");
    s->pcm_sample_rate_hz = (o && o->pcm_sample_rate_hz > 0) ? o->pcm_sample_rate_hz : 24000;
    return s;
}
void ca_speech_openai_synthesizer_destroy(ca_speech_openai_synthesizer_t *s) {
    if (!s) return;
    free(s->base_address); free(s->api_key); free(s->speech_model); free(s->default_voice);
    free(s);
}
bool ca_speech_openai_synthesizer_is_configured(const ca_speech_openai_synthesizer_t *s) {
    return s && !sblank(s->api_key);
}
static const char *openai_synth_backend_id(void *self) { (void)self; return "openai-tts"; }
static int openai_synth_synthesize(void *self, const char *text, const char *voice,
                                   const char *hint, ca_synthesis_result_t *out) {
    (void)hint;
    ca_speech_openai_synthesizer_t *s = (ca_speech_openai_synthesizer_t *)self;
    if (!out) return -1;
    if (!ca_speech_openai_synthesizer_is_configured(s)) { empty_synthesis(out, 0); return 0; }
    const char *rv = sblank(voice) ? s->default_voice : voice;
    cb_t j = {0};
    cb_str(&j, "{\"model\":"); cb_json_str(&j, s->speech_model);
    cb_str(&j, ",\"input\":"); cb_json_str(&j, text ? text : "");
    cb_str(&j, ",\"voice\":"); cb_json_str(&j, rv);
    cb_str(&j, ",\"response_format\":\"pcm\"}");

    cb_t auth = {0}; cb_str(&auth, "Bearer "); cb_str(&auth, s->api_key);
    ca_speech_http_header_t headers[2] = {
        { "Authorization", auth.buf ? auth.buf : "Bearer " },
        { "Content-Type", "application/json" },
    };
    http_resp_t resp = http_send(&s->http, "POST", "/v1/audio/speech", headers, 2,
                                 (const uint8_t *)j.buf, j.len);
    free(auth.buf); free(j.buf);
    if (!resp.ok || !http_status_ok(resp.status)) { http_resp_free(&resp); empty_synthesis(out, 0); return 0; }

    memset(out, 0, sizeof *out);
    out->audio_len = resp.body_len;
    out->audio_pcm16_mono = resp.body; /* transfer ownership */
    resp.body = NULL;
    out->sample_rate_hz = s->pcm_sample_rate_hz;
    out->duration_ms = pcm_duration_ms(out->audio_len, s->pcm_sample_rate_hz);
    http_resp_free(&resp);
    return 0;
}
ca_speech_synthesizer_t ca_speech_openai_synthesizer_as_synthesizer(ca_speech_openai_synthesizer_t *s) {
    ca_speech_synthesizer_t v; v.self = s; v.backend_id = openai_synth_backend_id;
    v.synthesize = openai_synth_synthesize; return v;
}

/* ===========================================================================
 * Deepgram recognizer (/v1/listen, raw PCM linear16)
 * =========================================================================== */

struct ca_speech_deepgram_recognizer {
    ca_speech_http_t http; char *base_address, *api_key, *model;
};
ca_speech_deepgram_recognizer_t *ca_speech_deepgram_recognizer_create(
    const ca_speech_http_t *http, const ca_speech_deepgram_stt_options_t *o) {
    if (!http || !http->request) return NULL;
    ca_speech_deepgram_recognizer_t *r = (ca_speech_deepgram_recognizer_t *)calloc(1, sizeof *r);
    if (!r) return NULL;
    r->http = *http;
    r->base_address = sdup_def(o ? o->base_address : NULL, "https://api.deepgram.com");
    r->api_key = sdup(o ? o->api_key : NULL);
    r->model = sdup_def(o ? o->model : NULL, "nova-2-general");
    return r;
}
void ca_speech_deepgram_recognizer_destroy(ca_speech_deepgram_recognizer_t *r) {
    if (!r) return; free(r->base_address); free(r->api_key); free(r->model); free(r);
}
bool ca_speech_deepgram_recognizer_is_configured(const ca_speech_deepgram_recognizer_t *r) {
    return r && !sblank(r->api_key);
}
static const char *deepgram_recognizer_backend_id(void *self) { (void)self; return "deepgram"; }
static int deepgram_recognizer_transcribe(void *self, const uint8_t *audio, size_t len,
                                          int rate, const char *hint, ca_transcription_result_t *out) {
    ca_speech_deepgram_recognizer_t *r = (ca_speech_deepgram_recognizer_t *)self;
    if (!out) return -1;
    if (!ca_speech_deepgram_recognizer_is_configured(r)) { empty_transcription(out, NULL); return 0; }

    char *emodel = url_escape(r->model);
    cb_t path = {0};
    char ratebuf[16]; snprintf(ratebuf, sizeof ratebuf, "%d", rate);
    cb_str(&path, "/v1/listen?model="); cb_str(&path, emodel ? emodel : "");
    cb_str(&path, "&encoding=linear16&sample_rate="); cb_str(&path, ratebuf);
    cb_str(&path, "&channels=1&punctuate=true");
    free(emodel);
    if (!sblank(hint)) { char *eh = url_escape(hint); cb_str(&path, "&language="); cb_str(&path, eh ? eh : ""); free(eh); }

    cb_t auth = {0}; cb_str(&auth, "Token "); cb_str(&auth, r->api_key);
    ca_speech_http_header_t headers[2] = {
        { "Authorization", auth.buf ? auth.buf : "Token " },
        { "Content-Type", "audio/raw" },
    };
    http_resp_t resp = http_send(&r->http, "POST", path.buf ? path.buf : "/v1/listen",
                                 headers, 2, audio, len);
    free(auth.buf); free(path.buf);
    if (!resp.ok || !http_status_ok(resp.status)) { http_resp_free(&resp); empty_transcription(out, NULL); return 0; }
    char *json = body_to_cstr(&resp); http_resp_free(&resp);
    if (!json) return -1;

    /* results.channels[0].alternatives[0].transcript + words[] */
    empty_transcription(out, hint);
    const char *results = json_obj_get(json, "results");
    if (!results) { free(json); return 0; }
    const char *channels = json_obj_get(results, "channels");
    if (!channels || !json_is_array(channels) || json_array_len(channels) == 0) { free(json); return 0; }
    const char *ch0 = json_array_at(channels, 0);
    const char *alts = ch0 ? json_obj_get(ch0, "alternatives") : NULL;
    if (!alts || !json_is_array(alts) || json_array_len(alts) == 0) { free(json); return 0; }
    const char *alt0 = json_array_at(alts, 0);
    char *text = NULL;
    const char *tv = alt0 ? json_obj_get(alt0, "transcript") : NULL;
    if (tv && json_is_string(tv)) text = json_read_string(tv);

    seglist_t segs = {0};
    const char *words = alt0 ? json_obj_get(alt0, "words") : NULL;
    if (words && json_is_array(words)) {
        size_t n = json_array_len(words);
        for (size_t i = 0; i < n; ++i) {
            const char *w = json_array_at(words, i);
            if (!w) break;
            char *wt = NULL; const char *wtv = json_obj_get(w, "word");
            if (wtv && json_is_string(wtv)) wt = json_read_string(wtv);
            double ss = 0, ee = 0;
            const char *ssv = json_obj_get(w, "start"); if (ssv) ss = json_read_double(ssv);
            const char *eev = json_obj_get(w, "end");   if (eev) ee = json_read_double(eev);
            float conf = 0.0f;
            const char *cv = json_obj_get(w, "confidence"); if (cv) conf = (float)json_read_double(cv);
            seg_push(&segs, wt ? wt : "", (int64_t)(ss*1000.0), (int64_t)((ee-ss)*1000.0), hint, conf);
            free(wt);
        }
    }
    int64_t dur_ms = 0;
    const char *meta = json_obj_get(json, "metadata");
    if (meta) { const char *dv = json_obj_get(meta, "duration"); if (dv) dur_ms = (int64_t)(json_read_double(dv)*1000.0); }

    free(out->text);
    out->text = text ? text : sdup("");
    out->segments = segs.arr; out->segment_count = segs.n;
    out->total_duration_ms = dur_ms;
    free(json);
    return 0;
}
ca_speech_recognizer_t ca_speech_deepgram_recognizer_as_recognizer(ca_speech_deepgram_recognizer_t *r) {
    ca_speech_recognizer_t v; v.self = r; v.backend_id = deepgram_recognizer_backend_id;
    v.transcribe = deepgram_recognizer_transcribe; return v;
}

/* ===========================================================================
 * Deepgram Aura synthesizer (/v1/speak, linear16)
 * =========================================================================== */

struct ca_speech_deepgram_synthesizer {
    ca_speech_http_t http; char *base_address, *api_key, *voice; int pcm_sample_rate_hz;
};
ca_speech_deepgram_synthesizer_t *ca_speech_deepgram_synthesizer_create(
    const ca_speech_http_t *http, const ca_speech_deepgram_tts_options_t *o) {
    if (!http || !http->request) return NULL;
    ca_speech_deepgram_synthesizer_t *s = (ca_speech_deepgram_synthesizer_t *)calloc(1, sizeof *s);
    if (!s) return NULL;
    s->http = *http;
    s->base_address = sdup_def(o ? o->base_address : NULL, "https://api.deepgram.com");
    s->api_key = sdup(o ? o->api_key : NULL);
    s->voice = sdup_def(o ? o->voice : NULL, "aura-asteria-en");
    s->pcm_sample_rate_hz = (o && o->pcm_sample_rate_hz > 0) ? o->pcm_sample_rate_hz : 24000;
    return s;
}
void ca_speech_deepgram_synthesizer_destroy(ca_speech_deepgram_synthesizer_t *s) {
    if (!s) return; free(s->base_address); free(s->api_key); free(s->voice); free(s);
}
bool ca_speech_deepgram_synthesizer_is_configured(const ca_speech_deepgram_synthesizer_t *s) {
    return s && !sblank(s->api_key);
}
static const char *deepgram_synth_backend_id(void *self) { (void)self; return "deepgram-aura"; }
static int deepgram_synth_synthesize(void *self, const char *text, const char *voice,
                                     const char *hint, ca_synthesis_result_t *out) {
    (void)hint;
    ca_speech_deepgram_synthesizer_t *s = (ca_speech_deepgram_synthesizer_t *)self;
    if (!out) return -1;
    if (!ca_speech_deepgram_synthesizer_is_configured(s)) { empty_synthesis(out, 0); return 0; }
    const char *v = sblank(voice) ? s->voice : voice;
    char *ev = url_escape(v);
    cb_t path = {0};
    char ratebuf[16]; snprintf(ratebuf, sizeof ratebuf, "%d", s->pcm_sample_rate_hz);
    cb_str(&path, "/v1/speak?model="); cb_str(&path, ev ? ev : "");
    cb_str(&path, "&encoding=linear16&sample_rate="); cb_str(&path, ratebuf);
    free(ev);
    cb_t j = {0}; cb_str(&j, "{\"text\":"); cb_json_str(&j, text ? text : ""); cb_str(&j, "}");
    cb_t auth = {0}; cb_str(&auth, "Token "); cb_str(&auth, s->api_key);
    ca_speech_http_header_t headers[2] = {
        { "Authorization", auth.buf ? auth.buf : "Token " },
        { "Content-Type", "application/json" },
    };
    http_resp_t resp = http_send(&s->http, "POST", path.buf ? path.buf : "/v1/speak",
                                 headers, 2, (const uint8_t *)j.buf, j.len);
    free(auth.buf); free(path.buf); free(j.buf);
    if (!resp.ok || !http_status_ok(resp.status)) { http_resp_free(&resp); empty_synthesis(out, 0); return 0; }
    memset(out, 0, sizeof *out);
    out->audio_len = resp.body_len; out->audio_pcm16_mono = resp.body; resp.body = NULL;
    out->sample_rate_hz = s->pcm_sample_rate_hz;
    out->duration_ms = pcm_duration_ms(out->audio_len, s->pcm_sample_rate_hz);
    http_resp_free(&resp);
    return 0;
}
ca_speech_synthesizer_t ca_speech_deepgram_synthesizer_as_synthesizer(ca_speech_deepgram_synthesizer_t *s) {
    ca_speech_synthesizer_t v; v.self = s; v.backend_id = deepgram_synth_backend_id;
    v.synthesize = deepgram_synth_synthesize; return v;
}

/* ===========================================================================
 * Azure recognizer (detailed JSON; HNS ticks)
 * =========================================================================== */

struct ca_speech_azure_recognizer {
    ca_speech_http_t http; char *base_address, *api_key, *language_code;
};
ca_speech_azure_recognizer_t *ca_speech_azure_recognizer_create(
    const ca_speech_http_t *http, const ca_speech_azure_stt_options_t *o) {
    if (!http || !http->request) return NULL;
    ca_speech_azure_recognizer_t *r = (ca_speech_azure_recognizer_t *)calloc(1, sizeof *r);
    if (!r) return NULL;
    r->http = *http;
    r->base_address = sdup(o ? o->base_address : NULL); /* may be NULL */
    r->api_key = sdup(o ? o->api_key : NULL);
    r->language_code = sdup_def(o ? o->language_code : NULL, "en-US");
    return r;
}
void ca_speech_azure_recognizer_destroy(ca_speech_azure_recognizer_t *r) {
    if (!r) return; free(r->base_address); free(r->api_key); free(r->language_code); free(r);
}
bool ca_speech_azure_recognizer_is_configured(const ca_speech_azure_recognizer_t *r) {
    return r && !sblank(r->api_key) && r->base_address != NULL;
}
static const char *azure_recognizer_backend_id(void *self) { (void)self; return "azure-stt"; }
static int azure_recognizer_transcribe(void *self, const uint8_t *audio, size_t len,
                                       int rate, const char *hint, ca_transcription_result_t *out) {
    ca_speech_azure_recognizer_t *r = (ca_speech_azure_recognizer_t *)self;
    if (!out) return -1;
    if (!ca_speech_azure_recognizer_is_configured(r)) { empty_transcription(out, NULL); return 0; }
    const char *lang = sblank(hint) ? r->language_code : hint;
    char *elang = url_escape(lang);
    cb_t path = {0};
    cb_str(&path, "/speech/recognition/conversation/cognitiveservices/v1?language=");
    cb_str(&path, elang ? elang : ""); cb_str(&path, "&format=detailed");
    free(elang);
    cb_t ct = {0};
    char ratebuf[16]; snprintf(ratebuf, sizeof ratebuf, "%d", rate);
    cb_str(&ct, "audio/wav; codecs=audio/pcm; samplerate="); cb_str(&ct, ratebuf);
    ca_speech_http_header_t headers[3] = {
        { "Content-Type", ct.buf ? ct.buf : "audio/wav" },
        { "Ocp-Apim-Subscription-Key", r->api_key },
        { "Accept", "application/json" },
    };
    http_resp_t resp = http_send(&r->http, "POST", path.buf ? path.buf : "/", headers, 3, audio, len);
    free(path.buf); free(ct.buf);
    if (!resp.ok || !http_status_ok(resp.status)) { http_resp_free(&resp); empty_transcription(out, NULL); return 0; }
    char *json = body_to_cstr(&resp); http_resp_free(&resp);
    if (!json) return -1;

    empty_transcription(out, NULL);
    char *status = NULL; const char *stv = json_obj_get(json, "RecognitionStatus");
    if (stv && json_is_string(stv)) status = json_read_string(stv);
    if (!status || strcmp(status, "Success") != 0) { free(status); free(json); return 0; }
    free(status);

    char *text = NULL; const char *tv = json_obj_get(json, "DisplayText");
    if (tv && json_is_string(tv)) text = json_read_string(tv);
    long long off_ticks = 0, dur_ticks = 0;
    const char *ov = json_obj_get(json, "Offset");   if (ov) off_ticks = json_read_int64(ov);
    const char *dv = json_obj_get(json, "Duration"); if (dv) dur_ticks = json_read_int64(dv);
    /* 100-ns ticks -> ms */
    int64_t off_ms = (int64_t)(off_ticks / 10000);
    int64_t dur_ms = (int64_t)(dur_ticks / 10000);
    float conf = 0.0f;
    const char *nb = json_obj_get(json, "NBest");
    if (nb && json_is_array(nb) && json_array_len(nb) > 0) {
        const char *nb0 = json_array_at(nb, 0);
        const char *cv = nb0 ? json_obj_get(nb0, "Confidence") : NULL;
        if (cv) conf = (float)json_read_double(cv);
    }
    seglist_t segs = {0};
    seg_push(&segs, text ? text : "", off_ms, dur_ms, lang, conf);
    free(out->text);
    out->text = text ? text : sdup("");
    out->language = sdup(lang);
    out->segments = segs.arr; out->segment_count = segs.n;
    out->total_duration_ms = dur_ms;
    free(json);
    return 0;
}
ca_speech_recognizer_t ca_speech_azure_recognizer_as_recognizer(ca_speech_azure_recognizer_t *r) {
    ca_speech_recognizer_t v; v.self = r; v.backend_id = azure_recognizer_backend_id;
    v.transcribe = azure_recognizer_transcribe; return v;
}

/* ===========================================================================
 * Azure synthesizer (SSML; raw-<k>khz-16bit-mono-pcm)
 * =========================================================================== */

struct ca_speech_azure_synthesizer {
    ca_speech_http_t http; char *base_address, *api_key, *language_code, *default_voice_name;
    int pcm_sample_rate_hz;
};
ca_speech_azure_synthesizer_t *ca_speech_azure_synthesizer_create(
    const ca_speech_http_t *http, const ca_speech_azure_tts_options_t *o) {
    if (!http || !http->request) return NULL;
    ca_speech_azure_synthesizer_t *s = (ca_speech_azure_synthesizer_t *)calloc(1, sizeof *s);
    if (!s) return NULL;
    s->http = *http;
    s->base_address = sdup(o ? o->base_address : NULL);
    s->api_key = sdup(o ? o->api_key : NULL);
    s->language_code = sdup_def(o ? o->language_code : NULL, "en-US");
    s->default_voice_name = sdup_def(o ? o->default_voice_name : NULL, "en-US-AvaMultilingualNeural");
    s->pcm_sample_rate_hz = (o && o->pcm_sample_rate_hz > 0) ? o->pcm_sample_rate_hz : 24000;
    return s;
}
void ca_speech_azure_synthesizer_destroy(ca_speech_azure_synthesizer_t *s) {
    if (!s) return; free(s->base_address); free(s->api_key); free(s->language_code);
    free(s->default_voice_name); free(s);
}
bool ca_speech_azure_synthesizer_is_configured(const ca_speech_azure_synthesizer_t *s) {
    return s && !sblank(s->api_key) && s->base_address != NULL;
}
static const char *azure_synth_backend_id(void *self) { (void)self; return "azure-tts"; }
/* HtmlEncode (& < > " ') into a char buffer. */
static void cb_html_encode(cb_t *b, const char *s) {
    for (const char *p = s ? s : ""; *p; ++p) {
        switch (*p) {
            case '&': cb_str(b, "&amp;"); break;
            case '<': cb_str(b, "&lt;"); break;
            case '>': cb_str(b, "&gt;"); break;
            case '"': cb_str(b, "&quot;"); break;
            case '\'': cb_str(b, "&#39;"); break;
            default: cb_ch(b, *p); break;
        }
    }
}
static int azure_synth_synthesize(void *self, const char *text, const char *voice,
                                  const char *hint, ca_synthesis_result_t *out) {
    ca_speech_azure_synthesizer_t *s = (ca_speech_azure_synthesizer_t *)self;
    if (!out) return -1;
    if (!ca_speech_azure_synthesizer_is_configured(s)) { empty_synthesis(out, 0); return 0; }
    const char *v = sblank(voice) ? s->default_voice_name : voice;
    const char *lang = sblank(hint) ? s->language_code : hint;
    int rate = s->pcm_sample_rate_hz;
    cb_t ssml = {0};
    cb_str(&ssml, "<speak version='1.0' xml:lang='"); cb_str(&ssml, lang);
    cb_str(&ssml, "'>\n  <voice name='"); cb_str(&ssml, v); cb_str(&ssml, "'>");
    cb_html_encode(&ssml, text);
    cb_str(&ssml, "</voice>\n</speak>");
    char fmt[48]; snprintf(fmt, sizeof fmt, "raw-%dkhz-16bit-mono-pcm", rate / 1000);
    ca_speech_http_header_t headers[4] = {
        { "Content-Type", "application/ssml+xml" },
        { "Ocp-Apim-Subscription-Key", s->api_key },
        { "X-Microsoft-OutputFormat", fmt },
        { "User-Agent", "CircleAI" },
    };
    http_resp_t resp = http_send(&s->http, "POST", "/cognitiveservices/v1", headers, 4,
                                 (const uint8_t *)ssml.buf, ssml.len);
    free(ssml.buf);
    if (!resp.ok || !http_status_ok(resp.status)) { http_resp_free(&resp); empty_synthesis(out, 0); return 0; }
    memset(out, 0, sizeof *out);
    out->audio_len = resp.body_len; out->audio_pcm16_mono = resp.body; resp.body = NULL;
    out->sample_rate_hz = rate;
    out->duration_ms = pcm_duration_ms(out->audio_len, rate);
    http_resp_free(&resp);
    return 0;
}
ca_speech_synthesizer_t ca_speech_azure_synthesizer_as_synthesizer(ca_speech_azure_synthesizer_t *s) {
    ca_speech_synthesizer_t v; v.self = s; v.backend_id = azure_synth_backend_id;
    v.synthesize = azure_synth_synthesize; return v;
}

/* ===========================================================================
 * Google recognizer (v1 speech:recognize, base64 LINEAR16, API-key)
 * =========================================================================== */

struct ca_speech_google_recognizer {
    ca_speech_http_t http; char *base_address, *api_key, *language_code;
};
ca_speech_google_recognizer_t *ca_speech_google_recognizer_create(
    const ca_speech_http_t *http, const ca_speech_google_stt_options_t *o) {
    if (!http || !http->request) return NULL;
    ca_speech_google_recognizer_t *r = (ca_speech_google_recognizer_t *)calloc(1, sizeof *r);
    if (!r) return NULL;
    r->http = *http;
    r->base_address = sdup_def(o ? o->base_address : NULL, "https://speech.googleapis.com");
    r->api_key = sdup(o ? o->api_key : NULL);
    r->language_code = sdup_def(o ? o->language_code : NULL, "en-US");
    return r;
}
void ca_speech_google_recognizer_destroy(ca_speech_google_recognizer_t *r) {
    if (!r) return; free(r->base_address); free(r->api_key); free(r->language_code); free(r);
}
bool ca_speech_google_recognizer_is_configured(const ca_speech_google_recognizer_t *r) {
    return r && !sblank(r->api_key);
}
static const char *google_recognizer_backend_id(void *self) { (void)self; return "google-stt"; }
/* Google encodes word times as e.g. "1.500s". */
static double google_parse_seconds(const char *val_json) {
    char *s = json_is_string(val_json) ? json_read_string(val_json) : NULL;
    if (!s) return 0.0;
    size_t n = strlen(s);
    if (n && s[n-1]=='s') s[n-1] = '\0';
    double d = strtod(s, NULL);
    free(s);
    return d;
}
static int google_recognizer_transcribe(void *self, const uint8_t *audio, size_t len,
                                        int rate, const char *hint, ca_transcription_result_t *out) {
    ca_speech_google_recognizer_t *r = (ca_speech_google_recognizer_t *)self;
    if (!out) return -1;
    if (!ca_speech_google_recognizer_is_configured(r)) { empty_transcription(out, NULL); return 0; }
    const char *lang = sblank(hint) ? r->language_code : hint;
    char *b64 = b64_encode(audio, len);
    if (!b64) return -1;
    cb_t j = {0};
    char ratebuf[16]; snprintf(ratebuf, sizeof ratebuf, "%d", rate);
    cb_str(&j, "{\"config\":{\"encoding\":\"LINEAR16\",\"sampleRateHertz\":");
    cb_str(&j, ratebuf);
    cb_str(&j, ",\"languageCode\":"); cb_json_str(&j, lang);
    cb_str(&j, ",\"enableWordTimeOffsets\":true,\"enableWordConfidence\":true},\"audio\":{\"content\":");
    cb_json_str(&j, b64);
    cb_str(&j, "}}");
    free(b64);
    char *ekey = url_escape(r->api_key);
    cb_t path = {0};
    cb_str(&path, "/v1/speech:recognize?key="); cb_str(&path, ekey ? ekey : "");
    free(ekey);
    ca_speech_http_header_t headers[1] = { { "Content-Type", "application/json" } };
    http_resp_t resp = http_send(&r->http, "POST", path.buf ? path.buf : "/v1/speech:recognize",
                                 headers, 1, (const uint8_t *)j.buf, j.len);
    free(path.buf); free(j.buf);
    if (!resp.ok || !http_status_ok(resp.status)) { http_resp_free(&resp); empty_transcription(out, NULL); return 0; }
    char *json = body_to_cstr(&resp); http_resp_free(&resp);
    if (!json) return -1;

    cb_t alltext = {0};
    seglist_t segs = {0};
    const char *results = json_obj_get(json, "results");
    if (results && json_is_array(results)) {
        size_t rn = json_array_len(results);
        for (size_t ri = 0; ri < rn; ++ri) {
            const char *res = json_array_at(results, ri);
            const char *alts = res ? json_obj_get(res, "alternatives") : NULL;
            if (!alts || !json_is_array(alts) || json_array_len(alts) == 0) continue;
            const char *alt = json_array_at(alts, 0);
            const char *tv = alt ? json_obj_get(alt, "transcript") : NULL;
            if (tv && json_is_string(tv)) {
                char *t = json_read_string(tv);
                if (alltext.len > 0) cb_ch(&alltext, ' ');
                cb_str(&alltext, t ? t : "");
                free(t);
            }
            const char *words = alt ? json_obj_get(alt, "words") : NULL;
            if (words && json_is_array(words)) {
                size_t wn = json_array_len(words);
                for (size_t wi = 0; wi < wn; ++wi) {
                    const char *w = json_array_at(words, wi);
                    if (!w) break;
                    double start = 0, end = 0;
                    const char *sv = json_obj_get(w, "startTime"); if (sv) start = google_parse_seconds(sv);
                    const char *ev = json_obj_get(w, "endTime");   if (ev) end = google_parse_seconds(ev);
                    char *wt = NULL; const char *wtv = json_obj_get(w, "word");
                    if (wtv && json_is_string(wtv)) wt = json_read_string(wtv);
                    float conf = 0.0f;
                    const char *cv = json_obj_get(w, "confidence"); if (cv) conf = (float)json_read_double(cv);
                    double d = end - start; if (d < 0) d = 0;
                    seg_push(&segs, wt ? wt : "", (int64_t)(start*1000.0), (int64_t)(d*1000.0), lang, conf);
                    free(wt);
                }
            }
        }
    }
    memset(out, 0, sizeof *out);
    out->text = alltext.buf ? alltext.buf : sdup("");
    out->language = sdup(lang);
    out->segments = segs.arr; out->segment_count = segs.n;
    out->total_duration_ms = 0;
    free(json);
    return 0;
}
ca_speech_recognizer_t ca_speech_google_recognizer_as_recognizer(ca_speech_google_recognizer_t *r) {
    ca_speech_recognizer_t v; v.self = r; v.backend_id = google_recognizer_backend_id;
    v.transcribe = google_recognizer_transcribe; return v;
}

/* ===========================================================================
 * Google synthesizer (v1 text:synthesize, base64 LINEAR16 -> strip WAV)
 * =========================================================================== */

struct ca_speech_google_synthesizer {
    ca_speech_http_t http; char *base_address, *api_key, *language_code, *default_voice_name;
    int pcm_sample_rate_hz;
};
ca_speech_google_synthesizer_t *ca_speech_google_synthesizer_create(
    const ca_speech_http_t *http, const ca_speech_google_tts_options_t *o) {
    if (!http || !http->request) return NULL;
    ca_speech_google_synthesizer_t *s = (ca_speech_google_synthesizer_t *)calloc(1, sizeof *s);
    if (!s) return NULL;
    s->http = *http;
    s->base_address = sdup_def(o ? o->base_address : NULL, "https://texttospeech.googleapis.com");
    s->api_key = sdup(o ? o->api_key : NULL);
    s->language_code = sdup_def(o ? o->language_code : NULL, "en-US");
    s->default_voice_name = sdup_def(o ? o->default_voice_name : NULL, "en-US-Studio-O");
    s->pcm_sample_rate_hz = (o && o->pcm_sample_rate_hz > 0) ? o->pcm_sample_rate_hz : 24000;
    return s;
}
void ca_speech_google_synthesizer_destroy(ca_speech_google_synthesizer_t *s) {
    if (!s) return; free(s->base_address); free(s->api_key); free(s->language_code);
    free(s->default_voice_name); free(s);
}
bool ca_speech_google_synthesizer_is_configured(const ca_speech_google_synthesizer_t *s) {
    return s && !sblank(s->api_key);
}
static const char *google_synth_backend_id(void *self) { (void)self; return "google-tts"; }
static int google_synth_synthesize(void *self, const char *text, const char *voice,
                                   const char *hint, ca_synthesis_result_t *out) {
    ca_speech_google_synthesizer_t *s = (ca_speech_google_synthesizer_t *)self;
    if (!out) return -1;
    if (!ca_speech_google_synthesizer_is_configured(s)) { empty_synthesis(out, 0); return 0; }
    const char *v = sblank(voice) ? s->default_voice_name : voice;
    const char *lang = sblank(hint) ? s->language_code : hint;
    cb_t j = {0};
    char ratebuf[16]; snprintf(ratebuf, sizeof ratebuf, "%d", s->pcm_sample_rate_hz);
    cb_str(&j, "{\"input\":{\"text\":"); cb_json_str(&j, text ? text : "");
    cb_str(&j, "},\"voice\":{\"languageCode\":"); cb_json_str(&j, lang);
    cb_str(&j, ",\"name\":"); cb_json_str(&j, v);
    cb_str(&j, "},\"audioConfig\":{\"audioEncoding\":\"LINEAR16\",\"sampleRateHertz\":");
    cb_str(&j, ratebuf); cb_str(&j, "}}");
    char *ekey = url_escape(s->api_key);
    cb_t path = {0};
    cb_str(&path, "/v1/text:synthesize?key="); cb_str(&path, ekey ? ekey : "");
    free(ekey);
    ca_speech_http_header_t headers[1] = { { "Content-Type", "application/json" } };
    http_resp_t resp = http_send(&s->http, "POST", path.buf ? path.buf : "/v1/text:synthesize",
                                 headers, 1, (const uint8_t *)j.buf, j.len);
    free(path.buf); free(j.buf);
    if (!resp.ok || !http_status_ok(resp.status)) { http_resp_free(&resp); empty_synthesis(out, 0); return 0; }
    char *json = body_to_cstr(&resp); http_resp_free(&resp);
    if (!json) return -1;
    char *ac = NULL; const char *av = json_obj_get(json, "audioContent");
    if (av && json_is_string(av)) ac = json_read_string(av);
    if (!ac || !*ac) { free(ac); free(json); empty_synthesis(out, 0); return 0; }
    size_t raw_len = 0;
    uint8_t *raw = b64_decode(ac, &raw_len);
    free(ac); free(json);
    if (!raw) return -1;
    size_t pcm_len = 0;
    uint8_t *pcm = strip_wav_header(raw, raw_len, &pcm_len);
    free(raw);
    if (!pcm) return -1;
    memset(out, 0, sizeof *out);
    out->audio_pcm16_mono = pcm; out->audio_len = pcm_len;
    out->sample_rate_hz = s->pcm_sample_rate_hz;
    out->duration_ms = pcm_duration_ms(pcm_len, s->pcm_sample_rate_hz);
    return 0;
}
ca_speech_synthesizer_t ca_speech_google_synthesizer_as_synthesizer(ca_speech_google_synthesizer_t *s) {
    ca_speech_synthesizer_t v; v.self = s; v.backend_id = google_synth_backend_id;
    v.synthesize = google_synth_synthesize; return v;
}

/* ===========================================================================
 * AssemblyAI recognizer (upload -> submit -> poll)
 * =========================================================================== */

struct ca_speech_assemblyai_recognizer {
    ca_speech_http_t http; char *base_address, *api_key, *speech_model;
};
ca_speech_assemblyai_recognizer_t *ca_speech_assemblyai_recognizer_create(
    const ca_speech_http_t *http, const ca_speech_assemblyai_options_t *o) {
    if (!http || !http->request) return NULL;
    ca_speech_assemblyai_recognizer_t *r = (ca_speech_assemblyai_recognizer_t *)calloc(1, sizeof *r);
    if (!r) return NULL;
    r->http = *http;
    r->base_address = sdup_def(o ? o->base_address : NULL, "https://api.assemblyai.com");
    r->api_key = sdup(o ? o->api_key : NULL);
    r->speech_model = sdup_def(o ? o->speech_model : NULL, "universal");
    return r;
}
void ca_speech_assemblyai_recognizer_destroy(ca_speech_assemblyai_recognizer_t *r) {
    if (!r) return; free(r->base_address); free(r->api_key); free(r->speech_model); free(r);
}
bool ca_speech_assemblyai_recognizer_is_configured(const ca_speech_assemblyai_recognizer_t *r) {
    return r && !sblank(r->api_key);
}
static const char *assemblyai_recognizer_backend_id(void *self) { (void)self; return "assemblyai"; }
static int assemblyai_recognizer_transcribe(void *self, const uint8_t *audio, size_t len,
                                            int rate, const char *hint, ca_transcription_result_t *out) {
    ca_speech_assemblyai_recognizer_t *r = (ca_speech_assemblyai_recognizer_t *)self;
    if (!out) return -1;
    if (!ca_speech_assemblyai_recognizer_is_configured(r)) { empty_transcription(out, NULL); return 0; }

    /* 1) upload the WAV-wrapped bytes */
    size_t wav_len = 0;
    uint8_t *wav = wrap_pcm_as_wav(audio, len, rate, &wav_len);
    if (!wav) return -1;
    ca_speech_http_header_t up_headers[2] = {
        { "Content-Type", "application/octet-stream" },
        { "Authorization", r->api_key },
    };
    http_resp_t up = http_send(&r->http, "POST", "/v2/upload", up_headers, 2, wav, wav_len);
    free(wav);
    if (!up.ok || !http_status_ok(up.status)) { http_resp_free(&up); empty_transcription(out, NULL); return 0; }
    char *up_json = body_to_cstr(&up); http_resp_free(&up);
    if (!up_json) return -1;
    char *upload_url = NULL; const char *uv = json_obj_get(up_json, "upload_url");
    if (uv && json_is_string(uv)) upload_url = json_read_string(uv);
    free(up_json);
    if (sblank(upload_url)) { free(upload_url); empty_transcription(out, NULL); return 0; }

    /* 2) submit transcript job */
    cb_t body = {0};
    cb_str(&body, "{\"audio_url\":"); cb_json_str(&body, upload_url);
    cb_str(&body, ",\"speech_model\":"); cb_json_str(&body, r->speech_model);
    if (!sblank(hint)) { cb_str(&body, ",\"language_code\":"); cb_json_str(&body, hint); }
    cb_str(&body, "}");
    free(upload_url);
    ca_speech_http_header_t sub_headers[2] = {
        { "Content-Type", "application/json" },
        { "Authorization", r->api_key },
    };
    http_resp_t sub = http_send(&r->http, "POST", "/v2/transcript", sub_headers, 2,
                                (const uint8_t *)body.buf, body.len);
    free(body.buf);
    if (!sub.ok || !http_status_ok(sub.status)) { http_resp_free(&sub); empty_transcription(out, NULL); return 0; }
    char *sub_json = body_to_cstr(&sub); http_resp_free(&sub);
    if (!sub_json) return -1;
    char *tid = NULL; const char *idv = json_obj_get(sub_json, "id");
    if (idv && json_is_string(idv)) tid = json_read_string(idv);
    free(sub_json);
    if (sblank(tid)) { free(tid); empty_transcription(out, NULL); return 0; }

    /* 3) poll (max 60 attempts). No sleep in the hermetic C port — the injected
     * transport decides when a poll returns "completed". */
    cb_t poll_path = {0};
    cb_str(&poll_path, "/v2/transcript/"); cb_str(&poll_path, tid);
    free(tid);
    ca_speech_http_header_t poll_headers[1] = { { "Authorization", r->api_key } };
    for (int attempt = 0; attempt < 60; ++attempt) {
        http_resp_t pr = http_send(&r->http, "GET", poll_path.buf ? poll_path.buf : "/", poll_headers, 1, NULL, 0);
        if (!pr.ok || !http_status_ok(pr.status)) { http_resp_free(&pr); continue; }
        char *pj = body_to_cstr(&pr); http_resp_free(&pr);
        if (!pj) { free(poll_path.buf); return -1; }
        char *status = NULL; const char *sv = json_obj_get(pj, "status");
        if (sv && json_is_string(sv)) status = json_read_string(sv);
        if (status && strcmp(status, "completed") == 0) {
            char *text = NULL; const char *tv = json_obj_get(pj, "text");
            if (tv && json_is_string(tv)) text = json_read_string(tv);
            char *lang = NULL; const char *lv = json_obj_get(pj, "language_code");
            if (lv && json_is_string(lv)) lang = json_read_string(lv);
            else if (!sblank(hint)) lang = sdup(hint);
            int64_t dur_ms = 0;
            const char *adv = json_obj_get(pj, "audio_duration");
            if (adv && json_is_number(adv)) dur_ms = (int64_t)(json_read_double(adv) * 1000.0);
            seglist_t segs = {0};
            const char *words = json_obj_get(pj, "words");
            if (words && json_is_array(words)) {
                size_t wn = json_array_len(words);
                for (size_t wi = 0; wi < wn; ++wi) {
                    const char *w = json_array_at(words, wi);
                    if (!w) break;
                    double start = 0, end = 0;
                    const char *ws = json_obj_get(w, "start"); if (ws) start = json_read_double(ws)/1000.0;
                    const char *we = json_obj_get(w, "end");   if (we) end = json_read_double(we)/1000.0; else end = start;
                    char *wt = NULL; const char *wtv = json_obj_get(w, "text");
                    if (wtv && json_is_string(wtv)) wt = json_read_string(wtv);
                    float conf = 0.0f; const char *cv = json_obj_get(w, "confidence"); if (cv) conf = (float)json_read_double(cv);
                    double d = end - start; if (d < 0) d = 0;
                    seg_push(&segs, wt ? wt : "", (int64_t)(start*1000.0), (int64_t)(d*1000.0), lang, conf);
                    free(wt);
                }
            }
            memset(out, 0, sizeof *out);
            out->text = text ? text : sdup("");
            out->language = lang;
            out->segments = segs.arr; out->segment_count = segs.n;
            out->total_duration_ms = dur_ms;
            free(status); free(pj); free(poll_path.buf);
            return 0;
        }
        if (status && strcmp(status, "error") == 0) {
            free(status); free(pj); free(poll_path.buf);
            empty_transcription(out, NULL);
            return 0;
        }
        free(status); free(pj);
    }
    free(poll_path.buf);
    empty_transcription(out, NULL); /* timed out */
    return 0;
}
ca_speech_recognizer_t ca_speech_assemblyai_recognizer_as_recognizer(ca_speech_assemblyai_recognizer_t *r) {
    ca_speech_recognizer_t v; v.self = r; v.backend_id = assemblyai_recognizer_backend_id;
    v.transcribe = assemblyai_recognizer_transcribe; return v;
}

/* ===========================================================================
 * Cartesia recognizer (/v1/transcribe, multipart WAV, Bearer)
 * =========================================================================== */

struct ca_speech_cartesia_recognizer {
    ca_speech_http_t http; char *base_address, *api_key, *model, *cartesia_version;
};
ca_speech_cartesia_recognizer_t *ca_speech_cartesia_recognizer_create(
    const ca_speech_http_t *http, const ca_speech_cartesia_stt_options_t *o) {
    if (!http || !http->request) return NULL;
    ca_speech_cartesia_recognizer_t *r = (ca_speech_cartesia_recognizer_t *)calloc(1, sizeof *r);
    if (!r) return NULL;
    r->http = *http;
    r->base_address = sdup_def(o ? o->base_address : NULL, "https://api.cartesia.ai");
    r->api_key = sdup(o ? o->api_key : NULL);
    r->model = sdup_def(o ? o->model : NULL, "ink-whisper");
    r->cartesia_version = sdup_def(o ? o->cartesia_version : NULL, "2025-04-16");
    return r;
}
void ca_speech_cartesia_recognizer_destroy(ca_speech_cartesia_recognizer_t *r) {
    if (!r) return; free(r->base_address); free(r->api_key); free(r->model);
    free(r->cartesia_version); free(r);
}
bool ca_speech_cartesia_recognizer_is_configured(const ca_speech_cartesia_recognizer_t *r) {
    return r && !sblank(r->api_key);
}
static const char *cartesia_recognizer_backend_id(void *self) { (void)self; return "cartesia-stt"; }
static int cartesia_recognizer_transcribe(void *self, const uint8_t *audio, size_t len,
                                          int rate, const char *hint, ca_transcription_result_t *out) {
    ca_speech_cartesia_recognizer_t *r = (ca_speech_cartesia_recognizer_t *)self;
    if (!out) return -1;
    if (!ca_speech_cartesia_recognizer_is_configured(r)) { empty_transcription(out, NULL); return 0; }
    size_t wav_len = 0;
    uint8_t *wav = wrap_pcm_as_wav(audio, len, rate, &wav_len);
    if (!wav) return -1;
    form_field_t fields[2]; size_t fc = 0;
    fields[fc].name = "model"; fields[fc].value = r->model; ++fc;
    if (!sblank(hint)) { fields[fc].name = "language"; fields[fc].value = hint; ++fc; }
    size_t body_len = 0; char *content_type = NULL;
    uint8_t *body = build_multipart(fields, fc, "file", "audio.wav", "audio/wav",
                                    wav, wav_len, &body_len, &content_type);
    free(wav);
    if (!body || !content_type) { free(body); free(content_type); return -1; }
    cb_t auth = {0}; cb_str(&auth, "Bearer "); cb_str(&auth, r->api_key);
    ca_speech_http_header_t headers[3] = {
        { "Authorization", auth.buf ? auth.buf : "Bearer " },
        { "Cartesia-Version", r->cartesia_version },
        { "Content-Type", content_type },
    };
    http_resp_t resp = http_send(&r->http, "POST", "/v1/transcribe", headers, 3, body, body_len);
    free(auth.buf); free(content_type); free(body);
    if (!resp.ok || !http_status_ok(resp.status)) { http_resp_free(&resp); empty_transcription(out, NULL); return 0; }
    char *json = body_to_cstr(&resp); http_resp_free(&resp);
    if (!json) return -1;
    char *text = NULL; const char *tv = json_obj_get(json, "text");
    if (tv && json_is_string(tv)) text = json_read_string(tv);
    char *lang = NULL; const char *lv = json_obj_get(json, "language");
    if (lv && json_is_string(lv)) lang = json_read_string(lv);
    else if (!sblank(hint)) lang = sdup(hint);
    int64_t dur_ms = 0;
    const char *dv = json_obj_get(json, "duration");
    if (dv && json_is_number(dv)) dur_ms = (int64_t)(json_read_double(dv) * 1000.0);
    memset(out, 0, sizeof *out);
    out->text = text ? text : sdup("");
    out->language = lang;
    out->segments = NULL; out->segment_count = 0;
    out->total_duration_ms = dur_ms;
    free(json);
    return 0;
}
ca_speech_recognizer_t ca_speech_cartesia_recognizer_as_recognizer(ca_speech_cartesia_recognizer_t *r) {
    ca_speech_recognizer_t v; v.self = r; v.backend_id = cartesia_recognizer_backend_id;
    v.transcribe = cartesia_recognizer_transcribe; return v;
}

/* ===========================================================================
 * Cartesia Sonic synthesizer (/v1/tts/bytes)
 * =========================================================================== */

struct ca_speech_cartesia_synthesizer {
    ca_speech_http_t http;
    char *base_address, *api_key, *model, *default_voice_id, *output_container, *output_encoding, *cartesia_version;
    int pcm_sample_rate_hz;
};
ca_speech_cartesia_synthesizer_t *ca_speech_cartesia_synthesizer_create(
    const ca_speech_http_t *http, const ca_speech_cartesia_tts_options_t *o) {
    if (!http || !http->request) return NULL;
    ca_speech_cartesia_synthesizer_t *s = (ca_speech_cartesia_synthesizer_t *)calloc(1, sizeof *s);
    if (!s) return NULL;
    s->http = *http;
    s->base_address = sdup_def(o ? o->base_address : NULL, "https://api.cartesia.ai");
    s->api_key = sdup(o ? o->api_key : NULL);
    s->model = sdup_def(o ? o->model : NULL, "sonic-2");
    s->default_voice_id = sdup_def(o ? o->default_voice_id : NULL, "a0e99841-438c-4a64-b679-ae501e7d6091");
    s->output_container = sdup_def(o ? o->output_container : NULL, "raw");
    s->output_encoding = sdup_def(o ? o->output_encoding : NULL, "pcm_s16le");
    s->cartesia_version = sdup_def(o ? o->cartesia_version : NULL, "2025-04-16");
    s->pcm_sample_rate_hz = (o && o->pcm_sample_rate_hz > 0) ? o->pcm_sample_rate_hz : 24000;
    return s;
}
void ca_speech_cartesia_synthesizer_destroy(ca_speech_cartesia_synthesizer_t *s) {
    if (!s) return;
    free(s->base_address); free(s->api_key); free(s->model); free(s->default_voice_id);
    free(s->output_container); free(s->output_encoding); free(s->cartesia_version); free(s);
}
bool ca_speech_cartesia_synthesizer_is_configured(const ca_speech_cartesia_synthesizer_t *s) {
    return s && !sblank(s->api_key);
}
static const char *cartesia_synth_backend_id(void *self) { (void)self; return "cartesia-tts"; }
static int cartesia_synth_synthesize(void *self, const char *text, const char *voice,
                                     const char *hint, ca_synthesis_result_t *out) {
    ca_speech_cartesia_synthesizer_t *s = (ca_speech_cartesia_synthesizer_t *)self;
    if (!out) return -1;
    if (!ca_speech_cartesia_synthesizer_is_configured(s)) { empty_synthesis(out, 0); return 0; }
    const char *v = sblank(voice) ? s->default_voice_id : voice;
    cb_t j = {0};
    char ratebuf[16]; snprintf(ratebuf, sizeof ratebuf, "%d", s->pcm_sample_rate_hz);
    cb_str(&j, "{\"model_id\":"); cb_json_str(&j, s->model);
    cb_str(&j, ",\"transcript\":"); cb_json_str(&j, text ? text : "");
    cb_str(&j, ",\"voice\":{\"mode\":\"id\",\"id\":"); cb_json_str(&j, v); cb_str(&j, "}");
    cb_str(&j, ",\"output_format\":{\"container\":"); cb_json_str(&j, s->output_container);
    cb_str(&j, ",\"encoding\":"); cb_json_str(&j, s->output_encoding);
    cb_str(&j, ",\"sample_rate\":"); cb_str(&j, ratebuf); cb_str(&j, "}");
    cb_str(&j, ",\"language\":"); cb_json_str(&j, sblank(hint) ? "en" : hint);
    cb_str(&j, "}");
    cb_t auth = {0}; cb_str(&auth, "Bearer "); cb_str(&auth, s->api_key);
    ca_speech_http_header_t headers[3] = {
        { "Authorization", auth.buf ? auth.buf : "Bearer " },
        { "Cartesia-Version", s->cartesia_version },
        { "Content-Type", "application/json" },
    };
    http_resp_t resp = http_send(&s->http, "POST", "/v1/tts/bytes", headers, 3,
                                 (const uint8_t *)j.buf, j.len);
    free(auth.buf); free(j.buf);
    if (!resp.ok || !http_status_ok(resp.status)) { http_resp_free(&resp); empty_synthesis(out, 0); return 0; }
    memset(out, 0, sizeof *out);
    out->audio_len = resp.body_len; out->audio_pcm16_mono = resp.body; resp.body = NULL;
    out->sample_rate_hz = s->pcm_sample_rate_hz;
    out->duration_ms = pcm_duration_ms(out->audio_len, s->pcm_sample_rate_hz);
    http_resp_free(&resp);
    return 0;
}
ca_speech_synthesizer_t ca_speech_cartesia_synthesizer_as_synthesizer(ca_speech_cartesia_synthesizer_t *s) {
    ca_speech_synthesizer_t v; v.self = s; v.backend_id = cartesia_synth_backend_id;
    v.synthesize = cartesia_synth_synthesize; return v;
}

/* ===========================================================================
 * ElevenLabs synthesizer (/v1/text-to-speech/{voice}?output_format=pcm_*)
 * =========================================================================== */

struct ca_speech_elevenlabs_synthesizer {
    ca_speech_http_t http; char *base_address, *api_key, *default_voice_id, *model, *output_format;
    int pcm_sample_rate_hz;
};
ca_speech_elevenlabs_synthesizer_t *ca_speech_elevenlabs_synthesizer_create(
    const ca_speech_http_t *http, const ca_speech_elevenlabs_options_t *o) {
    if (!http || !http->request) return NULL;
    ca_speech_elevenlabs_synthesizer_t *s = (ca_speech_elevenlabs_synthesizer_t *)calloc(1, sizeof *s);
    if (!s) return NULL;
    s->http = *http;
    s->base_address = sdup_def(o ? o->base_address : NULL, "https://api.elevenlabs.io");
    s->api_key = sdup(o ? o->api_key : NULL);
    s->default_voice_id = sdup_def(o ? o->default_voice_id : NULL, "21m00Tcm4TlvDq8ikWAM");
    s->model = sdup_def(o ? o->model : NULL, "eleven_flash_v2_5");
    s->output_format = sdup_def(o ? o->output_format : NULL, "pcm_24000");
    s->pcm_sample_rate_hz = (o && o->pcm_sample_rate_hz > 0) ? o->pcm_sample_rate_hz : 24000;
    return s;
}
void ca_speech_elevenlabs_synthesizer_destroy(ca_speech_elevenlabs_synthesizer_t *s) {
    if (!s) return; free(s->base_address); free(s->api_key); free(s->default_voice_id);
    free(s->model); free(s->output_format); free(s);
}
bool ca_speech_elevenlabs_synthesizer_is_configured(const ca_speech_elevenlabs_synthesizer_t *s) {
    return s && !sblank(s->api_key);
}
static const char *elevenlabs_synth_backend_id(void *self) { (void)self; return "elevenlabs"; }
/* parse pcm_<rate> -> rate, else fallback */
static int parse_pcm_rate(const char *fmt, int fallback) {
    if (!fmt) return fallback;
    const char *p = strstr(fmt, "pcm_");
    if (!p) return fallback;
    p += 4;
    if (!isdigit((unsigned char)*p)) return fallback;
    int r = (int)strtol(p, NULL, 10);
    return r > 0 ? r : fallback;
}
static int elevenlabs_synth_synthesize(void *self, const char *text, const char *voice,
                                       const char *hint, ca_synthesis_result_t *out) {
    (void)hint;
    ca_speech_elevenlabs_synthesizer_t *s = (ca_speech_elevenlabs_synthesizer_t *)self;
    if (!out) return -1;
    if (!ca_speech_elevenlabs_synthesizer_is_configured(s)) { empty_synthesis(out, 0); return 0; }
    const char *v = sblank(voice) ? s->default_voice_id : voice;
    int rate = parse_pcm_rate(s->output_format, s->pcm_sample_rate_hz);
    char *ev = url_escape(v);
    cb_t path = {0};
    cb_str(&path, "/v1/text-to-speech/"); cb_str(&path, ev ? ev : "");
    cb_str(&path, "?output_format="); cb_str(&path, s->output_format);
    free(ev);
    cb_t j = {0};
    cb_str(&j, "{\"text\":"); cb_json_str(&j, text ? text : "");
    cb_str(&j, ",\"model_id\":"); cb_json_str(&j, s->model); cb_str(&j, "}");
    ca_speech_http_header_t headers[2] = {
        { "xi-api-key", s->api_key },
        { "Content-Type", "application/json" },
    };
    http_resp_t resp = http_send(&s->http, "POST", path.buf ? path.buf : "/v1/text-to-speech/",
                                 headers, 2, (const uint8_t *)j.buf, j.len);
    free(path.buf); free(j.buf);
    if (!resp.ok || !http_status_ok(resp.status)) { http_resp_free(&resp); empty_synthesis(out, 0); return 0; }
    memset(out, 0, sizeof *out);
    out->audio_len = resp.body_len; out->audio_pcm16_mono = resp.body; resp.body = NULL;
    out->sample_rate_hz = rate;
    out->duration_ms = pcm_duration_ms(out->audio_len, rate);
    http_resp_free(&resp);
    return 0;
}
ca_speech_synthesizer_t ca_speech_elevenlabs_synthesizer_as_synthesizer(ca_speech_elevenlabs_synthesizer_t *s) {
    ca_speech_synthesizer_t v; v.self = s; v.backend_id = elevenlabs_synth_backend_id;
    v.synthesize = elevenlabs_synth_synthesize; return v;
}

/* ===========================================================================
 * PlayHT synthesizer (/api/v2/tts/stream, output_format=raw)
 * =========================================================================== */

struct ca_speech_playht_synthesizer {
    ca_speech_http_t http; char *base_address, *api_key, *user_id, *default_voice, *model;
    int pcm_sample_rate_hz;
};
ca_speech_playht_synthesizer_t *ca_speech_playht_synthesizer_create(
    const ca_speech_http_t *http, const ca_speech_playht_options_t *o) {
    if (!http || !http->request) return NULL;
    ca_speech_playht_synthesizer_t *s = (ca_speech_playht_synthesizer_t *)calloc(1, sizeof *s);
    if (!s) return NULL;
    s->http = *http;
    s->base_address = sdup_def(o ? o->base_address : NULL, "https://api.play.ht");
    s->api_key = sdup(o ? o->api_key : NULL);
    s->user_id = sdup(o ? o->user_id : NULL);
    s->default_voice = sdup_def(o ? o->default_voice : NULL,
        "s3://voice-cloning-zero-shot/d9ff78ba-d016-47f6-b0ef-dd630f59414e/female-cs/manifest.json");
    s->model = sdup_def(o ? o->model : NULL, "PlayDialog");
    s->pcm_sample_rate_hz = (o && o->pcm_sample_rate_hz > 0) ? o->pcm_sample_rate_hz : 24000;
    return s;
}
void ca_speech_playht_synthesizer_destroy(ca_speech_playht_synthesizer_t *s) {
    if (!s) return; free(s->base_address); free(s->api_key); free(s->user_id);
    free(s->default_voice); free(s->model); free(s);
}
bool ca_speech_playht_synthesizer_is_configured(const ca_speech_playht_synthesizer_t *s) {
    return s && !sblank(s->api_key) && !sblank(s->user_id);
}
static const char *playht_synth_backend_id(void *self) { (void)self; return "playht"; }
static int playht_synth_synthesize(void *self, const char *text, const char *voice,
                                   const char *hint, ca_synthesis_result_t *out) {
    ca_speech_playht_synthesizer_t *s = (ca_speech_playht_synthesizer_t *)self;
    if (!out) return -1;
    if (!ca_speech_playht_synthesizer_is_configured(s)) { empty_synthesis(out, 0); return 0; }
    const char *v = sblank(voice) ? s->default_voice : voice;
    cb_t j = {0};
    char ratebuf[16]; snprintf(ratebuf, sizeof ratebuf, "%d", s->pcm_sample_rate_hz);
    cb_str(&j, "{\"text\":"); cb_json_str(&j, text ? text : "");
    cb_str(&j, ",\"voice\":"); cb_json_str(&j, v);
    cb_str(&j, ",\"voice_engine\":"); cb_json_str(&j, s->model);
    cb_str(&j, ",\"output_format\":\"raw\",\"sample_rate\":"); cb_str(&j, ratebuf);
    cb_str(&j, ",\"language\":"); cb_json_str(&j, sblank(hint) ? "english" : hint);
    cb_str(&j, "}");
    cb_t auth = {0}; cb_str(&auth, "Bearer "); cb_str(&auth, s->api_key);
    ca_speech_http_header_t headers[4] = {
        { "Authorization", auth.buf ? auth.buf : "Bearer " },
        { "X-USER-ID", s->user_id },
        { "Accept", "audio/raw" },
        { "Content-Type", "application/json" },
    };
    http_resp_t resp = http_send(&s->http, "POST", "/api/v2/tts/stream", headers, 4,
                                 (const uint8_t *)j.buf, j.len);
    free(auth.buf); free(j.buf);
    if (!resp.ok || !http_status_ok(resp.status)) { http_resp_free(&resp); empty_synthesis(out, 0); return 0; }
    memset(out, 0, sizeof *out);
    out->audio_len = resp.body_len; out->audio_pcm16_mono = resp.body; resp.body = NULL;
    out->sample_rate_hz = s->pcm_sample_rate_hz;
    out->duration_ms = pcm_duration_ms(out->audio_len, s->pcm_sample_rate_hz);
    http_resp_free(&resp);
    return 0;
}
ca_speech_synthesizer_t ca_speech_playht_synthesizer_as_synthesizer(ca_speech_playht_synthesizer_t *s) {
    ca_speech_synthesizer_t v; v.self = s; v.backend_id = playht_synth_backend_id;
    v.synthesize = playht_synth_synthesize; return v;
}
