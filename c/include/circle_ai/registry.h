#ifndef CIRCLE_AI_REGISTRY_H
#define CIRCLE_AI_REGISTRY_H

/*
 * registry.h — ModelEntry + check_for_upgrades + write_installed_manifest.
 */

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include "models_v15.h"

typedef struct {
    const char       *name;
    const char       *version;
    const char       *quantization;
    const char       *repo;
    int64_t           total_bytes;
    ca_bundle_file_t *bundle_files;       /* may be NULL when bundle_count == 0 */
    size_t            bundle_count;
    const char       *capabilities;       /* may be NULL */
} ca_model_entry_t;

typedef struct {
    const char       *registry_url;
    int64_t           last_updated_unix_ms;
    ca_model_entry_t *models;             /* caller owns */
    size_t            models_count;
} ca_model_registry_t;

/* ---------------------------------------------------------------------------
 * Manifest IO
 * --------------------------------------------------------------------------- */

/* Writes <model_dir>/installed.json. Returns 0 on success. Best-effort: a
 * non-zero return means the manifest is missing on disk; the caller decides
 * whether to retry. */
int ca_write_installed_manifest(
    const char             *model_dir,
    const char             *model_id,
    const char             *version,
    const char             *repo,                /* may be NULL */
    const ca_bundle_file_t *files,
    size_t                  files_count,
    int64_t                 installed_at_unix_ms);

/* Reads <model_dir>/installed.json into an allocated manifest. Caller frees
 * with ca_installed_manifest_free. Returns 0 on success. */
int ca_read_installed_manifest(
    const char              *model_dir,
    ca_installed_manifest_t *out);

void ca_installed_manifest_free(ca_installed_manifest_t *m);

/* ---------------------------------------------------------------------------
 * Upgrade detection
 * --------------------------------------------------------------------------- */

/* Walks the storage dir and emits an UpgradeInfo for every installed model
 * whose manifest is missing or drifts from the catalog.
 *
 *   out: caller-owned array sized at registry->models_count. Filled with
 *        ca_upgrade_info_t records. The number of valid entries is written
 *        to *out_count.
 *
 * The strings in each emitted record point into the registry / a static
 * "" for installed_version when reason == UNKNOWN. They are valid only as
 * long as the registry stays alive. */
int ca_check_for_upgrades(
    const ca_model_registry_t *registry,
    const char                *storage_directory,
    int64_t                    now_unix_ms,
    ca_upgrade_info_t         *out,
    size_t                    *out_count);

#endif /* CIRCLE_AI_REGISTRY_H */
