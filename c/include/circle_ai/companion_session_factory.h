#ifndef CIRCLE_AI_COMPANION_SESSION_FACTORY_H
#define CIRCLE_AI_COMPANION_SESSION_FACTORY_H

/*
 * companion_session_factory.h — CircleAI CompanionSessionFactory (C11 port).
 *
 * Ported from CompanionSessionFactory.cs. The C# factory resolves a rich
 * display name + preferred language from the identity store, then constructs a
 * CompanionSession with all optional backing services pulled from the DI
 * container. In C the DI container and the concrete session wiring live behind
 * companion_brain.h (ca_companion_session_create); this factory ports the piece
 * the C# owns: the per-identity, per-surface parameter resolution that precedes
 * session construction.
 *
 * The identity store is an INJECTED seam: given nothing, it fills the current
 * identity's display name + preferred language (or returns false when there is
 * no current identity). The factory then applies the exact C# fallback:
 *   displayName    = identityId              (default)
 *   preferredLang  = null                    (default)
 *   if the store resolves an identity → use its DisplayName + PreferredLanguage.
 *
 * The interface kind mirrors InterfaceKind (Mobile/Wearable/Desktop/Web/IoT/
 * Ambient/Headless).
 *
 * Ownership: the resolved struct owns strdup'd copies with a matching *_free.
 * A blank identityId is rejected (ArgumentException.ThrowIfNullOrWhiteSpace).
 *
 * Pure C11 + libc.
 */

#include <stddef.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* InterfaceKind (same order as the C# enum). */
typedef enum {
    CA_INTERFACE_MOBILE   = 0,
    CA_INTERFACE_WEARABLE = 1,
    CA_INTERFACE_DESKTOP  = 2,
    CA_INTERFACE_WEB      = 3,
    CA_INTERFACE_IOT      = 4,
    CA_INTERFACE_AMBIENT  = 5,
    CA_INTERFACE_HEADLESS = 6
} ca_companion_interface_kind_t;

/* Resolved per-identity, per-surface session parameters. */
typedef struct {
    char                          *identity_id;        /* owned */
    char                          *display_name;       /* owned */
    ca_companion_interface_kind_t  interface_kind;
    char                          *preferred_language; /* owned, or NULL */
} ca_companion_session_params_t;

void ca_companion_session_params_free(ca_companion_session_params_t *p);

/* Identity-resolver seam. Fill *out_display_name / *out_preferred_language with
 * malloc'd strings for the current identity and return true; return false when
 * there is no current identity (the C# GetCurrentIdentityAsync → null). The
 * factory takes ownership of any strings written. */
typedef bool (*ca_identity_resolver_fn)(void *user,
                                        char **out_display_name,
                                        char **out_preferred_language);

typedef struct ca_companion_session_factory ca_companion_session_factory_t;

/* Create a factory. identity may be NULL (no identity store → identityId is the
 * display name and preferred language stays NULL). */
ca_companion_session_factory_t *ca_companion_session_factory_create(
    ca_identity_resolver_fn identity, void *identity_user);
void ca_companion_session_factory_destroy(ca_companion_session_factory_t *f);

/* Resolve the session parameters for an identity + surface. Writes *out (deep
 * copy the caller frees with ca_companion_session_params_free) and returns true;
 * returns false on a blank identityId or NULL out. */
bool ca_companion_session_factory_create_params(
    ca_companion_session_factory_t *f,
    const char *identity_id, ca_companion_interface_kind_t interface_kind,
    ca_companion_session_params_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_COMPANION_SESSION_FACTORY_H */
