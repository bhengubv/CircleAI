#ifndef CIRCLE_AI_DEPBOT_H
#define CIRCLE_AI_DEPBOT_H

/*
 * depbot.h — CircleAI.DepBot (C11 port of Contracts.cs + InMemoryDepBot.cs +
 * NullImplementations.cs).
 *
 * The C# analyzer walks a repo for manifests; here the filesystem is the
 * injected boundary and the host registers manifest content (path + text). The
 * manifest parsers (package.json deps/devDependencies, requirements.txt,
 * Cargo.toml [dependencies], *.csproj PackageReference) and the manifest-rewrite
 * updater port faithfully over that in-memory content.
 *
 *   Records : Dependency(Ecosystem, Name, CurrentVersion, LatestVersion?);
 *             DependencyUpdate(Ecosystem, Name, FromVersion, ToVersion,
 *                              bool IsBreaking).
 *   Analyzer: IDependencyAnalyzer -> ManifestDependencyAnalyzer. AddManifest(
 *               path, content) (type from filename: package.json / requirements
 *               .txt / Cargo.toml / *.csproj; node_modules paths skipped for
 *               package.json). Scan(repoPath) -> the parsed dependencies (the
 *               repoPath argument is accepted for parity; all registered
 *               manifests are scanned). BackendId "manifest".
 *   Updater : IDependencyUpdater -> TextRewriteDependencyUpdater.
 *               ProposeUpdates(repoPath) -> empty (no invented LatestVersion);
 *               ApplyUpdate(repoPath, update) rewrites nuget / npm / pypi
 *               manifest entries in place. BackendId "text-rewrite".
 *   Null variants return empty / no-op.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Dependency(Ecosystem, Name, CurrentVersion, LatestVersion?). */
typedef struct {
    char *ecosystem;       /* owned, non-null: npm/pypi/cargo/nuget */
    char *name;            /* owned, non-null */
    char *current_version; /* owned, non-null (may be "") */
    char *latest_version;  /* owned, or NULL */
} ca_dependency_t;

void ca_dependency_free(ca_dependency_t *d);
void ca_dependency_free_array(ca_dependency_t *arr, size_t count);

/* DependencyUpdate(Ecosystem, Name, FromVersion, ToVersion, IsBreaking). */
typedef struct {
    char *ecosystem;    /* owned, non-null */
    char *name;         /* owned, non-null */
    char *from_version; /* owned, non-null */
    char *to_version;   /* owned, non-null */
    bool  is_breaking;
} ca_dependency_update_t;

void ca_dependency_update_free(ca_dependency_update_t *u);
void ca_dependency_update_free_array(ca_dependency_update_t *arr, size_t count);

/* ── IDependencyAnalyzer -> ManifestDependencyAnalyzer ──────────────────── */

typedef struct ca_dependency_analyzer ca_dependency_analyzer_t;

ca_dependency_analyzer_t *ca_dependency_analyzer_create(void); /* NULL on OOM */
void ca_dependency_analyzer_destroy(ca_dependency_analyzer_t *a);
const char *ca_dependency_analyzer_backend_id(const ca_dependency_analyzer_t *a);

/* AddManifest(path, content) — the manifest type is inferred from the path's
 * basename. 0 / -1 on bad args / OOM. */
int ca_dependency_analyzer_add_manifest(ca_dependency_analyzer_t *a,
                                        const char *path, const char *content);
/* Scan(repoPath) -> fresh Dependency array parsed from all registered
 * manifests. NULL + 0 empty; NULL + SIZE_MAX on error (repoPath required). */
ca_dependency_t *ca_dependency_analyzer_scan(const ca_dependency_analyzer_t *a,
                                             const char *repo_path,
                                             size_t *out_count);

const char *ca_depbot_null_analyzer_backend_id(void); /* "null" */

/* ── IDependencyUpdater -> TextRewriteDependencyUpdater ──────────────────── */

typedef struct ca_dependency_updater ca_dependency_updater_t;

/* An updater over a shared analyzer (borrowed; ApplyUpdate rewrites its
 * registered manifest content). NULL on a NULL analyzer / OOM. */
ca_dependency_updater_t *ca_dependency_updater_create(ca_dependency_analyzer_t *analyzer);
void ca_dependency_updater_destroy(ca_dependency_updater_t *u);
const char *ca_dependency_updater_backend_id(const ca_dependency_updater_t *u);

/* ProposeUpdates(repoPath) -> always empty (no invented LatestVersion), mirroring
 * the C# TextRewrite updater. NULL + 0; NULL + SIZE_MAX on error (repoPath
 * required). */
ca_dependency_update_t *ca_dependency_updater_propose(
    const ca_dependency_updater_t *u, const char *repo_path, size_t *out_count);
/* ApplyUpdate(repoPath, update) rewrites nuget / npm / pypi entries in place.
 * 0 / -1 on bad args. */
int ca_dependency_updater_apply(ca_dependency_updater_t *u, const char *repo_path,
                                const ca_dependency_update_t *update);

const char *ca_depbot_null_updater_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_DEPBOT_H */
