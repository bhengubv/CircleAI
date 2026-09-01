#ifndef CIRCLE_AI_BUILDFARM_H
#define CIRCLE_AI_BUILDFARM_H

/*
 * buildfarm.h - CircleAI.BuildFarm (C11).
 *
 * Jobs, the agents that run them, and where the artifacts land.
 *
 * WHY A POOL AND NOT A QUEUE. The agents are not interchangeable: a Swift build
 * needs a Mac, an Android build needs the NDK, and a job handed to an agent that
 * cannot run it does not fail fast — it fails ten minutes in, having downloaded
 * a toolchain first. So an agent declares what it IS, and the pool matches.
 *
 * PHASES ARE REPORTED, not inferred from timing. "Queued for eleven minutes" and
 * "compiling for eleven minutes" look identical from outside, and only one of
 * them means somebody should go and look at the pool.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, errors via NULL / false / SIZE_MAX. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* What an agent can build. An agent is one kind, not a set: a machine that
 * claims everything is a machine that is wrong about something. */
typedef enum {
    CA_BUILD_AGENT_LINUX_X64 = 0,
    CA_BUILD_AGENT_LINUX_ARM64,
    CA_BUILD_AGENT_MACOS_ARM64,
    CA_BUILD_AGENT_WINDOWS_X64,
    CA_BUILD_AGENT_ANDROID,
    CA_BUILD_AGENT_IOS
} ca_build_agent_kind_t;

/* Where a job has got to.
 *
 * FAILED and CANCELLED are separate terminal states, deliberately: one needs
 * somebody to read a log, the other needs nothing at all, and a single "not
 * succeeded" makes every cancelled job look like a problem. */
typedef enum {
    CA_BUILD_PHASE_QUEUED = 0,
    CA_BUILD_PHASE_FETCHING,
    CA_BUILD_PHASE_BUILDING,
    CA_BUILD_PHASE_TESTING,
    CA_BUILD_PHASE_PUBLISHING,
    CA_BUILD_PHASE_SUCCEEDED,
    CA_BUILD_PHASE_FAILED,
    CA_BUILD_PHASE_CANCELLED
} ca_build_job_phase_t;

const char *ca_build_job_phase_name(ca_build_job_phase_t phase);

/* True for succeeded, failed and cancelled. A caller polling a job needs one
 * question to ask, not three. */
bool ca_build_job_phase_is_terminal(ca_build_job_phase_t phase);

typedef struct {
    char *agent_id;
    char *display_name;
    ca_build_agent_kind_t kind;
    /* False takes an agent out of rotation without deleting it — a machine down
     * for maintenance should not be a config change somebody has to undo. */
    bool available;
    /* How many jobs it will take at once. */
    size_t max_concurrent;
} ca_build_agent_t;

void ca_build_agent_free(ca_build_agent_t *agent);

typedef struct {
    char *job_id;
    char *repository;
    char *git_ref;
    ca_build_agent_kind_t requires_kind;
    ca_build_job_phase_t phase;
    char *assigned_agent_id;
    /* Why it ended, when it ended badly. Empty otherwise, never a placeholder:
     * "unknown error" in a log is a message that has already wasted somebody's
     * afternoon. */
    char *failure_reason;
    int64_t queued_unix;
    int64_t started_unix;
    int64_t finished_unix;
} ca_build_job_t;

void ca_build_job_free(ca_build_job_t *job);

typedef struct {
    char *artifact_id;
    char *job_id;
    char *name;
    uint8_t *bytes;
    size_t byte_count;
    /* Hex SHA-256 of the bytes, computed on store. An artifact whose hash is
     * taken by the producer proves nothing about what the store holds. */
    char *sha256;
    int64_t stored_unix;
} ca_build_artifact_t;

void ca_build_artifact_free(ca_build_artifact_t *artifact);

/* ── the pool ─────────────────────────────────────────────────────────────── */

typedef struct ca_build_agent_pool {
    void *state;
    bool (*add)(void *state, const ca_build_agent_t *agent);
    /* An agent that can run `kind` and is not full, or NULL. Caller frees. */
    ca_build_agent_t *(*acquire)(void *state, ca_build_agent_kind_t kind);
    void (*release)(void *state, const char *agent_id);
    ca_build_agent_t *(*list)(void *state, size_t *out_count);
    void (*free_fn)(void *state);
} ca_build_agent_pool_t;

void ca_build_agent_pool_free(ca_build_agent_pool_t *pool);

ca_build_agent_pool_t *ca_in_memory_build_agent_pool_new(void);

/* Accepts agents and never hands one back. For a host with no farm wired: jobs
 * queue honestly rather than appearing to run. */
ca_build_agent_pool_t *ca_null_build_agent_pool_new(void);

/* ── the runner ───────────────────────────────────────────────────────────── */

typedef struct ca_build_job_runner {
    void *state;
    /* Returns the job id, or NULL. */
    char *(*submit)(void *state, const ca_build_job_t *job);
    ca_build_job_t *(*get)(void *state, const char *job_id);
    ca_build_job_t *(*list)(void *state, size_t *out_count);
    bool (*advance)(void *state, const char *job_id, ca_build_job_phase_t phase,
                    const char *failure_reason);
    bool (*cancel)(void *state, const char *job_id);
    void (*free_fn)(void *state);
} ca_build_job_runner_t;

void ca_build_job_runner_free(ca_build_job_runner_t *runner);

ca_build_job_runner_t *ca_in_memory_build_job_runner_new(ca_build_agent_pool_t *pool);

ca_build_job_runner_t *ca_null_build_job_runner_new(void);

/* ── artifacts ────────────────────────────────────────────────────────────── */

typedef struct ca_build_artifact_store {
    void *state;
    /* Returns the artifact id, or NULL. Hashes the bytes on the way in. */
    char *(*store)(void *state, const char *job_id, const char *name,
                   const uint8_t *bytes, size_t byte_count);
    ca_build_artifact_t *(*get)(void *state, const char *artifact_id);
    ca_build_artifact_t *(*for_job)(void *state, const char *job_id, size_t *out_count);
    void (*free_fn)(void *state);
} ca_build_artifact_store_t;

void ca_build_artifact_store_free(ca_build_artifact_store_t *store);

ca_build_artifact_store_t *ca_in_memory_build_artifact_store_new(void);

/* Accepts and discards, and SAYS SO by returning NULL rather than a plausible
 * id. An id that does not resolve is worse than no id. */
ca_build_artifact_store_t *ca_null_build_artifact_store_new(void);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_BUILDFARM_H */
