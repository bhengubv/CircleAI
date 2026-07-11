#ifndef CIRCLE_AI_SKILLS_H
#define CIRCLE_AI_SKILLS_H

/*
 * skills.h — CircleAI.Skills (C11 port of SkillSource.cs / ISkillStore.cs /
 * SkillSummary.cs / SkillDetail.cs / SkillDraft.cs / InMemorySkillStore.cs +
 * the skill-pack-download seam SkillPackSource / IPackDownloader).
 *
 *   Enum    : SkillSource { File, InMemory, Remote }.
 *   Records : SkillDraft(Name, Description, Instructions, Tags);
 *             SkillSummary(Id, Name, Description, Tags, SkillSource Source);
 *             SkillDetail(Id, Name, Description, Instructions, Tags,
 *                         SkillSource Source, DateTimeOffset LastModified);
 *             SkillPackSource(Name, RepoUrl, GitRef, License, SkillSubdir,
 *                             EstimatedSkillCount, IsDefaultEnabled, DefaultTags);
 *             ParsedSkill(Id, Name, Description, Instructions, Tags,
 *                         SourceFilePath).
 *   Store   : ISkillStore -> InMemorySkillStore — List() summaries ordered by
 *               Name (case-insensitive) asc; Get(id) -> detail?; Search(query)
 *               summaries whose Name/Description/Tags contain query (case-
 *               insensitive substring), ordered by Name asc (empty query ->
 *               empty); Upsert(id?, draft) — id blank auto-slugs from Name,
 *               Source InMemory, stamps now; Delete(id). GenerateSlug exposed.
 *   Pack    : IPackDownloader (vtable) — Download(source) yields ParsedSkill
 *               records (the concrete git/HTTP downloader is host-injected).
 *               ImportAll(store, downloader, source, now) upserts each parsed
 *               skill (tags merged with "pack:{name}" lowercased, de-duped
 *               case-insensitive) and returns the count imported.
 *
 * Upsert stamps LastModified from a caller-supplied now (the C# reads UtcNow).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX.
 * LastModified as int64 Unix ms UTC. Linear arrays, no pthreads. Pure C11+libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    CA_SKILL_SOURCE_FILE     = 0,
    CA_SKILL_SOURCE_INMEMORY = 1,
    CA_SKILL_SOURCE_REMOTE   = 2
} ca_skill_source_t;

/* SkillDraft(Name, Description, Instructions, Tags). */
typedef struct {
    char  *name;         /* owned, non-null */
    char  *description;  /* owned, non-null */
    char  *instructions; /* owned, non-null */
    char **tags;         /* owned array (tag_count) */
    size_t tag_count;
} ca_skill_draft_t;

void ca_skill_draft_free(ca_skill_draft_t *d);

/* SkillSummary(Id, Name, Description, Tags, Source). */
typedef struct {
    char             *id;          /* owned, non-null */
    char             *name;        /* owned, non-null */
    char             *description; /* owned, non-null */
    char            **tags;        /* owned array (tag_count) */
    size_t            tag_count;
    ca_skill_source_t source;
} ca_skill_summary_t;

void ca_skill_summary_free(ca_skill_summary_t *s);
void ca_skill_summary_free_array(ca_skill_summary_t *arr, size_t count);

/* SkillDetail(Id, Name, Description, Instructions, Tags, Source, LastModified). */
typedef struct {
    char             *id;           /* owned, non-null */
    char             *name;         /* owned, non-null */
    char             *description;  /* owned, non-null */
    char             *instructions; /* owned, non-null */
    char            **tags;         /* owned array (tag_count) */
    size_t            tag_count;
    ca_skill_source_t source;
    int64_t           last_modified_ms;
} ca_skill_detail_t;

void ca_skill_detail_free(ca_skill_detail_t *d);

/* GenerateSlug(name): lowercase, spaces->'-', strip non [a-z0-9-], collapse
 * '-', trim '-'. Empty result -> a fresh 32-hex Guid("N"). Writes into a fresh
 * owned string; NULL on OOM. */
char *ca_skill_generate_slug(const char *name);

/* ── ISkillStore -> InMemorySkillStore ──────────────────────────────────── */

typedef struct ca_skill_store ca_skill_store_t;

ca_skill_store_t *ca_skill_store_create(void); /* NULL on OOM */
void ca_skill_store_destroy(ca_skill_store_t *s);

/* List() -> fresh owned summary array (*out_count) ordered by Name (case-
 * insensitive) asc. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_skill_summary_t *ca_skill_store_list(const ca_skill_store_t *s,
                                        size_t *out_count);
/* Get(id) -> fresh detail into *out, true; false on miss / bad args (id
 * required, non-whitespace). */
bool ca_skill_store_get(const ca_skill_store_t *s, const char *id,
                        ca_skill_detail_t *out);
/* Search(query) -> summaries matching (case-insensitive substring in Name/
 * Description/Tags), ordered by Name asc. NULL + 0 for empty query / no match;
 * NULL + SIZE_MAX on error. */
ca_skill_summary_t *ca_skill_store_search(const ca_skill_store_t *s,
                                          const char *query, size_t *out_count);
/* Upsert(id?, draft, now_ms) — id NULL/blank auto-slugs from draft.Name. Fills
 * *out with the resulting detail (owned). 0 on success, -1 on bad args (null
 * draft) or OOM. */
int ca_skill_store_upsert(ca_skill_store_t *s, const char *id,
                          const ca_skill_draft_t *draft, int64_t now_ms,
                          ca_skill_detail_t *out);
/* Delete(id) — no-op when absent. 0 / -1 on bad args (id required). */
int ca_skill_store_delete(ca_skill_store_t *s, const char *id);
/* Count (diagnostic). */
size_t ca_skill_store_count(const ca_skill_store_t *s);

/* ── SkillPackSource + IPackDownloader ──────────────────────────────────── */

/* SkillPackSource(Name, RepoUrl, GitRef, License, SkillSubdir,
 * EstimatedSkillCount, IsDefaultEnabled, DefaultTags). */
typedef struct {
    char  *name;         /* owned, non-null */
    char  *repo_url;     /* owned, non-null */
    char  *git_ref;      /* owned, non-null (default "main") */
    char  *license;      /* owned, non-null (default "unknown") */
    char  *skill_subdir; /* owned, non-null (default "") */
    int    estimated_skill_count;
    bool   is_default_enabled;
    char **default_tags; /* owned array (default_tag_count); may be empty/NULL */
    size_t default_tag_count;
} ca_skill_pack_source_t;

void ca_skill_pack_source_free(ca_skill_pack_source_t *p);

/* ParsedSkill(Id, Name, Description, Instructions, Tags, SourceFilePath). */
typedef struct {
    char  *id;               /* owned, non-null */
    char  *name;             /* owned, non-null */
    char  *description;      /* owned, non-null */
    char  *instructions;     /* owned, non-null */
    char **tags;             /* owned array (tag_count) */
    size_t tag_count;
    char  *source_file_path; /* owned, non-null */
} ca_skill_parsed_t;

void ca_skill_parsed_free(ca_skill_parsed_t *p);
void ca_skill_parsed_free_array(ca_skill_parsed_t *arr, size_t count);

/* Download(source) -> fresh owned ParsedSkill array (*out_count). Injected: the
 * concrete git/HTTP downloader lives in the host. NULL + SIZE_MAX on error. */
typedef ca_skill_parsed_t *(*ca_skill_download_fn)(
    void *ctx, const ca_skill_pack_source_t *source, size_t *out_count);

typedef struct {
    ca_skill_download_fn download;
    void                *ctx;
} ca_skill_pack_downloader_t;

/* ImportAll(store, downloader, source, now_ms): download the pack, upsert each
 * parsed skill (tags merged with "pack:{name-lowercased}", de-duped case-
 * insensitive), return the count imported via *out_imported. 0 on success,
 * -1 on bad args / download failure / OOM. */
int ca_skill_pack_import_all(ca_skill_store_t *store,
                             const ca_skill_pack_downloader_t *downloader,
                             const ca_skill_pack_source_t *source,
                             int64_t now_ms, size_t *out_imported);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SKILLS_H */
