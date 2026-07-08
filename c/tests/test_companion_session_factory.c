/*
 * test_companion_session_factory.c — CompanionSessionFactory (C11).
 *
 * Verifies the per-identity, per-surface parameter resolution from
 * CompanionSessionFactory.cs: default display name = identityId, override with
 * the identity store's DisplayName + PreferredLanguage when one resolves.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* identity resolver that yields a rich display name + language */
static bool resolve_rich(void *user, char **dn, char **pl) {
    (void)user;
    *dn = strdup("Alice Bhengu");
    *pl = strdup("zu");
    return true;
}
/* resolver with a display name but null preferred language */
static bool resolve_no_lang(void *user, char **dn, char **pl) {
    (void)user;
    *dn = strdup("Bob");
    *pl = NULL;
    return true;
}
/* resolver that reports no current identity */
static bool resolve_none(void *user, char **dn, char **pl) {
    (void)user;
    *dn = strdup("SHOULD_BE_DISCARDED");   /* factory must free this on false */
    *pl = strdup("xx");
    return false;
}

int main(void) {
    ca_companion_session_params_t p;

    /* --- no identity store: display name defaults to identityId, lang NULL --- */
    ca_companion_session_factory_t *f0 = ca_companion_session_factory_create(NULL, NULL);
    assert(f0);
    assert(ca_companion_session_factory_create_params(f0, "user-123", CA_INTERFACE_MOBILE, &p));
    assert(strcmp(p.identity_id, "user-123") == 0);
    assert(strcmp(p.display_name, "user-123") == 0);
    assert(p.preferred_language == NULL);
    assert(p.interface_kind == CA_INTERFACE_MOBILE);
    ca_companion_session_params_free(&p);

    /* blank identity id rejected */
    assert(!ca_companion_session_factory_create_params(f0, "  ", CA_INTERFACE_WEB, &p));
    assert(!ca_companion_session_factory_create_params(f0, NULL, CA_INTERFACE_WEB, &p));
    assert(!ca_companion_session_factory_create_params(f0, "x", CA_INTERFACE_WEB, NULL));
    ca_companion_session_factory_destroy(f0);

    /* --- rich identity: display name + language overridden --- */
    ca_companion_session_factory_t *f1 = ca_companion_session_factory_create(resolve_rich, NULL);
    assert(ca_companion_session_factory_create_params(f1, "u", CA_INTERFACE_DESKTOP, &p));
    assert(strcmp(p.identity_id, "u") == 0);
    assert(strcmp(p.display_name, "Alice Bhengu") == 0);
    assert(strcmp(p.preferred_language, "zu") == 0);
    assert(p.interface_kind == CA_INTERFACE_DESKTOP);
    ca_companion_session_params_free(&p);
    ca_companion_session_factory_destroy(f1);

    /* --- identity with display name but null preferred language --- */
    ca_companion_session_factory_t *f2 = ca_companion_session_factory_create(resolve_no_lang, NULL);
    assert(ca_companion_session_factory_create_params(f2, "u", CA_INTERFACE_AMBIENT, &p));
    assert(strcmp(p.display_name, "Bob") == 0);
    assert(p.preferred_language == NULL);
    ca_companion_session_params_free(&p);
    ca_companion_session_factory_destroy(f2);

    /* --- no current identity: keep defaults (identityId), discard store output --- */
    ca_companion_session_factory_t *f3 = ca_companion_session_factory_create(resolve_none, NULL);
    assert(ca_companion_session_factory_create_params(f3, "fallback-id", CA_INTERFACE_IOT, &p));
    assert(strcmp(p.display_name, "fallback-id") == 0);   /* NOT "SHOULD_BE_DISCARDED" */
    assert(p.preferred_language == NULL);
    ca_companion_session_params_free(&p);
    ca_companion_session_factory_destroy(f3);

    /* interface kinds enumerate as the C# enum order */
    assert(CA_INTERFACE_MOBILE == 0);
    assert(CA_INTERFACE_HEADLESS == 6);

    printf("test_companion_session_factory: all assertions passed\n");
    return 0;
}
