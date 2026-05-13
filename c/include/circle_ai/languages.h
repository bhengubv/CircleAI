#ifndef CIRCLE_AI_LANGUAGES_H
#define CIRCLE_AI_LANGUAGES_H

#include <stdbool.h>

#define CA_LANGUAGE_COUNT 20

typedef enum {
    CA_WS_LATIN = 0,
    CA_WS_ARABIC,
    CA_WS_HANZI,
    CA_WS_DEVANAGARI,
    CA_WS_ETHIOPIC,
    CA_WS_HEBREW,
    CA_WS_CYRILLIC,
    CA_WS_OTHER
} ca_writing_system_t;

typedef struct {
    const char*        bcp_tag;
    const char*        english_name;
    const char*        native_name;
    ca_writing_system_t writing_system;
    bool               is_rtl;
    const char*        primary_region;
} ca_language_tag_t;

/* Returns a pointer to the static array of 20 language tags */
const ca_language_tag_t* ca_known_languages(void);
int ca_language_count(void);

/* Returns NULL if not found */
const ca_language_tag_t* ca_find_language(const char* bcp_tag);

#endif /* CIRCLE_AI_LANGUAGES_H */
