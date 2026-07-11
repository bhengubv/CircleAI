#ifndef CIRCLE_AI_LANGUAGES_PACKS_H
#define CIRCLE_AI_LANGUAGES_PACKS_H

/*
 * languages_packs.h — CircleAI.Languages.Language (C11 port).
 *
 * Ports the language-pack base contracts and all 8 concrete packs 1:1:
 *
 *   Base contracts (src/CircleAI.Languages.Language):
 *     ILanguagePack.cs               — LanguagePackMetadata, CulturalNote,
 *                                      ILanguagePack (Metadata, GetIdiomaticExpression,
 *                                      AdaptSystemPrompt, GetCulturalNotes, GetGreeting,
 *                                      GetLocaleHints).
 *     ILanguagePackRegistry.cs       — ILanguagePackRegistry (Register, GetByBcpTag,
 *                                      GetAvailablePacks, HasPack).
 *     DefaultLanguagePackRegistry.cs — in-memory registry keyed by Metadata.BcpTag
 *                                      (Ordinal / exact); replace on duplicate.
 *     LanguagePackHelpers.cs         — the additional OrdinalIgnoreCase registry
 *                                      `LanguagePackRegistry` (GetByExactTag,
 *                                      GetByLanguage prefix, ForRegion, AllTags) plus
 *                                      LocaleHintMerge.Merge (primary wins).
 *
 *   Concrete packs (src/CircleAI.Languages.Language, one file each):
 *     AfrikaansLanguagePack, AmharicLanguagePack, ArabicLanguagePack,
 *     HausaLanguagePack, PortugueseLanguagePack, SesothoLanguagePack,
 *     SwahiliLanguagePack, IsiZuluLanguagePack.
 *
 * Behaviour parity notes:
 *   - GetIdiomaticExpression(phrase): OrdinalIgnoreCase key lookup over the pack's
 *     idiom table; malloc'd copy of the translation, or NULL when absent.
 *   - AdaptSystemPrompt(base): the fixed template
 *       "You are a culturally aware AI assistant for <DisplayName> speakers. "
 *       "Respond in <DisplayName> (<NativeName>) unless instructed otherwise. "
 *       "Use natural, idiomatic expressions. Respect regional customs. \n\n<base>".
 *   - GetCulturalNotes(context): OrdinalIgnoreCase key lookup; the "greeting" note
 *     (only key present) or empty (count 0) otherwise.
 *   - GetGreeting(timeOfDay): C# lowercases via ToLowerInvariant() then returns the
 *     morning greeting when the input equals "morning" or "am", else the evening/
 *     farewell greeting. Replicated with an ASCII-lowercased compare.
 *   - GetLocaleHints(): a fixed {bcp_tag, region, rtl, date_format} map, returned as
 *     a key/value array.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free / *_free_array, deep-copy getters, errors via NULL / count
 * SIZE_MAX (0 when empty). Linear arrays, no hashtable, no pthreads. Dictionary
 * lookups (idioms, notes, locale hints) are OrdinalIgnoreCase linear scans over
 * static const arrays. String literals (including native-script UTF-8) are stored
 * as UTF-8 bytes directly in the .c file.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * Records — LanguagePackMetadata, CulturalNote, LocaleHint
 * =========================================================================== */

/* LanguagePackMetadata(BcpTag, DisplayName, NativeName, PrimaryRegion,
 * SpokenInRegions[], Version PackVersion). Version carried as major.minor. */
typedef struct {
    char   *bcp_tag;              /* owned, non-null */
    char   *display_name;        /* owned, non-null */
    char   *native_name;         /* owned, non-null (UTF-8, may be native script) */
    char   *primary_region;      /* owned, non-null */
    char  **spoken_in_regions;   /* owned array of owned strings */
    size_t  spoken_count;
    int     pack_version_major;
    int     pack_version_minor;
} ca_lang_pack_metadata_t;

/* Deep-free the owned fields of one metadata record (not the struct itself). */
void ca_lang_pack_metadata_free(ca_lang_pack_metadata_t *m);
/* Free an owned array of metadata records (each record's fields + the block). */
void ca_lang_pack_metadata_free_array(ca_lang_pack_metadata_t *arr, size_t count);

/* CulturalNote(Context, Guidance, Examples[]). */
typedef struct {
    char   *context;    /* owned, non-null */
    char   *guidance;   /* owned, non-null */
    char  **examples;   /* owned array of owned strings */
    size_t  examples_count;
} ca_cultural_note_t;

void ca_cultural_note_free(ca_cultural_note_t *n);
void ca_cultural_note_free_array(ca_cultural_note_t *arr, size_t count);

/* A single locale-hint key/value pair (one entry of GetLocaleHints()). */
typedef struct {
    char *key;    /* owned, non-null */
    char *value;  /* owned, non-null */
} ca_locale_hint_t;

void ca_locale_hint_free(ca_locale_hint_t *h);
void ca_locale_hint_free_array(ca_locale_hint_t *arr, size_t count);

/* ===========================================================================
 * ILanguagePack — opaque handle + accessors
 *
 * A pack handle is a process-lifetime singleton returned by the factory
 * accessors below; it is backed by static const data and is NOT freed by the
 * caller. All accessors deep-copy into caller-owned storage.
 * =========================================================================== */

typedef struct ca_language_pack ca_language_pack_t;

/* Metadata getter — writes a fresh owned copy into *out (deep-copy). Returns true
 * on success, false on bad args / OOM (with *out zeroed). Free *out with
 * ca_lang_pack_metadata_free. */
bool ca_language_pack_metadata(const ca_language_pack_t *pack,
                               ca_lang_pack_metadata_t *out);

/* GetIdiomaticExpression(phrase) -> malloc'd idiomatic translation, or NULL when
 * the phrase is absent (OrdinalIgnoreCase key lookup) or on bad args / OOM.
 * Caller frees with free(). */
char *ca_language_pack_idiomatic(const ca_language_pack_t *pack,
                                 const char *phrase);

/* AdaptSystemPrompt(base) -> malloc'd adapted prompt (fixed template with the
 * pack's DisplayName/NativeName wrapping base). NULL on bad args / OOM. A NULL
 * base is treated as the C# null interpolated into the template (empty). Caller
 * frees with free(). */
char *ca_language_pack_adapt_system_prompt(const ca_language_pack_t *pack,
                                           const char *base);

/* GetCulturalNotes(context) -> fresh owned array (*out_count). OrdinalIgnoreCase
 * key lookup: the matching note(s) (only "greeting" is defined) or empty. NULL +
 * *out_count 0 when no match; NULL + SIZE_MAX on bad args / OOM. Free with
 * ca_cultural_note_free_array. */
ca_cultural_note_t *ca_language_pack_cultural_notes(const ca_language_pack_t *pack,
                                                    const char *context,
                                                    size_t *out_count);

/* GetGreeting(timeOfDay) -> malloc'd greeting. Morning greeting when timeOfDay
 * (ASCII-lowercased) is "morning" or "am", else the evening/farewell greeting.
 * A NULL timeOfDay yields the evening/farewell greeting (no match). NULL only on
 * bad pack / OOM. Caller frees with free(). */
char *ca_language_pack_greeting(const ca_language_pack_t *pack,
                                const char *time_of_day);

/* GetLocaleHints() -> fresh owned array (*out_count) of {key,value} pairs. NULL +
 * SIZE_MAX on bad args / OOM (the map is never empty). Free with
 * ca_locale_hint_free_array. */
ca_locale_hint_t *ca_language_pack_locale_hints(const ca_language_pack_t *pack,
                                                size_t *out_count);

/* ===========================================================================
 * Factory accessors — one process-lifetime singleton per language.
 * The returned handle is backed by static const data; do NOT free it.
 * =========================================================================== */

const ca_language_pack_t *ca_language_pack_afrikaans(void);
const ca_language_pack_t *ca_language_pack_amharic(void);
const ca_language_pack_t *ca_language_pack_arabic(void);
const ca_language_pack_t *ca_language_pack_hausa(void);
const ca_language_pack_t *ca_language_pack_portuguese(void);
const ca_language_pack_t *ca_language_pack_sesotho(void);
const ca_language_pack_t *ca_language_pack_swahili(void);
const ca_language_pack_t *ca_language_pack_isizulu(void);

/* ===========================================================================
 * Registry — DefaultLanguagePackRegistry (Ordinal) and the OrdinalIgnoreCase
 * helper registry LanguagePackRegistry (via create_ci).
 *
 * Both share one opaque type. A plain registry keys by BcpTag with Ordinal
 * (exact byte) matching; a CI registry keys OrdinalIgnoreCase and additionally
 * supports _get_by_language (prefix), _for_region and _all_tags. The registry
 * holds borrowed pack pointers (the singletons above); destroy frees only the
 * registry's own storage, never the packs.
 * =========================================================================== */

typedef struct ca_language_pack_registry ca_language_pack_registry_t;

/* DefaultLanguagePackRegistry() — Ordinal (exact) BcpTag keying. NULL on OOM. */
ca_language_pack_registry_t *ca_language_pack_registry_create(void);
/* LanguagePackRegistry (helpers) — OrdinalIgnoreCase BcpTag keying. NULL on OOM. */
ca_language_pack_registry_t *ca_language_pack_registry_create_ci(void);
void ca_language_pack_registry_destroy(ca_language_pack_registry_t *reg);

/* Register(pack) — keys by pack's Metadata.BcpTag; replaces an existing entry with
 * the same key (Ordinal or OrdinalIgnoreCase per the registry's mode). Stores the
 * borrowed pack pointer. Returns 0 on success, -1 on bad args / OOM. */
int ca_language_pack_registry_register(ca_language_pack_registry_t *reg,
                                       const ca_language_pack_t *pack);

/* GetByBcpTag(bcpTag) — exact-key lookup (Ordinal, or OrdinalIgnoreCase on a CI
 * registry). Returns the borrowed pack, or NULL when absent / on bad args. On the
 * CI registry this mirrors GetByExactTag: a NULL/whitespace tag returns NULL. */
const ca_language_pack_t *ca_language_pack_registry_get_by_bcp_tag(
    const ca_language_pack_registry_t *reg, const char *bcp_tag);

/* HasPack(bcpTag) — ContainsKey. false on bad args. */
bool ca_language_pack_registry_has_pack(const ca_language_pack_registry_t *reg,
                                        const char *bcp_tag);

/* GetAvailablePacks() -> fresh owned metadata array (*out_count), one per
 * registered pack (registration order). NULL + 0 when empty; NULL + SIZE_MAX on
 * bad args / OOM. Free with ca_lang_pack_metadata_free_array. */
ca_lang_pack_metadata_t *ca_language_pack_registry_available_packs(
    const ca_language_pack_registry_t *reg, size_t *out_count);

/* GetByLanguage(langPrefix) — CI registry only. prefix = the substring of
 * langPrefix before the first '-'; returns the first registered pack whose BcpTag
 * StartsWith(prefix, OrdinalIgnoreCase). NULL when none / on a plain (Ordinal)
 * registry / on bad args. Borrowed pack. */
const ca_language_pack_t *ca_language_pack_registry_get_by_language(
    const ca_language_pack_registry_t *reg, const char *lang_prefix);

/* ForRegion(region) -> fresh owned metadata array (*out_count) for every
 * registered pack whose SpokenInRegions contains region (OrdinalIgnoreCase).
 * region must be non-null / non-whitespace (C# throws otherwise): a bad region or
 * a plain (Ordinal) registry yields NULL + SIZE_MAX. NULL + 0 when no matches.
 * Free with ca_lang_pack_metadata_free_array. */
ca_lang_pack_metadata_t *ca_language_pack_registry_for_region(
    const ca_language_pack_registry_t *reg, const char *region,
    size_t *out_count);

/* AllTags() -> fresh owned array of BcpTag strings ascending (Ordinal sort).
 * CI registry only. NULL + 0 when empty; NULL + SIZE_MAX on a plain registry /
 * bad args / OOM. Free with ca_locale_hint_free_array-style: use
 * ca_language_pack_registry_free_tags. */
char **ca_language_pack_registry_all_tags(
    const ca_language_pack_registry_t *reg, size_t *out_count);

/* Free a tag array returned by ca_language_pack_registry_all_tags. */
void ca_language_pack_registry_free_tags(char **tags, size_t count);

/* ===========================================================================
 * LocaleHintMerge.Merge(primary, secondary) — OrdinalIgnoreCase-keyed merge with
 * primary winning. Result starts as a copy of secondary (dedup CI), then every
 * primary entry is overlaid (replacing a CI-equal secondary key, else appended).
 * Returns a fresh owned array (*out_count). NULL + SIZE_MAX on bad args / OOM;
 * NULL + 0 when the merged result is empty. Free with ca_locale_hint_free_array.
 * =========================================================================== */
ca_locale_hint_t *ca_locale_hint_merge(const ca_locale_hint_t *primary,
                                       size_t nprimary,
                                       const ca_locale_hint_t *secondary,
                                       size_t nsecondary,
                                       size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_LANGUAGES_PACKS_H */
