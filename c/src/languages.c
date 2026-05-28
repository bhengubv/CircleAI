/*
 * languages.c — KnownLanguages static registry (20 entries).
 *
 * All entries match fixtures/language_tags.json exactly:
 *   - english_name matches the "englishName" field
 *   - native_name  matches the "nativeName"  field (UTF-8 encoded)
 *   - primary_region matches the fixture (en -> "GB", pt -> "PT")
 *   - writing_system uses the enum order from languages.h:
 *       LATIN=0, ARABIC=1, ETHIOPIC=2, HANZI=3, DEVANAGARI=4
 *
 * Pure C11, no OS-specific headers.
 */

#include "circle_ai/languages.h"
#include <string.h>

/*
 * UTF-8 byte sequences for non-ASCII native names:
 *
 *   Amharic   "አማርኛ"  : \xe1\x8a\xa0\xe1\x88\x9b\xe1\x88\xad\xe1\x8a\x9b
 *   Yoruba    "Yorùbá" : Yor\xc3\xb9b\xc3\xa1
 *   Arabic    "العربية": \xd8\xa7\xd9\x84\xd8\xb9\xd8\xb1\xd8\xa8\xd9\x8a\xd8\xa9
 *   Mandarin  "中文"   : \xe4\xb8\xad\xe6\x96\x87
 *   Hindi     "हिन्दी"  : \xe0\xa4\xb9\xe0\xa4\xbf\xe0\xa4\xa8\xe0\xa5\x8d\xe0\xa4\xa6\xe0\xa5\x80
 *   Portuguese "Português": Portugu\xc3\xaas
 *   French    "Français" : Fran\xc3\xa7ais
 *   Spanish   "Español"  : Espa\xc3\xb1ol
 */

static const ca_language_tag_t LANGUAGES[CA_LANGUAGE_COUNT] = {
    /* 0 */ { "zu",  "isiZulu",     "isiZulu",
              CA_WS_LATIN,      0, "ZA" },
    /* 1 */ { "st",  "Sesotho",     "Sesotho",
              CA_WS_LATIN,      0, "ZA" },
    /* 2 */ { "af",  "Afrikaans",   "Afrikaans",
              CA_WS_LATIN,      0, "ZA" },
    /* 3 */ { "sw",  "Swahili",     "Kiswahili",
              CA_WS_LATIN,      0, "KE" },
    /* 4 */ { "ha",  "Hausa",       "Hausa",
              CA_WS_LATIN,      0, "NG" },
    /* 5 */ { "am",  "Amharic",
              "\xe1\x8a\xa0\xe1\x88\x9b\xe1\x88\xad\xe1\x8a\x9b",
              CA_WS_ETHIOPIC,   0, "ET" },
    /* 6 */ { "yo",  "Yoruba",
              "Yor\xc3\xb9\x62\xc3\xa1",   /* Yorùbá */
              CA_WS_LATIN,      0, "NG" },
    /* 7 */ { "ig",  "Igbo",        "Igbo",
              CA_WS_LATIN,      0, "NG" },
    /* 8 */ { "xh",  "isiXhosa",    "isiXhosa",
              CA_WS_LATIN,      0, "ZA" },
    /* 9 */ { "nso", "Sepedi",      "Sepedi",
              CA_WS_LATIN,      0, "ZA" },
    /*10 */ { "tn",  "Setswana",    "Setswana",
              CA_WS_LATIN,      0, "ZA" },
    /*11 */ { "so",  "Somali",      "Soomaali",
              CA_WS_LATIN,      0, "SO" },
    /*12 */ { "om",  "Oromo",       "Afaan Oromoo",
              CA_WS_LATIN,      0, "ET" },
    /*13 */ { "ar",  "Arabic",
              "\xd8\xa7\xd9\x84\xd8\xb9\xd8\xb1\xd8\xa8\xd9\x8a\xd8\xa9",
              CA_WS_ARABIC,     1, "SA" },
    /*14 */ { "en",  "English",     "English",
              CA_WS_LATIN,      0, "GB" },
    /*15 */ { "pt",  "Portuguese",
              "Portugu\xc3\xaas",           /* Português */
              CA_WS_LATIN,      0, "PT" },
    /*16 */ { "fr",  "French",
              "Fran\xc3\xa7\x61\x69\x73",  /* Français  */
              CA_WS_LATIN,      0, "FR" },
    /*17 */ { "es",  "Spanish",
              "Espa\xc3\xb1\x6f\x6c",      /* Español   */
              CA_WS_LATIN,      0, "ES" },
    /*18 */ { "zh",  "Mandarin",
              "\xe4\xb8\xad\xe6\x96\x87",  /* 中文      */
              CA_WS_HANZI,      0, "CN" },
    /*19 */ { "hi",  "Hindi",
              "\xe0\xa4\xb9\xe0\xa4\xbf\xe0\xa4\xa8\xe0\xa5\x8d\xe0\xa4\xa6\xe0\xa5\x80",
              CA_WS_DEVANAGARI, 0, "IN" },
};

const ca_language_tag_t *ca_known_languages(void) {
    return LANGUAGES;
}

int ca_language_count(void) {
    return CA_LANGUAGE_COUNT;
}

const ca_language_tag_t *ca_find_language(const char *bcp_tag) {
    for (int i = 0; i < CA_LANGUAGE_COUNT; i++) {
        if (strcmp(LANGUAGES[i].bcp_tag, bcp_tag) == 0) {
            return &LANGUAGES[i];
        }
    }
    return NULL;
}
