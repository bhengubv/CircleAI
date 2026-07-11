#ifndef CIRCLE_AI_TOOLS_CATALOG_H
#define CIRCLE_AI_TOOLS_CATALOG_H

/*
 * tools_catalog.h — CircleAI.Tools.Catalog (C11 port).
 *
 * Ports (from src/CircleAI.Tools.Catalog):
 *   Contracts.cs             — AuthKind enum; OAuth2Descriptor, ProviderDescriptor,
 *                              CredentialBundle, QuotaPolicy, ToolNamespace records;
 *                              IProviderCatalog / ICredentialStore /
 *                              IOAuth2FlowDriver / IQuotaGuard / IToolNamespaceStore.
 *   InMemoryToolsCatalog.cs  — InMemoryProviderCatalog (substring + tag/capability
 *                              scored search); AesGcmCredentialStore (encrypt at
 *                              rest); OAuth2FlowDriver (authorize-URL builder +
 *                              host token-exchange); SlidingWindowQuotaGuard;
 *                              InMemoryToolNamespaceStore.
 *   NullImplementations.cs   — fail-closed Null* for every contract.
 *
 * ── Boundary dependencies exposed as callback SEAMs (not implemented here) ──
 *   The C# AesGcmCredentialStore does AES-256-GCM + System.Text.Json. Neither is
 *   portable C11 + libc, so the encrypted store takes an encrypt/decrypt SEAM
 *   (ca_cred_encrypt_fn / ca_cred_decrypt_fn) and serialises the bundle with a
 *   self-contained, documented byte format (below) instead of JSON. The OAuth2
 *   token exchange is vendor HTTP in C#; it stays a SEAM (ca_oauth2_exchange_fn),
 *   as does the host client-id lookup (ca_oauth2_client_id_fn). This mirrors the
 *   port's convention: any native/HTTP/crypto boundary is a function pointer.
 *
 * ── CredentialBundle serialization format (v1, little-endian) ──
 *   The encrypted + plain in-memory stores serialise a CredentialBundle to a
 *   length-prefixed byte buffer you control (NOT JSON):
 *     u8   version            = 1
 *     u8   has_expires        (0/1)
 *     i64  expires_at_utc_ms  (valid only when has_expires; else 0)
 *     u32  provider_id_len,  bytes…            (UTF-8, no NUL)
 *     u32  user_id_len,      bytes…
 *     u32  field_count
 *     repeat field_count times:
 *       u32 key_len,   bytes…
 *       u32 value_len, bytes…
 *   All multi-byte integers are stored little-endian. Deserialization validates
 *   the version byte and every length against the remaining buffer; a malformed
 *   buffer fails the Get (false). The encrypted store hands this plaintext to the
 *   encrypt SEAM and stores the returned ciphertext; Get calls the decrypt SEAM
 *   then deserialises. The plain in-memory store stores the serialized bytes
 *   directly (no cipher) so tests run without a crypto seam.
 *
 * ── Clock + state-token decisions (documented divergences from C#) ──
 *   SlidingWindowQuotaGuard uses DateTimeOffset.UtcNow in C#. To stay
 *   deterministic and portable the C TryAcquire takes an explicit now_ms
 *   (Unix ms UTC) — the caller supplies the clock. Pruning drops call
 *   timestamps older than now-60000ms; the daily budget counts timestamps
 *   within the last 24h (now-86400000ms).
 *   OAuth2FlowDriver.StartAsync uses a cryptographically-random 16-byte state
 *   in C#. Portability does not require real randomness for the URL to be valid,
 *   so the C driver derives a base64url state token from a per-driver monotonic
 *   counter mixed with the address of the driver — unique per call, no platform
 *   RNG. (It is not a security boundary; the token is echoed back and checked by
 *   the host's exchange, which is a SEAM.)
 *
 * Conventions: ca_ prefix, _t types, opaque handles (forward-declared here /
 * defined in the .c), strdup-owning fields with matching *_free / *_free_array,
 * deep-copy getters, errors via NULL / count SIZE_MAX / -1. Linear arrays, no
 * hashtable, no pthreads. Ordinal == byte compare; OrdinalIgnoreCase ==
 * ASCII-lowercased byte compare.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * AuthKind + records
 * =========================================================================== */

/* enum AuthKind { None, ApiKey, BearerToken, OAuth2, Basic, Custom }. */
typedef enum {
    CA_AUTH_KIND_NONE         = 0,
    CA_AUTH_KIND_API_KEY      = 1,
    CA_AUTH_KIND_BEARER_TOKEN = 2,
    CA_AUTH_KIND_OAUTH2       = 3,
    CA_AUTH_KIND_BASIC        = 4,
    CA_AUTH_KIND_CUSTOM       = 5
} ca_auth_kind_t;

/* OAuth2Descriptor(AuthorizeUrl, TokenUrl, Scopes[], string? UserInfoUrl). */
typedef struct {
    char  *authorize_url;    /* owned, non-null */
    char  *token_url;        /* owned, non-null */
    char **scopes;           /* owned strings */
    size_t scopes_count;
    char  *user_info_url;    /* owned, NULL ok */
} ca_oauth2_descriptor_t;

/* ProviderDescriptor(ProviderId, DisplayName, Description, string? Homepage,
 * AuthKind Auth, Tags[], Capabilities[], OAuth2Descriptor? OAuth2). */
typedef struct {
    char  *provider_id;      /* owned, non-null */
    char  *display_name;     /* owned, non-null */
    char  *description;      /* owned, non-null */
    char  *homepage;         /* owned, NULL ok */
    ca_auth_kind_t auth;
    char **tags;             /* owned strings */
    size_t tags_count;
    char **capabilities;     /* owned strings */
    size_t capabilities_count;
    ca_oauth2_descriptor_t *oauth2;  /* owned, NULL ok */
} ca_provider_descriptor_t;

void ca_provider_descriptor_free(ca_provider_descriptor_t *p);
void ca_provider_descriptor_free_array(ca_provider_descriptor_t *arr, size_t count);

/* CredentialBundle(ProviderId, UserId, Fields<string,string>,
 * DateTimeOffset? ExpiresAtUtc). Fields are parallel key/value arrays. */
typedef struct {
    char  *provider_id;      /* owned, non-null */
    char  *user_id;          /* owned, non-null */
    char **field_keys;       /* owned strings */
    char **field_values;     /* owned strings (parallel to field_keys) */
    size_t field_count;
    bool    has_expires;
    int64_t expires_at_utc_ms; /* valid only when has_expires */
} ca_credential_bundle_t;

void ca_credential_bundle_free(ca_credential_bundle_t *b);

/* QuotaPolicy(ProviderId, UserId, DailyCallBudget, MaxConcurrent, PerMinuteCap). */
typedef struct {
    char *provider_id;       /* owned, non-null */
    char *user_id;           /* owned, non-null */
    int   daily_call_budget;
    int   max_concurrent;
    int   per_minute_cap;
} ca_quota_policy_t;

void ca_quota_policy_free(ca_quota_policy_t *p);

/* ToolNamespace(NamespaceId, OwnerUserId, ProviderIds[]). */
typedef struct {
    char  *namespace_id;     /* owned, non-null */
    char  *owner_user_id;    /* owned, non-null */
    char **provider_ids;     /* owned strings */
    size_t provider_ids_count;
} ca_tool_namespace_t;

void ca_tool_namespace_free(ca_tool_namespace_t *ns);
void ca_tool_namespace_free_array(ca_tool_namespace_t *arr, size_t count);

/* ===========================================================================
 * IProviderCatalog — InMemory + Null
 * =========================================================================== */

typedef struct ca_provider_catalog ca_provider_catalog_t;

/* InMemoryProviderCatalog() (BackendId "in-memory"). NULL on OOM. */
ca_provider_catalog_t *ca_provider_catalog_inmemory_create(void);
/* NullProviderCatalog (BackendId "null"). NULL on OOM. */
ca_provider_catalog_t *ca_provider_catalog_null_create(void);
void ca_provider_catalog_destroy(ca_provider_catalog_t *cat);

const char *ca_provider_catalog_backend_id(const ca_provider_catalog_t *cat);

/* Register(p) — deep-copies; an existing ProviderId (OrdinalIgnoreCase) is
 * replaced. In-memory only (a no-op reject on the Null catalog). 0 / -1 on bad
 * args / OOM. */
int ca_provider_catalog_register(ca_provider_catalog_t *cat,
                                 const ca_provider_descriptor_t *p);

/* ListProvidersAsync() -> fresh owned array (*out_count) ordered by ProviderId
 * ascending (Ordinal). NULL + *out_count 0 when empty (or on the Null catalog);
 * NULL + SIZE_MAX on error. Caller frees with ca_provider_descriptor_free_array. */
ca_provider_descriptor_t *ca_provider_catalog_list(
    const ca_provider_catalog_t *cat, size_t *out_count);

/* GetProviderAsync(id) -> writes a fresh owned copy into *out and returns true;
 * false (C# null) when absent or on the Null catalog. id required (non-null /
 * non-whitespace): a whitespace/NULL id returns false with *out zeroed. */
bool ca_provider_catalog_get(const ca_provider_catalog_t *cat, const char *id,
                             ca_provider_descriptor_t *out);

/* SearchProvidersAsync(query, topK) -> fresh owned array (*out_count), scored:
 * +3 DisplayName contains query (CI), +1 Description contains, +2 any Tag
 * contains, +2 any Capability contains; keep score>0, order by score desc
 * (stable), first top_k. query must be non-null; top_k must be > 0. NULL +
 * SIZE_MAX on error; NULL + 0 when no hits (or on the Null catalog). Use top_k 8
 * for the C# default. */
ca_provider_descriptor_t *ca_provider_catalog_search(
    const ca_provider_catalog_t *cat, const char *query, int top_k,
    size_t *out_count);

/* ===========================================================================
 * ICredentialStore — Encrypted (SEAM) + plain InMemory + Null
 *
 * "Implementations must encrypt at rest." The encrypted store hands the
 * serialized bundle bytes (format documented at top of file) to an injected
 * encryptor and stores the ciphertext; Get calls the decryptor then
 * deserialises. The plain in-memory store stores the serialized bytes directly
 * (no cipher) so tests without a crypto seam still exercise the surface.
 * =========================================================================== */

typedef struct ca_credential_store ca_credential_store_t;

/* Encrypt SEAM: consume `pt_len` plaintext bytes, malloc `*out_cipher`
 * (`*out_len` bytes), return 0 on success / non-zero on failure. Maps to the C#
 * AesGcm.Encrypt (nonce||tag||ciphertext) — a crypto boundary. */
typedef int (*ca_cred_encrypt_fn)(void *ctx, const uint8_t *plaintext,
                                  size_t pt_len, uint8_t **out_cipher,
                                  size_t *out_len);
/* Decrypt SEAM: consume `c_len` cipher bytes, malloc `*out_plain`
 * (`*out_len` bytes), return 0 on success / non-zero on failure (the C#
 * CryptographicException path -> Get returns null). Maps to AesGcm.Decrypt. */
typedef int (*ca_cred_decrypt_fn)(void *ctx, const uint8_t *cipher, size_t c_len,
                                  uint8_t **out_plain, size_t *out_len);

/* AesGcmCredentialStore surrogate (BackendId "encrypted"). enc + dec required.
 * NULL on OOM / missing seam. */
ca_credential_store_t *ca_credential_store_encrypted_create(
    ca_cred_encrypt_fn enc, ca_cred_decrypt_fn dec, void *ctx);
/* Plain in-memory store (BackendId "in-memory"): stores serialized bytes with no
 * encryption. NULL on OOM. */
ca_credential_store_t *ca_credential_store_inmemory_create(void);
/* NullCredentialStore (BackendId "null"): Upsert no-op, Get NULL, Delete no-op. */
ca_credential_store_t *ca_credential_store_null_create(void);
void ca_credential_store_destroy(ca_credential_store_t *store);

const char *ca_credential_store_backend_id(const ca_credential_store_t *store);

/* UpsertAsync(bundle). bundle required (its ProviderId/UserId non-null). Keyed
 * "<provider>/<user>", replacing any prior bundle. 0 on success; -1 on bad args
 * / serialize failure / encrypt-seam failure. No-op returning 0 on the Null
 * store. */
int ca_credential_store_upsert(ca_credential_store_t *store,
                               const ca_credential_bundle_t *bundle);

/* GetAsync(providerId, userId) -> writes a fresh owned bundle into *out and
 * returns true; false when absent, on bad args (null/whitespace), on a
 * decrypt-seam failure, or on a malformed buffer. *out is zeroed on false.
 * Caller frees *out with ca_credential_bundle_free. */
bool ca_credential_store_get(const ca_credential_store_t *store,
                             const char *provider_id, const char *user_id,
                             ca_credential_bundle_t *out);

/* DeleteAsync(providerId, userId). Removes the "<provider>/<user>" entry if
 * present. 0 on success; -1 on bad args. No-op returning 0 on the Null store. */
int ca_credential_store_delete(ca_credential_store_t *store,
                               const char *provider_id, const char *user_id);

/* ===========================================================================
 * IOAuth2FlowDriver — OAuth2 (SEAMs) + Null
 * =========================================================================== */

typedef struct ca_oauth2_flow_driver ca_oauth2_flow_driver_t;

/* Host client-id resolver: return a malloc'd client id for `provider_id` (the
 * caller frees it), or NULL. Maps to the C# Func<string,string> clientIdFor —
 * a host-config boundary. */
typedef char *(*ca_oauth2_client_id_fn)(void *ctx, const char *provider_id);
/* Token-exchange SEAM: exchange `code` at the redirect URI for a bundle written
 * into *out_bundle; return 0 on success / non-zero on failure. Maps to the C#
 * Func<…, ValueTask<CredentialBundle>> exchange — vendor token-endpoint HTTP.
 * On success the driver owns *out_bundle's fields (caller of complete frees). */
typedef int (*ca_oauth2_exchange_fn)(void *ctx, const char *provider_id,
                                     const char *user_id, const char *code,
                                     const char *redirect_uri,
                                     ca_credential_bundle_t *out_bundle);

/* OAuth2FlowDriver(catalog, clientIdFor, exchange) (BackendId "oauth2"). catalog
 * + both seams required. The driver borrows `catalog` (does not own it). NULL on
 * OOM / missing dependency. */
ca_oauth2_flow_driver_t *ca_oauth2_flow_driver_create(
    ca_provider_catalog_t *catalog, ca_oauth2_client_id_fn client_id_for,
    ca_oauth2_exchange_fn exchange, void *ctx);
/* NullOAuth2FlowDriver (BackendId "null"): Start -> "about:blank";
 * Complete -> error. NULL on OOM. */
ca_oauth2_flow_driver_t *ca_oauth2_flow_driver_null_create(void);
void ca_oauth2_flow_driver_destroy(ca_oauth2_flow_driver_t *drv);

const char *ca_oauth2_flow_driver_backend_id(const ca_oauth2_flow_driver_t *drv);

/* StartAsync(providerId, userId, redirectUri) -> malloc'd authorize URL:
 *   <AuthorizeUrl>?response_type=code&client_id=<enc>&redirect_uri=<enc>
 *   &scope=<enc>&state=<enc>
 * (URL-encoding percent-encodes all but unreserved chars; scope is the
 * space-joined OAuth2.Scopes; state is the counter-derived base64url token).
 * Returns NULL on bad args, an unknown provider, a non-OAuth2 provider, or OOM.
 * On the Null driver returns a malloc'd "about:blank". Caller frees with free(). */
char *ca_oauth2_flow_driver_start(ca_oauth2_flow_driver_t *drv,
                                  const char *provider_id, const char *user_id,
                                  const char *redirect_uri);

/* CompleteAsync(providerId, userId, code, redirectUri) -> writes a fresh owned
 * bundle into *out via the exchange seam and returns true; false on bad args or
 * a seam failure (and always false on the Null driver — "no real provider").
 * Caller frees *out with ca_credential_bundle_free. */
bool ca_oauth2_flow_driver_complete(ca_oauth2_flow_driver_t *drv,
                                    const char *provider_id, const char *user_id,
                                    const char *code, const char *redirect_uri,
                                    ca_credential_bundle_t *out);

/* ===========================================================================
 * IQuotaGuard — SlidingWindow + Null
 * =========================================================================== */

typedef struct ca_quota_guard ca_quota_guard_t;

/* SlidingWindowQuotaGuard() (BackendId "sliding-window"). NULL on OOM. */
ca_quota_guard_t *ca_quota_guard_slidingwindow_create(void);
/* NullQuotaGuard (BackendId "null"). NULL on OOM. */
ca_quota_guard_t *ca_quota_guard_null_create(void);
void ca_quota_guard_destroy(ca_quota_guard_t *g);

const char *ca_quota_guard_backend_id(const ca_quota_guard_t *g);

/* TryAcquireAsync(providerId, userId) at now_ms (Unix ms UTC — the C makes the
 * C# DateTimeOffset.UtcNow clock explicit). No policy for the key => true
 * (unlimited). Else: prune call timestamps older than now-60000ms; if the
 * per-minute list is >= PerMinuteCap -> false; if timestamps within the last 24h
 * are >= DailyCallBudget -> false; if inflight >= MaxConcurrent -> false; else
 * record now, inflight++, true. The Null guard always returns false. */
bool ca_quota_guard_try_acquire(ca_quota_guard_t *g, const char *provider_id,
                                const char *user_id, int64_t now_ms);

/* Release(providerId, userId): inflight-- (floored at 0). No-op on the Null
 * guard or an unknown key. */
void ca_quota_guard_release(ca_quota_guard_t *g, const char *provider_id,
                            const char *user_id);

/* SetPolicyAsync(policy) — deep-copies; replaces any prior policy for the key.
 * 0 / -1 on bad args / OOM. No-op returning 0 on the Null guard. */
int ca_quota_guard_set_policy(ca_quota_guard_t *g, const ca_quota_policy_t *policy);

/* GetPolicyAsync(providerId, userId) -> writes a fresh owned copy into *out and
 * returns true; false when absent (or on the Null guard). *out zeroed on false. */
bool ca_quota_guard_get_policy(const ca_quota_guard_t *g, const char *provider_id,
                               const char *user_id, ca_quota_policy_t *out);

/* ===========================================================================
 * IToolNamespaceStore — InMemory + Null
 * =========================================================================== */

typedef struct ca_tool_namespace_store ca_tool_namespace_store_t;

/* InMemoryToolNamespaceStore() (BackendId "in-memory"). NULL on OOM. */
ca_tool_namespace_store_t *ca_tool_namespace_store_inmemory_create(void);
/* NullToolNamespaceStore (BackendId "null"). NULL on OOM. */
ca_tool_namespace_store_t *ca_tool_namespace_store_null_create(void);
void ca_tool_namespace_store_destroy(ca_tool_namespace_store_t *store);

const char *ca_tool_namespace_store_backend_id(const ca_tool_namespace_store_t *store);

/* UpsertAsync(ns) — deep-copies; replaces by NamespaceId (Ordinal). ns required;
 * NamespaceId non-null / non-whitespace. 0 / -1 on bad args / OOM. No-op
 * returning 0 on the Null store. */
int ca_tool_namespace_store_upsert(ca_tool_namespace_store_t *store,
                                   const ca_tool_namespace_t *ns);

/* GetAsync(namespaceId) -> writes a fresh owned copy into *out and returns true;
 * false when absent or on bad args (null/whitespace). *out zeroed on false. */
bool ca_tool_namespace_store_get(const ca_tool_namespace_store_t *store,
                                 const char *namespace_id,
                                 ca_tool_namespace_t *out);

/* ListForUserAsync(userId) -> fresh owned array (*out_count) of namespaces whose
 * OwnerUserId == userId (Ordinal), in insertion order. NULL + 0 when none (or on
 * the Null store / bad args); NULL + SIZE_MAX on error. Caller frees with
 * ca_tool_namespace_free_array. */
ca_tool_namespace_t *ca_tool_namespace_store_list_for_user(
    const ca_tool_namespace_store_t *store, const char *user_id,
    size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_TOOLS_CATALOG_H */
