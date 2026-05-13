#include <stdio.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

int main(void) {
    ca_companion_context_t ctx;
    memset(&ctx, 0, sizeof(ctx));
    strncpy(ctx.session_id,  "550e8400-e29b-41d4-a716-446655440000", 36);
    strncpy(ctx.identity_id, "550e8400-e29b-41d4-a716-446655440001", 36);
    ctx.interface_kind = CA_IFACE_VOICE;
    ctx.locale = "en-US";
    ctx.started_at = 1704067200000LL;

    assert(ctx.interface_kind == CA_IFACE_VOICE);
    assert(strcmp(ctx.locale, "en-US") == 0);
    assert(ctx.started_at == 1704067200000LL);

    /* All interface kinds */
    assert(CA_IFACE_VOICE   == 0);
    assert(CA_IFACE_TEXT    == 1);
    assert(CA_IFACE_VISUAL  == 2);
    assert(CA_IFACE_AMBIENT == 3);

    ca_companion_turn_t turn;
    memset(&turn, 0, sizeof(turn));
    strncpy(turn.turn_id,    "550e8400-e29b-41d4-a716-446655440000", 36);
    strncpy(turn.session_id, "550e8400-e29b-41d4-a716-446655440001", 36);
    turn.user_input = "Hello";
    turn.assistant_response = "Hi there!";
    turn.turn_index = 0;

    assert(turn.turn_index == 0);
    assert(strcmp(turn.user_input, "Hello") == 0);
    assert(strcmp(turn.assistant_response, "Hi there!") == 0);

    /* Proactive event kinds */
    assert(CA_PROACTIVE_IDLE_TOO_LONG  == 0);
    assert(CA_PROACTIVE_TOPIC_SHIFT    == 1);
    assert(CA_PROACTIVE_GOAL_COMPLETED == 2);
    assert(CA_PROACTIVE_GOAL_SUGGESTED == 3);
    assert(CA_PROACTIVE_MEMORY_RECALLED== 4);

    ca_proactive_event_t event;
    event.kind = CA_PROACTIVE_GOAL_SUGGESTED;
    event.payload = "{\"goal\":\"test\"}";
    assert(event.kind == CA_PROACTIVE_GOAL_SUGGESTED);
    assert(strcmp(event.payload, "{\"goal\":\"test\"}") == 0);

    printf("All companion type tests passed.\n");
    return 0;
}
