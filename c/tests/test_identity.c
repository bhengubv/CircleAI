#include <stdio.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

int main(void) {
    /* Basic struct construction */
    ca_registered_device_t dev;
    memset(&dev, 0, sizeof(dev));
    strncpy(dev.device_id, "550e8400-e29b-41d4-a716-446655440000", 36);
    dev.device_name = "Pixel 8";
    dev.registered_at = 1704067200000LL;
    dev.is_primary = 1;

    ca_circle_identity_t identity;
    memset(&identity, 0, sizeof(identity));
    strncpy(identity.identity_id, "550e8400-e29b-41d4-a716-446655440001", 36);
    identity.tier = CA_IDENTITY_VERIFIED;
    identity.display_name = "Test User";
    identity.created_at = 1704067200000LL;
    identity.devices[0] = dev;
    identity.device_count = 1;

    assert(identity.tier == CA_IDENTITY_VERIFIED);
    assert(identity.device_count == 1);
    assert(identity.devices[0].is_primary == 1);
    assert(strcmp(identity.display_name, "Test User") == 0);

    /* Anonymous -- no display name */
    ca_circle_identity_t anon;
    memset(&anon, 0, sizeof(anon));
    anon.tier = CA_IDENTITY_ANONYMOUS;
    anon.display_name = NULL;
    assert(anon.display_name == NULL);
    assert(anon.device_count == 0);

    /* Tiers */
    assert(CA_IDENTITY_ANONYMOUS    == 0);
    assert(CA_IDENTITY_PSEUDONYMOUS == 1);
    assert(CA_IDENTITY_VERIFIED     == 2);

    /* Max devices constant */
    assert(CA_MAX_DEVICES == 32);

    /* Pseudonymous tier */
    ca_circle_identity_t pseudo;
    memset(&pseudo, 0, sizeof(pseudo));
    pseudo.tier = CA_IDENTITY_PSEUDONYMOUS;
    pseudo.display_name = "alias";
    assert(pseudo.tier == CA_IDENTITY_PSEUDONYMOUS);
    assert(strcmp(pseudo.display_name, "alias") == 0);

    printf("All identity tests passed.\n");
    return 0;
}
