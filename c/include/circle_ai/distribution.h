#ifndef CIRCLE_AI_DISTRIBUTION_H
#define CIRCLE_AI_DISTRIBUTION_H

/*
 * distribution.h — CircleAI.Distribution (C11 port of the four scoped Ubiquity
 * distribution rails: IAppStoreSubmitter / ISignedDeltaUpdater /
 * IOemPreloadCatalog / ICarrierPreloadCatalog + their records + Default impls).
 *
 *   Records : AppStorePackage(StoreName, PackagePath, Version,
 *                             IReadOnlyDictionary<string,string> Metadata);
 *             DeltaUpdate(Channel, FromVersion, ToVersion, byte[] Payload,
 *                         byte[] Signature).
 *   Submit  : IAppStoreSubmitter -> DefaultAppStoreSubmitter — Submit(package)
 *               validates StoreName/PackagePath/Version non-empty, returns false
 *               for an unknown store, else records under "{StoreName}/{Version}"
 *               and returns true. Submitted snapshot.
 *   Updater : ISignedDeltaUpdater -> DefaultSignedDeltaUpdater(hmacKey>=16) —
 *               Apply(update) false when Channel/ToVersion blank or FromVersion
 *               mismatches the channel's current version; else verifies
 *               HMAC-SHA256 over "Channel|FromVersion|ToVersion|" + Payload
 *               (constant-time), advances the channel to ToVersion, returns true.
 *               CurrentVersion(channel).
 *   OEM     : IOemPreloadCatalog -> DefaultOemPreloadCatalog — Partners =
 *               {Tecno, Itel, Samsung mid-tier, Xiaomi, Huawei}.
 *   Carrier : ICarrierPreloadCatalog -> DefaultCarrierPreloadCatalog — Carriers =
 *               {MTN, Vodacom, Cell C, Telkom, Safaricom, Airtel}.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Payload/
 * Signature owned byte copies. Linear arrays, no pthreads. Pure C11 + libc
 * (a self-contained SHA-256 + HMAC lives in the .c).
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* AppStorePackage(StoreName, PackagePath, Version, Metadata). Metadata is a
 * parallel key/value string array. */
typedef struct {
    char  *store_name;   /* owned, non-null */
    char  *package_path; /* owned, non-null */
    char  *version;      /* owned, non-null */
    char **meta_keys;    /* owned array (meta_count) */
    char **meta_values;  /* owned array (meta_count) */
    size_t meta_count;
} ca_dist_app_package_t;

void ca_dist_app_package_free(ca_dist_app_package_t *p);
void ca_dist_app_package_free_array(ca_dist_app_package_t *arr, size_t count);

/* DeltaUpdate(Channel, FromVersion, ToVersion, byte[] Payload,
 * byte[] Signature). */
typedef struct {
    char    *channel;      /* owned, non-null */
    char    *from_version; /* owned, non-null */
    char    *to_version;   /* owned, non-null */
    uint8_t *payload;      /* owned (may be NULL when len 0) */
    size_t   payload_len;
    uint8_t *signature;    /* owned (may be NULL when len 0) */
    size_t   signature_len;
} ca_dist_delta_update_t;

void ca_dist_delta_update_free(ca_dist_delta_update_t *u);

/* ── IAppStoreSubmitter -> DefaultAppStoreSubmitter ─────────────────────── */

typedef struct ca_dist_app_submitter ca_dist_app_submitter_t;

ca_dist_app_submitter_t *ca_dist_app_submitter_create(void); /* NULL on OOM */
void ca_dist_app_submitter_destroy(ca_dist_app_submitter_t *s);

/* Submit(package) — validates fields, false for an unknown store, else records
 * under "{StoreName}/{Version}" and true. Writes the accepted flag into
 * *accepted. Returns 0 on success, -1 on bad args (null / empty required field)
 * or OOM. */
int ca_dist_app_submitter_submit(ca_dist_app_submitter_t *s,
                                 const ca_dist_app_package_t *package,
                                 bool *accepted);
/* Submitted -> fresh owned array (*out_count). NULL + 0 empty; NULL + SIZE_MAX
 * on error. Order is insertion order of distinct "{StoreName}/{Version}" keys. */
ca_dist_app_package_t *ca_dist_app_submitter_submitted(
    const ca_dist_app_submitter_t *s, size_t *out_count);

/* ── ISignedDeltaUpdater -> DefaultSignedDeltaUpdater ───────────────────── */

typedef struct ca_dist_delta_updater ca_dist_delta_updater_t;

/* Construct with an HMAC key (>= 16 bytes). NULL on bad args/OOM. */
ca_dist_delta_updater_t *ca_dist_delta_updater_create(const uint8_t *hmac_key,
                                                      size_t hmac_key_len);
void ca_dist_delta_updater_destroy(ca_dist_delta_updater_t *u);

/* Apply(update) — false when Channel/ToVersion blank or FromVersion mismatches
 * the channel's current version, or the HMAC-SHA256 over
 * "Channel|FromVersion|ToVersion|" + Payload does not match Signature; else
 * advances the channel to ToVersion and true. Writes the applied flag into
 * *applied. Returns 0 on success, -1 on bad args/OOM. */
int ca_dist_delta_updater_apply(ca_dist_delta_updater_t *u,
                                const ca_dist_delta_update_t *update,
                                bool *applied);
/* CurrentVersion(channel) -> borrowed string (valid until the next Apply on
 * that channel), or NULL when the channel is unknown. */
const char *ca_dist_delta_updater_current_version(
    const ca_dist_delta_updater_t *u, const char *channel);

/* ── IOemPreloadCatalog / ICarrierPreloadCatalog ────────────────────────── */

/* DefaultOemPreloadCatalog.Partners — borrowed static array. *out_count set. */
const char *const *ca_dist_oem_partners(size_t *out_count);
/* DefaultCarrierPreloadCatalog.Carriers — borrowed static array. *out_count. */
const char *const *ca_dist_carrier_carriers(size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_DISTRIBUTION_H */
