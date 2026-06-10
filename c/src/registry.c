/*
 * registry.c — installed.json IO + check_for_upgrades.
 *
 * Hand-rolled minimal JSON for our exact manifest schema:
 *   { "modelId":"...","version":"...","repo":"...","totalBytes":N,
 *     "files":[{"name":"...","sha256":"...","sizeBytes":N},...],
 *     "installedAtUtc":"<ISO-8601>" }
 *
 * Pure C11, no JSON lib dep.
 */

#include "circle_ai/registry.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <time.h>
#include <sys/stat.h>

#if defined(_WIN32)
  #include <direct.h>
  #define CA_MKDIR(p) _mkdir(p)
  #define CA_SEP '\\'
  #define CA_SEP_S "\\"
#else
  #include <sys/types.h>
  #define CA_MKDIR(p) mkdir((p), 0777)
  #define CA_SEP '/'
  #define CA_SEP_S "/"
#endif

/* ─────────────────────── path helpers ─────────────────────── */

static int dir_exists(const char *path) {
    struct stat st;
    if (stat(path, &st) != 0) return 0;
#if defined(_WIN32)
    return (st.st_mode & _S_IFDIR) != 0;
#else
    return S_ISDIR(st.st_mode);
#endif
}

static int file_exists(const char *path) {
    struct stat st;
    if (stat(path, &st) != 0) return 0;
#if defined(_WIN32)
    return (st.st_mode & _S_IFREG) != 0;
#else
    return S_ISREG(st.st_mode);
#endif
}

static int mkdir_p(const char *path) {
    if (dir_exists(path)) return 0;
    if (CA_MKDIR(path) == 0) return 0;
    return -1;
}

static void join_path(char *out, size_t out_cap, const char *a, const char *b) {
    size_t la = strlen(a);
    if (la == 0) { snprintf(out, out_cap, "%s", b); return; }
    if (a[la - 1] == CA_SEP || a[la - 1] == '/' || a[la - 1] == '\\')
        snprintf(out, out_cap, "%s%s", a, b);
    else
        snprintf(out, out_cap, "%s%c%s", a, CA_SEP, b);
}

/* ─────────────────────── ISO-8601 helper ─────────────────────── */

static void format_iso8601_utc(int64_t unix_ms, char *out, size_t cap) {
    time_t s = (time_t)(unix_ms / 1000);
    int ms = (int)(unix_ms % 1000);
    struct tm tm_buf;
#if defined(_WIN32)
    gmtime_s(&tm_buf, &s);
#else
    gmtime_r(&s, &tm_buf);
#endif
    snprintf(out, cap, "%04d-%02d-%02dT%02d:%02d:%02d.%03dZ",
             tm_buf.tm_year + 1900, tm_buf.tm_mon + 1, tm_buf.tm_mday,
             tm_buf.tm_hour, tm_buf.tm_min, tm_buf.tm_sec, ms);
}

/* ─────────────────────── JSON writer ─────────────────────── */

static int write_string_escaped(FILE *f, const char *s) {
    if (fputc('"', f) == EOF) return -1;
    for (const unsigned char *p = (const unsigned char *)s; *p; p++) {
        if (*p == '"') { if (fputs("\\\"", f) < 0) return -1; }
        else if (*p == '\\') { if (fputs("\\\\", f) < 0) return -1; }
        else if (*p == '\n') { if (fputs("\\n", f) < 0) return -1; }
        else if (*p == '\r') { if (fputs("\\r", f) < 0) return -1; }
        else if (*p == '\t') { if (fputs("\\t", f) < 0) return -1; }
        else if (*p < 0x20) {
            char esc[8]; snprintf(esc, sizeof(esc), "\\u%04x", *p);
            if (fputs(esc, f) < 0) return -1;
        }
        else { if (fputc((int)*p, f) == EOF) return -1; }
    }
    if (fputc('"', f) == EOF) return -1;
    return 0;
}

int ca_write_installed_manifest(
    const char             *model_dir,
    const char             *model_id,
    const char             *version,
    const char             *repo,
    const ca_bundle_file_t *files,
    size_t                  files_count,
    int64_t                 installed_at_unix_ms)
{
    if (!model_dir || !model_id || !version) return -1;
    if (mkdir_p(model_dir) != 0) return -1;

    char path[1024];
    join_path(path, sizeof(path), model_dir, "installed.json");

    FILE *f = fopen(path, "wb");
    if (!f) return -1;

    int64_t total = 0;
    for (size_t i = 0; i < files_count; i++) {
        if (files[i].size_bytes > 0) total += files[i].size_bytes;
    }

    char ts[40];
    format_iso8601_utc(installed_at_unix_ms, ts, sizeof(ts));

    int err = 0;
    err |= (fputs("{\n  \"modelId\": ", f) < 0);
    err |= write_string_escaped(f, model_id);
    err |= (fputs(",\n  \"version\": ", f) < 0);
    err |= write_string_escaped(f, version);
    if (repo) {
        err |= (fputs(",\n  \"repo\": ", f) < 0);
        err |= write_string_escaped(f, repo);
    }
    err |= (fprintf(f, ",\n  \"totalBytes\": %lld", (long long)total) < 0);
    err |= (fputs(",\n  \"files\": [\n", f) < 0);
    for (size_t i = 0; i < files_count; i++) {
        err |= (fputs("    { \"name\": ", f) < 0);
        err |= write_string_escaped(f, files[i].name);
        err |= (fputs(", \"sha256\": ", f) < 0);
        err |= write_string_escaped(f, files[i].sha256);
        err |= (fprintf(f, ", \"sizeBytes\": %lld }", (long long)files[i].size_bytes) < 0);
        if (i + 1 < files_count) err |= (fputs(",\n", f) < 0);
        else err |= (fputc('\n', f) == EOF);
    }
    err |= (fputs("  ],\n  \"installedAtUtc\": \"", f) < 0);
    err |= (fputs(ts, f) < 0);
    err |= (fputs("\"\n}\n", f) < 0);

    int close_err = fclose(f);
    if (err || close_err) return -1;
    return 0;
}

/* ─────────────────────── JSON reader (minimal, schema-specific) ─────────────────────── */

static const char *skip_ws(const char *p) {
    while (*p && (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r')) p++;
    return p;
}

/* Parse a JSON string starting at *p (which must be at the opening "). Writes
 * up to cap-1 bytes (NUL terminated) to out. Advances *p past the closing ". */
static int parse_string(const char **pp, char *out, size_t cap) {
    const char *p = *pp;
    if (*p != '"') return -1;
    p++;
    size_t i = 0;
    while (*p && *p != '"') {
        char c = *p++;
        if (c == '\\') {
            char esc = *p++;
            if (esc == 'n') c = '\n';
            else if (esc == 'r') c = '\r';
            else if (esc == 't') c = '\t';
            else if (esc == '"') c = '"';
            else if (esc == '\\') c = '\\';
            else if (esc == '/') c = '/';
            else if (esc == 'u') {
                /* skip 4 hex chars; convert to ASCII fallback */
                if (!p[0] || !p[1] || !p[2] || !p[3]) return -1;
                p += 4;
                c = '?';
            }
        }
        if (i + 1 < cap) out[i++] = c;
    }
    if (*p != '"') return -1;
    p++;
    out[i] = 0;
    *pp = p;
    return 0;
}

static int parse_int64(const char **pp, int64_t *out) {
    const char *p = skip_ws(*pp);
    char *end = NULL;
    long long v = strtoll(p, &end, 10);
    if (end == p) return -1;
    *out = (int64_t)v;
    *pp = end;
    return 0;
}

/* Locate "key" in [start, end), positioning *after* the ':'. Returns NULL when
 * not found. */
static const char *find_key(const char *p, const char *key) {
    char needle[64];
    int n = snprintf(needle, sizeof(needle), "\"%s\"", key);
    if (n <= 0 || (size_t)n >= sizeof(needle)) return NULL;
    const char *q = strstr(p, needle);
    if (!q) return NULL;
    q += n;
    q = skip_ws(q);
    if (*q != ':') return NULL;
    q++;
    return skip_ws(q);
}

int ca_read_installed_manifest(const char *model_dir, ca_installed_manifest_t *out) {
    if (!model_dir || !out) return -1;
    char path[1024];
    join_path(path, sizeof(path), model_dir, "installed.json");
    FILE *f = fopen(path, "rb");
    if (!f) return -1;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (sz < 0 || sz > 1024 * 1024) { fclose(f); return -1; }
    char *buf = (char *)malloc((size_t)sz + 1);
    if (!buf) { fclose(f); return -1; }
    size_t nread = fread(buf, 1, (size_t)sz, f);
    fclose(f);
    buf[nread] = 0;

    char tmp[512];

    memset(out, 0, sizeof(*out));

    const char *p = find_key(buf, "modelId");
    if (!p || parse_string(&p, tmp, sizeof(tmp)) != 0) { free(buf); return -1; }
    out->model_id = strdup(tmp);

    p = find_key(buf, "version");
    if (!p || parse_string(&p, tmp, sizeof(tmp)) != 0) { free(buf); return -1; }
    out->version = strdup(tmp);

    p = find_key(buf, "repo");
    if (p && parse_string(&p, tmp, sizeof(tmp)) == 0) {
        out->repo = strdup(tmp);
    }

    p = find_key(buf, "totalBytes");
    int64_t total = 0;
    if (p) parse_int64(&p, &total);
    out->total_bytes = total;

    /* parse files array */
    p = find_key(buf, "files");
    if (!p || *p != '[') { free(buf); ca_installed_manifest_free(out); return -1; }
    p++;

    size_t cap = 8;
    out->files = (ca_bundle_file_t *)calloc(cap, sizeof(ca_bundle_file_t));
    out->files_count = 0;

    for (;;) {
        p = skip_ws(p);
        if (*p == ']') { p++; break; }
        if (*p != '{') break;
        const char *start = p;
        const char *end = strchr(start, '}');
        if (!end) break;
        size_t obj_len = (size_t)(end - start) + 1;
        char *obj = (char *)malloc(obj_len + 1);
        memcpy(obj, start, obj_len);
        obj[obj_len] = 0;

        char name[256] = {0}, sha[256] = {0};
        int64_t sz_bytes = 0;
        const char *q;
        q = find_key(obj, "name");
        if (q) parse_string(&q, name, sizeof(name));
        q = find_key(obj, "sha256");
        if (q) parse_string(&q, sha, sizeof(sha));
        q = find_key(obj, "sizeBytes");
        if (q) parse_int64(&q, &sz_bytes);

        if (out->files_count >= cap) {
            cap *= 2;
            out->files = (ca_bundle_file_t *)realloc(out->files, cap * sizeof(ca_bundle_file_t));
        }
        out->files[out->files_count].name = strdup(name);
        out->files[out->files_count].sha256 = strdup(sha);
        out->files[out->files_count].size_bytes = sz_bytes;
        out->files_count++;

        free(obj);
        p = end + 1;
        p = skip_ws(p);
        if (*p == ',') p++;
    }

    free(buf);
    return 0;
}

void ca_installed_manifest_free(ca_installed_manifest_t *m) {
    if (!m) return;
    free((void *)m->model_id);
    free((void *)m->version);
    free((void *)m->repo);
    for (size_t i = 0; i < m->files_count; i++) {
        free((void *)m->files[i].name);
        free((void *)m->files[i].sha256);
    }
    free(m->files);
    memset(m, 0, sizeof(*m));
}

/* ─────────────────────── upgrade detection ─────────────────────── */

static int sha_eq_ci(const char *a, const char *b) {
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b)) return 0;
        a++; b++;
    }
    return *a == 0 && *b == 0;
}

static void compare_bundle(
    const ca_bundle_file_t *installed, size_t inst_count,
    const ca_bundle_file_t *available, size_t avail_count,
    int *drift_out, int64_t *bytes_out)
{
    int drift = 0;
    int64_t bytes = 0;
    if (avail_count == 0) {
        *drift_out = 0; *bytes_out = 0; return;
    }
    for (size_t i = 0; i < avail_count; i++) {
        const ca_bundle_file_t *av = &available[i];
        const ca_bundle_file_t *match = NULL;
        for (size_t j = 0; j < inst_count; j++) {
            if (strcmp(installed[j].name, av->name) == 0) { match = &installed[j]; break; }
        }
        if (!match || !sha_eq_ci(match->sha256, av->sha256)) {
            drift = 1;
            bytes += av->size_bytes;
        }
    }
    *drift_out = drift;
    *bytes_out = bytes;
}

int ca_check_for_upgrades(
    const ca_model_registry_t *registry,
    const char                *storage_directory,
    int64_t                    now_unix_ms,
    ca_upgrade_info_t         *out,
    size_t                    *out_count)
{
    if (!registry || !storage_directory || !out || !out_count) return -1;
    *out_count = 0;

    for (size_t i = 0; i < registry->models_count; i++) {
        const ca_model_entry_t *e = &registry->models[i];
        char model_dir[1024];
        join_path(model_dir, sizeof(model_dir), storage_directory, e->name);
        if (!dir_exists(model_dir)) continue;

        char manifest_path[1024];
        join_path(manifest_path, sizeof(manifest_path), model_dir, "installed.json");
        int has_manifest = file_exists(manifest_path);

        if (!has_manifest) {
            out[*out_count].model_id = e->name;
            out[*out_count].installed_version = NULL;
            out[*out_count].available_version = e->version;
            out[*out_count].reason = CA_UPGRADE_UNKNOWN;
            out[*out_count].estimated_download_bytes = e->total_bytes;
            out[*out_count].detected_at_unix_ms = now_unix_ms;
            (*out_count)++;
            continue;
        }

        ca_installed_manifest_t m;
        if (ca_read_installed_manifest(model_dir, &m) != 0) continue;

        int version_changed = strcmp(m.version, e->version) != 0;
        int sha_changed = 0;
        int64_t drift_bytes = 0;
        compare_bundle(m.files, m.files_count, e->bundle_files, e->bundle_count,
                       &sha_changed, &drift_bytes);

        if (!version_changed && !sha_changed) {
            ca_installed_manifest_free(&m);
            continue;
        }

        ca_upgrade_reason_t reason = CA_UPGRADE_UNKNOWN;
        if (version_changed && sha_changed) reason = CA_UPGRADE_BOTH;
        else if (version_changed) reason = CA_UPGRADE_VERSION_CHANGED;
        else reason = CA_UPGRADE_SHA_CHANGED;

        /* Note: installed_version is freed when m is freed; copy now. The
         * test owns lifecycle by keeping the manifest alive. We dup so callers
         * can free the manifest immediately. */
        out[*out_count].model_id = e->name;
        out[*out_count].installed_version = strdup(m.version);
        out[*out_count].available_version = e->version;
        out[*out_count].reason = reason;
        out[*out_count].estimated_download_bytes = drift_bytes;
        out[*out_count].detected_at_unix_ms = now_unix_ms;
        (*out_count)++;

        ca_installed_manifest_free(&m);
    }
    return 0;
}
