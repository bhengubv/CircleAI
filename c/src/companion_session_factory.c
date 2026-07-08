/*
 * companion_session_factory.c — CircleAI CompanionSessionFactory (C11 port).
 *
 * Ports the per-identity, per-surface parameter resolution from
 * CompanionSessionFactory.cs: default the display name to the identity id, then
 * override with the identity store's DisplayName + PreferredLanguage when a
 * current identity resolves.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/companion_session_factory.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>

static char *sf_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static bool sf_blank(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p)
        if (!isspace(*p)) return false;
    return true;
}

void ca_companion_session_params_free(ca_companion_session_params_t *p) {
    if (!p) return;
    free(p->identity_id);
    free(p->display_name);
    free(p->preferred_language);
    p->identity_id = p->display_name = p->preferred_language = NULL;
}

struct ca_companion_session_factory {
    ca_identity_resolver_fn identity;
    void                   *identity_user;
};

ca_companion_session_factory_t *ca_companion_session_factory_create(
    ca_identity_resolver_fn identity, void *identity_user) {
    ca_companion_session_factory_t *f =
        (ca_companion_session_factory_t *)calloc(1, sizeof(*f));
    if (!f) return NULL;
    f->identity = identity;
    f->identity_user = identity_user;
    return f;
}
void ca_companion_session_factory_destroy(ca_companion_session_factory_t *f) {
    free(f);
}

bool ca_companion_session_factory_create_params(
    ca_companion_session_factory_t *f,
    const char *identity_id, ca_companion_interface_kind_t interface_kind,
    ca_companion_session_params_t *out) {
    if (!f || !out || sf_blank(identity_id)) return false;   /* ThrowIfNullOrWhiteSpace */

    /* Defaults: displayName = identityId, preferredLang = null. */
    char *display_name = sf_strdup(identity_id);
    char *preferred_lang = NULL;

    if (f->identity) {
        char *dn = NULL, *pl = NULL;
        if (f->identity(f->identity_user, &dn, &pl)) {
            /* resolved != null → use its DisplayName + PreferredLanguage */
            if (dn) { free(display_name); display_name = dn; }
            else    { /* DisplayName is non-null in the C# record; keep default */ }
            preferred_lang = pl;   /* may be NULL (nullable in the record) */
        } else {
            free(dn); free(pl);    /* no current identity: discard anything written */
        }
    }

    out->identity_id = sf_strdup(identity_id);
    out->display_name = display_name;
    out->interface_kind = interface_kind;
    out->preferred_language = preferred_lang;
    return true;
}
