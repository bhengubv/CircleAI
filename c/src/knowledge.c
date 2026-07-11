/*
 * knowledge.c — CircleAI.Knowledge (C11 port).
 *
 * KnowledgeNote + flat YAML frontmatter (Write/Read faithful to the C# quoting/
 * escaping/validation), an in-memory IKnowledgeStore, and the markdown episodic
 * store that maps ca_episodic_entry_t to notes. Timestamps round-trip as Unix-ms
 * decimal strings (the C tree carries time as Unix ms). Embeddings round-trip as
 * base64 of the raw float bytes (ca_base64_encode / _decode). Pure C11 + libc
 * (+ libm). No pthreads.
 */

#include "circle_ai/knowledge.h"
#include "circle_ai/compression.h"  /* ca_base64_encode / ca_base64_decode */
#include "circle_ai/security.h"     /* ca_uuid_v4, CA_UUID_STR_LEN */
#include "board_common.h"
#include <stdio.h>
#include <inttypes.h>

/* ── growable buffer ────────────────────────────────────────────────────── */

typedef struct { char *buf; size_t len, cap; } kbuf_t;
static bool kbuf_reserve(kbuf_t *b, size_t extra) {
    if (b->len + extra + 1 > b->cap) {
        size_t nc = b->cap ? b->cap : 64;
        while (nc < b->len + extra + 1) nc *= 2;
        char *nb = (char *)realloc(b->buf, nc);
        if (!nb) return false;
        b->buf = nb; b->cap = nc;
    }
    return true;
}
static bool kbuf_puts(kbuf_t *b, const char *s) {
    size_t n = strlen(s);
    if (!kbuf_reserve(b, n)) return false;
    memcpy(b->buf + b->len, s, n); b->len += n; b->buf[b->len] = '\0';
    return true;
}
static bool kbuf_putc(kbuf_t *b, char c) {
    if (!kbuf_reserve(b, 1)) return false;
    b->buf[b->len++] = c; b->buf[b->len] = '\0';
    return true;
}

/* ── kv helpers ─────────────────────────────────────────────────────────── */

static void kkv_free(ca_knowledge_kv_t *kv, size_t n) {
    if (!kv) return;
    for (size_t i = 0; i < n; ++i) { free(kv[i].key); free(kv[i].value); }
    free(kv);
}
static bool kkv_copy(ca_knowledge_kv_t **out, const ca_knowledge_kv_t *src, size_t n) {
    *out = NULL;
    if (n == 0) return true;
    ca_knowledge_kv_t *v = (ca_knowledge_kv_t *)calloc(n, sizeof(*v));
    if (!v) return false;
    for (size_t i = 0; i < n; ++i) {
        v[i].key   = cab_strdup_empty(src ? src[i].key : NULL);
        v[i].value = cab_strdup_empty(src ? src[i].value : NULL);
        if (!v[i].key || !v[i].value) { kkv_free(v, i + 1); return false; }
    }
    *out = v;
    return true;
}
/* Append (key, value) to a growable kv vector. */
static bool kkv_push(ca_knowledge_kv_t **v, size_t *n, size_t *cap,
                     const char *key, const char *value) {
    if (*n == *cap) {
        size_t nc = *cap ? *cap * 2 : 4;
        void *nv = realloc(*v, nc * sizeof(ca_knowledge_kv_t));
        if (!nv) return false;
        *v = (ca_knowledge_kv_t *)nv; *cap = nc;
    }
    (*v)[*n].key = cab_strdup_empty(key);
    (*v)[*n].value = cab_strdup_empty(value);
    if (!(*v)[*n].key || !(*v)[*n].value) return false;
    (*n)++;
    return true;
}

/* ── KnowledgeNote record ───────────────────────────────────────────────── */

void ca_knowledge_note_free(ca_knowledge_note_t *n) {
    if (!n) return;
    free(n->id);
    free(n->title);
    free(n->body_markdown);
    kkv_free(n->frontmatter, n->frontmatter_count);
    cab_strv_free(n->tags, n->tag_count);
    memset(n, 0, sizeof(*n));
}
void ca_knowledge_note_free_array(ca_knowledge_note_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_knowledge_note_free(&arr[i]);
    free(arr);
}
bool ca_knowledge_note_copy(ca_knowledge_note_t *dst, const ca_knowledge_note_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->created_at_ms = src->created_at_ms;
    dst->updated_at_ms = src->updated_at_ms;
    dst->id = cab_strdup_empty(src->id);
    dst->title = cab_strdup_empty(src->title);
    dst->body_markdown = cab_strdup_empty(src->body_markdown);
    if (!dst->id || !dst->title || !dst->body_markdown) { ca_knowledge_note_free(dst); return false; }
    if (!kkv_copy(&dst->frontmatter, src->frontmatter, src->frontmatter_count)) {
        ca_knowledge_note_free(dst); return false;
    }
    dst->frontmatter_count = src->frontmatter_count;
    if (!cab_strv_copy(&dst->tags, src->tags, src->tag_count)) {
        ca_knowledge_note_free(dst); return false;
    }
    dst->tag_count = src->tag_count;
    return true;
}

/* ── YAML frontmatter ───────────────────────────────────────────────────── */

static bool yaml_key_valid(const char *key) {
    if (cab_is_ws(key)) return false;
    for (const char *p = key; *p; ++p)
        if (!(isalnum((unsigned char)*p) || *p == '_' || *p == '-' || *p == '.'))
            return false;
    return true;
}

/* EncodeValue: quote when reserved chars / leading-trailing space present. */
static bool yaml_encode_value(kbuf_t *b, const char *value) {
    if (!value || value[0] == '\0') return kbuf_puts(b, value ? "\"\"" : "");
    bool needs = false;
    for (const char *p = value; *p; ++p) {
        char c = *p;
        if (c == ':' || c == '#' || c == '\n' || c == '\r' || c == '\t' ||
            c == '"' || c == '\\' || c == '\'' || c == '{' || c == '[') { needs = true; break; }
    }
    size_t vl = strlen(value);
    if (!needs && (value[0] == ' ' || value[vl - 1] == ' ')) needs = true;
    if (!needs) return kbuf_puts(b, value);
    if (!kbuf_putc(b, '"')) return false;
    for (const char *p = value; *p; ++p) {
        switch (*p) {
            case '\\': if (!kbuf_puts(b, "\\\\")) return false; break;
            case '"':  if (!kbuf_puts(b, "\\\"")) return false; break;
            case '\n': if (!kbuf_puts(b, "\\n")) return false; break;
            case '\r': if (!kbuf_puts(b, "\\r")) return false; break;
            case '\t': if (!kbuf_puts(b, "\\t")) return false; break;
            default:   if (!kbuf_putc(b, *p)) return false; break;
        }
    }
    return kbuf_putc(b, '"');
}

/* DecodeValue: strip a trailing " #comment" from bare values; decode a double-
 * quoted literal. Returns a fresh string, or NULL on malformed input. */
static char *yaml_decode_value(const char *raw) {
    if (raw[0] == '\0') return cab_strdup_empty("");
    if (raw[0] != '"' && raw[0] != '\'') {
        const char *hash = strstr(raw, " #");
        size_t len = hash ? (size_t)(hash - raw) : strlen(raw);
        while (len > 0 && (raw[len - 1] == ' ' || raw[len - 1] == '\t')) len--;
        char *out = (char *)malloc(len + 1);
        if (!out) return NULL;
        memcpy(out, raw, len); out[len] = '\0';
        return out;
    }
    if (raw[0] == '\'') return NULL; /* single-quoted rejected */
    size_t rl = strlen(raw);
    if (rl < 2 || raw[rl - 1] != '"') return NULL; /* unterminated */
    kbuf_t b = {0};
    for (size_t i = 1; i < rl - 1; ++i) {
        char c = raw[i];
        if (c != '\\') { if (!kbuf_putc(&b, c)) { free(b.buf); return NULL; } continue; }
        if (i + 1 >= rl - 1) { free(b.buf); return NULL; } /* trailing backslash */
        char next = raw[++i];
        char dec;
        switch (next) {
            case '\\': dec = '\\'; break;
            case '"':  dec = '"';  break;
            case 'n':  dec = '\n'; break;
            case 'r':  dec = '\r'; break;
            case 't':  dec = '\t'; break;
            default: free(b.buf); return NULL; /* unsupported escape */
        }
        if (!kbuf_putc(&b, dec)) { free(b.buf); return NULL; }
    }
    return b.buf ? b.buf : cab_strdup_empty("");
}

/* Write(frontmatter, body) -> "---\nk: v\n...---\nbody". NULL on OOM / bad key. */
static char *yaml_write(const ca_knowledge_kv_t *fm, size_t fm_n, const char *body) {
    kbuf_t b = {0};
    if (!kbuf_puts(&b, "---\n")) { free(b.buf); return NULL; }
    for (size_t i = 0; i < fm_n; ++i) {
        if (!yaml_key_valid(fm[i].key)) { free(b.buf); return NULL; }
        if (!kbuf_puts(&b, fm[i].key) || !kbuf_puts(&b, ": ")) { free(b.buf); return NULL; }
        if (!yaml_encode_value(&b, fm[i].value)) { free(b.buf); return NULL; }
        if (!kbuf_putc(&b, '\n')) { free(b.buf); return NULL; }
    }
    if (!kbuf_puts(&b, "---\n") || !kbuf_puts(&b, body)) { free(b.buf); return NULL; }
    return b.buf ? b.buf : cab_strdup_empty("");
}

/* Read(text) -> frontmatter kv + body. Returns false on malformed input. Sets
 * the owned frontmatter array + count and the owned body string. */
static bool yaml_read(const char *text_in, ca_knowledge_kv_t **fm_out, size_t *fm_n_out,
                      char **body_out) {
    *fm_out = NULL; *fm_n_out = 0; *body_out = NULL;
    /* normalise CRLF/CR -> LF */
    size_t tl = strlen(text_in);
    char *text = (char *)malloc(tl + 1);
    if (!text) return false;
    size_t k = 0;
    for (size_t i = 0; i < tl; ++i) {
        if (text_in[i] == '\r') {
            if (text_in[i + 1] == '\n') continue; /* CRLF -> single LF */
            text[k++] = '\n';
        } else text[k++] = text_in[i];
    }
    text[k] = '\0';

    if (strncmp(text, "---\n", 4) != 0) { free(text); return false; }
    size_t search_start = 4;
    /* find "\n---\n" */
    const char *close = strstr(text + search_start, "\n---\n");
    if (!close) { free(text); return false; }
    size_t yaml_len = (size_t)(close - (text + search_start));
    const char *body = close + 5;

    ca_knowledge_kv_t *fm = NULL; size_t fm_n = 0, fm_cap = 0;
    bool ok = true;
    /* iterate yaml lines */
    const char *p = text + search_start;
    const char *yaml_end = text + search_start + yaml_len;
    while (p < yaml_end && ok) {
        const char *nl = memchr(p, '\n', (size_t)(yaml_end - p));
        size_t linelen = nl ? (size_t)(nl - p) : (size_t)(yaml_end - p);
        /* copy line */
        char line[1024];
        size_t ll = linelen < sizeof(line) - 1 ? linelen : sizeof(line) - 1;
        memcpy(line, p, ll); line[ll] = '\0';
        p = nl ? nl + 1 : yaml_end;

        if (cab_is_ws(line)) continue;
        if (line[0] == ' ' || line[0] == '\t') { ok = false; break; } /* nesting */
        if (line[0] == '-' && line[1] == ' ') { ok = false; break; }  /* list */
        char *colon = strchr(line, ':');
        if (!colon || colon == line) { ok = false; break; }
        *colon = '\0';
        /* trim key */
        char *key = line;
        while (*key == ' ' || *key == '\t') key++;
        char *ke = colon; while (ke > key && (ke[-1] == ' ' || ke[-1] == '\t')) ke--;
        *ke = '\0';
        char *rest = colon + 1;
        while (*rest == ' ' || *rest == '\t') rest++;
        if (!yaml_key_valid(key)) { ok = false; break; }
        if (rest[0] == '{' || rest[0] == '[') { ok = false; break; } /* flow style */
        char *val = yaml_decode_value(rest);
        if (!val) { ok = false; break; }
        ok = kkv_push(&fm, &fm_n, &fm_cap, key, val);
        free(val);
    }

    if (!ok) { kkv_free(fm, fm_n); free(text); return false; }
    char *body_copy = cab_strdup_empty(body);
    free(text);
    if (!body_copy) { kkv_free(fm, fm_n); return false; }
    *fm_out = fm; *fm_n_out = fm_n; *body_out = body_copy;
    return true;
}

/* ── ToFileText / ParseFile ─────────────────────────────────────────────── */

char *ca_knowledge_note_to_file_text(const ca_knowledge_note_t *note) {
    if (!note) return NULL;
    /* merge user frontmatter, then well-known keys (which win). */
    ca_knowledge_kv_t *merged = NULL; size_t mn = 0, mcap = 0;
    bool ok = true;
    for (size_t i = 0; ok && i < note->frontmatter_count; ++i)
        ok = kkv_push(&merged, &mn, &mcap, note->frontmatter[i].key,
                      note->frontmatter[i].value);
    /* upsert helper for well-known keys */
    #define UPSERT(k, v) do { \
        bool found = false; \
        for (size_t _i = 0; _i < mn; ++_i) if (cab_ord_eq(merged[_i].key, (k))) { \
            char *nv = cab_strdup_empty(v); if (!nv) { ok = false; break; } \
            free(merged[_i].value); merged[_i].value = nv; found = true; break; } \
        if (ok && !found) ok = kkv_push(&merged, &mn, &mcap, (k), (v)); \
    } while (0)

    char tags_joined[1] = ""; (void)tags_joined;
    char created[32], updated[32];
    snprintf(created, sizeof(created), "%" PRId64, note->created_at_ms);
    snprintf(updated, sizeof(updated), "%" PRId64, note->updated_at_ms);
    /* tags joined by ',' */
    kbuf_t tj = {0};
    for (size_t i = 0; ok && i < note->tag_count; ++i) {
        if (i > 0) ok = kbuf_putc(&tj, ',');
        ok = ok && kbuf_puts(&tj, note->tags[i]);
    }
    const char *tags_str = tj.buf ? tj.buf : "";

    if (ok) UPSERT("id", note->id);
    if (ok) UPSERT("title", note->title);
    if (ok) UPSERT("created_at", created);
    if (ok) UPSERT("updated_at", updated);
    if (ok) UPSERT("tags", tags_str);
    #undef UPSERT

    char *out = NULL;
    if (ok) out = yaml_write(merged, mn, note->body_markdown);
    kkv_free(merged, mn);
    free(tj.buf);
    return out;
}

bool ca_knowledge_note_parse_file(const char *text, ca_knowledge_note_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out || !text) return false;
    ca_knowledge_kv_t *fm = NULL; size_t fm_n = 0;
    char *body = NULL;
    if (!yaml_read(text, &fm, &fm_n, &body)) return false;

    /* well-known keys */
    const char *id = NULL, *title = "", *created = NULL, *updated = NULL, *tags = NULL;
    for (size_t i = 0; i < fm_n; ++i) {
        if (cab_ord_eq(fm[i].key, "id")) id = fm[i].value;
        else if (cab_ord_eq(fm[i].key, "title")) title = fm[i].value;
        else if (cab_ord_eq(fm[i].key, "created_at")) created = fm[i].value;
        else if (cab_ord_eq(fm[i].key, "updated_at")) updated = fm[i].value;
        else if (cab_ord_eq(fm[i].key, "tags")) tags = fm[i].value;
    }
    if (!id || cab_is_ws(id)) { kkv_free(fm, fm_n); free(body); return false; }

    out->id = cab_strdup_empty(id);
    out->title = cab_strdup_empty(title ? title : "");
    out->body_markdown = body; /* transfer */
    body = NULL;
    out->created_at_ms = created ? strtoll(created, NULL, 10) : 0;
    out->updated_at_ms = updated ? strtoll(updated, NULL, 10) : 0;
    if (!out->id || !out->title) { kkv_free(fm, fm_n); ca_knowledge_note_free(out); return false; }

    /* tags split on ',' (trim, drop empties) */
    if (tags && !cab_is_ws(tags)) {
        char **tv = NULL; size_t tn = 0, tcap = 0;
        const char *p = tags;
        while (*p) {
            while (*p == ',') p++;
            const char *s = p;
            while (*p && *p != ',') p++;
            const char *e = p;
            while (s < e && (*s == ' ' || *s == '\t')) s++;
            while (e > s && (e[-1] == ' ' || e[-1] == '\t')) e--;
            if (e > s) {
                if (tn == tcap) { size_t nc = tcap ? tcap * 2 : 4; char **nt = realloc(tv, nc * sizeof(char*)); if (!nt) { cab_strv_free(tv, tn); kkv_free(fm, fm_n); ca_knowledge_note_free(out); return false; } tv = nt; tcap = nc; }
                size_t len = (size_t)(e - s);
                char *t = (char *)malloc(len + 1);
                if (!t) { cab_strv_free(tv, tn); kkv_free(fm, fm_n); ca_knowledge_note_free(out); return false; }
                memcpy(t, s, len); t[len] = '\0';
                tv[tn++] = t;
            }
        }
        out->tags = tv; out->tag_count = tn;
    }

    /* user frontmatter = all keys except the well-known ones */
    ca_knowledge_kv_t *uf = NULL; size_t un = 0, ucap = 0;
    bool ok = true;
    for (size_t i = 0; ok && i < fm_n; ++i) {
        const char *kk = fm[i].key;
        if (cab_ord_eq(kk, "id") || cab_ord_eq(kk, "title") ||
            cab_ord_eq(kk, "created_at") || cab_ord_eq(kk, "updated_at") ||
            cab_ord_eq(kk, "tags")) continue;
        ok = kkv_push(&uf, &un, &ucap, kk, fm[i].value);
    }
    kkv_free(fm, fm_n);
    if (!ok) { kkv_free(uf, un); ca_knowledge_note_free(out); return false; }
    out->frontmatter = uf; out->frontmatter_count = un;
    return true;
}

/* ── InMemoryKnowledgeStore ─────────────────────────────────────────────── */

struct ca_knowledge_store {
    ca_knowledge_note_t *items; size_t count, cap;
};

ca_knowledge_store_t *ca_knowledge_store_create(void) {
    return (ca_knowledge_store_t *)calloc(1, sizeof(ca_knowledge_store_t));
}
void ca_knowledge_store_destroy(ca_knowledge_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_knowledge_note_free(&s->items[i]);
    free(s->items);
    free(s);
}

bool ca_knowledge_store_get(const ca_knowledge_store_t *s, const char *id,
                            ca_knowledge_note_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || cab_is_ws(id) || !out) return false;
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->items[i].id, id))
            return ca_knowledge_note_copy(out, &s->items[i]);
    return false;
}

int ca_knowledge_store_save(ca_knowledge_store_t *s, const ca_knowledge_note_t *note,
                            int64_t now_ms, ca_knowledge_note_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || !note || cab_is_ws(note->id)) return -1;
    ca_knowledge_note_t refreshed;
    if (!ca_knowledge_note_copy(&refreshed, note)) return -1;
    refreshed.updated_at_ms = now_ms;

    for (size_t i = 0; i < s->count; ++i) {
        if (cab_ord_eq(s->items[i].id, note->id)) {
            ca_knowledge_note_free(&s->items[i]);
            if (!ca_knowledge_note_copy(&s->items[i], &refreshed)) { ca_knowledge_note_free(&refreshed); return -1; }
            if (out && !ca_knowledge_note_copy(out, &refreshed)) { ca_knowledge_note_free(&refreshed); return -1; }
            ca_knowledge_note_free(&refreshed);
            return 0;
        }
    }
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->items, nc * sizeof(*s->items));
        if (!n) { ca_knowledge_note_free(&refreshed); return -1; }
        s->items = (ca_knowledge_note_t *)n; s->cap = nc;
    }
    if (!ca_knowledge_note_copy(&s->items[s->count], &refreshed)) { ca_knowledge_note_free(&refreshed); return -1; }
    s->count++;
    if (out && !ca_knowledge_note_copy(out, &refreshed)) { ca_knowledge_note_free(&refreshed); return -1; }
    ca_knowledge_note_free(&refreshed);
    return 0;
}

int ca_knowledge_store_delete(ca_knowledge_store_t *s, const char *id) {
    if (!s || cab_is_ws(id)) return -1;
    for (size_t i = 0; i < s->count; ++i) {
        if (cab_ord_eq(s->items[i].id, id)) {
            ca_knowledge_note_free(&s->items[i]);
            for (size_t j = i; j + 1 < s->count; ++j) s->items[j] = s->items[j + 1];
            s->count--;
            return 0;
        }
    }
    return 0; /* no-op when absent */
}

ca_knowledge_note_t *ca_knowledge_store_search_by_tag(const ca_knowledge_store_t *s,
                                                      const char *tag,
                                                      size_t *out_count) {
    if (!out_count) return NULL;
    if (!s || cab_is_ws(tag)) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < s->count; ++i)
        if (cab_strv_ci_contains(s->items[i].tags, s->items[i].tag_count, tag)) n++;
    if (n == 0) { *out_count = 0; return NULL; }
    ca_knowledge_note_t *out = (ca_knowledge_note_t *)calloc(n, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    size_t k = 0;
    for (size_t i = 0; i < s->count; ++i) {
        if (!cab_strv_ci_contains(s->items[i].tags, s->items[i].tag_count, tag)) continue;
        if (!ca_knowledge_note_copy(&out[k], &s->items[i])) {
            ca_knowledge_note_free_array(out, k);
            *out_count = (size_t)-1; return NULL;
        }
        k++;
    }
    *out_count = n;
    return out;
}

ca_knowledge_note_t *ca_knowledge_store_enumerate_all(const ca_knowledge_store_t *s,
                                                      size_t *out_count) {
    if (!out_count) return NULL;
    if (!s) { *out_count = (size_t)-1; return NULL; }
    if (s->count == 0) { *out_count = 0; return NULL; }
    ca_knowledge_note_t *out = (ca_knowledge_note_t *)calloc(s->count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < s->count; ++i) {
        if (!ca_knowledge_note_copy(&out[i], &s->items[i])) {
            ca_knowledge_note_free_array(out, i);
            *out_count = (size_t)-1; return NULL;
        }
    }
    *out_count = s->count;
    return out;
}

/* ── MarkdownEpisodicMemoryStore ────────────────────────────────────────── */

struct ca_markdown_episodic_store {
    ca_knowledge_store_t *store; /* borrowed */
};

ca_markdown_episodic_store_t *ca_markdown_episodic_store_create(ca_knowledge_store_t *store) {
    if (!store) return NULL;
    ca_markdown_episodic_store_t *s = (ca_markdown_episodic_store_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->store = store;
    return s;
}
void ca_markdown_episodic_store_destroy(ca_markdown_episodic_store_t *s) { free(s); }

/* Truncate the first line of user text to <=64 chars for the note title. */
static char *title_from_user(const char *user) {
    if (cab_is_ws(user)) return cab_strdup_empty("(untitled)");
    /* single-line: replace newlines with spaces, trim */
    size_t n = strlen(user);
    char *tmp = (char *)malloc(n + 1);
    if (!tmp) return NULL;
    for (size_t i = 0; i < n; ++i) tmp[i] = (user[i] == '\n' || user[i] == '\r') ? ' ' : user[i];
    tmp[n] = '\0';
    char *s = tmp;
    while (*s == ' ') s++;
    size_t sl = strlen(s);
    while (sl > 0 && s[sl - 1] == ' ') sl--;
    size_t take = sl <= 64 ? sl : 64;
    char *out = (char *)malloc(take + 1);
    if (!out) { free(tmp); return NULL; }
    memcpy(out, s, take); out[take] = '\0';
    free(tmp);
    return out;
}

/* Map a ca_episodic_entry_t -> ca_knowledge_note_t (ToNote). */
static bool entry_to_note(const ca_episodic_entry_t *entry, int64_t now_ms,
                          ca_knowledge_note_t *note) {
    memset(note, 0, sizeof(*note));
    /* id */
    const char *eid = (entry->id && entry->id[0]) ? entry->id : NULL;
    if (eid) note->id = cab_strdup_empty(eid);
    else { char uuid[CA_UUID_STR_LEN]; ca_uuid_v4(uuid); note->id = cab_strdup_empty(uuid); }
    if (!note->id) return false;

    int64_t recorded = entry->recorded_at_ms ? entry->recorded_at_ms : now_ms;
    note->created_at_ms = recorded;
    note->updated_at_ms = recorded;

    note->title = title_from_user(entry->user_text ? entry->user_text : "");
    if (!note->title) { ca_knowledge_note_free(note); return false; }

    /* body */
    kbuf_t body = {0};
    bool ok = kbuf_puts(&body, "## User\n\n") &&
              kbuf_puts(&body, entry->user_text ? entry->user_text : "") &&
              kbuf_puts(&body, "\n\n## Assistant\n\n") &&
              kbuf_puts(&body, entry->assistant_text ? entry->assistant_text : "");
    if (!ok) { free(body.buf); ca_knowledge_note_free(note); return false; }
    note->body_markdown = body.buf ? body.buf : cab_strdup_empty("");
    if (!note->body_markdown) { ca_knowledge_note_free(note); return false; }

    /* frontmatter: episode_id, recorded_at, app_context?, embedding(+dims)?,
     * tag_<k> for each tag. */
    ca_knowledge_kv_t *fm = NULL; size_t fn = 0, fcap = 0;
    char idbuf[64], recbuf[32];
    snprintf(idbuf, sizeof(idbuf), "%s", note->id);
    snprintf(recbuf, sizeof(recbuf), "%" PRId64, recorded);
    ok = kkv_push(&fm, &fn, &fcap, "episode_id", idbuf) &&
         kkv_push(&fm, &fn, &fcap, "recorded_at", recbuf);
    if (ok && entry->app_context && !cab_is_ws(entry->app_context))
        ok = kkv_push(&fm, &fn, &fcap, "app_context", entry->app_context);
    if (ok && entry->embedding && entry->embedding_len > 0) {
        char *b64 = ca_base64_encode((const uint8_t *)entry->embedding,
                                     entry->embedding_len * sizeof(float));
        char dims[16];
        snprintf(dims, sizeof(dims), "%zu", entry->embedding_len);
        if (b64) {
            ok = kkv_push(&fm, &fn, &fcap, "embedding", b64) &&
                 kkv_push(&fm, &fn, &fcap, "embedding_dims", dims);
            free(b64);
        } else ok = false;
    }
    /* tags -> frontmatter tag_<k>, and note->tags = keys */
    char **tagkeys = NULL; size_t tk = 0;
    if (ok && entry->tag_count > 0) {
        tagkeys = (char **)calloc(entry->tag_count, sizeof(char *));
        if (!tagkeys) ok = false;
        for (size_t i = 0; ok && i < entry->tag_count; ++i) {
            char keybuf[128];
            snprintf(keybuf, sizeof(keybuf), "tag_%s", entry->tag_keys[i]);
            ok = kkv_push(&fm, &fn, &fcap, keybuf, entry->tag_values[i]);
            if (ok) { tagkeys[tk] = cab_strdup_empty(entry->tag_keys[i]); if (!tagkeys[tk]) ok = false; else tk++; }
        }
    }
    if (!ok) { kkv_free(fm, fn); cab_strv_free(tagkeys, tk); ca_knowledge_note_free(note); return false; }
    note->frontmatter = fm; note->frontmatter_count = fn;
    note->tags = tk ? tagkeys : (free(tagkeys), NULL); note->tag_count = tk;
    return true;
}

/* Split a note body into user + assistant text. */
static void split_body(const char *body, char **user_out, char **asst_out) {
    *user_out = NULL; *asst_out = NULL;
    if (!body || body[0] == '\0') { *user_out = cab_strdup_empty(""); *asst_out = cab_strdup_empty(""); return; }
    /* normalise CRLF */
    size_t bl = strlen(body);
    char *norm = (char *)malloc(bl + 1);
    if (!norm) return;
    size_t k = 0;
    for (size_t i = 0; i < bl; ++i) { if (body[i] == '\r' && body[i + 1] == '\n') continue; norm[k++] = body[i]; }
    norm[k] = '\0';
    const char *um = "## User\n\n";
    const char *am = "\n\n## Assistant\n\n";
    const char *ui = strstr(norm, um);
    const char *ai = strstr(norm, am);
    if (!ui || !ai || ai <= ui) { *user_out = cab_strdup_empty(norm); *asst_out = cab_strdup_empty(""); free(norm); return; }
    const char *ustart = ui + strlen(um);
    size_t ulen = (size_t)(ai - ustart);
    *user_out = (char *)malloc(ulen + 1);
    if (*user_out) { memcpy(*user_out, ustart, ulen); (*user_out)[ulen] = '\0'; }
    *asst_out = cab_strdup_empty(ai + strlen(am));
    free(norm);
}

/* Map a note -> ca_episodic_entry_t (FromNote). Deep-owns its fields. */
static bool note_to_entry(const ca_knowledge_note_t *note, ca_episodic_entry_t *entry) {
    memset(entry, 0, sizeof(*entry));
    /* episode_id / recorded_at / app_context / embedding from frontmatter */
    const char *epid = NULL, *rec = NULL, *appc = NULL, *emb = NULL;
    for (size_t i = 0; i < note->frontmatter_count; ++i) {
        const char *kk = note->frontmatter[i].key;
        if (cab_ord_eq(kk, "episode_id")) epid = note->frontmatter[i].value;
        else if (cab_ord_eq(kk, "recorded_at")) rec = note->frontmatter[i].value;
        else if (cab_ord_eq(kk, "app_context")) appc = note->frontmatter[i].value;
        else if (cab_ord_eq(kk, "embedding")) emb = note->frontmatter[i].value;
    }
    entry->id = cab_strdup_empty(epid && epid[0] ? epid : note->id);
    entry->recorded_at_ms = rec ? strtoll(rec, NULL, 10) : note->created_at_ms;
    if (!entry->id) return false;

    char *ut = NULL, *at = NULL;
    split_body(note->body_markdown, &ut, &at);
    entry->user_text = ut ? ut : cab_strdup_empty("");
    entry->assistant_text = at ? at : cab_strdup_empty("");
    if (!entry->user_text || !entry->assistant_text) { ca_episodic_entry_free(entry); return false; }
    entry->app_context = (appc && appc[0]) ? cab_strdup(appc) : NULL;
    if (appc && appc[0] && !entry->app_context) { ca_episodic_entry_free(entry); return false; }

    if (emb && !cab_is_ws(emb)) {
        size_t blen = 0;
        uint8_t *bytes = ca_base64_decode(emb, &blen);
        if (bytes && blen >= sizeof(float)) {
            size_t n = blen / sizeof(float);
            entry->embedding = (float *)malloc(n * sizeof(float));
            if (entry->embedding) { memcpy(entry->embedding, bytes, n * sizeof(float)); entry->embedding_len = n; }
            free(bytes);
        } else free(bytes);
    }

    /* tag_<k> -> tag arrays */
    size_t tcount = 0;
    for (size_t i = 0; i < note->frontmatter_count; ++i)
        if (cab_ci_cmp_prefix(note->frontmatter[i].key, "tag_")) tcount++;
    if (tcount > 0) {
        entry->tag_keys = (char **)calloc(tcount, sizeof(char *));
        entry->tag_values = (char **)calloc(tcount, sizeof(char *));
        if (!entry->tag_keys || !entry->tag_values) { ca_episodic_entry_free(entry); return false; }
        size_t k = 0;
        for (size_t i = 0; i < note->frontmatter_count; ++i) {
            if (!cab_ci_cmp_prefix(note->frontmatter[i].key, "tag_")) continue;
            entry->tag_keys[k] = cab_strdup_empty(note->frontmatter[i].key + 4);
            entry->tag_values[k] = cab_strdup_empty(note->frontmatter[i].value);
            if (!entry->tag_keys[k] || !entry->tag_values[k]) { entry->tag_count = k + 1; ca_episodic_entry_free(entry); return false; }
            k++;
        }
        entry->tag_count = tcount;
    }
    return true;
}

int ca_markdown_episodic_store_add(ca_markdown_episodic_store_t *s,
                                   const ca_episodic_entry_t *entry, int64_t now_ms) {
    if (!s || !entry) return -1;
    ca_knowledge_note_t note;
    if (!entry_to_note(entry, now_ms, &note)) return -1;
    int rc = ca_knowledge_store_save(s->store, &note, note.updated_at_ms, NULL);
    ca_knowledge_note_free(&note);
    return rc;
}

/* Snapshot all entries (FromNote over every note). Returns owned array. */
static ca_episodic_entry_t *snapshot_entries(const ca_markdown_episodic_store_t *s,
                                             size_t *out_n) {
    size_t nn = 0;
    ca_knowledge_note_t *notes = ca_knowledge_store_enumerate_all(s->store, &nn);
    if (nn == (size_t)-1) { *out_n = (size_t)-1; return NULL; }
    if (nn == 0) { *out_n = 0; return NULL; }
    ca_episodic_entry_t *entries = (ca_episodic_entry_t *)calloc(nn, sizeof(*entries));
    if (!entries) { ca_knowledge_note_free_array(notes, nn); *out_n = (size_t)-1; return NULL; }
    size_t k = 0;
    bool ok = true;
    for (size_t i = 0; i < nn; ++i) {
        if (!note_to_entry(&notes[i], &entries[k])) { ok = false; break; }
        k++;
    }
    ca_knowledge_note_free_array(notes, nn);
    if (!ok) { ca_episodic_entry_free_array(entries, k); *out_n = (size_t)-1; return NULL; }
    *out_n = nn;
    return entries;
}

/* Sort entry indices by recorded_at desc (stable). */
static void sort_recent(const ca_episodic_entry_t *e, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i]; int64_t kt = e[key].recorded_at_ms;
        size_t j = i;
        while (j > 0 && e[idx[j - 1]].recorded_at_ms < kt) { idx[j] = idx[j - 1]; j--; }
        idx[j] = key;
    }
}

ca_episodic_entry_t *ca_markdown_episodic_store_search(const ca_markdown_episodic_store_t *s,
                                                       const float *query_embedding,
                                                       size_t query_len, int top_k,
                                                       size_t *out_count) {
    if (!out_count) return NULL;
    if (!s || top_k <= 0) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    ca_episodic_entry_t *entries = snapshot_entries(s, &n);
    if (n == (size_t)-1) { *out_count = (size_t)-1; return NULL; }
    if (n == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { ca_episodic_entry_free_array(entries, n); *out_count = (size_t)-1; return NULL; }
    size_t m = 0;

    if (!query_embedding || query_len == 0) {
        for (size_t i = 0; i < n; ++i) idx[m++] = i;
        sort_recent(entries, idx, m);
    } else {
        /* dot-product ranking over matching-dimension embeddings */
        float *scores = (float *)malloc(n * sizeof(float));
        if (!scores) { free(idx); ca_episodic_entry_free_array(entries, n); *out_count = (size_t)-1; return NULL; }
        for (size_t i = 0; i < n; ++i) {
            if (entries[i].embedding && entries[i].embedding_len == query_len) {
                float dot = 0.0f;
                for (size_t k = 0; k < query_len; ++k) dot += entries[i].embedding[k] * query_embedding[k];
                idx[m] = i; scores[m] = dot; m++;
            }
        }
        for (size_t i = 1; i < m; ++i) {
            size_t ki = idx[i]; float ks = scores[i]; size_t j = i;
            while (j > 0 && scores[j - 1] < ks) { idx[j] = idx[j - 1]; scores[j] = scores[j - 1]; j--; }
            idx[j] = ki; scores[j] = ks;
        }
        free(scores);
    }

    if ((size_t)top_k < m) m = (size_t)top_k;
    if (m == 0) { free(idx); ca_episodic_entry_free_array(entries, n); *out_count = 0; return NULL; }

    ca_episodic_entry_t *out = (ca_episodic_entry_t *)calloc(m, sizeof(*out));
    if (!out) { free(idx); ca_episodic_entry_free_array(entries, n); *out_count = (size_t)-1; return NULL; }
    /* move selected entries out; free the rest */
    bool *taken = (bool *)calloc(n, sizeof(bool));
    if (!taken) { free(out); free(idx); ca_episodic_entry_free_array(entries, n); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < m; ++i) { out[i] = entries[idx[i]]; taken[idx[i]] = true; }
    for (size_t i = 0; i < n; ++i) if (!taken[i]) ca_episodic_entry_free(&entries[i]);
    free(taken); free(idx); free(entries);
    *out_count = m;
    return out;
}

ca_episodic_entry_t *ca_markdown_episodic_store_get_recent(const ca_markdown_episodic_store_t *s,
                                                           int count, size_t *out_count) {
    return ca_markdown_episodic_store_search(s, NULL, 0, count, out_count);
}

long ca_markdown_episodic_store_count(const ca_markdown_episodic_store_t *s) {
    if (!s) return -1;
    size_t n = 0;
    ca_knowledge_note_t *notes = ca_knowledge_store_enumerate_all(s->store, &n);
    if (n == (size_t)-1) return -1;
    ca_knowledge_note_free_array(notes, n);
    return (long)n;
}

long ca_markdown_episodic_store_prune_older_than(ca_markdown_episodic_store_t *s,
                                                 int64_t cutoff_ms) {
    if (!s) return -1;
    size_t n = 0;
    ca_episodic_entry_t *entries = snapshot_entries(s, &n);
    if (n == (size_t)-1) return -1;
    long removed = 0;
    for (size_t i = 0; i < n; ++i) {
        if (entries[i].recorded_at_ms < cutoff_ms) {
            ca_knowledge_store_delete(s->store, entries[i].id);
            removed++;
        }
    }
    ca_episodic_entry_free_array(entries, n);
    return removed;
}
