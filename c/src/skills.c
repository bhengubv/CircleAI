/*
 * skills.c — CircleAI.Skills (C11 port).
 *
 * InMemorySkillStore: details keyed by Id in a linear array; List/Search return
 * summaries ordered by Name (case-insensitive). Upsert auto-slugs a blank id.
 * Pack import walks an injected downloader's ParsedSkill records and upserts
 * each with a "pack:{name}" tag merged in.
 *
 * Pure C11 + libc. No pthreads.
 */

#include <stdio.h> /* snprintf */

#include "circle_ai/skills.h"
#include "circle_ai/security.h" /* ca_uuid_v4, CA_UUID_STR_LEN */
#include "board_common.h"

/* ── SkillDraft ─────────────────────────────────────────────────────────── */

void ca_skill_draft_free(ca_skill_draft_t *d) {
    if (!d) return;
    free(d->name);
    free(d->description);
    free(d->instructions);
    cab_strv_free(d->tags, d->tag_count);
    d->name = d->description = d->instructions = NULL;
    d->tags = NULL;
    d->tag_count = 0;
}

/* ── SkillSummary ───────────────────────────────────────────────────────── */

void ca_skill_summary_free(ca_skill_summary_t *s) {
    if (!s) return;
    free(s->id);
    free(s->name);
    free(s->description);
    cab_strv_free(s->tags, s->tag_count);
    s->id = s->name = s->description = NULL;
    s->tags = NULL;
    s->tag_count = 0;
}
void ca_skill_summary_free_array(ca_skill_summary_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_skill_summary_free(&arr[i]);
    free(arr);
}

/* ── SkillDetail ────────────────────────────────────────────────────────── */

void ca_skill_detail_free(ca_skill_detail_t *d) {
    if (!d) return;
    free(d->id);
    free(d->name);
    free(d->description);
    free(d->instructions);
    cab_strv_free(d->tags, d->tag_count);
    d->id = d->name = d->description = d->instructions = NULL;
    d->tags = NULL;
    d->tag_count = 0;
}
static bool detail_copy(ca_skill_detail_t *dst, const ca_skill_detail_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->source           = src->source;
    dst->last_modified_ms = src->last_modified_ms;
    dst->id           = cab_strdup_empty(src->id);
    dst->name         = cab_strdup_empty(src->name);
    dst->description  = cab_strdup_empty(src->description);
    dst->instructions = cab_strdup_empty(src->instructions);
    if (!dst->id || !dst->name || !dst->description || !dst->instructions) {
        ca_skill_detail_free(dst);
        return false;
    }
    if (!cab_strv_copy(&dst->tags, src->tags, src->tag_count)) {
        ca_skill_detail_free(dst);
        return false;
    }
    dst->tag_count = src->tag_count;
    return true;
}
static bool detail_to_summary(ca_skill_summary_t *dst,
                              const ca_skill_detail_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->source      = src->source;
    dst->id          = cab_strdup_empty(src->id);
    dst->name        = cab_strdup_empty(src->name);
    dst->description = cab_strdup_empty(src->description);
    if (!dst->id || !dst->name || !dst->description) {
        ca_skill_summary_free(dst);
        return false;
    }
    if (!cab_strv_copy(&dst->tags, src->tags, src->tag_count)) {
        ca_skill_summary_free(dst);
        return false;
    }
    dst->tag_count = src->tag_count;
    return true;
}

/* ── GenerateSlug ───────────────────────────────────────────────────────── */

/* Fresh 32-hex Guid("N") string. NULL on OOM. */
static char *guid_n(void) {
    char uuid[CA_UUID_STR_LEN];
    ca_uuid_v4(uuid); /* 36 chars with dashes */
    char *n = (char *)malloc(33);
    if (!n) return NULL;
    size_t j = 0;
    for (size_t i = 0; uuid[i] && j < 32; ++i)
        if (uuid[i] != '-') n[j++] = (char)tolower((unsigned char)uuid[i]);
    n[j] = '\0';
    return n;
}

char *ca_skill_generate_slug(const char *name) {
    if (cab_is_ws(name)) return guid_n();

    /* Trim + lowercase, whitespace-run -> '-', drop non [a-z0-9-], collapse
     * '--' runs, trim leading/trailing '-'. */
    size_t len = strlen(name);
    char *buf = (char *)malloc(len + 1);
    if (!buf) return NULL;
    size_t o = 0;
    bool in_space_run = false;
    /* find trimmed bounds */
    size_t start = 0, end = len;
    while (start < end && isspace((unsigned char)name[start])) start++;
    while (end > start && isspace((unsigned char)name[end - 1])) end--;

    for (size_t i = start; i < end; ++i) {
        unsigned char c = (unsigned char)name[i];
        if (isspace(c)) { in_space_run = true; continue; }
        char lc = (char)tolower(c);
        bool keep = (lc >= 'a' && lc <= 'z') || (lc >= '0' && lc <= '9') || lc == '-';
        if (in_space_run) { if (o > 0) buf[o++] = '-'; in_space_run = false; }
        if (keep) buf[o++] = lc;
        /* non-kept, non-space chars are dropped (Regex removes them) */
    }
    buf[o] = '\0';

    /* collapse '--'+ runs, trim trailing '-' */
    char *slug = (char *)malloc(o + 1);
    if (!slug) { free(buf); return NULL; }
    size_t so = 0;
    bool prev_dash = false;
    for (size_t i = 0; i < o; ++i) {
        if (buf[i] == '-') {
            if (prev_dash) continue;
            prev_dash = true;
            slug[so++] = '-';
        } else {
            prev_dash = false;
            slug[so++] = buf[i];
        }
    }
    /* trim leading/trailing '-' */
    while (so > 0 && slug[so - 1] == '-') so--;
    size_t lead = 0;
    while (lead < so && slug[lead] == '-') lead++;
    if (lead > 0) { memmove(slug, slug + lead, so - lead); so -= lead; }
    slug[so] = '\0';
    free(buf);

    if (so == 0) { free(slug); return guid_n(); }
    return slug;
}

/* ── InMemorySkillStore ─────────────────────────────────────────────────── */

struct ca_skill_store {
    ca_skill_detail_t *items;
    size_t             count, cap;
};

ca_skill_store_t *ca_skill_store_create(void) {
    return (ca_skill_store_t *)calloc(1, sizeof(ca_skill_store_t));
}
void ca_skill_store_destroy(ca_skill_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_skill_detail_free(&s->items[i]);
    free(s->items);
    free(s);
}

static ca_skill_detail_t *store_find(const ca_skill_store_t *s, const char *id) {
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->items[i].id, id)) return &s->items[i];
    return NULL;
}

/* Stable ascending sort of indices by Name (OrdinalIgnoreCase). */
static void store_sort_name(const ca_skill_store_t *s, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 &&
               cab_ci_cmp(s->items[idx[j - 1]].name, s->items[key].name) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

/* Build a summary array over collected indices (already ordered). */
static ca_skill_summary_t *store_summaries(const ca_skill_store_t *s,
                                           const size_t *idx, size_t n,
                                           size_t *out_count) {
    if (n == 0) { *out_count = 0; return NULL; }
    ca_skill_summary_t *out = (ca_skill_summary_t *)calloc(n, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!detail_to_summary(&out[i], &s->items[idx[i]])) {
            ca_skill_summary_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = n;
    return out;
}

ca_skill_summary_t *ca_skill_store_list(const ca_skill_store_t *s,
                                        size_t *out_count) {
    if (!out_count) return NULL;
    if (!s) { *out_count = (size_t)-1; return NULL; }
    if (s->count == 0) { *out_count = 0; return NULL; }
    size_t n = s->count;
    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) idx[i] = i;
    store_sort_name(s, idx, n);
    ca_skill_summary_t *out = store_summaries(s, idx, n, out_count);
    free(idx);
    return out;
}

bool ca_skill_store_get(const ca_skill_store_t *s, const char *id,
                        ca_skill_detail_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || cab_is_ws(id) || !out) return false;
    ca_skill_detail_t *d = store_find(s, id);
    if (!d) return false;
    return detail_copy(out, d);
}

static bool detail_matches_query(const ca_skill_detail_t *d, const char *q) {
    if (cab_ci_contains(d->name, q)) return true;
    if (cab_ci_contains(d->description, q)) return true;
    for (size_t i = 0; i < d->tag_count; ++i)
        if (cab_ci_contains(d->tags[i], q)) return true;
    return false;
}

ca_skill_summary_t *ca_skill_store_search(const ca_skill_store_t *s,
                                          const char *query, size_t *out_count) {
    if (!out_count) return NULL;
    if (!s) { *out_count = (size_t)-1; return NULL; }
    if (cab_is_ws(query) || s->count == 0) { *out_count = 0; return NULL; }

    /* C# trims the query before matching. */
    const char *q = query;
    while (*q && isspace((unsigned char)*q)) q++;
    size_t qend = strlen(q);
    while (qend > 0 && isspace((unsigned char)q[qend - 1])) qend--;
    char *trimmed = (char *)malloc(qend + 1);
    if (!trimmed) { *out_count = (size_t)-1; return NULL; }
    memcpy(trimmed, q, qend);
    trimmed[qend] = '\0';

    size_t *idx = (size_t *)malloc(s->count * sizeof(size_t));
    if (!idx) { free(trimmed); *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < s->count; ++i)
        if (detail_matches_query(&s->items[i], trimmed)) idx[n++] = i;
    free(trimmed);
    store_sort_name(s, idx, n);
    ca_skill_summary_t *out = store_summaries(s, idx, n, out_count);
    free(idx);
    return out;
}

int ca_skill_store_upsert(ca_skill_store_t *s, const char *id,
                          const ca_skill_draft_t *draft, int64_t now_ms,
                          ca_skill_detail_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || !draft) return -1;

    char *effective_id = NULL;
    if (cab_is_ws(id)) {
        effective_id = ca_skill_generate_slug(draft->name);
    } else {
        /* id.Trim() */
        const char *p = id;
        while (*p && isspace((unsigned char)*p)) p++;
        size_t e = strlen(p);
        while (e > 0 && isspace((unsigned char)p[e - 1])) e--;
        effective_id = (char *)malloc(e + 1);
        if (effective_id) { memcpy(effective_id, p, e); effective_id[e] = '\0'; }
    }
    if (!effective_id) return -1;

    ca_skill_detail_t detail;
    memset(&detail, 0, sizeof(detail));
    detail.id           = effective_id;
    detail.name         = (char *)draft->name;
    detail.description  = (char *)draft->description;
    detail.instructions = (char *)draft->instructions;
    detail.tags         = draft->tags;
    detail.tag_count    = draft->tag_count;
    detail.source       = CA_SKILL_SOURCE_INMEMORY;
    detail.last_modified_ms = now_ms;

    /* store (replace by id) */
    ca_skill_detail_t stored;
    if (!detail_copy(&stored, &detail)) { free(effective_id); return -1; }
    free(effective_id);

    ca_skill_detail_t *existing = store_find(s, stored.id);
    if (existing) {
        ca_skill_detail_free(existing);
        *existing = stored;
    } else {
        if (s->count == s->cap) {
            size_t nc = s->cap ? s->cap * 2 : 4;
            void *n = realloc(s->items, nc * sizeof(*s->items));
            if (!n) { ca_skill_detail_free(&stored); return -1; }
            s->items = (ca_skill_detail_t *)n;
            s->cap = nc;
        }
        s->items[s->count++] = stored;
        existing = &s->items[s->count - 1];
    }

    return detail_copy(out, existing) ? 0 : -1;
}

int ca_skill_store_delete(ca_skill_store_t *s, const char *id) {
    if (!s || cab_is_ws(id)) return -1;
    for (size_t i = 0; i < s->count; ++i) {
        if (cab_ord_eq(s->items[i].id, id)) {
            ca_skill_detail_free(&s->items[i]);
            s->items[i] = s->items[s->count - 1];
            s->count--;
            break;
        }
    }
    return 0;
}

size_t ca_skill_store_count(const ca_skill_store_t *s) {
    return s ? s->count : 0;
}

/* ── SkillPackSource + parsed ───────────────────────────────────────────── */

void ca_skill_pack_source_free(ca_skill_pack_source_t *p) {
    if (!p) return;
    free(p->name);
    free(p->repo_url);
    free(p->git_ref);
    free(p->license);
    free(p->skill_subdir);
    cab_strv_free(p->default_tags, p->default_tag_count);
    memset(p, 0, sizeof(*p));
}

void ca_skill_parsed_free(ca_skill_parsed_t *p) {
    if (!p) return;
    free(p->id);
    free(p->name);
    free(p->description);
    free(p->instructions);
    cab_strv_free(p->tags, p->tag_count);
    free(p->source_file_path);
    memset(p, 0, sizeof(*p));
}
void ca_skill_parsed_free_array(ca_skill_parsed_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_skill_parsed_free(&arr[i]);
    free(arr);
}

/* Lowercase a fresh copy of `s`. NULL on OOM. */
static char *to_lower_dup(const char *s) {
    if (!s) return cab_strdup_empty("");
    size_t n = strlen(s);
    char *o = (char *)malloc(n + 1);
    if (!o) return NULL;
    for (size_t i = 0; i < n; ++i) o[i] = (char)tolower((unsigned char)s[i]);
    o[n] = '\0';
    return o;
}

int ca_skill_pack_import_all(ca_skill_store_t *store,
                             const ca_skill_pack_downloader_t *downloader,
                             const ca_skill_pack_source_t *source,
                             int64_t now_ms, size_t *out_imported) {
    if (out_imported) *out_imported = 0;
    if (!store || !downloader || !downloader->download || !source || !out_imported)
        return -1;

    size_t pc = 0;
    ca_skill_parsed_t *parsed = downloader->download(downloader->ctx, source, &pc);
    if (pc == (size_t)-1) return -1; /* download failure */

    /* "pack:{name-lowercased}" tag to merge into each skill. */
    char *lname = to_lower_dup(source->name);
    if (!lname) { ca_skill_parsed_free_array(parsed, pc); return -1; }
    size_t pref = strlen("pack:") + strlen(lname) + 1;
    char *pack_tag = (char *)malloc(pref);
    if (!pack_tag) { free(lname); ca_skill_parsed_free_array(parsed, pc); return -1; }
    snprintf(pack_tag, pref, "pack:%s", lname);
    free(lname);

    int rc = 0;
    size_t imported = 0;
    for (size_t i = 0; i < pc && rc == 0; ++i) {
        ca_skill_parsed_t *ps = &parsed[i];
        /* merged tags = ps.tags + pack_tag, de-duped case-insensitive. */
        size_t max_tags = ps->tag_count + 1;
        char **tags = (char **)calloc(max_tags, sizeof(char *));
        if (!tags) { rc = -1; break; }
        size_t tc = 0;
        for (size_t t = 0; t < ps->tag_count; ++t) {
            bool dup = false;
            for (size_t k = 0; k < tc; ++k)
                if (cab_ci_eq(tags[k], ps->tags[t])) { dup = true; break; }
            if (dup) continue;
            tags[tc] = cab_strdup_empty(ps->tags[t]);
            if (!tags[tc]) { rc = -1; break; }
            tc++;
        }
        if (rc == 0) {
            bool dup = false;
            for (size_t k = 0; k < tc; ++k)
                if (cab_ci_eq(tags[k], pack_tag)) { dup = true; break; }
            if (!dup) {
                tags[tc] = cab_strdup(pack_tag);
                if (!tags[tc]) rc = -1; else tc++;
            }
        }
        if (rc == 0) {
            ca_skill_draft_t draft;
            memset(&draft, 0, sizeof(draft));
            draft.name         = ps->name;
            draft.description  = ps->description;
            draft.instructions = ps->instructions;
            draft.tags         = tags;
            draft.tag_count    = tc;
            ca_skill_detail_t detail;
            if (ca_skill_store_upsert(store, ps->id, &draft, now_ms, &detail) == 0) {
                ca_skill_detail_free(&detail);
                imported++;
            } else {
                rc = -1;
            }
        }
        cab_strv_free(tags, tc);
    }

    free(pack_tag);
    ca_skill_parsed_free_array(parsed, pc);
    if (rc == 0) *out_imported = imported;
    return rc;
}
