/*
 * test_accessibility.c — CircleAI.Accessibility (C11 port) verification against
 * AccessibilityPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_accessibility(void) {
    ca_accessibility_board_t *b = ca_accessibility_board_create();
    assert(b);
    assert(ca_accessibility_board_set_profile(b, NULL) == -1);

    /* No profile -> empty hints. */
    size_t n = 0;
    ca_accessibility_hint_t *h = ca_accessibility_board_hints_for(b, "u1", &n);
    assert(h == NULL && n == 0);

    ca_accessibility_need_t needs[] = { CA_ACCESSIBILITY_NEED_VISUAL,
                                        CA_ACCESSIBILITY_NEED_MOTOR };
    ca_accessibility_profile_t p; memset(&p, 0, sizeof(p));
    p.user_id = (char *)"u1"; p.needs = needs; p.need_count = 2;
    p.text_scale = 1.5; p.high_contrast = true; p.reduced_motion = false;
    p.screen_reader = true;
    assert(ca_accessibility_board_set_profile(b, &p) == 0);

    ca_accessibility_profile_t got;
    assert(ca_accessibility_board_get_profile(b, "u1", &got) &&
           got.need_count == 2 && got.high_contrast && !got.reduced_motion);
    ca_accessibility_profile_free(&got);

    /* Hints order: contrast/high, aria/verbose, text-scale/1.50,
     * need/Visual, need/Motor. (motion skipped: reduced_motion false) */
    h = ca_accessibility_board_hints_for(b, "u1", &n);
    assert(n == 5);
    assert(strcmp(h[0].kind, "contrast") == 0 && strcmp(h[0].value, "high") == 0);
    assert(strcmp(h[1].kind, "aria") == 0 && strcmp(h[1].value, "verbose") == 0);
    assert(strcmp(h[2].kind, "text-scale") == 0 && strcmp(h[2].value, "1.50") == 0);
    assert(strcmp(h[3].kind, "need") == 0 && strcmp(h[3].value, "Visual") == 0);
    assert(strcmp(h[4].kind, "need") == 0 && strcmp(h[4].value, "Motor") == 0);
    ca_accessibility_hint_free_array(h, n);

    /* Profile with everything off + scale 1.0 -> no flag/text hints, only needs. */
    ca_accessibility_need_t nd2[] = { CA_ACCESSIBILITY_NEED_HEARING };
    ca_accessibility_profile_t p2; memset(&p2, 0, sizeof(p2));
    p2.user_id = (char *)"u2"; p2.needs = nd2; p2.need_count = 1; p2.text_scale = 1.0;
    assert(ca_accessibility_board_set_profile(b, &p2) == 0);
    h = ca_accessibility_board_hints_for(b, "u2", &n);
    assert(n == 1 && strcmp(h[0].kind, "need") == 0 && strcmp(h[0].value, "Hearing") == 0);
    ca_accessibility_hint_free_array(h, n);

    ca_accessibility_board_destroy(b);
    printf("  accessibility: ok\n");
}

int main(void) {
    test_accessibility();
    printf("test_accessibility: all assertions passed\n");
    return 0;
}
