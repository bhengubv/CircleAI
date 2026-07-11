#ifndef CIRCLE_AI_COMMERCE_XERO_H
#define CIRCLE_AI_COMMERCE_XERO_H

/*
 * commerce_xero.h — CircleAI.Commerce.Integration.Xero (C11 port of
 * XeroPrimitives.cs). OAuth token / tenant / webhook board.
 *
 *   Records : XeroTokens(AccessToken, RefreshToken, ExpiresAtUtc, IdToken);
 *             XeroTenant(TenantId, TenantName, TenantType);
 *             XeroWebhookEvent(TenantId, ResourceType, ResourceId, AtUtc).
 *   Board   : IXeroBoard -> InMemoryXeroBoard.
 *             StoreTokens(userId, t) (userId keyed set), GetTokens(userId) ->
 *             tokens?, TokensExpired(userId, now) (true if no tokens || now >=
 *             ExpiresAtUtc), AddTenant(userId, t) (append to the user's list,
 *             deduped by TenantId), TenantsFor(userId) (the user's tenants, empty
 *             when none), RecordWebhook(e) (appended list), RecentEvents(limit)
 *             (ordered by AtUtc descending, first `limit`; default 20).
 *
 * Conventions: ca_ prefix, _t types, opaque handle, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX.
 * ExpiresAtUtc / AtUtc as int64 Unix ms UTC. Linear arrays, no pthreads.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* XeroTokens(AccessToken, RefreshToken, DateTimeOffset ExpiresAtUtc, IdToken). */
typedef struct {
    char   *access_token;   /* owned, non-null */
    char   *refresh_token;  /* owned, non-null */
    int64_t expires_at_utc_ms;/* DateTimeOffset as Unix ms UTC */
    char   *id_token;       /* owned, non-null */
} ca_xero_tokens_t;

void ca_xero_tokens_free(ca_xero_tokens_t *t);

/* XeroTenant(TenantId, TenantName, TenantType). */
typedef struct {
    char *tenant_id;    /* owned, non-null */
    char *tenant_name;  /* owned, non-null */
    char *tenant_type;  /* owned, non-null */
} ca_xero_tenant_t;

void ca_xero_tenant_free(ca_xero_tenant_t *t);
void ca_xero_tenant_free_array(ca_xero_tenant_t *arr, size_t count);

/* XeroWebhookEvent(TenantId, ResourceType, ResourceId, DateTimeOffset AtUtc). */
typedef struct {
    char   *tenant_id;     /* owned, non-null */
    char   *resource_type; /* owned, non-null */
    char   *resource_id;   /* owned, non-null */
    int64_t at_utc_ms;     /* DateTimeOffset as Unix ms UTC */
} ca_xero_event_t;

void ca_xero_event_free(ca_xero_event_t *e);
void ca_xero_event_free_array(ca_xero_event_t *arr, size_t count);

typedef struct ca_xero_board ca_xero_board_t;

/* InMemoryXeroBoard(). NULL on OOM. */
ca_xero_board_t *ca_xero_board_create(void);
void ca_xero_board_destroy(ca_xero_board_t *b);

/* StoreTokens(userId, t) — deep-copies; userId keyed set. 0 / -1 on bad args/OOM. */
int ca_xero_board_store_tokens(ca_xero_board_t *b, const char *user_id,
                               const ca_xero_tokens_t *t);
/* GetTokens(userId) -> fresh owned copy into *out, true; false on miss. */
bool ca_xero_board_get_tokens(const ca_xero_board_t *b, const char *user_id,
                              ca_xero_tokens_t *out);
/* TokensExpired(userId, now_ms) -> true when no tokens stored OR now_ms >=
 * ExpiresAtUtc. */
bool ca_xero_board_tokens_expired(const ca_xero_board_t *b, const char *user_id,
                                  int64_t now_ms);

/* AddTenant(userId, t) — deep-copies; appended to the user's tenant list, deduped
 * by TenantId. 0 / -1 on bad args/OOM. */
int ca_xero_board_add_tenant(ca_xero_board_t *b, const char *user_id,
                             const ca_xero_tenant_t *t);
/* TenantsFor(userId) -> fresh owned array (*out_count) of the user's tenants in
 * insertion order. NULL + 0 when none; NULL + SIZE_MAX on error. */
ca_xero_tenant_t *ca_xero_board_tenants_for(const ca_xero_board_t *b,
                                            const char *user_id,
                                            size_t *out_count);

/* RecordWebhook(e) — deep-copies; appended list. 0 / -1. */
int ca_xero_board_record_webhook(ca_xero_board_t *b, const ca_xero_event_t *e);
/* RecentEvents(limit) -> fresh owned array (*out_count): events ordered by AtUtc
 * descending, first `limit`. limit < 0 -> SIZE_MAX error; limit 0 -> empty. Use
 * 20 for the C# default. NULL + 0 when empty. */
ca_xero_event_t *ca_xero_board_recent_events(const ca_xero_board_t *b, int limit,
                                             size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_COMMERCE_XERO_H */
