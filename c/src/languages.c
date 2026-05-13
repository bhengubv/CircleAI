#include "circle_ai/languages.h"
#include <string.h>

static const ca_language_tag_t LANGUAGES[CA_LANGUAGE_COUNT] = {
    { "zu",  "Zulu",        "isiZulu",           CA_WS_LATIN,      0, "ZA" },
    { "st",  "Sotho",       "Sesotho",           CA_WS_LATIN,      0, "ZA" },
    { "af",  "Afrikaans",   "Afrikaans",         CA_WS_LATIN,      0, "ZA" },
    { "sw",  "Swahili",     "Kiswahili",         CA_WS_LATIN,      0, "KE" },
    { "ha",  "Hausa",       "Hausa",             CA_WS_LATIN,      0, "NG" },
    { "am",  "Amharic",     "\xe1\x8a\xa0\xe1\x88\x9b\xe1\x88\xad\xe1\x8a\x9b", CA_WS_ETHIOPIC,  0, "ET" },
    { "yo",  "Yoruba",      "Yor\xc3\xb9" "b\xc3\xa1", CA_WS_LATIN,  0, "NG" },
    { "ig",  "Igbo",        "Igbo",              CA_WS_LATIN,      0, "NG" },
    { "xh",  "Xhosa",       "isiXhosa",          CA_WS_LATIN,      0, "ZA" },
    { "nso", "Sepedi",      "Sesotho sa Leboa",  CA_WS_LATIN,      0, "ZA" },
    { "tn",  "Tswana",      "Setswana",          CA_WS_LATIN,      0, "ZA" },
    { "so",  "Somali",      "Soomaali",          CA_WS_LATIN,      0, "SO" },
    { "om",  "Oromo",       "Oromoo",            CA_WS_LATIN,      0, "ET" },
    { "ar",  "Arabic",      "\xd8\xa7\xd9\x84\xd8\xb9\xd8\xb1\xd8\xa8\xd9\x8a\xd8\xa9", CA_WS_ARABIC, 1, "SA" },
    { "en",  "English",     "English",           CA_WS_LATIN,      0, "US" },
    { "pt",  "Portuguese",  "Portugu\xc3\xaas",  CA_WS_LATIN,      0, "BR" },
    { "fr",  "French",      "Fran\xc3\xa7" "ais",   CA_WS_LATIN,      0, "FR" },
    { "es",  "Spanish",     "Espa\xc3\xb1ol",    CA_WS_LATIN,      0, "ES" },
    { "zh",  "Chinese",     "\xe4\xb8\xad\xe6\x96\x87", CA_WS_HANZI, 0, "CN" },
    { "hi",  "Hindi",       "\xe0\xa4\xb9\xe0\xa4\xbf\xe0\xa4\xa8\xe0\xa5\x8d\xe0\xa4\xa6\xe0\xa5\x80", CA_WS_DEVANAGARI, 0, "IN" },
};

const ca_language_tag_t* ca_known_languages(void) { return LANGUAGES; }
int ca_language_count(void) { return CA_LANGUAGE_COUNT; }

const ca_language_tag_t* ca_find_language(const char* bcp_tag) {
    int i;
    for (i = 0; i < CA_LANGUAGE_COUNT; i++) {
        if (strcmp(LANGUAGES[i].bcp_tag, bcp_tag) == 0) return &LANGUAGES[i];
    }
    return NULL;
}
