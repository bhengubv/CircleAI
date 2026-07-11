#ifndef CIRCLE_AI_ACCESSIBILITY_H
#define CIRCLE_AI_ACCESSIBILITY_H

/*
 * accessibility.h — CircleAI.Accessibility (C11 port of AccessibilityPrimitives.cs).
 *
 *   Enum    : AccessibilityNeed { Visual, Hearing, Motor, Cognitive, Speech }.
 *   Records : UserAccessibilityProfile(UserId, IReadOnlyList<AccessibilityNeed>
 *                       Needs, double TextScale, bool HighContrast,
 *                       bool ReducedMotion, bool ScreenReader);
 *             AdaptationHint(Kind, Value).
 *   Board   : IAccessibilityBoard -> InMemoryAccessibilityBoard
 *               SetProfile (UserId keyed), GetProfile(userId), HintsFor(userId) —
 *               derives, in this order: "contrast"/"high" if HighContrast,
 *               "motion"/"reduced" if ReducedMotion, "aria"/"verbose" if
 *               ScreenReader, "text-scale"/F2(TextScale) if TextScale > 1, then a
 *               "need"/<EnumName> hint per Need. Empty when no profile.
 *
 * TextScale is formatted like C#'s ToString("F2") (invariant, 2 decimals). Enum
 * names use the C# identifiers ("Visual", "Hearing", ...).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields, deep
 * copy getters, errors via NULL / count SIZE_MAX. Linear arrays, no pthreads.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    CA_ACCESSIBILITY_NEED_VISUAL = 0,
    CA_ACCESSIBILITY_NEED_HEARING = 1,
    CA_ACCESSIBILITY_NEED_MOTOR = 2,
    CA_ACCESSIBILITY_NEED_COGNITIVE = 3,
    CA_ACCESSIBILITY_NEED_SPEECH = 4
} ca_accessibility_need_t;

/* UserAccessibilityProfile(UserId, Needs[], double TextScale, bool HighContrast,
 * bool ReducedMotion, bool ScreenReader). */
typedef struct {
    char   *user_id;    /* owned, non-null */
    ca_accessibility_need_t *needs; /* owned array (may be NULL if 0) */
    size_t  need_count;
    double  text_scale;
    bool    high_contrast;
    bool    reduced_motion;
    bool    screen_reader;
} ca_accessibility_profile_t;

void ca_accessibility_profile_free(ca_accessibility_profile_t *p);

/* AdaptationHint(Kind, Value). */
typedef struct {
    char *kind;  /* owned, non-null */
    char *value; /* owned, non-null */
} ca_accessibility_hint_t;

void ca_accessibility_hint_free(ca_accessibility_hint_t *h);
void ca_accessibility_hint_free_array(ca_accessibility_hint_t *arr, size_t count);

typedef struct ca_accessibility_board ca_accessibility_board_t;

ca_accessibility_board_t *ca_accessibility_board_create(void); /* NULL on OOM */
void ca_accessibility_board_destroy(ca_accessibility_board_t *b);

/* SetProfile(p) — UserId keyed set. 0 / -1. */
int ca_accessibility_board_set_profile(ca_accessibility_board_t *b,
                                       const ca_accessibility_profile_t *p);

/* GetProfile(userId) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_accessibility_board_get_profile(const ca_accessibility_board_t *b,
                                        const char *user_id,
                                        ca_accessibility_profile_t *out);

/* HintsFor(userId) -> fresh owned array of derived hints (order above). Empty when
 * no profile. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_accessibility_hint_t *ca_accessibility_board_hints_for(
    const ca_accessibility_board_t *b, const char *user_id, size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_ACCESSIBILITY_H */
