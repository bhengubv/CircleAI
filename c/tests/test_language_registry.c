/*
 * test_language_registry.c — KnownLanguages registry tests.
 *
 * Verifies count, declaration order, RTL flag, writing systems, and
 * english_name / primary_region values against fixtures/language_tags.json.
 * Returns 0 on all-pass, calls assert() on first failure.
 */

#include <stdio.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

int main(void) {
    int i;

    /* Total count */
    assert(ca_language_count() == 20);

    const ca_language_tag_t *langs = ca_known_languages();

    /* Declaration order spot-checks (index 0..19) */
    assert(strcmp(langs[0].bcp_tag, "zu")  == 0);
    assert(strcmp(langs[1].bcp_tag, "st")  == 0);
    assert(strcmp(langs[2].bcp_tag, "af")  == 0);
    assert(strcmp(langs[3].bcp_tag, "sw")  == 0);
    assert(strcmp(langs[4].bcp_tag, "ha")  == 0);
    assert(strcmp(langs[5].bcp_tag, "am")  == 0);
    assert(strcmp(langs[6].bcp_tag, "yo")  == 0);
    assert(strcmp(langs[7].bcp_tag, "ig")  == 0);
    assert(strcmp(langs[8].bcp_tag, "xh")  == 0);
    assert(strcmp(langs[9].bcp_tag, "nso") == 0);
    assert(strcmp(langs[10].bcp_tag,"tn")  == 0);
    assert(strcmp(langs[11].bcp_tag,"so")  == 0);
    assert(strcmp(langs[12].bcp_tag,"om")  == 0);
    assert(strcmp(langs[13].bcp_tag,"ar")  == 0);
    assert(strcmp(langs[14].bcp_tag,"en")  == 0);
    assert(strcmp(langs[15].bcp_tag,"pt")  == 0);
    assert(strcmp(langs[16].bcp_tag,"fr")  == 0);
    assert(strcmp(langs[17].bcp_tag,"es")  == 0);
    assert(strcmp(langs[18].bcp_tag,"zh")  == 0);
    assert(strcmp(langs[19].bcp_tag,"hi")  == 0);

    /* English names (must match fixtures/language_tags.json "englishName") */
    assert(strcmp(langs[0].english_name,  "isiZulu")     == 0);
    assert(strcmp(langs[1].english_name,  "Sesotho")     == 0);
    assert(strcmp(langs[2].english_name,  "Afrikaans")   == 0);
    assert(strcmp(langs[3].english_name,  "Swahili")     == 0);
    assert(strcmp(langs[4].english_name,  "Hausa")       == 0);
    assert(strcmp(langs[5].english_name,  "Amharic")     == 0);
    assert(strcmp(langs[6].english_name,  "Yoruba")      == 0);
    assert(strcmp(langs[7].english_name,  "Igbo")        == 0);
    assert(strcmp(langs[8].english_name,  "isiXhosa")    == 0);
    assert(strcmp(langs[9].english_name,  "Sepedi")      == 0);
    assert(strcmp(langs[10].english_name, "Setswana")    == 0);
    assert(strcmp(langs[11].english_name, "Somali")      == 0);
    assert(strcmp(langs[12].english_name, "Oromo")       == 0);
    assert(strcmp(langs[13].english_name, "Arabic")      == 0);
    assert(strcmp(langs[14].english_name, "English")     == 0);
    assert(strcmp(langs[15].english_name, "Portuguese")  == 0);
    assert(strcmp(langs[16].english_name, "French")      == 0);
    assert(strcmp(langs[17].english_name, "Spanish")     == 0);
    assert(strcmp(langs[18].english_name, "Mandarin")    == 0);
    assert(strcmp(langs[19].english_name, "Hindi")       == 0);

    /* Writing systems */
    assert(langs[0].writing_system  == CA_WS_LATIN);
    assert(langs[5].writing_system  == CA_WS_ETHIOPIC);
    assert(langs[13].writing_system == CA_WS_ARABIC);
    assert(langs[14].writing_system == CA_WS_LATIN);
    assert(langs[18].writing_system == CA_WS_HANZI);
    assert(langs[19].writing_system == CA_WS_DEVANAGARI);

    /* RTL: only Arabic */
    int rtl_count = 0;
    for (i = 0; i < ca_language_count(); i++) {
        if (langs[i].is_rtl) rtl_count++;
    }
    assert(rtl_count == 1);

    const ca_language_tag_t *ar = ca_find_language("ar");
    assert(ar != NULL);
    assert(ar->is_rtl == 1);
    assert(ar->writing_system == CA_WS_ARABIC);
    assert(strcmp(ar->primary_region, "SA") == 0);

    /* Primary regions */
    assert(strcmp(langs[14].primary_region, "GB") == 0); /* en -> GB */
    assert(strcmp(langs[15].primary_region, "PT") == 0); /* pt -> PT */
    assert(strcmp(langs[18].primary_region, "CN") == 0); /* zh -> CN */

    /* ca_find_language */
    assert(ca_find_language("xx") == NULL);
    assert(ca_find_language("zu") != NULL);
    assert(ca_find_language("en") != NULL);
    assert(ca_find_language("zh") != NULL);
    assert(ca_find_language("hi") != NULL);
    assert(ca_find_language("nso")!= NULL);

    /* Chinese is Hanzi, not RTL */
    const ca_language_tag_t *zh = ca_find_language("zh");
    assert(zh != NULL);
    assert(zh->writing_system == CA_WS_HANZI);
    assert(zh->is_rtl == 0);

    /* Last entry is Hindi / Devanagari */
    assert(strcmp(langs[19].bcp_tag, "hi") == 0);
    assert(langs[19].writing_system == CA_WS_DEVANAGARI);
    assert(langs[19].is_rtl == 0);

    printf("All language registry tests passed.\n");
    return 0;
}
