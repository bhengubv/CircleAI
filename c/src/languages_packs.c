/*
 * languages_packs.c — CircleAI.Languages.Language (C11 port).
 *
 * Base contracts (LanguagePackMetadata, CulturalNote, ILanguagePack,
 * DefaultLanguagePackRegistry, the OrdinalIgnoreCase LanguagePackRegistry helper,
 * LocaleHintMerge) + all 8 concrete packs (Afrikaans, Amharic, Arabic, Hausa,
 * Portuguese, Sesotho, Swahili, isiZulu).
 *
 * Native-script string literals are stored as UTF-8 bytes directly in this file.
 * Pure C11 + libc. Linear arrays (no hashtable), no pthreads.
 */

#include "circle_ai/languages_packs.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>

/* ── shared helpers (mirrors media.c md_*) ──────────────────────────────────*/

static char *lp_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *lp_strdup_empty(const char *s) { return lp_strdup(s ? s : ""); }

/* string.IsNullOrWhiteSpace (ASCII whitespace). */
static bool lp_is_ws(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p)
        if (!isspace((unsigned char)*p)) return false;
    return true;
}

/* OrdinalIgnoreCase full-string comparison (StringComparer.OrdinalIgnoreCase). */
static int lp_ci_cmp(const char *a, const char *b) {
    const unsigned char *x = (const unsigned char *)a;
    const unsigned char *y = (const unsigned char *)b;
    for (;; ++x, ++y) {
        int ca = tolower(*x), cb = tolower(*y);
        if (ca != cb) return ca - cb;
        if (ca == 0) return 0;
    }
}
static bool lp_ci_eq(const char *a, const char *b) {
    if (!a || !b) return false;
    return lp_ci_cmp(a, b) == 0;
}

/* Ordinal (byte) equality. */
static bool lp_ord_eq(const char *a, const char *b) {
    if (!a || !b) return false;
    return strcmp(a, b) == 0;
}

/* OrdinalIgnoreCase StartsWith(prefix). */
static bool lp_ci_starts_with(const char *s, const char *prefix) {
    if (!s || !prefix) return false;
    const unsigned char *x = (const unsigned char *)s;
    const unsigned char *p = (const unsigned char *)prefix;
    for (; *p; ++x, ++p) {
        if (tolower(*x) != tolower(*p)) return false;
    }
    return true; /* empty prefix matches everything (C# StartsWith("")) */
}

/* ===========================================================================
 * Static const pack data
 *
 * Each pack is a fixed table of idioms, a morning/evening greeting, the single
 * "greeting" cultural note, and four locale hints. The opaque handle IS this
 * struct; the factory accessors return a pointer to one process-lifetime const
 * instance per language.
 * =========================================================================== */

typedef struct { const char *en; const char *tr; } lp_idiom_t;
typedef struct { const char *key; const char *value; } lp_kv_t;

struct ca_language_pack {
    const char *bcp_tag;
    const char *display_name;
    const char *native_name;
    const char *primary_region;
    const char *const *spoken_in;      /* array of spoken_count region strings */
    size_t             spoken_count;
    int                ver_major;
    int                ver_minor;

    const lp_idiom_t  *idioms;
    size_t             idiom_count;

    const char        *greeting_morning; /* "morning"/"am" */
    const char        *greeting_other;   /* everything else */

    /* The single cultural note keyed "greeting". */
    const char        *note_context;     /* "greeting" */
    const char        *note_guidance;
    const char *const *note_examples;
    size_t             note_example_count;

    const lp_kv_t     *locale_hints;
    size_t             locale_hint_count;
};

/* ── Afrikaans ──────────────────────────────────────────────────────────────*/
static const char *const af_spoken[] = { "ZA", "NA" };
static const lp_idiom_t af_idioms[] = {
    { "hello", "Hallo" },
    { "good morning", "Goeie m\xC3\xB4re" },
    { "good afternoon", "Goeie middag" },
    { "good evening", "Goeie naand" },
    { "goodbye", "Totsiens" },
    { "thank you", "Dankie" },
    { "please", "Asseblief" },
    { "yes", "Ja" },
    { "no", "Nee" },
    { "sorry", "Jammer" },
    { "how are you", "Hoe gaan dit" },
    { "I am fine", "Dit gaan goed" },
    { "water", "water" },
    { "food", "kos" },
    { "family", "familie" },
    { "friend", "vriend" },
    { "love", "liefde" },
    { "mother", "ma" },
    { "father", "pa" },
    { "child", "kind" },
};
static const char *const af_note_examples[] = { "Goeie m\xC3\xB4re", "Totsiens" };
static const lp_kv_t af_locale[] = {
    { "bcp_tag", "af" }, { "region", "ZA" }, { "rtl", "false" },
    { "date_format", "dd/MM/yyyy" },
};
static const ca_language_pack_t k_af = {
    "af", "Afrikaans", "Afrikaans", "ZA", af_spoken, 2, 1, 0,
    af_idioms, sizeof(af_idioms) / sizeof(af_idioms[0]),
    "Goeie m\xC3\xB4re", "Totsiens",
    "greeting", "Use 'Goeie m\xC3\xB4re' in the morning. Show respect to elders.",
    af_note_examples, 2,
    af_locale, sizeof(af_locale) / sizeof(af_locale[0]),
};

/* ── Amharic ────────────────────────────────────────────────────────────────*/
static const char *const am_spoken[] = { "ET" };
static const lp_idiom_t am_idioms[] = {
    { "hello", "\xE1\x88\xB0\xE1\x88\x8B\xE1\x88\x9D" },
    { "hello (respectful)", "\xE1\x8C\xA4\xE1\x8A\x93 \xE1\x8B\xAD\xE1\x88\xB5\xE1\x8C\xA5\xE1\x88\x8D\xE1\x8A\x9D" },
    { "good morning", "\xE1\x8A\xA5\xE1\x8A\x95\xE1\x8B\xB0\xE1\x88\x9D\xE1\x8A\x95 \xE1\x8A\xA0\xE1\x8B\xB0\xE1\x88\xAD\xE1\x8A\x8D" },
    { "good evening", "\xE1\x88\x98\xE1\x88\x8D\xE1\x8A\xAB\xE1\x88\x9D \xE1\x88\x9D\xE1\x88\xB7\xE1\x89\xB5" },
    { "goodbye", "\xE1\x89\xBB\xE1\x8B\x8D" },
    { "thank you", "\xE1\x8A\xA0\xE1\x88\x98\xE1\x88\xB0\xE1\x8C\x8D\xE1\x8A\x93\xE1\x88\x88\xE1\x88\x81" },
    { "please", "\xE1\x8A\xA5\xE1\x89\xA3\xE1\x8A\xAD\xE1\x88\x85" },
    { "yes", "\xE1\x8A\xA0\xE1\x8B\x8E" },
    { "no", "\xE1\x8A\xA0\xE1\x8B\xAD" },
    { "sorry", "\xE1\x8B\xAD\xE1\x89\x85\xE1\x88\xAD\xE1\x89\xB3" },
    { "how are you", "\xE1\x8A\xA5\xE1\x8A\x95\xE1\x8B\xB4\xE1\x89\xB5 \xE1\x8A\x90\xE1\x88\x85" },
    { "I am fine", "\xE1\x8B\xB0\xE1\x88\x85\xE1\x8A\x93 \xE1\x8A\x90\xE1\x8A\x9D" },
    { "water", "\xE1\x8B\x8D\xE1\x88\x83" },
    { "food", "\xE1\x88\x9D\xE1\x8C\x8D\xE1\x89\xA5" },
    { "family", "\xE1\x89\xA4\xE1\x89\xB0\xE1\x88\xB0\xE1\x89\xA5" },
    { "friend", "\xE1\x8C\x93\xE1\x8B\xB0\xE1\x8A\x9B" },
    { "love", "\xE1\x8D\x8D\xE1\x89\x85\xE1\x88\xAD" },
    { "mother", "\xE1\x8A\xA5\xE1\x8A\x93\xE1\x89\xB5" },
    { "father", "\xE1\x8A\xA0\xE1\x89\xA3\xE1\x89\xB5" },
    { "child", "\xE1\x88\x8D\xE1\x8C\x85" },
};
static const char *const am_note_examples[] = {
    "\xE1\x8C\xA4\xE1\x8A\x93 \xE1\x8B\xAD\xE1\x88\xB5\xE1\x8C\xA5\xE1\x88\x8D\xE1\x8A\x9D",
    "\xE1\x88\x98\xE1\x88\x8D\xE1\x8A\xAB\xE1\x88\x9D \xE1\x88\x9D\xE1\x88\xB7\xE1\x89\xB5",
};
static const lp_kv_t am_locale[] = {
    { "bcp_tag", "am" }, { "region", "ET" }, { "rtl", "false" },
    { "date_format", "dd/MM/yyyy" },
};
static const ca_language_pack_t k_am = {
    "am", "Amharic", "\xE1\x8A\xA0\xE1\x88\x9B\xE1\x88\xAD\xE1\x8A\x9B", "ET",
    am_spoken, 1, 1, 0,
    am_idioms, sizeof(am_idioms) / sizeof(am_idioms[0]),
    "\xE1\x8C\xA4\xE1\x8A\x93 \xE1\x8B\xAD\xE1\x88\xB5\xE1\x8C\xA5\xE1\x88\x8D\xE1\x8A\x9D",
    "\xE1\x88\x98\xE1\x88\x8D\xE1\x8A\xAB\xE1\x88\x9D \xE1\x88\x9D\xE1\x88\xB7\xE1\x89\xB5",
    "greeting",
    "Use '\xE1\x8C\xA4\xE1\x8A\x93 \xE1\x8B\xAD\xE1\x88\xB5\xE1\x8C\xA5\xE1\x88\x8D\xE1\x8A\x9D' in the morning. Show respect to elders.",
    am_note_examples, 2,
    am_locale, sizeof(am_locale) / sizeof(am_locale[0]),
};

/* ── Arabic ─────────────────────────────────────────────────────────────────*/
static const char *const ar_spoken[] = { "SA", "EG", "MA", "AE" };
static const lp_idiom_t ar_idioms[] = {
    { "hello", "\xD9\x85\xD8\xB1\xD8\xAD\xD8\xA8\xD8\xA7" },
    { "peace be upon you", "\xD8\xA7\xD9\x84\xD8\xB3\xD9\x84\xD8\xA7\xD9\x85 \xD8\xB9\xD9\x84\xD9\x8A\xD9\x83\xD9\x85" },
    { "good morning", "\xD8\xB5\xD8\xA8\xD8\xA7\xD8\xAD \xD8\xA7\xD9\x84\xD8\xAE\xD9\x8A\xD8\xB1" },
    { "good evening", "\xD9\x85\xD8\xB3\xD8\xA7\xD8\xA1 \xD8\xA7\xD9\x84\xD8\xAE\xD9\x8A\xD8\xB1" },
    { "goodbye", "\xD9\x85\xD8\xB9 \xD8\xA7\xD9\x84\xD8\xB3\xD9\x84\xD8\xA7\xD9\x85\xD8\xA9" },
    { "thank you", "\xD8\xB4\xD9\x83\xD8\xB1\xD8\xA7" },
    { "please", "\xD9\x85\xD9\x86 \xD9\x81\xD8\xB6\xD9\x84\xD9\x83" },
    { "yes", "\xD9\x86\xD8\xB9\xD9\x85" },
    { "no", "\xD9\x84\xD8\xA7" },
    { "sorry", "\xD8\xA2\xD8\xB3\xD9\x81" },
    { "how are you", "\xD9\x83\xD9\x8A\xD9\x81 \xD8\xAD\xD8\xA7\xD9\x84\xD9\x83" },
    { "I am fine", "\xD8\xA3\xD9\x86\xD8\xA7 \xD8\xA8\xD8\xAE\xD9\x8A\xD8\xB1" },
    { "water", "\xD9\x85\xD8\xA7\xD8\xA1" },
    { "food", "\xD8\xB7\xD8\xB9\xD8\xA7\xD9\x85" },
    { "family", "\xD8\xB9\xD8\xA7\xD8\xA6\xD9\x84\xD8\xA9" },
    { "friend", "\xD8\xB5\xD8\xAF\xD9\x8A\xD9\x82" },
    { "love", "\xD8\xAD\xD8\xA8" },
    { "mother", "\xD8\xA3\xD9\x85" },
    { "father", "\xD8\xA3\xD8\xA8" },
    { "child", "\xD8\xB7\xD9\x81\xD9\x84" },
};
static const char *const ar_note_examples[] = {
    "\xD8\xB5\xD8\xA8\xD8\xA7\xD8\xAD \xD8\xA7\xD9\x84\xD8\xAE\xD9\x8A\xD8\xB1",
    "\xD9\x85\xD8\xB3\xD8\xA7\xD8\xA1 \xD8\xA7\xD9\x84\xD8\xAE\xD9\x8A\xD8\xB1",
};
static const lp_kv_t ar_locale[] = {
    { "bcp_tag", "ar" }, { "region", "SA" }, { "rtl", "true" },
    { "date_format", "dd/MM/yyyy" },
};
static const ca_language_pack_t k_ar = {
    "ar", "Arabic", "\xD8\xA7\xD9\x84\xD8\xB9\xD8\xB1\xD8\xA8\xD9\x8A\xD8\xA9", "SA",
    ar_spoken, 4, 1, 0,
    ar_idioms, sizeof(ar_idioms) / sizeof(ar_idioms[0]),
    "\xD8\xB5\xD8\xA8\xD8\xA7\xD8\xAD \xD8\xA7\xD9\x84\xD8\xAE\xD9\x8A\xD8\xB1",
    "\xD9\x85\xD8\xB3\xD8\xA7\xD8\xA1 \xD8\xA7\xD9\x84\xD8\xAE\xD9\x8A\xD8\xB1",
    "greeting",
    "Use '\xD8\xB5\xD8\xA8\xD8\xA7\xD8\xAD \xD8\xA7\xD9\x84\xD8\xAE\xD9\x8A\xD8\xB1' in the morning. Show respect to elders.",
    ar_note_examples, 2,
    ar_locale, sizeof(ar_locale) / sizeof(ar_locale[0]),
};

/* ── Hausa ──────────────────────────────────────────────────────────────────*/
static const char *const ha_spoken[] = { "NG", "NE", "GH" };
static const lp_idiom_t ha_idioms[] = {
    { "hello", "Sannu" },
    { "good morning", "Barka da safe" },
    { "good afternoon", "Barka da rana" },
    { "good evening", "Barka da yamma" },
    { "goodbye", "Sai anjima" },
    { "see you later", "Sai gobe" },
    { "thank you", "Na gode" },
    { "please", "Don Allah" },
    { "yes", "Eh" },
    { "no", "A'a" },
    { "sorry", "Yi hakuri" },
    { "how are you", "Yaya kake" },
    { "I am fine", "Lafiya lau" },
    { "water", "ruwa" },
    { "food", "abinci" },
    { "family", "iyali" },
    { "friend", "aboki" },
    { "love", "kauna" },
    { "mother", "uwa" },
    { "father", "uba" },
    { "child", "yaro" },
};
static const char *const ha_note_examples[] = { "Barka da safe", "Sai anjima" };
static const lp_kv_t ha_locale[] = {
    { "bcp_tag", "ha" }, { "region", "NG" }, { "rtl", "false" },
    { "date_format", "dd/MM/yyyy" },
};
static const ca_language_pack_t k_ha = {
    "ha", "Hausa", "Hausa", "NG", ha_spoken, 3, 1, 0,
    ha_idioms, sizeof(ha_idioms) / sizeof(ha_idioms[0]),
    "Barka da safe", "Sai anjima",
    "greeting", "Use 'Barka da safe' in the morning. Show respect to elders.",
    ha_note_examples, 2,
    ha_locale, sizeof(ha_locale) / sizeof(ha_locale[0]),
};

/* ── Portuguese ─────────────────────────────────────────────────────────────*/
static const char *const pt_spoken[] = { "PT", "BR", "MZ", "AO" };
static const lp_idiom_t pt_idioms[] = {
    { "hello", "Ol\xC3\xA1" },
    { "good morning", "Bom dia" },
    { "good afternoon", "Boa tarde" },
    { "good evening", "Boa noite" },
    { "goodbye", "Adeus" },
    { "see you later", "At\xC3\xA9 logo" },
    { "thank you", "Obrigado" },
    { "thank you (f)", "Obrigada" },
    { "please", "Por favor" },
    { "sorry", "Desculpe" },
    { "yes", "Sim" },
    { "no", "N\xC3\xA3o" },
    { "how are you", "Como est\xC3\xA1" },
    { "I am fine", "Estou bem" },
    { "water", "\xC3\xA1gua" },
    { "food", "comida" },
    { "family", "fam\xC3\xADlia" },
    { "friend", "amigo" },
    { "love", "amor" },
    { "mother", "m\xC3\xA3" "e" },
    { "father", "pai" },
    { "child", "crian\xC3\xA7" "a" },
};
static const char *const pt_note_examples[] = { "Bom dia", "Boa noite" };
static const lp_kv_t pt_locale[] = {
    { "bcp_tag", "pt" }, { "region", "PT" }, { "rtl", "false" },
    { "date_format", "dd/MM/yyyy" },
};
static const ca_language_pack_t k_pt = {
    "pt", "Portuguese", "Portugu\xC3\xAAs", "PT", pt_spoken, 4, 1, 0,
    pt_idioms, sizeof(pt_idioms) / sizeof(pt_idioms[0]),
    "Bom dia", "Boa noite",
    "greeting", "Use 'Bom dia' in the morning. Show respect to elders.",
    pt_note_examples, 2,
    pt_locale, sizeof(pt_locale) / sizeof(pt_locale[0]),
};

/* ── Sesotho ────────────────────────────────────────────────────────────────*/
static const char *const st_spoken[] = { "ZA", "LS" };
static const lp_idiom_t st_idioms[] = {
    { "hello", "Dumela" },
    { "hello (plural)", "Dumelang" },
    { "goodbye", "Sala hantle" },
    { "goodbye (sleep)", "Robala hantle" },
    { "thank you", "Kea leboha" },
    { "please", "Ka kopo" },
    { "yes", "E" },
    { "no", "Che" },
    { "how are you", "O phela joang" },
    { "I am fine", "Ke phela hantle" },
    { "sorry", "Tshwarelo" },
    { "family", "lelapa" },
    { "love", "lerato" },
    { "water", "metsi" },
    { "food", "dijo" },
    { "mother", "'me" },
    { "father", "ntate" },
    { "child", "ngwana" },
    { "friend", "motswalle" },
};
static const char *const st_note_examples[] = { "Dumela", "Robala hantle" };
static const lp_kv_t st_locale[] = {
    { "bcp_tag", "st" }, { "region", "ZA" }, { "rtl", "false" },
    { "date_format", "dd/MM/yyyy" },
};
static const ca_language_pack_t k_st = {
    "st", "Sesotho", "Sesotho", "ZA", st_spoken, 2, 1, 0,
    st_idioms, sizeof(st_idioms) / sizeof(st_idioms[0]),
    "Dumela", "Robala hantle",
    "greeting", "Use 'Dumela' in the morning. Show respect to elders.",
    st_note_examples, 2,
    st_locale, sizeof(st_locale) / sizeof(st_locale[0]),
};

/* ── Swahili ────────────────────────────────────────────────────────────────*/
static const char *const sw_spoken[] = { "KE", "TZ", "UG" };
static const lp_idiom_t sw_idioms[] = {
    { "hello", "Habari" },
    { "hello (informal)", "Mambo" },
    { "good morning", "Habari ya asubuhi" },
    { "good evening", "Habari ya jioni" },
    { "goodbye", "Kwaheri" },
    { "goodbye (sleep)", "Usiku mwema" },
    { "thank you", "Asante" },
    { "thank you (very)", "Asante sana" },
    { "please", "Tafadhali" },
    { "yes", "Ndio" },
    { "no", "Hapana" },
    { "how are you", "Habari yako" },
    { "I am fine", "Nzuri" },
    { "sorry", "Pole" },
    { "family", "familia" },
    { "love", "upendo" },
    { "water", "maji" },
    { "food", "chakula" },
    { "mother", "mama" },
    { "father", "baba" },
    { "child", "mtoto" },
    { "friend", "rafiki" },
    { "no problem", "Hakuna matata" },
};
static const char *const sw_note_examples[] = { "Habari", "Usiku mwema" };
static const lp_kv_t sw_locale[] = {
    { "bcp_tag", "sw" }, { "region", "KE" }, { "rtl", "false" },
    { "date_format", "dd/MM/yyyy" },
};
static const ca_language_pack_t k_sw = {
    "sw", "Swahili", "Kiswahili", "KE", sw_spoken, 3, 1, 0,
    sw_idioms, sizeof(sw_idioms) / sizeof(sw_idioms[0]),
    "Habari", "Usiku mwema",
    "greeting", "Use 'Habari' in the morning. Show respect to elders.",
    sw_note_examples, 2,
    sw_locale, sizeof(sw_locale) / sizeof(sw_locale[0]),
};

/* ── isiZulu ────────────────────────────────────────────────────────────────*/
static const char *const zu_spoken[] = { "ZA" };
static const lp_idiom_t zu_idioms[] = {
    { "hello", "Sawubona" },
    { "hello (plural)", "Sanibonani" },
    { "goodbye", "Sala kahle" },
    { "goodbye (sleep)", "Lala kahle" },
    { "thank you", "Ngiyabonga" },
    { "thank you (pl)", "Siyabonga" },
    { "please", "Ngicela" },
    { "yes", "Yebo" },
    { "no", "Cha" },
    { "how are you", "Unjani" },
    { "I am fine", "Ngikhona" },
    { "sorry", "Uxolo" },
    { "family", "umndeni" },
    { "love", "uthando" },
    { "water", "amanzi" },
    { "food", "ukudla" },
    { "mother", "umama" },
    { "father", "ubaba" },
    { "child", "ingane" },
    { "friend", "umngani" },
};
static const char *const zu_note_examples[] = { "Sawubona", "Lala kahle" };
static const lp_kv_t zu_locale[] = {
    { "bcp_tag", "zu" }, { "region", "ZA" }, { "rtl", "false" },
    { "date_format", "dd/MM/yyyy" },
};
static const ca_language_pack_t k_zu = {
    "zu", "isiZulu", "isiZulu", "ZA", zu_spoken, 1, 1, 0,
    zu_idioms, sizeof(zu_idioms) / sizeof(zu_idioms[0]),
    "Sawubona", "Lala kahle",
    "greeting", "Use 'Sawubona' in the morning. Show respect to elders.",
    zu_note_examples, 2,
    zu_locale, sizeof(zu_locale) / sizeof(zu_locale[0]),
};

/* Factory accessors — process-lifetime singletons, never freed by the caller. */
const ca_language_pack_t *ca_language_pack_afrikaans(void)  { return &k_af; }
const ca_language_pack_t *ca_language_pack_amharic(void)    { return &k_am; }
const ca_language_pack_t *ca_language_pack_arabic(void)     { return &k_ar; }
const ca_language_pack_t *ca_language_pack_hausa(void)      { return &k_ha; }
const ca_language_pack_t *ca_language_pack_portuguese(void) { return &k_pt; }
const ca_language_pack_t *ca_language_pack_sesotho(void)    { return &k_st; }
const ca_language_pack_t *ca_language_pack_swahili(void)    { return &k_sw; }
const ca_language_pack_t *ca_language_pack_isizulu(void)    { return &k_zu; }

/* ===========================================================================
 * Records — free + deep-copy
 * =========================================================================== */

/* Deep-copy a C-array of C strings. On OOM, frees whatever was allocated and
 * returns NULL (with *ok=false). An empty source yields NULL with *ok=true. */
static char **lp_dup_str_array(const char *const *src, size_t n, bool *ok) {
    *ok = true;
    if (n == 0) return NULL;
    char **out = (char **)calloc(n, sizeof(char *));
    if (!out) { *ok = false; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        out[i] = lp_strdup_empty(src[i]);
        if (!out[i]) {
            for (size_t j = 0; j < i; ++j) free(out[j]);
            free(out);
            *ok = false;
            return NULL;
        }
    }
    return out;
}
static void lp_free_str_array(char **arr, size_t n) {
    if (!arr) return;
    for (size_t i = 0; i < n; ++i) free(arr[i]);
    free(arr);
}

void ca_lang_pack_metadata_free(ca_lang_pack_metadata_t *m) {
    if (!m) return;
    free(m->bcp_tag);
    free(m->display_name);
    free(m->native_name);
    free(m->primary_region);
    lp_free_str_array(m->spoken_in_regions, m->spoken_count);
    m->bcp_tag = m->display_name = m->native_name = m->primary_region = NULL;
    m->spoken_in_regions = NULL;
    m->spoken_count = 0;
}
void ca_lang_pack_metadata_free_array(ca_lang_pack_metadata_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_lang_pack_metadata_free(&arr[i]);
    free(arr);
}

/* Populate an out metadata from a pack's static data. false on OOM. */
static bool metadata_from_pack(ca_lang_pack_metadata_t *dst,
                               const ca_language_pack_t *p) {
    memset(dst, 0, sizeof(*dst));
    dst->bcp_tag        = lp_strdup_empty(p->bcp_tag);
    dst->display_name   = lp_strdup_empty(p->display_name);
    dst->native_name    = lp_strdup_empty(p->native_name);
    dst->primary_region = lp_strdup_empty(p->primary_region);
    dst->pack_version_major = p->ver_major;
    dst->pack_version_minor = p->ver_minor;
    if (!dst->bcp_tag || !dst->display_name || !dst->native_name ||
        !dst->primary_region) {
        ca_lang_pack_metadata_free(dst);
        return false;
    }
    bool ok = true;
    dst->spoken_in_regions = lp_dup_str_array(p->spoken_in, p->spoken_count, &ok);
    if (!ok) { ca_lang_pack_metadata_free(dst); return false; }
    dst->spoken_count = p->spoken_count;
    return true;
}

void ca_cultural_note_free(ca_cultural_note_t *n) {
    if (!n) return;
    free(n->context);
    free(n->guidance);
    lp_free_str_array(n->examples, n->examples_count);
    n->context = n->guidance = NULL;
    n->examples = NULL;
    n->examples_count = 0;
}
void ca_cultural_note_free_array(ca_cultural_note_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_cultural_note_free(&arr[i]);
    free(arr);
}

void ca_locale_hint_free(ca_locale_hint_t *h) {
    if (!h) return;
    free(h->key);
    free(h->value);
    h->key = h->value = NULL;
}
void ca_locale_hint_free_array(ca_locale_hint_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_locale_hint_free(&arr[i]);
    free(arr);
}

/* ===========================================================================
 * ILanguagePack accessors
 * =========================================================================== */

bool ca_language_pack_metadata(const ca_language_pack_t *pack,
                               ca_lang_pack_metadata_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!pack || !out) return false;
    return metadata_from_pack(out, pack);
}

char *ca_language_pack_idiomatic(const ca_language_pack_t *pack,
                                 const char *phrase) {
    if (!pack || !phrase) return NULL;
    for (size_t i = 0; i < pack->idiom_count; ++i)
        if (lp_ci_eq(pack->idioms[i].en, phrase))
            return lp_strdup(pack->idioms[i].tr);
    return NULL; /* TryGetValue miss -> null */
}

char *ca_language_pack_adapt_system_prompt(const ca_language_pack_t *pack,
                                           const char *base) {
    if (!pack) return NULL;
    /* Fixed template; a null base interpolates as empty (C# $"...{basePrompt}"
     * with a null string yields empty at that position). */
    static const char *pre = "You are a culturally aware AI assistant for ";
    static const char *mid1 = " speakers. Respond in ";
    static const char *mid2 = " (";
    static const char *mid3 = ") unless instructed otherwise. Use natural, "
                              "idiomatic expressions. Respect regional customs. "
                              "\n\n";
    const char *dn = pack->display_name ? pack->display_name : "";
    const char *nn = pack->native_name ? pack->native_name : "";
    const char *bp = base ? base : "";

    size_t len = strlen(pre) + strlen(dn) + strlen(mid1) + strlen(dn) +
                 strlen(mid2) + strlen(nn) + strlen(mid3) + strlen(bp) + 1;
    char *out = (char *)malloc(len);
    if (!out) return NULL;
    char *w = out;
    #define LP_APPEND(s) do { size_t _n = strlen(s); memcpy(w, (s), _n); w += _n; } while (0)
    LP_APPEND(pre);
    LP_APPEND(dn);
    LP_APPEND(mid1);
    LP_APPEND(dn);
    LP_APPEND(mid2);
    LP_APPEND(nn);
    LP_APPEND(mid3);
    LP_APPEND(bp);
    #undef LP_APPEND
    *w = '\0';
    return out;
}

ca_cultural_note_t *ca_language_pack_cultural_notes(const ca_language_pack_t *pack,
                                                    const char *context,
                                                    size_t *out_count) {
    if (!out_count) return NULL;
    if (!pack || !context) { *out_count = (size_t)-1; return NULL; }
    /* Only the "greeting" key is defined; TryGetValue miss -> empty list. */
    if (!lp_ci_eq(pack->note_context, context)) { *out_count = 0; return NULL; }

    ca_cultural_note_t *out = (ca_cultural_note_t *)calloc(1, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    out->context  = lp_strdup_empty(pack->note_context);
    out->guidance = lp_strdup_empty(pack->note_guidance);
    if (!out->context || !out->guidance) {
        ca_cultural_note_free_array(out, 1);
        *out_count = (size_t)-1;
        return NULL;
    }
    bool ok = true;
    out->examples = lp_dup_str_array(pack->note_examples,
                                     pack->note_example_count, &ok);
    if (!ok) {
        ca_cultural_note_free_array(out, 1);
        *out_count = (size_t)-1;
        return NULL;
    }
    out->examples_count = pack->note_example_count;
    *out_count = 1;
    return out;
}

char *ca_language_pack_greeting(const ca_language_pack_t *pack,
                                const char *time_of_day) {
    if (!pack) return NULL;
    /* C#: timeOfDay.ToLowerInvariant() == "morning" || == "am".
     * Replicate with an ASCII-lowercased compare of the raw input. A null
     * timeOfDay matches neither, so it falls to the else branch. */
    bool morning = false;
    if (time_of_day) {
        morning = lp_ci_eq(time_of_day, "morning") ||
                  lp_ci_eq(time_of_day, "am");
    }
    return lp_strdup(morning ? pack->greeting_morning : pack->greeting_other);
}

ca_locale_hint_t *ca_language_pack_locale_hints(const ca_language_pack_t *pack,
                                                size_t *out_count) {
    if (!out_count) return NULL;
    if (!pack) { *out_count = (size_t)-1; return NULL; }
    size_t n = pack->locale_hint_count;
    ca_locale_hint_t *out = (ca_locale_hint_t *)calloc(n, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        out[i].key   = lp_strdup_empty(pack->locale_hints[i].key);
        out[i].value = lp_strdup_empty(pack->locale_hints[i].value);
        if (!out[i].key || !out[i].value) {
            ca_locale_hint_free_array(out, i + 1);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = n;
    return out;
}

/* ===========================================================================
 * Registry — DefaultLanguagePackRegistry (Ordinal) + LanguagePackRegistry (CI)
 * =========================================================================== */

struct ca_language_pack_registry {
    bool                        ci;    /* OrdinalIgnoreCase keying + helper ops */
    const ca_language_pack_t  **packs; /* borrowed pointers */
    size_t                      count, cap;
};

static const char *reg_tag_of(const ca_language_pack_t *p) {
    return p->bcp_tag ? p->bcp_tag : "";
}

static bool reg_key_eq(const ca_language_pack_registry_t *reg,
                       const char *a, const char *b) {
    return reg->ci ? lp_ci_eq(a, b) : lp_ord_eq(a, b);
}

static ca_language_pack_registry_t *reg_create(bool ci) {
    ca_language_pack_registry_t *r =
        (ca_language_pack_registry_t *)calloc(1, sizeof(*r));
    if (r) r->ci = ci;
    return r;
}
ca_language_pack_registry_t *ca_language_pack_registry_create(void) {
    return reg_create(false);
}
ca_language_pack_registry_t *ca_language_pack_registry_create_ci(void) {
    return reg_create(true);
}
void ca_language_pack_registry_destroy(ca_language_pack_registry_t *reg) {
    if (!reg) return;
    free(reg->packs);   /* pack pointers are borrowed — do not free the packs */
    free(reg);
}

int ca_language_pack_registry_register(ca_language_pack_registry_t *reg,
                                       const ca_language_pack_t *pack) {
    if (!reg || !pack) return -1;
    const char *tag = reg_tag_of(pack);
    /* Dictionary set: replace an existing entry with the same key. */
    for (size_t i = 0; i < reg->count; ++i) {
        if (reg_key_eq(reg, reg_tag_of(reg->packs[i]), tag)) {
            reg->packs[i] = pack;
            return 0;
        }
    }
    if (reg->count == reg->cap) {
        size_t nc = reg->cap ? reg->cap * 2 : 4;
        void *n = realloc(reg->packs, nc * sizeof(*reg->packs));
        if (!n) return -1;
        reg->packs = (const ca_language_pack_t **)n;
        reg->cap = nc;
    }
    reg->packs[reg->count++] = pack;
    return 0;
}

const ca_language_pack_t *ca_language_pack_registry_get_by_bcp_tag(
    const ca_language_pack_registry_t *reg, const char *bcp_tag) {
    if (!reg || !bcp_tag) return NULL;
    /* On the CI registry GetByExactTag returns null for null/whitespace. The
     * Default (Ordinal) registry's GetByBcpTag dictionary lookup with a whitespace
     * key simply misses, so guarding whitespace here is behaviour-preserving for
     * both. */
    if (lp_is_ws(bcp_tag)) return NULL;
    for (size_t i = 0; i < reg->count; ++i)
        if (reg_key_eq(reg, reg_tag_of(reg->packs[i]), bcp_tag))
            return reg->packs[i];
    return NULL;
}

bool ca_language_pack_registry_has_pack(const ca_language_pack_registry_t *reg,
                                        const char *bcp_tag) {
    if (!reg || !bcp_tag) return false;
    for (size_t i = 0; i < reg->count; ++i)
        if (reg_key_eq(reg, reg_tag_of(reg->packs[i]), bcp_tag))
            return true;
    return false;
}

ca_lang_pack_metadata_t *ca_language_pack_registry_available_packs(
    const ca_language_pack_registry_t *reg, size_t *out_count) {
    if (!out_count) return NULL;
    if (!reg) { *out_count = (size_t)-1; return NULL; }
    if (reg->count == 0) { *out_count = 0; return NULL; }
    ca_lang_pack_metadata_t *out =
        (ca_lang_pack_metadata_t *)calloc(reg->count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < reg->count; ++i) {
        if (!metadata_from_pack(&out[i], reg->packs[i])) {
            ca_lang_pack_metadata_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = reg->count;
    return out;
}

const ca_language_pack_t *ca_language_pack_registry_get_by_language(
    const ca_language_pack_registry_t *reg, const char *lang_prefix) {
    if (!reg || !reg->ci || !lang_prefix) return NULL;
    /* prefix = langPrefix.Split('-')[0] (the part before the first '-'). */
    size_t plen = 0;
    while (lang_prefix[plen] && lang_prefix[plen] != '-') plen++;
    char stackbuf[64];
    char *prefix = stackbuf;
    if (plen + 1 > sizeof(stackbuf)) {
        prefix = (char *)malloc(plen + 1);
        if (!prefix) return NULL;
    }
    memcpy(prefix, lang_prefix, plen);
    prefix[plen] = '\0';

    const ca_language_pack_t *found = NULL;
    for (size_t i = 0; i < reg->count; ++i) {
        if (lp_ci_starts_with(reg_tag_of(reg->packs[i]), prefix)) {
            found = reg->packs[i];
            break;
        }
    }
    if (prefix != stackbuf) free(prefix);
    return found;
}

/* Does a pack's SpokenInRegions contain region (OrdinalIgnoreCase)? */
static bool pack_spoken_in(const ca_language_pack_t *p, const char *region) {
    for (size_t i = 0; i < p->spoken_count; ++i)
        if (lp_ci_eq(p->spoken_in[i], region)) return true;
    return false;
}

ca_lang_pack_metadata_t *ca_language_pack_registry_for_region(
    const ca_language_pack_registry_t *reg, const char *region,
    size_t *out_count) {
    if (!out_count) return NULL;
    /* ForRegion throws on null/whitespace region; CI helper registry only. */
    if (!reg || !reg->ci || lp_is_ws(region)) { *out_count = (size_t)-1; return NULL; }

    size_t *idx = NULL;
    size_t n = 0;
    if (reg->count > 0) {
        idx = (size_t *)malloc(reg->count * sizeof(size_t));
        if (!idx) { *out_count = (size_t)-1; return NULL; }
        for (size_t i = 0; i < reg->count; ++i)
            if (pack_spoken_in(reg->packs[i], region)) idx[n++] = i;
    }
    if (n == 0) { free(idx); *out_count = 0; return NULL; }

    ca_lang_pack_metadata_t *out =
        (ca_lang_pack_metadata_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!metadata_from_pack(&out[i], reg->packs[idx[i]])) {
            ca_lang_pack_metadata_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

void ca_language_pack_registry_free_tags(char **tags, size_t count) {
    lp_free_str_array(tags, count);
}

char **ca_language_pack_registry_all_tags(
    const ca_language_pack_registry_t *reg, size_t *out_count) {
    if (!out_count) return NULL;
    /* AllTags is a helper-registry (CI) operation; keys ordered ascending. */
    if (!reg || !reg->ci) { *out_count = (size_t)-1; return NULL; }
    if (reg->count == 0) { *out_count = 0; return NULL; }

    char **tags = (char **)calloc(reg->count, sizeof(char *));
    if (!tags) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < reg->count; ++i) {
        tags[i] = lp_strdup_empty(reg_tag_of(reg->packs[i]));
        if (!tags[i]) {
            lp_free_str_array(tags, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    /* OrderBy(k => k) — Ordinal ascending (Dictionary keys are the BcpTags).
     * Stable insertion sort over the owned string pointers. */
    for (size_t i = 1; i < reg->count; ++i) {
        char *key = tags[i];
        size_t j = i;
        while (j > 0 && strcmp(tags[j - 1], key) > 0) {
            tags[j] = tags[j - 1];
            j--;
        }
        tags[j] = key;
    }
    *out_count = reg->count;
    return tags;
}

/* ===========================================================================
 * LocaleHintMerge.Merge — primary wins, OrdinalIgnoreCase key dedupe.
 * =========================================================================== */

/* Find the CI-equal key in [0,n) of dst, or SIZE_MAX. */
static size_t hint_find_ci(const ca_locale_hint_t *arr, size_t n, const char *key) {
    for (size_t i = 0; i < n; ++i)
        if (lp_ci_eq(arr[i].key, key)) return i;
    return (size_t)-1;
}

/* Set an owning hint slot from (key,value). false on OOM. */
static bool hint_assign(ca_locale_hint_t *h, const char *key, const char *value) {
    char *k = lp_strdup_empty(key);
    char *v = lp_strdup_empty(value);
    if (!k || !v) { free(k); free(v); return false; }
    free(h->key);
    free(h->value);
    h->key = k;
    h->value = v;
    return true;
}

ca_locale_hint_t *ca_locale_hint_merge(const ca_locale_hint_t *primary,
                                       size_t nprimary,
                                       const ca_locale_hint_t *secondary,
                                       size_t nsecondary,
                                       size_t *out_count) {
    if (!out_count) return NULL;
    if ((nprimary && !primary) || (nsecondary && !secondary)) {
        *out_count = (size_t)-1;
        return NULL;
    }
    /* Upper bound: every secondary + every primary distinct. */
    size_t cap = nsecondary + nprimary;
    if (cap == 0) { *out_count = 0; return NULL; }

    ca_locale_hint_t *out = (ca_locale_hint_t *)calloc(cap, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;

    /* Copy of secondary (CI dedupe: a later duplicate key overwrites — Dictionary
     * indexer assignment). */
    for (size_t i = 0; i < nsecondary; ++i) {
        size_t at = hint_find_ci(out, n, secondary[i].key);
        if (at != (size_t)-1) {
            if (!hint_assign(&out[at], secondary[i].key, secondary[i].value))
                goto oom;
        } else {
            if (!hint_assign(&out[n], secondary[i].key, secondary[i].value))
                goto oom;
            n++;
        }
    }
    /* Overlay primary (primary wins): replace CI-equal key, else append. */
    for (size_t i = 0; i < nprimary; ++i) {
        size_t at = hint_find_ci(out, n, primary[i].key);
        if (at != (size_t)-1) {
            if (!hint_assign(&out[at], primary[i].key, primary[i].value))
                goto oom;
        } else {
            if (!hint_assign(&out[n], primary[i].key, primary[i].value))
                goto oom;
            n++;
        }
    }

    if (n == 0) { free(out); *out_count = 0; return NULL; }
    *out_count = n;
    return out;

oom:
    ca_locale_hint_free_array(out, n + 1);
    *out_count = (size_t)-1;
    return NULL;
}
