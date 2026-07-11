#ifndef CIRCLE_AI_BUILDFARM_H
#define CIRCLE_AI_BUILDFARM_H

/*
 * buildfarm.h — CircleAI.BuildFarm (C11 port of Contracts.cs +
 * InMemoryBuildFarm.cs + NullImplementations.cs).
 *
 *   Enums   : BuildAgentKind { Linux, Mac, Windows, Android, Ios };
 *             BuildJobPhase { Pending, Running, Succeeded, Failed }.
 *   Records : BuildAgent(AgentId, BuildAgentKind Kind, Os, string? Hardware);
 *             BuildJob(JobId, AgentId, Repo, Branch, BuildJobPhase Phase,
 *                      DateTimeOffset StartUtc);
 *             BuildArtifact(ArtifactId, JobId, Name, ReadOnlyMemory<byte> Payload).
 *   Pool    : IBuildAgentPool -> InMemoryBuildAgentPool — Register(agent),
 *               Acquire(kind) returns the first free agent of that kind and marks
 *               it busy (null when none free), Release(agentId), List(). BackendId
 *               "in-memory". Null pool -> Acquire null, List empty.
 *   Runner  : IBuildJobRunner -> InMemoryBuildJobRunner — Start(agentId, repo,
 *               branch) mints "job-{n}" in Running, Get(jobId) -> job?,
 *               Complete(jobId, success) -> Succeeded/Failed (unknown -> error).
 *               BackendId "in-memory". Null runner -> failed job.
 *   Store   : IBuildArtifactStore -> InMemoryBuildArtifactStore — Save(artifact)
 *               (keyed by ArtifactId), Get(artifactId) -> artifact?. BackendId
 *               "in-memory". Null store -> Save no-op, Get null.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Nullable
 * Hardware via has_*. Payload is an owned byte copy. StartUtc as int64 Unix ms
 * UTC. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    CA_BF_KIND_LINUX   = 0,
    CA_BF_KIND_MAC     = 1,
    CA_BF_KIND_WINDOWS = 2,
    CA_BF_KIND_ANDROID = 3,
    CA_BF_KIND_IOS     = 4
} ca_bf_agent_kind_t;

typedef enum {
    CA_BF_PHASE_PENDING   = 0,
    CA_BF_PHASE_RUNNING   = 1,
    CA_BF_PHASE_SUCCEEDED = 2,
    CA_BF_PHASE_FAILED    = 3
} ca_bf_job_phase_t;

/* BuildAgent(AgentId, Kind, Os, string? Hardware). */
typedef struct {
    char              *agent_id;     /* owned, non-null */
    ca_bf_agent_kind_t kind;
    char              *os;           /* owned, non-null */
    bool               has_hardware; /* false == C# null Hardware */
    char              *hardware;     /* owned, valid only when has_* */
} ca_bf_agent_t;

void ca_bf_agent_free(ca_bf_agent_t *a);
void ca_bf_agent_free_array(ca_bf_agent_t *arr, size_t count);

/* BuildJob(JobId, AgentId, Repo, Branch, Phase, StartUtc). */
typedef struct {
    char             *job_id;    /* owned, non-null */
    char             *agent_id;  /* owned, non-null */
    char             *repo;      /* owned, non-null */
    char             *branch;    /* owned, non-null */
    ca_bf_job_phase_t phase;
    int64_t           start_utc_ms;
} ca_bf_job_t;

void ca_bf_job_free(ca_bf_job_t *j);

/* BuildArtifact(ArtifactId, JobId, Name, ReadOnlyMemory<byte> Payload). */
typedef struct {
    char    *artifact_id; /* owned, non-null */
    char    *job_id;      /* owned, non-null */
    char    *name;        /* owned, non-null */
    uint8_t *payload;     /* owned (may be NULL when len 0) */
    size_t   payload_len;
} ca_bf_artifact_t;

void ca_bf_artifact_free(ca_bf_artifact_t *a);

/* ── IBuildAgentPool -> InMemoryBuildAgentPool ──────────────────────────── */

typedef struct ca_bf_pool ca_bf_pool_t;

ca_bf_pool_t *ca_bf_pool_create(void); /* NULL on OOM */
void ca_bf_pool_destroy(ca_bf_pool_t *p);
const char *ca_bf_pool_backend_id(const ca_bf_pool_t *p); /* "in-memory" */

/* Register(agent) — keyed by AgentId (replace). 0 / -1. */
int ca_bf_pool_register(ca_bf_pool_t *p, const ca_bf_agent_t *agent);
/* Acquire(kind) -> first free agent of that kind into *out (owned; free with
 * ca_bf_agent_free), marking it busy; true. false when none free / bad args. */
bool ca_bf_pool_acquire(ca_bf_pool_t *p, ca_bf_agent_kind_t kind,
                        ca_bf_agent_t *out);
/* Release(agentId) — clears busy. 0 / -1 on bad args (null/empty). */
int ca_bf_pool_release(ca_bf_pool_t *p, const char *agent_id);
/* List() -> fresh owned array (*out_count) in registration order. NULL + 0
 * empty; NULL + SIZE_MAX on error. */
ca_bf_agent_t *ca_bf_pool_list(const ca_bf_pool_t *p, size_t *out_count);

const char *ca_bf_null_pool_backend_id(void); /* "null" */

/* ── IBuildJobRunner -> InMemoryBuildJobRunner ──────────────────────────── */

typedef struct ca_bf_runner ca_bf_runner_t;

ca_bf_runner_t *ca_bf_runner_create(void); /* NULL on OOM */
void ca_bf_runner_destroy(ca_bf_runner_t *r);
const char *ca_bf_runner_backend_id(const ca_bf_runner_t *r); /* "in-memory" */

/* Start(agentId, repo, branch, now_ms) -> fill *out (owned) with a Running job
 * "job-{n}". now_ms is the StartUtc clock. 0 on success, -1 on bad args
 * (null/empty) or OOM. */
int ca_bf_runner_start(ca_bf_runner_t *r, const char *agent_id, const char *repo,
                       const char *branch, int64_t now_ms, ca_bf_job_t *out);
/* Get(jobId) -> fresh copy into *out, true; false on miss/bad args. */
bool ca_bf_runner_get(const ca_bf_runner_t *r, const char *job_id,
                      ca_bf_job_t *out);
/* Complete(jobId, success) -> Phase Succeeded/Failed. 0 on success, -1 on
 * unknown job / bad args. */
int ca_bf_runner_complete(ca_bf_runner_t *r, const char *job_id, bool success);

const char *ca_bf_null_runner_backend_id(void); /* "null" */
/* Null runner Start -> failed job {JobId Guid.Empty, Phase Failed}. 0 / -1. */
int ca_bf_null_runner_start(const char *agent_id, const char *repo,
                            const char *branch, ca_bf_job_t *out);

/* ── IBuildArtifactStore -> InMemoryBuildArtifactStore ──────────────────── */

typedef struct ca_bf_store ca_bf_store_t;

ca_bf_store_t *ca_bf_store_create(void); /* NULL on OOM */
void ca_bf_store_destroy(ca_bf_store_t *s);
const char *ca_bf_store_backend_id(const ca_bf_store_t *s); /* "in-memory" */

/* Save(artifact) — keyed by ArtifactId (replace). 0 on success, -1 on bad args
 * (null / empty ArtifactId) or OOM. */
int ca_bf_store_save(ca_bf_store_t *s, const ca_bf_artifact_t *artifact);
/* Get(artifactId) -> fresh copy into *out, true; false on miss/bad args. */
bool ca_bf_store_get(const ca_bf_store_t *s, const char *artifact_id,
                     ca_bf_artifact_t *out);

const char *ca_bf_null_store_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_BUILDFARM_H */
