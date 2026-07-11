/*
 * sdd.c — CircleAI.SDD (C11 port).
 *
 * Spec store keyed by SpecId. The validator checks required fields and — when a
 * schema is supplied — that it is a JSON object declaring a top-level "type"
 * (shallow scan, matching the C# JsonShape validator's intent). The scaffolder
 * emits a minimal compilable project for csharp / typescript / python.
 * Deterministic. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/sdd.h"
#include "board_common.h"
#include <stdarg.h>
#include <stdio.h>

/* ── metadata helpers ───────────────────────────────────────────────────── */

static void meta_free(ca_spec_meta_t *m, size_t n) {
    if (!m) return;
    for (size_t i = 0; i < n; ++i) { free(m[i].key); free(m[i].value); }
    free(m);
}
static bool meta_copy(ca_spec_meta_t **out, const ca_spec_meta_t *src, size_t n) {
    *out = NULL;
    if (n == 0) return true;
    ca_spec_meta_t *v = (ca_spec_meta_t *)calloc(n, sizeof(*v));
    if (!v) return false;
    for (size_t i = 0; i < n; ++i) {
        v[i].key   = cab_strdup_empty(src ? src[i].key : NULL);
        v[i].value = cab_strdup_empty(src ? src[i].value : NULL);
        if (!v[i].key || !v[i].value) { meta_free(v, i + 1); return false; }
    }
    *out = v;
    return true;
}

/* ── Specification ──────────────────────────────────────────────────────── */

void ca_specification_free(ca_specification_t *s) {
    if (!s) return;
    free(s->spec_id);
    free(s->title);
    free(s->body);
    free(s->schema);
    meta_free(s->metadata, s->metadata_count);
    memset(s, 0, sizeof(*s));
}
void ca_specification_free_array(ca_specification_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_specification_free(&arr[i]);
    free(arr);
}
static bool spec_copy(ca_specification_t *dst, const ca_specification_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->spec_id = cab_strdup_empty(src->spec_id);
    dst->title   = cab_strdup_empty(src->title);
    dst->body    = cab_strdup_empty(src->body);
    dst->schema  = src->schema ? cab_strdup(src->schema) : NULL;
    if (!dst->spec_id || !dst->title || !dst->body || (src->schema && !dst->schema)) {
        ca_specification_free(dst); return false;
    }
    if (!meta_copy(&dst->metadata, src->metadata, src->metadata_count)) {
        ca_specification_free(dst); return false;
    }
    dst->metadata_count = src->metadata_count;
    return true;
}

/* ── SpecValidationResult ───────────────────────────────────────────────── */

void ca_spec_validation_result_free(ca_spec_validation_result_t *r) {
    if (!r) return;
    cab_strv_free(r->errors, r->error_count);
    r->errors = NULL; r->error_count = 0;
}

/* ── ScaffoldedProject ──────────────────────────────────────────────────── */

void ca_scaffolded_project_free(ca_scaffolded_project_t *p) {
    if (!p) return;
    free(p->project_id);
    if (p->files) {
        for (size_t i = 0; i < p->file_count; ++i) {
            free(p->files[i].path);
            free(p->files[i].bytes);
        }
        free(p->files);
    }
    p->project_id = NULL; p->files = NULL; p->file_count = 0;
}

/* ── InMemorySpecificationStore ─────────────────────────────────────────── */

struct ca_specification_store {
    ca_specification_t *items;
    size_t              count, cap;
};

ca_specification_store_t *ca_specification_store_create(void) {
    return (ca_specification_store_t *)calloc(1, sizeof(ca_specification_store_t));
}
void ca_specification_store_destroy(ca_specification_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_specification_free(&s->items[i]);
    free(s->items);
    free(s);
}
const char *ca_specification_store_backend_id(const ca_specification_store_t *s) {
    (void)s; return "in-memory";
}

int ca_specification_store_upsert(ca_specification_store_t *s,
                                  const ca_specification_t *spec) {
    if (!s || !spec || cab_is_ws(spec->spec_id)) return -1;
    for (size_t i = 0; i < s->count; ++i) {
        if (cab_ord_eq(s->items[i].spec_id, spec->spec_id)) {
            ca_specification_t copy;
            if (!spec_copy(&copy, spec)) return -1;
            ca_specification_free(&s->items[i]);
            s->items[i] = copy;
            return 0;
        }
    }
    ca_specification_t copy;
    if (!spec_copy(&copy, spec)) return -1;
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->items, nc * sizeof(*s->items));
        if (!n) { ca_specification_free(&copy); return -1; }
        s->items = (ca_specification_t *)n;
        s->cap = nc;
    }
    s->items[s->count++] = copy;
    return 0;
}

bool ca_specification_store_get(const ca_specification_store_t *s,
                                const char *spec_id, ca_specification_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || cab_is_ws(spec_id) || !out) return false;
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->items[i].spec_id, spec_id))
            return spec_copy(out, &s->items[i]);
    return false;
}

ca_specification_t *ca_specification_store_list(const ca_specification_store_t *s,
                                                size_t *out_count) {
    if (!out_count) return NULL;
    if (!s) { *out_count = (size_t)-1; return NULL; }
    if (s->count == 0) { *out_count = 0; return NULL; }
    ca_specification_t *out = (ca_specification_t *)calloc(s->count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < s->count; ++i) {
        if (!spec_copy(&out[i], &s->items[i])) {
            ca_specification_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = s->count;
    return out;
}

const char *ca_sdd_null_spec_store_backend_id(void) { return "null"; }

/* ── JsonShapeSpecificationValidator ────────────────────────────────────── */

const char *ca_spec_validator_backend_id(void) { return "json-shape"; }

/* Growable string-vector for error accumulation. */
static bool errv_push(char ***v, size_t *n, size_t *cap, const char *msg) {
    if (*n == *cap) {
        size_t nc = *cap ? *cap * 2 : 4;
        char **nv = (char **)realloc(*v, nc * sizeof(char *));
        if (!nv) return false;
        *v = nv; *cap = nc;
    }
    (*v)[*n] = cab_strdup_empty(msg);
    if (!(*v)[*n]) return false;
    (*n)++;
    return true;
}

/* Shallow JSON schema shape check. On success sets *is_object / *has_type;
 * returns true when the text parses as an object at top level, false when it is
 * not valid JSON at all (mirrors the catch(JsonException) branch). This is a
 * lenient scanner sufficient for schema-shape validation. */
static bool schema_shape(const char *s, bool *is_object, bool *has_type) {
    *is_object = false; *has_type = false;
    size_t i = 0;
    while (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r') i++;
    if (s[i] == '\0') return false;             /* empty -> not valid JSON */
    if (s[i] != '{') { *is_object = false; return true; } /* valid JSON, non-object */
    *is_object = true;
    i++;
    int depth = 1;
    bool in_str = false, expect_key = true, at_key = false;
    /* scan top-level keys; detect a top-level "type" key. */
    while (s[i] != '\0' && depth > 0) {
        char c = s[i];
        if (in_str) {
            if (c == '\\' && s[i + 1] != '\0') { i += 2; continue; }
            if (c == '"') {
                in_str = false;
                if (at_key && depth == 1) {
                    /* we captured a key literal that ended here; compared below */
                }
            }
            i++;
            continue;
        }
        if (c == '"') {
            /* start of a string; if we're at top level expecting a key, capture it */
            if (depth == 1 && expect_key) {
                size_t j = i + 1;
                /* read raw key (no escapes expected in schema keys we test) */
                char keybuf[16]; size_t kb = 0;
                bool simple = true;
                while (s[j] != '\0' && s[j] != '"') {
                    if (s[j] == '\\') { simple = false; break; }
                    if (kb < sizeof(keybuf) - 1) keybuf[kb++] = s[j];
                    j++;
                }
                keybuf[kb] = '\0';
                if (simple && strcmp(keybuf, "type") == 0) *has_type = true;
            }
            in_str = true; at_key = (depth == 1 && expect_key);
            i++;
            continue;
        }
        if (c == '{' || c == '[') { depth++; expect_key = (c == '{'); i++; continue; }
        if (c == '}' || c == ']') { depth--; i++; continue; }
        if (c == ':') { expect_key = false; i++; continue; }
        if (c == ',') { expect_key = (depth == 1); i++; continue; }
        i++;
    }
    if (depth != 0) return false; /* unbalanced -> not valid JSON */
    (void)at_key;
    return true;
}

bool ca_spec_validate(const ca_specification_t *spec,
                      ca_spec_validation_result_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!spec || !out) return false;

    char **errs = NULL; size_t n = 0, cap = 0;
    bool ok = true;
    if (cab_is_ws(spec->title)) ok = ok && errv_push(&errs, &n, &cap, "Title is required.");
    if (cab_is_ws(spec->body))  ok = ok && errv_push(&errs, &n, &cap, "Body is required.");
    if (ok && spec->schema && !cab_is_ws(spec->schema)) {
        bool is_object, has_type;
        if (!schema_shape(spec->schema, &is_object, &has_type)) {
            ok = errv_push(&errs, &n, &cap, "Schema is not valid JSON.");
        } else if (!is_object) {
            ok = errv_push(&errs, &n, &cap, "Schema must be a JSON object.");
        } else if (!has_type) {
            ok = errv_push(&errs, &n, &cap, "Schema must declare a top-level 'type'.");
        }
    }
    if (!ok) { cab_strv_free(errs, n); return false; }

    out->errors = errs;
    out->error_count = n;
    out->is_valid = (n == 0);
    return true;
}

/* ── NullSpecificationValidator ─────────────────────────────────────────── */

const char *ca_sdd_null_spec_validator_backend_id(void) { return "null"; }

bool ca_sdd_null_spec_validate(const ca_specification_t *spec,
                               ca_spec_validation_result_t *out) {
    (void)spec;
    if (out) memset(out, 0, sizeof(*out));
    if (!out) return false;
    char **errs = (char **)calloc(1, sizeof(char *));
    if (!errs) return false;
    errs[0] = cab_strdup_empty("No real validator wired.");
    if (!errs[0]) { free(errs); return false; }
    out->errors = errs;
    out->error_count = 1;
    out->is_valid = false;
    return true;
}

/* ── HelloWorldSpecToScaffold ───────────────────────────────────────────── */

const char *ca_spec_scaffold_backend_id(void) { return "hello-world"; }
const char *ca_sdd_null_spec_scaffold_backend_id(void) { return "null"; }

/* SanitizeName: keep alnum / '_' / '-'; "project" when empty. */
static char *sanitize_name(const char *id) {
    if (cab_is_ws(id)) return cab_strdup_empty("project");
    size_t n = strlen(id);
    char *out = (char *)malloc(n + 1);
    if (!out) return NULL;
    size_t k = 0;
    for (size_t i = 0; i < n; ++i) {
        char c = id[i];
        if (isalnum((unsigned char)c) || c == '_' || c == '-') out[k++] = c;
    }
    out[k] = '\0';
    if (k == 0) { free(out); return cab_strdup_empty("project"); }
    return out;
}

/* EscapeText: \ -> \\, " -> \", newline -> \n (literal two chars). */
static char *escape_text(const char *s) {
    if (!s) return cab_strdup_empty("");
    size_t n = strlen(s);
    char *out = (char *)malloc(n * 2 + 1);
    if (!out) return NULL;
    size_t k = 0;
    for (size_t i = 0; i < n; ++i) {
        char c = s[i];
        if (c == '\\') { out[k++] = '\\'; out[k++] = '\\'; }
        else if (c == '"') { out[k++] = '\\'; out[k++] = '"'; }
        else if (c == '\n') { out[k++] = '\\'; out[k++] = 'n'; }
        else out[k++] = c;
    }
    out[k] = '\0';
    return out;
}

/* ASCII-lowercase a copy. */
static char *lower_dup(const char *s) {
    size_t n = strlen(s);
    char *out = (char *)malloc(n + 1);
    if (!out) return NULL;
    for (size_t i = 0; i < n; ++i) out[i] = (char)tolower((unsigned char)s[i]);
    out[n] = '\0';
    return out;
}

/* Append a file (path + UTF-8 content) to the project. `content` is consumed
 * (freed) by this call. path is copied. false on OOM. */
static bool scaffold_add(ca_scaffolded_project_t *p, const char *path,
                         char *content) {
    if (!content) return false;
    size_t clen = strlen(content);
    ca_scaffold_file_t *nf = (ca_scaffold_file_t *)realloc(
        p->files, (p->file_count + 1) * sizeof(*nf));
    if (!nf) { free(content); return false; }
    p->files = nf;
    ca_scaffold_file_t *slot = &p->files[p->file_count];
    slot->path = cab_strdup_empty(path);
    slot->len = clen;
    slot->bytes = NULL;
    if (!slot->path) { free(content); return false; }
    if (clen > 0) {
        slot->bytes = (uint8_t *)malloc(clen);
        if (!slot->bytes) { free(slot->path); free(content); return false; }
        memcpy(slot->bytes, content, clen);
    }
    p->file_count++;
    free(content);
    return true;
}

/* asprintf-style: format into a fresh buffer. Returns NULL on OOM. */
static char *afmt(const char *fmt, ...) {
    va_list ap, ap2;
    va_start(ap, fmt);
    va_copy(ap2, ap);
    int need = vsnprintf(NULL, 0, fmt, ap);
    va_end(ap);
    if (need < 0) { va_end(ap2); return NULL; }
    char *buf = (char *)malloc((size_t)need + 1);
    if (!buf) { va_end(ap2); return NULL; }
    vsnprintf(buf, (size_t)need + 1, fmt, ap2);
    va_end(ap2);
    return buf;
}

bool ca_spec_scaffold(const ca_specification_t *spec, const char *target_language,
                      ca_scaffolded_project_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!spec || !out || cab_is_ws(target_language)) return false;

    char *lang = lower_dup(target_language);
    char *name = sanitize_name(spec->spec_id);
    char *title = escape_text(spec->title);
    char *body  = escape_text(spec->body);
    if (!lang || !name || !title || !body) { free(lang); free(name); free(title); free(body); return false; }

    bool ok = true;
    char *project_id = NULL;

    if (strcmp(lang, "csharp") == 0 || strcmp(lang, "c#") == 0) {
        ok = ok && scaffold_add(out, "Program.cs",
                afmt("Console.WriteLine(\"%s: %s\");\n", name, title));
        char *csproj_path = afmt("%s.csproj", name);
        ok = ok && csproj_path && scaffold_add(out, csproj_path,
                cab_strdup_empty("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>"
                    "<OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework>"
                    "<Nullable>enable</Nullable></PropertyGroup>\n</Project>\n"));
        free(csproj_path);
        ok = ok && scaffold_add(out, "README.md", afmt("# %s\n\n%s\n", title, body));
    } else if (strcmp(lang, "typescript") == 0 || strcmp(lang, "ts") == 0) {
        ok = ok && scaffold_add(out, "index.ts",
                afmt("console.log(\"%s: %s\");\n", name, title));
        ok = ok && scaffold_add(out, "package.json",
                afmt("{\"name\":\"%s\",\"version\":\"0.1.0\",\"main\":\"index.ts\","
                     "\"scripts\":{\"start\":\"ts-node index.ts\"}}\n", name));
        ok = ok && scaffold_add(out, "tsconfig.json",
                cab_strdup_empty("{\"compilerOptions\":{\"strict\":true,\"target\":"
                    "\"ES2022\",\"module\":\"commonjs\"}}\n"));
        ok = ok && scaffold_add(out, "README.md", afmt("# %s\n\n%s\n", title, body));
    } else if (strcmp(lang, "python") == 0 || strcmp(lang, "py") == 0) {
        ok = ok && scaffold_add(out, "main.py",
                afmt("def main():\n    print(\"%s: %s\")\n\n"
                     "if __name__ == \"__main__\":\n    main()\n", name, title));
        ok = ok && scaffold_add(out, "pyproject.toml",
                afmt("[project]\nname = \"%s\"\nversion = \"0.1.0\"\n"
                     "requires-python = \">=3.10\"\n", name));
        ok = ok && scaffold_add(out, "README.md", afmt("# %s\n\n%s\n", title, body));
    } else {
        ok = false; /* unsupported language */
    }

    if (ok) {
        project_id = afmt("%s-%s", name, lang);
        if (!project_id) ok = false;
    }

    free(lang); free(name); free(title); free(body);
    if (!ok) { ca_scaffolded_project_free(out); free(project_id); return false; }
    out->project_id = project_id;
    return true;
}
