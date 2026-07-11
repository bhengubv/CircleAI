#ifndef CIRCLE_AI_KNOWLEDGE_H
#define CIRCLE_AI_KNOWLEDGE_H

/*
 * knowledge.h — CircleAI.Knowledge (C11 port of KnowledgeNote.cs /
 * YamlFrontmatter.cs / IKnowledgeStore.cs / FileSystemKnowledgeStore.cs /
 * MarkdownEpisodicMemoryStore.cs).
 *
 *   Record  : KnowledgeNote(Id, Title, BodyMarkdown, Frontmatter{}, Tags[],
 *                           CreatedAt, UpdatedAt).
 *   YAML    : flat-only frontmatter Write/Read — well-known keys id/title/
 *             created_at/updated_at/tags merged in on ToFileText; quoting +
 *             escaping ported faithfully. Read rejects nesting / lists / flow-
 *             style. (Timestamps round-trip as int64 Unix-ms decimal strings —
 *             the C tree carries time as Unix ms rather than .NET ISO text.)
 *   Store   : IKnowledgeStore -> InMemoryKnowledgeStore. Get(id); Save(note)
 *             refreshes UpdatedAt to `now`; Delete(id); SearchByTag(tag)
 *             (OrdinalIgnoreCase Tags membership); EnumerateAll().
 *   Episodic: MarkdownEpisodicMemoryStore over an IKnowledgeStore. Add(entry)
 *             maps a ca_episodic_entry_t to a note (frontmatter episode_id /
 *             recorded_at / app_context / embedding(+dims) / tag_<k>; body
 *             "## User\n\n...## Assistant\n\n..."); Search(queryEmbedding, topK)
 *             (dot-product ranking, recency when no query); GetRecent(count);
 *             Count(); PruneOlderThan(cutoff).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Id as a
 * UUID string; timestamps as int64 Unix ms UTC. Linear arrays, no pthreads.
 * Pure C11 + libc (+ libm). Consumes memory_brain.h's ca_episodic_entry_t.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "memory_brain.h" /* ca_episodic_entry_t */

#ifdef __cplusplus
extern "C" {
#endif

/* key/value pair for note frontmatter. */
typedef struct { char *key; char *value; } ca_knowledge_kv_t;

/* KnowledgeNote(Id, Title, BodyMarkdown, Frontmatter{}, Tags[], CreatedAt,
 * UpdatedAt). Frontmatter holds only the USER keys (well-known keys are merged
 * in by ToFileText and stripped by ParseFile). */
typedef struct {
    char              *id;            /* owned, non-null UUID string */
    char              *title;         /* owned, non-null */
    char              *body_markdown; /* owned, non-null */
    ca_knowledge_kv_t *frontmatter;   /* owned; NULL when frontmatter_count == 0 */
    size_t             frontmatter_count;
    char             **tags;          /* owned; NULL when tag_count == 0 */
    size_t             tag_count;
    int64_t            created_at_ms;
    int64_t            updated_at_ms;
} ca_knowledge_note_t;

void ca_knowledge_note_free(ca_knowledge_note_t *n);
void ca_knowledge_note_free_array(ca_knowledge_note_t *arr, size_t count);
bool ca_knowledge_note_copy(ca_knowledge_note_t *dst, const ca_knowledge_note_t *src);

/* ToFileText -> a fresh serialised note (frontmatter block + body). NULL on OOM
 * / a frontmatter key with an invalid character. */
char *ca_knowledge_note_to_file_text(const ca_knowledge_note_t *note);
/* ParseFile -> fresh note into *out, true; false on a malformed document
 * (missing/invalid id, bad frontmatter, nesting/lists). */
bool ca_knowledge_note_parse_file(const char *text, ca_knowledge_note_t *out);

/* ── IKnowledgeStore -> InMemoryKnowledgeStore ──────────────────────────── */

typedef struct ca_knowledge_store ca_knowledge_store_t;

ca_knowledge_store_t *ca_knowledge_store_create(void); /* NULL on OOM */
void ca_knowledge_store_destroy(ca_knowledge_store_t *s);

/* Get(id) -> fresh copy into *out, true; false when absent / bad args. */
bool ca_knowledge_store_get(const ca_knowledge_store_t *s, const char *id,
                            ca_knowledge_note_t *out);
/* Save(note) at now_ms — refreshes UpdatedAt, stores (Id keyed), returns the
 * saved record into *out (may be NULL). 0 / -1 on bad args / OOM. */
int ca_knowledge_store_save(ca_knowledge_store_t *s, const ca_knowledge_note_t *note,
                            int64_t now_ms, ca_knowledge_note_t *out);
/* Delete(id) — no-op when absent. 0 / -1 on bad args. */
int ca_knowledge_store_delete(ca_knowledge_store_t *s, const char *id);
/* SearchByTag(tag) — notes carrying `tag` (OrdinalIgnoreCase). NULL + 0 empty;
 * NULL + SIZE_MAX on error (tag required). */
ca_knowledge_note_t *ca_knowledge_store_search_by_tag(const ca_knowledge_store_t *s,
                                                      const char *tag,
                                                      size_t *out_count);
/* EnumerateAll() insertion order. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_knowledge_note_t *ca_knowledge_store_enumerate_all(const ca_knowledge_store_t *s,
                                                      size_t *out_count);

/* ── MarkdownEpisodicMemoryStore over an IKnowledgeStore ─────────────────── */

typedef struct ca_markdown_episodic_store ca_markdown_episodic_store_t;

/* Create over a knowledge store (borrowed; must outlive the episodic store).
 * NULL on a NULL store / OOM. */
ca_markdown_episodic_store_t *ca_markdown_episodic_store_create(ca_knowledge_store_t *store);
void ca_markdown_episodic_store_destroy(ca_markdown_episodic_store_t *s);

/* Add(entry) at now_ms (used only for the note timestamps if entry has none).
 * 0 / -1 on bad args / OOM. */
int ca_markdown_episodic_store_add(ca_markdown_episodic_store_t *s,
                                   const ca_episodic_entry_t *entry, int64_t now_ms);
/* Search(queryEmbedding, topK) — dot-product ranking over entries whose embedding
 * dimension matches; recency (RecordedAt desc) when queryEmbedding is NULL/empty.
 * NULL + 0 empty; NULL + SIZE_MAX on error (top_k > 0). */
ca_episodic_entry_t *ca_markdown_episodic_store_search(const ca_markdown_episodic_store_t *s,
                                                       const float *query_embedding,
                                                       size_t query_len, int top_k,
                                                       size_t *out_count);
/* GetRecent(count) — RecordedAt desc, take count. NULL + 0 empty; SIZE_MAX err. */
ca_episodic_entry_t *ca_markdown_episodic_store_get_recent(const ca_markdown_episodic_store_t *s,
                                                           int count, size_t *out_count);
/* Count() — total entries. -1 on bad args. */
long ca_markdown_episodic_store_count(const ca_markdown_episodic_store_t *s);
/* PruneOlderThan(cutoffMs) — deletes entries with RecordedAt < cutoff. Returns
 * the number removed, or -1 on bad args. */
long ca_markdown_episodic_store_prune_older_than(ca_markdown_episodic_store_t *s,
                                                 int64_t cutoff_ms);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_KNOWLEDGE_H */
