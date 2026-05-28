#ifndef CIRCLE_AI_LANGUAGES_H
#define CIRCLE_AI_LANGUAGES_H

/*
 * languages.h — KnownLanguages registry: 20 BCP-47 language tags.
 * Matches fixtures/language_tags.json exactly.
 * Pure C11, no OS-specific headers.
 */

#include <stdbool.h>

/* ---------------------------------------------------------------------------
 * WritingSystem enum
 * Order matches fixtures/language_tags.json writingSystems array:
 *   ["Latin", "Arabic", "Ethiopic", "Han", "Devanagari"]
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_WS_LATIN      = 0,
    CA_WS_ARABIC     = 1,
    CA_WS_ETHIOPIC   = 2,
    CA_WS_HANZI      = 3,   /* Han / CJK */
    CA_WS_DEVANAGARI = 4,
    /* Extended — not in the 20-language registry but available for future use */
    CA_WS_HEBREW     = 5,
    CA_WS_CYRILLIC   = 6,
    CA_WS_OTHER      = 7
} ca_writing_system_t;

#define CA_LANGUAGE_COUNT 20

/* ---------------------------------------------------------------------------
 * LanguageTag
 * --------------------------------------------------------------------------- */

typedef struct {
    const char         *bcp_tag;        /* e.g. "en", "zu", "nso" */
    const char         *english_name;   /* e.g. "English"          */
    const char         *native_name;    /* UTF-8; e.g. "isiZulu"   */
    ca_writing_system_t writing_system;
    bool                is_rtl;
    const char         *primary_region; /* ISO 3166-1 alpha-2      */
} ca_language_tag_t;

/* ---------------------------------------------------------------------------
 * Registry access
 * --------------------------------------------------------------------------- */

/* Returns a pointer to the static array of 20 language tags. */
const ca_language_tag_t *ca_known_languages(void);

/* Returns CA_LANGUAGE_COUNT (20). */
int ca_language_count(void);

/* Linear scan by bcp_tag. Returns NULL if not found. */
const ca_language_tag_t *ca_find_language(const char *bcp_tag);

#endif /* CIRCLE_AI_LANGUAGES_H */
