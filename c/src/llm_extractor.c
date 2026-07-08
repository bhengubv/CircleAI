/*
 * llm_extractor.c — LLM-backed knowledge-graph extraction (C11 port).
 *
 * Asks an on-device LLM (ca_generate_fn) for strict-JSON triples and parses
 * them defensively with a small tolerant scanner (no JSON library). Ported from
 * CircleAI.Companion.LlmKnowledgeGraphExtractor (C#) and mirroring the verified
 * TypeScript reference 1:1. Pure C11 + libc.
 *
 * Parser contract (matches the C# System.Text.Json behaviour it replaces):
 *   - Slice = raw[firstBracket .. lastBracket]; if no valid slice → empty.
 *   - The slice must parse as a JSON array; ANY syntax error inside it yields an
 *     empty result (the C# JsonDocument.Parse throws and the catch returns []).
 *   - Each array element that is an object contributes a triple: s/p/o read as
 *     strings, c as a number (clamped [0,1], default 0.75 when absent/non-number).
 *   - Non-object elements are skipped; objects with a blank s/p/o are skipped.
 */

#include "circle_ai/llm_extractor.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <stdio.h>
#include <time.h>
#include <math.h>

/* ── small shared helpers (kept file-local to avoid cross-TU coupling) ── */

static char *lx_dup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static int64_t lx_now_ms(void) {
    return (int64_t)time(NULL) * 1000;
}

static bool lx_is_blank(const char *s) {
    if (!s) return true;
    for (; *s; ++s) {
        if (!isspace((unsigned char)*s)) return false;
    }
    return true;
}

static double lx_clamp(double x, double lo, double hi) {
    return x < lo ? lo : (x > hi ? hi : x);
}

static const char CA_LLM_SYSTEM_PROMPT[] =
    "You are a knowledge-graph extractor. Read the conversation turn between USER and ASSISTANT. "
    "Identify entities (people, places, things, concepts) and facts. "
    "Output a single JSON array of triples like [{\"s\":\"Subject\",\"p\":\"predicate\",\"o\":\"object\",\"c\":0.0-1.0}, ...]. "
    "Only output the JSON — no prose, no markdown fences.";

const char *ca_llm_extractor_system_prompt(void) {
    return CA_LLM_SYSTEM_PROMPT;
}

/* ===========================================================================
 * Tolerant JSON scanner (subset sufficient for the extractor)
 * ===========================================================================
 *
 * Recursive-descent over the array slice. On any structural error it sets
 * st->error and unwinds, and the caller returns an empty result. It fully
 * consumes/validates every value it walks (so malformed nested values are
 * detected), while only capturing s/p/o/c out of top-level array objects.
 */

typedef struct {
    const char *p;   /* cursor */
    const char *end; /* one past the last char */
    bool        error;
} lx_scan_t;

typedef struct {
    char  *s, *p, *o;   /* owned; NULL when the key was absent/non-string */
    double c;
    bool   c_present;   /* true only when c parsed as a JSON number */
} lx_obj_t;

static void lx_skip_ws(lx_scan_t *st) {
    while (st->p < st->end && isspace((unsigned char)*st->p)) st->p++;
}

static bool lx_at_end(lx_scan_t *st) { return st->p >= st->end; }

/* Parse a JSON string starting at '"'. On success returns a freshly malloc'd
 * decoded string and advances past the closing quote; on error sets st->error
 * and returns NULL. Handles the standard escapes and \uXXXX (BMP). */
static char *lx_parse_string(lx_scan_t *st) {
    if (lx_at_end(st) || *st->p != '"') { st->error = true; return NULL; }
    st->p++; /* opening quote */
    size_t cap = 16, len = 0;
    char *out = (char *)malloc(cap);
    if (!out) { st->error = true; return NULL; }
    while (!lx_at_end(st)) {
        char ch = *st->p++;
        if (ch == '"') { out[len] = '\0'; return out; }
        if (ch == '\\') {
            if (lx_at_end(st)) break;
            char esc = *st->p++;
            char decoded;
            switch (esc) {
                case '"':  decoded = '"';  break;
                case '\\': decoded = '\\'; break;
                case '/':  decoded = '/';  break;
                case 'b':  decoded = '\b'; break;
                case 'f':  decoded = '\f'; break;
                case 'n':  decoded = '\n'; break;
                case 'r':  decoded = '\r'; break;
                case 't':  decoded = '\t'; break;
                case 'u': {
                    if (st->end - st->p < 4) { free(out); st->error = true; return NULL; }
                    unsigned code = 0;
                    for (int i = 0; i < 4; ++i) {
                        char h = *st->p++;
                        code <<= 4;
                        if      (h >= '0' && h <= '9') code |= (unsigned)(h - '0');
                        else if (h >= 'a' && h <= 'f') code |= (unsigned)(h - 'a' + 10);
                        else if (h >= 'A' && h <= 'F') code |= (unsigned)(h - 'A' + 10);
                        else { free(out); st->error = true; return NULL; }
                    }
                    /* Minimal UTF-8 encode of the BMP code point. */
                    char utf[4]; int n;
                    if (code < 0x80) { utf[0] = (char)code; n = 1; }
                    else if (code < 0x800) {
                        utf[0] = (char)(0xC0 | (code >> 6));
                        utf[1] = (char)(0x80 | (code & 0x3F));
                        n = 2;
                    } else {
                        utf[0] = (char)(0xE0 | (code >> 12));
                        utf[1] = (char)(0x80 | ((code >> 6) & 0x3F));
                        utf[2] = (char)(0x80 | (code & 0x3F));
                        n = 3;
                    }
                    if (len + (size_t)n + 1 > cap) {
                        while (len + (size_t)n + 1 > cap) cap *= 2;
                        char *nb = (char *)realloc(out, cap);
                        if (!nb) { free(out); st->error = true; return NULL; }
                        out = nb;
                    }
                    memcpy(out + len, utf, (size_t)n);
                    len += (size_t)n;
                    continue;
                }
                default: free(out); st->error = true; return NULL;
            }
            if (len + 2 > cap) {
                cap *= 2;
                char *nb = (char *)realloc(out, cap);
                if (!nb) { free(out); st->error = true; return NULL; }
                out = nb;
            }
            out[len++] = decoded;
        } else {
            if (len + 2 > cap) {
                cap *= 2;
                char *nb = (char *)realloc(out, cap);
                if (!nb) { free(out); st->error = true; return NULL; }
                out = nb;
            }
            out[len++] = ch;
        }
    }
    free(out);
    st->error = true; /* unterminated string */
    return NULL;
}

/* Parse (and validate) a JSON number, returning its value. Sets st->error on a
 * malformed number. */
static double lx_parse_number(lx_scan_t *st, double *out) {
    const char *start = st->p;
    if (!lx_at_end(st) && *st->p == '-') st->p++;
    bool any_digit = false;
    while (!lx_at_end(st) && isdigit((unsigned char)*st->p)) { st->p++; any_digit = true; }
    if (!lx_at_end(st) && *st->p == '.') {
        st->p++;
        while (!lx_at_end(st) && isdigit((unsigned char)*st->p)) { st->p++; any_digit = true; }
    }
    if (!lx_at_end(st) && (*st->p == 'e' || *st->p == 'E')) {
        st->p++;
        if (!lx_at_end(st) && (*st->p == '+' || *st->p == '-')) st->p++;
        bool exp_digit = false;
        while (!lx_at_end(st) && isdigit((unsigned char)*st->p)) { st->p++; exp_digit = true; }
        if (!exp_digit) { st->error = true; return 0; }
    }
    if (!any_digit) { st->error = true; return 0; }
    size_t n = (size_t)(st->p - start);
    char tmp[64];
    if (n >= sizeof(tmp)) { st->error = true; return 0; }
    memcpy(tmp, start, n);
    tmp[n] = '\0';
    *out = strtod(tmp, NULL);
    return *out;
}

/* Forward decl: parse & fully consume any JSON value (used to walk/validate
 * elements we don't capture). */
static void lx_skip_value(lx_scan_t *st);

static void lx_skip_array(lx_scan_t *st) {
    st->p++; /* '[' */
    lx_skip_ws(st);
    if (!lx_at_end(st) && *st->p == ']') { st->p++; return; }
    for (;;) {
        lx_skip_value(st);
        if (st->error) return;
        lx_skip_ws(st);
        if (lx_at_end(st)) { st->error = true; return; }
        if (*st->p == ',') { st->p++; lx_skip_ws(st); continue; }
        if (*st->p == ']') { st->p++; return; }
        st->error = true; return;
    }
}

static void lx_skip_object(lx_scan_t *st) {
    st->p++; /* '{' */
    lx_skip_ws(st);
    if (!lx_at_end(st) && *st->p == '}') { st->p++; return; }
    for (;;) {
        lx_skip_ws(st);
        char *key = lx_parse_string(st);
        if (st->error) { free(key); return; }
        free(key);
        lx_skip_ws(st);
        if (lx_at_end(st) || *st->p != ':') { st->error = true; return; }
        st->p++;
        lx_skip_value(st);
        if (st->error) return;
        lx_skip_ws(st);
        if (lx_at_end(st)) { st->error = true; return; }
        if (*st->p == ',') { st->p++; continue; }
        if (*st->p == '}') { st->p++; return; }
        st->error = true; return;
    }
}

/* Match a bare literal (true/false/null). */
static void lx_match_literal(lx_scan_t *st, const char *lit) {
    size_t n = strlen(lit);
    if ((size_t)(st->end - st->p) < n || strncmp(st->p, lit, n) != 0) {
        st->error = true; return;
    }
    st->p += n;
}

static void lx_skip_value(lx_scan_t *st) {
    lx_skip_ws(st);
    if (lx_at_end(st)) { st->error = true; return; }
    char ch = *st->p;
    if (ch == '"') { char *s = lx_parse_string(st); free(s); return; }
    if (ch == '{') { lx_skip_object(st); return; }
    if (ch == '[') { lx_skip_array(st); return; }
    if (ch == 't') { lx_match_literal(st, "true"); return; }
    if (ch == 'f') { lx_match_literal(st, "false"); return; }
    if (ch == 'n') { lx_match_literal(st, "null"); return; }
    if (ch == '-' || isdigit((unsigned char)ch)) { double d; lx_parse_number(st, &d); return; }
    st->error = true;
}

/* Parse one top-level object, capturing s/p/o/c. Fully validates the object. */
static void lx_capture_object(lx_scan_t *st, lx_obj_t *obj) {
    memset(obj, 0, sizeof(*obj));
    obj->c = CA_LLM_EXTRACTOR_DEFAULT_CONFIDENCE;
    st->p++; /* '{' */
    lx_skip_ws(st);
    if (!lx_at_end(st) && *st->p == '}') { st->p++; return; }
    for (;;) {
        lx_skip_ws(st);
        char *key = lx_parse_string(st);
        if (st->error) { free(key); return; }
        lx_skip_ws(st);
        if (lx_at_end(st) || *st->p != ':') { free(key); st->error = true; return; }
        st->p++;
        lx_skip_ws(st);
        bool is_s = key && strcmp(key, "s") == 0;
        bool is_p = key && strcmp(key, "p") == 0;
        bool is_o = key && strcmp(key, "o") == 0;
        bool is_c = key && strcmp(key, "c") == 0;
        free(key);

        if (!lx_at_end(st) && *st->p == '"') {
            char *val = lx_parse_string(st);
            if (st->error) { free(val); return; }
            if      (is_s) { free(obj->s); obj->s = val; }
            else if (is_p) { free(obj->p); obj->p = val; }
            else if (is_o) { free(obj->o); obj->o = val; }
            else           { free(val); }
        } else if (!lx_at_end(st) && (*st->p == '-' || isdigit((unsigned char)*st->p))) {
            double d;
            lx_parse_number(st, &d);
            if (st->error) return;
            if (is_c) { obj->c = lx_clamp(d, 0.0, 1.0); obj->c_present = true; }
        } else {
            /* Non-string, non-number value (object/array/true/false/null): walk
             * it (so 'c':"high" etc. leaves c at its default), validating. */
            lx_skip_value(st);
            if (st->error) return;
            /* is_c with a non-number value → keep default (already set). */
            (void)is_c;
        }

        lx_skip_ws(st);
        if (lx_at_end(st)) { st->error = true; return; }
        if (*st->p == ',') { st->p++; continue; }
        if (*st->p == '}') { st->p++; return; }
        st->error = true; return;
    }
}

static void lx_obj_free(lx_obj_t *o) {
    free(o->s); free(o->p); free(o->o);
    o->s = o->p = o->o = NULL;
}

ca_knowledge_triple_t *ca_llm_extractor_parse_triples(const char *raw,
                                                      const char *source_episode_id,
                                                      size_t *out_count) {
    if (out_count) *out_count = 0;
    if (lx_is_blank(raw)) return NULL;

    const char *first = strchr(raw, '[');
    const char *last = strrchr(raw, ']');
    if (!first || !last || last <= first) return NULL;

    lx_scan_t st;
    st.p = first;
    st.end = last + 1;
    st.error = false;

    lx_skip_ws(&st);
    if (lx_at_end(&st) || *st.p != '[') return NULL;
    st.p++; /* consume '[' */

    lx_obj_t *objs = NULL;
    size_t obj_count = 0, obj_cap = 0;

    lx_skip_ws(&st);
    if (!lx_at_end(&st) && *st.p == ']') {
        st.p++;
        /* empty array → empty result */
        free(objs);
        return NULL;
    }

    for (;;) {
        lx_skip_ws(&st);
        if (lx_at_end(&st)) { st.error = true; break; }
        if (*st.p == '{') {
            lx_obj_t obj;
            lx_capture_object(&st, &obj);
            if (st.error) { lx_obj_free(&obj); break; }
            if (obj_count == obj_cap) {
                size_t nc = obj_cap ? obj_cap * 2 : 8;
                lx_obj_t *n = (lx_obj_t *)realloc(objs, nc * sizeof(*n));
                if (!n) { lx_obj_free(&obj); st.error = true; break; }
                objs = n; obj_cap = nc;
            }
            objs[obj_count++] = obj;
        } else {
            /* Non-object element (number/string/array/literal): skip it. */
            lx_skip_value(&st);
            if (st.error) break;
        }
        lx_skip_ws(&st);
        if (lx_at_end(&st)) { st.error = true; break; }
        if (*st.p == ',') { st.p++; continue; }
        if (*st.p == ']') { st.p++; break; }
        st.error = true; break;
    }

    if (st.error) {
        for (size_t i = 0; i < obj_count; ++i) lx_obj_free(&objs[i]);
        free(objs);
        return NULL; /* malformed → empty */
    }

    /* Build triples from captured objects, skipping blank s/p/o. */
    ca_knowledge_triple_t *triples = NULL;
    size_t tn = 0;
    if (obj_count > 0) {
        triples = (ca_knowledge_triple_t *)calloc(obj_count, sizeof(*triples));
        if (!triples) {
            for (size_t i = 0; i < obj_count; ++i) lx_obj_free(&objs[i]);
            free(objs);
            return NULL;
        }
    }
    int64_t now = lx_now_ms();
    for (size_t i = 0; i < obj_count; ++i) {
        lx_obj_t *o = &objs[i];
        if (lx_is_blank(o->s) || lx_is_blank(o->p) || lx_is_blank(o->o)) {
            lx_obj_free(o);
            continue;
        }
        ca_knowledge_triple_t *t = &triples[tn++];
        t->subject = o->s;   o->s = NULL;   /* transfer ownership */
        t->predicate = o->p; o->p = NULL;
        t->object = o->o;    o->o = NULL;
        t->source = lx_dup(source_episode_id);
        t->confidence = o->c;
        t->recorded_at_ms = now;
        lx_obj_free(o); /* frees anything not transferred (none) */
    }
    free(objs);

    if (tn == 0) {
        free(triples);
        return NULL;
    }
    if (out_count) *out_count = tn;
    return triples;
}

ca_knowledge_triple_t *ca_llm_extract_from_turn(ca_generate_fn generator,
                                                void *generator_user,
                                                const char *user_text,
                                                const char *assistant_text,
                                                const char *source_episode_id,
                                                size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!generator) return NULL;
    if (lx_is_blank(user_text) && lx_is_blank(assistant_text)) return NULL;

    const char *u = user_text ? user_text : "";
    const char *a = assistant_text ? assistant_text : "";
    /* "USER:\n<u>\nASSISTANT:\n<a>\n" */
    size_t len = strlen("USER:\n") + strlen(u) + 1 /* \n */
               + strlen("ASSISTANT:\n") + strlen(a) + 1 /* \n */ + 1 /* NUL */;
    char *user_msg = (char *)malloc(len);
    if (!user_msg) return NULL;
    snprintf(user_msg, len, "USER:\n%s\nASSISTANT:\n%s\n", u, a);

    ca_chat_message_t msgs[2];
    msgs[0].role = CA_ROLE_SYSTEM;
    msgs[0].content = CA_LLM_SYSTEM_PROMPT;
    msgs[0].created_at = 0;
    msgs[1].role = CA_ROLE_USER;
    msgs[1].content = user_msg;
    msgs[1].created_at = 0;

    char *reply = generator(generator_user, msgs, 2);
    free(user_msg);

    if (!reply) return NULL; /* generator failure → degrade to empty */

    ca_knowledge_triple_t *triples =
        ca_llm_extractor_parse_triples(reply, source_episode_id, out_count);
    free(reply);
    return triples;
}
