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
    /* What the machine IS, as opposed to what it will accept. A job that needs
     * a mac cannot be placed on availability alone. */
    char *os;
    char *hardware;
    /* Whether `hardware` was ever set. An empty string means "nothing special";
     * absent means "nobody said" - and a scheduler must not read the second as
     * the first. */
    bool has_hardware;
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

/* Which machines exist and which are free. OPAQUE: the layout is
 * buildfarm.c's, so adding a scheduling field later is not an ABI break. */
typedef struct ca_build_pool ca_build_pool_t;

ca_build_pool_t *ca_build_pool_create(void);
void ca_build_pool_destroy(ca_build_pool_t *pool);

/* Adds or replaces an agent. Returns the number registered, or negative. */
int ca_build_pool_register(ca_build_pool_t *pool, const ca_build_agent_t *agent);

/* Takes a free agent of `kind`. False when none is free - an ANSWER, not an
 * error: a queue with nothing available is the normal state. */
bool ca_build_pool_acquire(ca_build_pool_t *pool, ca_build_agent_kind_t kind,
                           ca_build_agent_t *out);

int ca_build_pool_release(ca_build_pool_t *pool, const char *agent_id);
/* The agents, as a heap array the caller frees with
 * ca_build_agent_free_array. `*out_count` is (size_t)-1 on a bad pool, so
 * "no agents" and "no pool" are distinguishable. */
ca_build_agent_t *ca_build_pool_list(const ca_build_pool_t *pool, size_t *out_count);
const char *ca_build_pool_backend_id(const ca_build_pool_t *pool);
void ca_build_agent_free_array(ca_build_agent_t *agents, size_t count);

/* ── the runner ───────────────────────────────────────────────────────────── */

/* Runs jobs. OPAQUE, for the same reason. */
typedef struct ca_build_runner ca_build_runner_t;

ca_build_runner_t *ca_build_runner_create(void);
void ca_build_runner_destroy(ca_build_runner_t *runner);

/* Starts a job and fills `out`. `now_ms` is passed IN so a caller can test
 * at a fixed time; a runner that reads the clock itself cannot be. */
int ca_build_runner_start(ca_build_runner_t *runner, const char *agent_id,
                          const char *repository, const char *git_ref,
                          int64_t now_ms, ca_build_job_t *out);
bool ca_build_runner_get(const ca_build_runner_t *runner, const char *job_id,
                         ca_build_job_t *out);
int ca_build_runner_complete(ca_build_runner_t *runner, const char *job_id,
                             bool success);

/* Refuses, and says so. The default when no farm is wired: a build that never
 * starts beats one reporting a job id nothing will ever run. */
int ca_build_null_runner_start(const char *agent_id, const char *repository,
                               const char *git_ref, ca_build_job_t *out);
const char *ca_build_null_runner_backend_id(void);

/* ── artifacts ────────────────────────────────────────────────────────────── */

/* Keeps what builds produced. OPAQUE, for the same reason. */
typedef struct ca_build_store ca_build_store_t;

ca_build_store_t *ca_build_store_create(void);
void ca_build_store_destroy(ca_build_store_t *store);

/* Stores and hashes on the way in. Returns the number held, or negative - an
 * artifact with no bytes is refused rather than recorded as empty. */
int ca_build_store_save(ca_build_store_t *store, const ca_build_artifact_t *artifact);
bool ca_build_store_get(const ca_build_store_t *store, const char *artifact_id,
                        ca_build_artifact_t *out);
const char *ca_build_null_store_backend_id(void);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_BUILDFARM_H */
