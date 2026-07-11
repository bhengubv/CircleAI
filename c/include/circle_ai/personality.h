#ifndef CIRCLE_AI_PERSONALITY_H
#define CIRCLE_AI_PERSONALITY_H

/*
 * personality.h — CircleAI.Personality (C11 port of Persona.cs / IPersonaProvider
 * .cs / JsonPersonaProvider.cs / IPersonaConflictResolver.cs / PersonaPromptBuilder
 * .cs). The user-DECLARED persona artefact (distinct from memory.h's learned
 * ca_persona_state_t).
 *
 *   Records : Persona(Id, DisplayName, Pronouns?, IdentityTags[], Values[],
 *                     Taboos[], PreferredLocale, VoicePreference?, Formality,
 *                     Privacy, CreatedAt, UpdatedAt);
 *             FormalityRange(Floor, Ceiling) — values "casual"/"neutral"/"formal";
 *             PrivacyLevel { Strict, Balanced, Open }.
 *   Create  : Persona.Create(displayName, locale) — new UUID id, balanced
 *             privacy, empty tags/values/taboos, range "casual".."formal",
 *             now timestamps.
 *   Provider: IPersonaProvider -> InMemoryPersonaProvider. Get(userId) (null when
 *             absent); Save(userId, persona) refreshes UpdatedAt to `now`;
 *             Exists(userId); ExportAll() -> every stored persona.
 *   Resolve : IPersonaConflictResolver.Resolve(declared, learned) ->
 *             DeclaredWinsResolver (clamp learned formality into the declared
 *             range; declared otherwise wins) and LearnedWinsResolver (pass the
 *             declared through).
 *   Prompt  : PersonaPromptBuilder.BuildSystemHint(persona) — "" when effectively
 *             default; otherwise a compact [Persona] block with every user string
 *             JSON-quoted (prompt-injection defence).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Id as a
 * UUID string; timestamps as int64 Unix ms UTC. Linear arrays, no pthreads.
 * Pure C11 + libc. Consumes memory.h's ca_persona_state_t / ca_formality_t.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "memory.h" /* ca_persona_state_t, ca_formality_t */

#ifdef __cplusplus
extern "C" {
#endif

/* PrivacyLevel. */
typedef enum {
    CA_PRIVACY_STRICT   = 0,
    CA_PRIVACY_BALANCED = 1,
    CA_PRIVACY_OPEN     = 2
} ca_privacy_level_t;

/* FormalityRange(Floor, Ceiling). */
typedef struct {
    char *floor;   /* owned, non-null: "casual"/"neutral"/"formal" */
    char *ceiling; /* owned, non-null */
} ca_formality_range_t;

/* Persona. */
typedef struct {
    char                *id;               /* owned, non-null UUID string */
    char                *display_name;     /* owned, non-null */
    char                *pronouns;         /* owned, or NULL */
    char               **identity_tags;    /* owned; NULL when tag_count == 0 */
    size_t               identity_tag_count;
    char               **values;           /* owned; NULL when value_count == 0 */
    size_t               value_count;
    char               **taboos;           /* owned; NULL when taboo_count == 0 */
    size_t               taboo_count;
    char                *preferred_locale; /* owned, non-null */
    char                *voice_preference; /* owned, or NULL */
    ca_formality_range_t formality;        /* owned */
    ca_privacy_level_t   privacy;
    int64_t              created_at_ms;
    int64_t              updated_at_ms;
} ca_persona_t;

void ca_persona_free(ca_persona_t *p);
void ca_persona_free_array(ca_persona_t *arr, size_t count);
/* Deep-copy `src` into `dst`. false on OOM (dst cleared). */
bool ca_persona_copy(ca_persona_t *dst, const ca_persona_t *src);

/* Persona.Create(displayName, locale) at time now_ms -> fresh persona into *out,
 * true; false on bad args (both required) / OOM. Stamps a new UUID id. */
bool ca_persona_create(const char *display_name, const char *locale,
                       int64_t now_ms, ca_persona_t *out);

/* ── IPersonaProvider -> InMemoryPersonaProvider ────────────────────────── */

typedef struct ca_persona_provider ca_persona_provider_t;

ca_persona_provider_t *ca_persona_provider_create(void); /* NULL on OOM */
void ca_persona_provider_destroy(ca_persona_provider_t *p);

/* Get(userId) -> fresh copy into *out, true; false when absent / bad args. */
bool ca_persona_provider_get(const ca_persona_provider_t *p, const char *user_id,
                             ca_persona_t *out);
/* Save(userId, persona) at now_ms — refreshes UpdatedAt, stores, returns the
 * saved record into *out (may be NULL to discard). 0 / -1 on bad args / OOM. */
int ca_persona_provider_save(ca_persona_provider_t *p, const char *user_id,
                             const ca_persona_t *persona, int64_t now_ms,
                             ca_persona_t *out);
/* Exists(userId). */
bool ca_persona_provider_exists(const ca_persona_provider_t *p, const char *user_id);
/* ExportAll() -> every stored persona. NULL + 0 empty; NULL + SIZE_MAX error. */
ca_persona_t *ca_persona_provider_export_all(const ca_persona_provider_t *p,
                                             size_t *out_count);

/* ── IPersonaConflictResolver ───────────────────────────────────────────── */

/* DeclaredWinsResolver.Resolve — clamp the learned formality into the declared
 * range; the declared persona is otherwise the source of truth. -> fresh persona
 * into *out, true; false on bad args / OOM. */
bool ca_persona_resolve_declared_wins(const ca_persona_t *declared,
                                      const ca_persona_state_t *learned,
                                      ca_persona_t *out);
/* LearnedWinsResolver.Resolve — passes the declared persona through unchanged. */
bool ca_persona_resolve_learned_wins(const ca_persona_t *declared,
                                     const ca_persona_state_t *learned,
                                     ca_persona_t *out);

/* ── PersonaPromptBuilder ───────────────────────────────────────────────── */

/* BuildSystemHint(persona) -> a fresh [Persona] block, or a fresh empty string
 * when the persona is effectively default. NULL on OOM / NULL persona. */
char *ca_persona_build_system_hint(const ca_persona_t *persona);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_PERSONALITY_H */
