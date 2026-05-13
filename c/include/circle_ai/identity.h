#ifndef CIRCLE_AI_IDENTITY_H
#define CIRCLE_AI_IDENTITY_H

#include <stdint.h>
#include <stdbool.h>

typedef enum {
    CA_IDENTITY_ANONYMOUS = 0,
    CA_IDENTITY_PSEUDONYMOUS,
    CA_IDENTITY_VERIFIED
} ca_identity_tier_t;

typedef struct {
    char    device_id[37];    /* UUID string */
    const char* device_name;
    int64_t registered_at;    /* unix ms */
    bool    is_primary;
} ca_registered_device_t;

#define CA_MAX_DEVICES 32

typedef struct {
    char               identity_id[37]; /* UUID string */
    ca_identity_tier_t tier;
    const char*        display_name;    /* NULL = anonymous */
    int64_t            created_at;      /* unix ms */
    ca_registered_device_t devices[CA_MAX_DEVICES];
    int                device_count;
} ca_circle_identity_t;

#endif /* CIRCLE_AI_IDENTITY_H */
