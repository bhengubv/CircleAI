#include <stdio.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

int main(void) {
    int i;

    /* count */
    assert(ca_language_count() == 20);

    /* first is Zulu */
    const ca_language_tag_t* langs = ca_known_languages();
    assert(strcmp(langs[0].bcp_tag, "zu") == 0);
    assert(strcmp(langs[0].english_name, "Zulu") == 0);
    assert(langs[0].writing_system == CA_WS_LATIN);
    assert(langs[0].is_rtl == 0);

    /* last is Hindi */
    assert(strcmp(langs[19].bcp_tag, "hi") == 0);
    assert(langs[19].writing_system == CA_WS_DEVANAGARI);

    /* Arabic RTL */
    const ca_language_tag_t* ar = ca_find_language("ar");
    assert(ar != NULL);
    assert(ar->is_rtl == 1);
    assert(ar->writing_system == CA_WS_ARABIC);

    /* only Arabic is RTL */
    int rtl_count = 0;
    for (i = 0; i < ca_language_count(); i++) {
        if (langs[i].is_rtl) rtl_count++;
    }
    assert(rtl_count == 1);

    /* find unknown */
    assert(ca_find_language("xx") == NULL);

    /* find all known languages by tag */
    assert(ca_find_language("zu") != NULL);
    assert(ca_find_language("en") != NULL);
    assert(ca_find_language("zh") != NULL);
    assert(ca_find_language("hi") != NULL);

    /* Chinese is Hanzi */
    const ca_language_tag_t* zh = ca_find_language("zh");
    assert(zh != NULL);
    assert(zh->writing_system == CA_WS_HANZI);
    assert(zh->is_rtl == 0);

    printf("All language registry tests passed.\n");
    return 0;
}
