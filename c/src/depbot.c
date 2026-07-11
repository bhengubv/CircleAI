/*
 * depbot.c — CircleAI.DepBot (C11 port).
 *
 * Manifests (path + content) are registered by the host (filesystem walk is the
 * injected boundary). The parsers mirror the C# FilesystemDependencyAnalyzer for
 * package.json / requirements.txt / Cargo.toml / *.csproj, and the updater
 * rewrites nuget / npm / pypi entries in place. Deterministic. Pure C11 + libc.
 * No pthreads.
 */

#include "circle_ai/depbot.h"
#include "board_common.h"
#include <stdio.h>

/* ── Dependency ─────────────────────────────────────────────────────────── */

void ca_dependency_free(ca_dependency_t *d) {
    if (!d) return;
    free(d->ecosystem);
    free(d->name);
    free(d->current_version);
    free(d->latest_version);
    memset(d, 0, sizeof(*d));
}
void ca_dependency_free_array(ca_dependency_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_dependency_free(&arr[i]);
    free(arr);
}

/* ── DependencyUpdate ───────────────────────────────────────────────────── */

void ca_dependency_update_free(ca_dependency_update_t *u) {
    if (!u) return;
    free(u->ecosystem);
    free(u->name);
    free(u->from_version);
    free(u->to_version);
    memset(u, 0, sizeof(*u));
}
void ca_dependency_update_free_array(ca_dependency_update_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_dependency_update_free(&arr[i]);
    free(arr);
}

/* ── manifest kinds ─────────────────────────────────────────────────────── */

typedef enum {
    MANIFEST_UNKNOWN = 0,
    MANIFEST_NPM,      /* package.json */
    MANIFEST_PYPI,     /* requirements.txt */
    MANIFEST_CARGO,    /* Cargo.toml */
    MANIFEST_NUGET     /* *.csproj */
} manifest_kind_t;

typedef struct {
    char           *path;    /* owned */
    char           *content; /* owned */
    manifest_kind_t kind;
} manifest_t;

struct ca_dependency_analyzer {
    manifest_t *items;
    size_t      count, cap;
};

/* basename of a path (after last '/' or '\\'). */
static const char *path_basename(const char *path) {
    const char *b = path;
    for (const char *p = path; *p; ++p)
        if (*p == '/' || *p == '\\') b = p + 1;
    return b;
}
static bool ends_with_ci(const char *s, const char *suffix) {
    size_t sl = strlen(s), fl = strlen(suffix);
    if (fl > sl) return false;
    return cab_ci_eq(s + (sl - fl), suffix);
}
static bool contains_substr(const char *hay, const char *needle) {
    return strstr(hay, needle) != NULL;
}

static manifest_kind_t detect_kind(const char *path) {
    const char *base = path_basename(path);
    if (cab_ci_eq(base, "package.json")) {
        /* node_modules paths skipped (C# analyzer). */
        if (contains_substr(path, "node_modules")) return MANIFEST_UNKNOWN;
        return MANIFEST_NPM;
    }
    if (cab_ci_eq(base, "requirements.txt")) return MANIFEST_PYPI;
    if (cab_ci_eq(base, "Cargo.toml")) {
        if (contains_substr(path, "target")) return MANIFEST_UNKNOWN;
        return MANIFEST_CARGO;
    }
    if (ends_with_ci(base, ".csproj")) return MANIFEST_NUGET;
    return MANIFEST_UNKNOWN;
}

ca_dependency_analyzer_t *ca_dependency_analyzer_create(void) {
    return (ca_dependency_analyzer_t *)calloc(1, sizeof(ca_dependency_analyzer_t));
}
void ca_dependency_analyzer_destroy(ca_dependency_analyzer_t *a) {
    if (!a) return;
    for (size_t i = 0; i < a->count; ++i) { free(a->items[i].path); free(a->items[i].content); }
    free(a->items);
    free(a);
}
const char *ca_dependency_analyzer_backend_id(const ca_dependency_analyzer_t *a) {
    (void)a; return "manifest";
}

int ca_dependency_analyzer_add_manifest(ca_dependency_analyzer_t *a,
                                        const char *path, const char *content) {
    if (!a || cab_is_ws(path) || !content) return -1;
    /* replace if path already present */
    for (size_t i = 0; i < a->count; ++i) {
        if (cab_ord_eq(a->items[i].path, path)) {
            char *nc = cab_strdup(content);
            if (!nc) return -1;
            free(a->items[i].content);
            a->items[i].content = nc;
            a->items[i].kind = detect_kind(path);
            return 0;
        }
    }
    if (a->count == a->cap) {
        size_t nc = a->cap ? a->cap * 2 : 4;
        void *n = realloc(a->items, nc * sizeof(manifest_t));
        if (!n) return -1;
        a->items = (manifest_t *)n;
        a->cap = nc;
    }
    char *p = cab_strdup_empty(path);
    char *c = cab_strdup(content);
    if (!p || !c) { free(p); free(c); return -1; }
    a->items[a->count].path = p;
    a->items[a->count].content = c;
    a->items[a->count].kind = detect_kind(path);
    a->count++;
    return 0;
}

/* growable dependency vector */
typedef struct { ca_dependency_t *v; size_t n, cap; } dep_vec_t;
static bool dep_push(dep_vec_t *dv, const char *eco, const char *name,
                     const char *ver) {
    if (dv->n == dv->cap) {
        size_t nc = dv->cap ? dv->cap * 2 : 8;
        void *n = realloc(dv->v, nc * sizeof(ca_dependency_t));
        if (!n) return false;
        dv->v = (ca_dependency_t *)n;
        dv->cap = nc;
    }
    ca_dependency_t *d = &dv->v[dv->n];
    memset(d, 0, sizeof(*d));
    d->ecosystem = cab_strdup_empty(eco);
    d->name = cab_strdup_empty(name);
    d->current_version = cab_strdup_empty(ver ? ver : "");
    d->latest_version = NULL;
    if (!d->ecosystem || !d->name || !d->current_version) { ca_dependency_free(d); return false; }
    dv->n++;
    return true;
}

/* Parse a JSON string literal (no escapes expected in dep names/versions). */
static char *json_str_at(const char *p, const char **endp) {
    if (*p != '"') return NULL;
    p++;
    const char *s = p;
    while (*p && *p != '"') p++;
    if (*p != '"') return NULL;
    size_t n = (size_t)(p - s);
    char *out = (char *)malloc(n + 1);
    if (!out) return NULL;
    memcpy(out, s, n); out[n] = '\0';
    if (endp) *endp = p + 1;
    return out;
}

/* Within a package.json object section keyed by `section` ("dependencies" or
 * "devDependencies"), push each "name":"version". */
static bool parse_npm_section(const char *content, const char *section,
                              dep_vec_t *dv) {
    /* find "section" */
    char key[32];
    snprintf(key, sizeof(key), "\"%s\"", section);
    const char *sk = strstr(content, key);
    if (!sk) return true;
    const char *brace = strchr(sk, '{');
    if (!brace) return true;
    const char *p = brace + 1;
    int depth = 1;
    while (*p && depth > 0) {
        if (*p == '{') { depth++; p++; continue; }
        if (*p == '}') { depth--; p++; continue; }
        if (*p == '"' && depth == 1) {
            const char *after = NULL;
            char *name = json_str_at(p, &after);
            if (!name) { p++; continue; }
            p = after;
            while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;
            if (*p == ':') {
                p++;
                while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;
                char *ver = NULL;
                if (*p == '"') { const char *ae = NULL; ver = json_str_at(p, &ae); if (ae) p = ae; }
                bool ok = dep_push(dv, "npm", name, ver ? ver : "");
                free(name); free(ver);
                if (!ok) return false;
                continue;
            }
            free(name);
        }
        p++;
    }
    return true;
}

/* Iterate lines of `content`, calling fn per line (trimmed copy not made; fn
 * gets [start,len)). */
static bool for_each_line(const char *content,
                          bool (*fn)(const char *line, size_t len, void *ud),
                          void *ud) {
    const char *p = content;
    while (*p) {
        const char *nl = strchr(p, '\n');
        size_t len = nl ? (size_t)(nl - p) : strlen(p);
        if (len > 0 && p[len - 1] == '\r') len--;
        if (!fn(p, len, ud)) return false;
        if (!nl) break;
        p = nl + 1;
    }
    return true;
}

/* requirements.txt line parser context. */
typedef struct { dep_vec_t *dv; } pypi_ctx_t;
static bool pypi_line(const char *line, size_t len, void *ud) {
    pypi_ctx_t *c = (pypi_ctx_t *)ud;
    /* trim */
    while (len && (line[0] == ' ' || line[0] == '\t')) { line++; len--; }
    while (len && (line[len - 1] == ' ' || line[len - 1] == '\t')) len--;
    if (len == 0 || line[0] == '#') return true;
    /* name = [A-Za-z0-9_.-]+ */
    size_t i = 0;
    while (i < len && (isalnum((unsigned char)line[i]) || line[i] == '_' ||
                       line[i] == '.' || line[i] == '-')) i++;
    if (i == 0) return true;
    char name[128]; size_t nn = i < sizeof(name) - 1 ? i : sizeof(name) - 1;
    memcpy(name, line, nn); name[nn] = '\0';
    /* optional operator then version */
    while (i < len && (line[i] == '=' || line[i] == '<' || line[i] == '>' ||
                       line[i] == '!' || line[i] == '~' || line[i] == ' ')) i++;
    char ver[128]; size_t vn = 0;
    while (i < len && vn < sizeof(ver) - 1 &&
           (isalnum((unsigned char)line[i]) || line[i] == '.' || line[i] == '_' ||
            line[i] == '-')) ver[vn++] = line[i++];
    ver[vn] = '\0';
    return dep_push(c->dv, "pypi", name, ver);
}

/* Cargo.toml parser context. */
typedef struct { dep_vec_t *dv; bool in_deps; } cargo_ctx_t;
static bool cargo_line(const char *line, size_t len, void *ud) {
    cargo_ctx_t *c = (cargo_ctx_t *)ud;
    while (len && (line[0] == ' ' || line[0] == '\t')) { line++; len--; }
    while (len && (line[len - 1] == ' ' || line[len - 1] == '\t')) len--;
    if (len == 0) return true;
    if (line[0] == '[') {
        c->in_deps = (len == 14 && strncmp(line, "[dependencies]", 14) == 0);
        return true;
    }
    if (!c->in_deps || line[0] == '#') return true;
    /* name = "version" */
    size_t i = 0;
    while (i < len && (isalnum((unsigned char)line[i]) || line[i] == '_' || line[i] == '-')) i++;
    if (i == 0) return true;
    char name[128]; size_t nn = i < sizeof(name) - 1 ? i : sizeof(name) - 1;
    memcpy(name, line, nn); name[nn] = '\0';
    while (i < len && (line[i] == ' ' || line[i] == '=')) i++;
    if (i >= len || line[i] != '"') return true;
    i++;
    char ver[128]; size_t vn = 0;
    while (i < len && line[i] != '"' && vn < sizeof(ver) - 1) ver[vn++] = line[i++];
    ver[vn] = '\0';
    if (i >= len || line[i] != '"') return true;
    return dep_push(c->dv, "cargo", name, ver);
}

/* Parse *.csproj: <PackageReference Include="X" Version="Y" ... */
static bool parse_nuget(const char *content, dep_vec_t *dv) {
    const char *p = content;
    const char *tag;
    while ((tag = strstr(p, "<PackageReference")) != NULL) {
        const char *inc = strstr(tag, "Include=\"");
        const char *end = strchr(tag, '>');
        p = tag + 1;
        if (!inc || (end && inc > end)) continue;
        inc += strlen("Include=\"");
        const char *inc_end = strchr(inc, '"');
        if (!inc_end) continue;
        const char *ver = strstr(inc_end, "Version=\"");
        if (!ver || (end && ver > end)) continue;
        ver += strlen("Version=\"");
        const char *ver_end = strchr(ver, '"');
        if (!ver_end) continue;
        char name[256]; size_t nn = (size_t)(inc_end - inc); if (nn >= sizeof(name)) nn = sizeof(name) - 1;
        memcpy(name, inc, nn); name[nn] = '\0';
        char vbuf[128]; size_t vn = (size_t)(ver_end - ver); if (vn >= sizeof(vbuf)) vn = sizeof(vbuf) - 1;
        memcpy(vbuf, ver, vn); vbuf[vn] = '\0';
        if (!dep_push(dv, "nuget", name, vbuf)) return false;
    }
    return true;
}

ca_dependency_t *ca_dependency_analyzer_scan(const ca_dependency_analyzer_t *a,
                                             const char *repo_path,
                                             size_t *out_count) {
    if (!out_count) return NULL;
    if (!a || cab_is_ws(repo_path)) { *out_count = (size_t)-1; return NULL; }

    dep_vec_t dv = {0};
    bool ok = true;
    /* Order mirrors C#: npm, then pypi, then cargo, then nuget. */
    for (size_t i = 0; ok && i < a->count; ++i)
        if (a->items[i].kind == MANIFEST_NPM) {
            ok = parse_npm_section(a->items[i].content, "dependencies", &dv) &&
                 parse_npm_section(a->items[i].content, "devDependencies", &dv);
        }
    for (size_t i = 0; ok && i < a->count; ++i)
        if (a->items[i].kind == MANIFEST_PYPI) {
            pypi_ctx_t ctx = { &dv };
            ok = for_each_line(a->items[i].content, pypi_line, &ctx);
        }
    for (size_t i = 0; ok && i < a->count; ++i)
        if (a->items[i].kind == MANIFEST_CARGO) {
            cargo_ctx_t ctx = { &dv, false };
            ok = for_each_line(a->items[i].content, cargo_line, &ctx);
        }
    for (size_t i = 0; ok && i < a->count; ++i)
        if (a->items[i].kind == MANIFEST_NUGET)
            ok = parse_nuget(a->items[i].content, &dv);

    if (!ok) { ca_dependency_free_array(dv.v, dv.n); *out_count = (size_t)-1; return NULL; }
    if (dv.n == 0) { free(dv.v); *out_count = 0; return NULL; }
    *out_count = dv.n;
    return dv.v;
}

const char *ca_depbot_null_analyzer_backend_id(void) { return "null"; }

/* ── TextRewriteDependencyUpdater ───────────────────────────────────────── */

struct ca_dependency_updater {
    ca_dependency_analyzer_t *analyzer; /* borrowed */
};

ca_dependency_updater_t *ca_dependency_updater_create(ca_dependency_analyzer_t *analyzer) {
    if (!analyzer) return NULL;
    ca_dependency_updater_t *u = (ca_dependency_updater_t *)calloc(1, sizeof(*u));
    if (!u) return NULL;
    u->analyzer = analyzer;
    return u;
}
void ca_dependency_updater_destroy(ca_dependency_updater_t *u) { free(u); }
const char *ca_dependency_updater_backend_id(const ca_dependency_updater_t *u) {
    (void)u; return "text-rewrite";
}

ca_dependency_update_t *ca_dependency_updater_propose(
    const ca_dependency_updater_t *u, const char *repo_path, size_t *out_count) {
    if (!out_count) return NULL;
    if (!u || cab_is_ws(repo_path)) { *out_count = (size_t)-1; return NULL; }
    *out_count = 0; /* always empty (mirrors C# TextRewrite updater) */
    return NULL;
}

/* Replace the version in a single npm/pypi/nuget manifest string for a package.
 * Returns a fresh string (caller frees) or NULL on OOM; *changed set. */
static char *rewrite_nuget(const char *content, const char *name,
                           const char *to_version, bool *changed) {
    *changed = false;
    /* Build result by scanning for <PackageReference Include="name" Version="..." */
    size_t out_cap = strlen(content) + 64, out_len = 0;
    char *out = (char *)malloc(out_cap);
    if (!out) return NULL;
    const char *p = content;
    while (*p) {
        const char *tag = strstr(p, "<PackageReference");
        if (!tag) break;
        const char *inc = strstr(tag, "Include=\"");
        const char *tag_end = strstr(tag, "\"");
        (void)tag_end;
        const char *close = strchr(tag, '>');
        if (!inc || (close && inc > close)) { p = tag + 1; continue; }
        const char *inc_v = inc + strlen("Include=\"");
        const char *inc_e = strchr(inc_v, '"');
        if (!inc_e) { p = tag + 1; continue; }
        size_t nn = (size_t)(inc_e - inc_v);
        if (nn != strlen(name) || strncmp(inc_v, name, nn) != 0) { p = tag + 1; continue; }
        const char *ver = strstr(inc_e, "Version=\"");
        if (!ver || (close && ver > close)) { p = tag + 1; continue; }
        const char *ver_v = ver + strlen("Version=\"");
        const char *ver_e = strchr(ver_v, '"');
        if (!ver_e) { p = tag + 1; continue; }
        /* copy [p, ver_v) then to_version then continue after ver_e */
        size_t head = (size_t)(ver_v - p);
        size_t need = out_len + head + strlen(to_version) + 1;
        if (need > out_cap) { size_t nc = out_cap * 2; while (nc < need) nc *= 2; char *nb = realloc(out, nc); if (!nb) { free(out); return NULL; } out = nb; out_cap = nc; }
        memcpy(out + out_len, p, head); out_len += head;
        memcpy(out + out_len, to_version, strlen(to_version)); out_len += strlen(to_version);
        out[out_len] = '\0';
        p = ver_e;
        *changed = true;
    }
    /* copy remainder */
    size_t rem = strlen(p);
    if (out_len + rem + 1 > out_cap) { char *nb = realloc(out, out_len + rem + 1); if (!nb) { free(out); return NULL; } out = nb; }
    memcpy(out + out_len, p, rem); out_len += rem;
    out[out_len] = '\0';
    return out;
}

/* Rewrite an npm "name": "..." version entry. */
static char *rewrite_npm(const char *content, const char *name,
                         const char *to_version, bool *changed) {
    *changed = false;
    char key[256];
    snprintf(key, sizeof(key), "\"%s\"", name);
    size_t out_cap = strlen(content) + 64, out_len = 0;
    char *out = (char *)malloc(out_cap);
    if (!out) return NULL;
    const char *p = content;
    while (*p) {
        const char *k = strstr(p, key);
        if (!k) break;
        const char *q = k + strlen(key);
        const char *r = q;
        while (*r == ' ' || *r == '\t') r++;
        if (*r != ':') { /* not a key: copy up to and incl this occurrence */
            size_t adv = (size_t)(q - p);
            size_t need = out_len + adv + 1;
            if (need > out_cap) { size_t nc = out_cap * 2; while (nc < need) nc *= 2; char *nb = realloc(out, nc); if (!nb) { free(out); return NULL; } out = nb; out_cap = nc; }
            memcpy(out + out_len, p, adv); out_len += adv; out[out_len] = '\0';
            p = q;
            continue;
        }
        r++;
        while (*r == ' ' || *r == '\t') r++;
        if (*r != '"') { p = k + 1; continue; }
        const char *ve = strchr(r + 1, '"');
        if (!ve) { p = k + 1; continue; }
        /* emit [p, k) + "name": "to_version" */
        size_t head = (size_t)(k - p);
        char repl[512];
        int rl = snprintf(repl, sizeof(repl), "\"%s\": \"%s\"", name, to_version);
        size_t need = out_len + head + (size_t)rl + 1;
        if (need > out_cap) { size_t nc = out_cap * 2; while (nc < need) nc *= 2; char *nb = realloc(out, nc); if (!nb) { free(out); return NULL; } out = nb; out_cap = nc; }
        memcpy(out + out_len, p, head); out_len += head;
        memcpy(out + out_len, repl, (size_t)rl); out_len += (size_t)rl;
        out[out_len] = '\0';
        p = ve + 1;
        *changed = true;
    }
    size_t rem = strlen(p);
    if (out_len + rem + 1 > out_cap) { char *nb = realloc(out, out_len + rem + 1); if (!nb) { free(out); return NULL; } out = nb; }
    memcpy(out + out_len, p, rem); out_len += rem; out[out_len] = '\0';
    return out;
}

/* Rewrite a requirements.txt line for `name` to "name==to_version". */
typedef struct {
    const char *name; const char *to_version;
    char *out; size_t out_len, out_cap; bool ok;
} pypi_rewrite_ctx_t;
static bool pypi_rewrite_line(const char *line, size_t len, void *ud) {
    pypi_rewrite_ctx_t *c = (pypi_rewrite_ctx_t *)ud;
    /* Determine trimmed leading name */
    const char *t = line; size_t tl = len;
    while (tl && (t[0] == ' ' || t[0] == '\t')) { t++; tl--; }
    bool replace = false;
    if (tl > 0 && t[0] != '#') {
        size_t i = 0;
        while (i < tl && (isalnum((unsigned char)t[i]) || t[i] == '_' || t[i] == '.' || t[i] == '-')) i++;
        if (i == strlen(c->name) && strncmp(t, c->name, i) == 0) {
            /* must be followed by operator + version (Regex requires a version) */
            size_t j = i;
            while (j < tl && (t[j] == '=' || t[j] == '<' || t[j] == '>' || t[j] == '!' || t[j] == '~' || t[j] == ' ')) j++;
            if (j < tl) replace = true;
        }
    }
    char linebuf[512];
    const char *emit; size_t emit_len;
    if (replace) {
        int n = snprintf(linebuf, sizeof(linebuf), "%s==%s", c->name, c->to_version);
        emit = linebuf; emit_len = (size_t)n;
    } else {
        emit = line; emit_len = len;
    }
    size_t need = c->out_len + emit_len + 2;
    if (need > c->out_cap) {
        size_t nc = c->out_cap ? c->out_cap * 2 : 128;
        while (nc < need) nc *= 2;
        char *nb = realloc(c->out, nc);
        if (!nb) { c->ok = false; return false; }
        c->out = nb; c->out_cap = nc;
    }
    memcpy(c->out + c->out_len, emit, emit_len); c->out_len += emit_len;
    c->out[c->out_len++] = '\n';
    c->out[c->out_len] = '\0';
    return true;
}

int ca_dependency_updater_apply(ca_dependency_updater_t *u, const char *repo_path,
                                const ca_dependency_update_t *update) {
    if (!u || !update || cab_is_ws(repo_path)) return -1;
    if (cab_is_ws(update->ecosystem)) return 0;
    ca_dependency_analyzer_t *a = u->analyzer;

    char *eco = cab_strdup_empty(update->ecosystem);
    if (!eco) return -1;
    for (char *p = eco; *p; ++p) *p = (char)tolower((unsigned char)*p);

    int rc = 0;
    if (strcmp(eco, "nuget") == 0) {
        for (size_t i = 0; i < a->count; ++i) {
            if (a->items[i].kind != MANIFEST_NUGET) continue;
            bool changed = false;
            char *nc = rewrite_nuget(a->items[i].content, update->name, update->to_version, &changed);
            if (!nc) { rc = -1; break; }
            if (changed && strcmp(nc, a->items[i].content) != 0) {
                free(a->items[i].content); a->items[i].content = nc;
            } else free(nc);
        }
    } else if (strcmp(eco, "npm") == 0) {
        for (size_t i = 0; i < a->count; ++i) {
            if (a->items[i].kind != MANIFEST_NPM) continue;
            bool changed = false;
            char *nc = rewrite_npm(a->items[i].content, update->name, update->to_version, &changed);
            if (!nc) { rc = -1; break; }
            free(a->items[i].content); a->items[i].content = nc;
        }
    } else if (strcmp(eco, "pypi") == 0) {
        for (size_t i = 0; i < a->count; ++i) {
            if (a->items[i].kind != MANIFEST_PYPI) continue;
            pypi_rewrite_ctx_t ctx = { update->name, update->to_version, NULL, 0, 0, true };
            ctx.out = (char *)malloc(1); if (ctx.out) ctx.out[0] = '\0'; else { rc = -1; break; }
            ctx.out_cap = 1;
            for_each_line(a->items[i].content, pypi_rewrite_line, &ctx);
            if (!ctx.ok) { free(ctx.out); rc = -1; break; }
            free(a->items[i].content);
            a->items[i].content = ctx.out ? ctx.out : cab_strdup_empty("");
        }
    }
    free(eco);
    return rc;
}

const char *ca_depbot_null_updater_backend_id(void) { return "null"; }
