/*
 * buildfarm.c — CircleAI.BuildFarm (C11 port).
 *
 * Pool: agents keyed by AgentId + a parallel busy flag; Acquire takes the first
 * free of a kind. Runner: jobs keyed by JobId, Start mints "job-{n}". Store:
 * artifacts keyed by ArtifactId with owned byte payloads.
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/buildfarm.h"
#include "board_common.h"

#include <stdio.h>

/* ── byte-blob helper ───────────────────────────────────────────────────── */

static bool bytes_copy(uint8_t **out, const uint8_t *src, size_t n) {
    *out = NULL;
    if (n == 0) return true;
    uint8_t *b = (uint8_t *)malloc(n);
    if (!b) return false;
    if (src) memcpy(b, src, n);
    else memset(b, 0, n);
    *out = b;
    return true;
}

/* ── BuildAgent ─────────────────────────────────────────────────────────── */

void ca_build_agent_free(ca_build_agent_t *a) {
    if (!a) return;
    free(a->agent_id);
    free(a->os);
    free(a->hardware);
    a->agent_id = a->os = a->hardware = NULL;
    a->has_hardware = false;
}
void ca_build_agent_free_array(ca_build_agent_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_build_agent_free(&arr[i]);
    free(arr);
}
static bool agent_copy(ca_build_agent_t *dst, const ca_build_agent_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->kind = src->kind;
    dst->agent_id = cab_strdup_empty(src->agent_id);
    dst->os       = cab_strdup_empty(src->os);
    bool ok = dst->agent_id && dst->os;
    if (ok && src->has_hardware) {
        dst->hardware = cab_strdup_empty(src->hardware);
        ok = dst->hardware != NULL;
        dst->has_hardware = ok;
    }
    if (!ok) { ca_build_agent_free(dst); return false; }
    return true;
}

/* ── BuildJob ───────────────────────────────────────────────────────────── */

void ca_build_job_free(ca_build_job_t *j) {
    if (!j) return;
    free(j->job_id);
    free(j->assigned_agent_id);
    free(j->repository);
    free(j->git_ref);
    j->job_id = j->assigned_agent_id = j->repository = j->git_ref = NULL;
}
static bool job_copy(ca_build_job_t *dst, const ca_build_job_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->phase        = src->phase;
    dst->started_unix = src->started_unix;
    dst->job_id   = cab_strdup_empty(src->job_id);
    dst->assigned_agent_id = cab_strdup_empty(src->assigned_agent_id);
    dst->repository     = cab_strdup_empty(src->repository);
    dst->git_ref   = cab_strdup_empty(src->git_ref);
    if (!dst->job_id || !dst->assigned_agent_id || !dst->repository || !dst->git_ref) {
        ca_build_job_free(dst);
        return false;
    }
    return true;
}

/* ── BuildArtifact ──────────────────────────────────────────────────────── */

void ca_build_artifact_free(ca_build_artifact_t *a) {
    if (!a) return;
    free(a->artifact_id);
    free(a->job_id);
    free(a->name);
    free(a->bytes);
    memset(a, 0, sizeof(*a));
}
static bool artifact_copy(ca_build_artifact_t *dst, const ca_build_artifact_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->artifact_id = cab_strdup_empty(src->artifact_id);
    dst->job_id      = cab_strdup_empty(src->job_id);
    dst->name        = cab_strdup_empty(src->name);
    bool ok = dst->artifact_id && dst->job_id && dst->name;
    if (ok) ok = bytes_copy(&dst->bytes, src->bytes, src->byte_count);
    if (ok) dst->byte_count = src->byte_count;
    if (!ok) { ca_build_artifact_free(dst); return false; }
    return true;
}

/* ── InMemoryBuildAgentPool ─────────────────────────────────────────────── */

typedef struct {
    ca_build_agent_t agent; /* owned */
    bool          busy;
} pool_slot_t;

struct ca_build_pool {
    pool_slot_t *slots;
    size_t       count, cap;
};

ca_build_pool_t *ca_build_pool_create(void) {
    return (ca_build_pool_t *)calloc(1, sizeof(ca_build_pool_t));
}
void ca_build_pool_destroy(ca_build_pool_t *p) {
    if (!p) return;
    for (size_t i = 0; i < p->count; ++i) ca_build_agent_free(&p->slots[i].agent);
    free(p->slots);
    free(p);
}
const char *ca_build_pool_backend_id(const ca_build_pool_t *p) {
    (void)p; return "in-memory";
}

int ca_build_pool_register(ca_build_pool_t *p, const ca_build_agent_t *agent) {
    if (!p || !agent) return -1;
    for (size_t i = 0; i < p->count; ++i) {
        if (cab_ord_eq(p->slots[i].agent.agent_id, agent->agent_id)) {
            ca_build_agent_t copy;
            if (!agent_copy(&copy, agent)) return -1;
            ca_build_agent_free(&p->slots[i].agent);
            p->slots[i].agent = copy;
            return 0;
        }
    }
    ca_build_agent_t copy;
    if (!agent_copy(&copy, agent)) return -1;
    if (p->count == p->cap) {
        size_t nc = p->cap ? p->cap * 2 : 4;
        void *n = realloc(p->slots, nc * sizeof(*p->slots));
        if (!n) { ca_build_agent_free(&copy); return -1; }
        p->slots = (pool_slot_t *)n;
        p->cap = nc;
    }
    p->slots[p->count].agent = copy;
    p->slots[p->count].busy = false;
    p->count++;
    return 0;
}

bool ca_build_pool_acquire(ca_build_pool_t *p, ca_build_agent_kind_t kind,
                        ca_build_agent_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!p || !out) return false;
    for (size_t i = 0; i < p->count; ++i) {
        if (p->slots[i].agent.kind == kind && !p->slots[i].busy) {
            if (!agent_copy(out, &p->slots[i].agent)) return false;
            p->slots[i].busy = true; /* _busy.TryAdd */
            return true;
        }
    }
    return false;
}

int ca_build_pool_release(ca_build_pool_t *p, const char *agent_id) {
    if (!p || cab_is_ws(agent_id)) return -1;
    for (size_t i = 0; i < p->count; ++i)
        if (cab_ord_eq(p->slots[i].agent.agent_id, agent_id))
            p->slots[i].busy = false;
    return 0;
}

ca_build_agent_t *ca_build_pool_list(const ca_build_pool_t *p, size_t *out_count) {
    if (!out_count) return NULL;
    if (!p) { *out_count = (size_t)-1; return NULL; }
    if (p->count == 0) { *out_count = 0; return NULL; }
    ca_build_agent_t *out = (ca_build_agent_t *)calloc(p->count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < p->count; ++i) {
        if (!agent_copy(&out[i], &p->slots[i].agent)) {
            ca_build_agent_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = p->count;
    return out;
}

const char *ca_build_null_pool_backend_id(void) { return "null"; }

/* ── InMemoryBuildJobRunner ─────────────────────────────────────────────── */

struct ca_build_runner {
    ca_build_job_t *jobs;
    size_t       count, cap;
    long         seq;
};

ca_build_runner_t *ca_build_runner_create(void) {
    return (ca_build_runner_t *)calloc(1, sizeof(ca_build_runner_t));
}
void ca_build_runner_destroy(ca_build_runner_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->count; ++i) ca_build_job_free(&r->jobs[i]);
    free(r->jobs);
    free(r);
}
const char *ca_build_runner_backend_id(const ca_build_runner_t *r) {
    (void)r; return "in-memory";
}

int ca_build_runner_start(ca_build_runner_t *r, const char *agent_id, const char *repo,
                       const char *branch, int64_t now_ms, ca_build_job_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!r || !out) return -1;
    if (cab_is_ws(agent_id) || cab_is_ws(repo) || cab_is_ws(branch)) return -1;

    char job_id[32];
    snprintf(job_id, sizeof(job_id), "job-%ld", ++r->seq);

    ca_build_job_t job;
    memset(&job, 0, sizeof(job));
    job.job_id = job_id;
    job.assigned_agent_id = (char *)agent_id;
    job.repository = (char *)repo;
    job.git_ref = (char *)branch;
    job.phase = CA_BUILD_PHASE_BUILDING;
    job.started_unix = now_ms;

    if (r->count == r->cap) {
        size_t nc = r->cap ? r->cap * 2 : 4;
        void *n = realloc(r->jobs, nc * sizeof(*r->jobs));
        if (!n) return -1;
        r->jobs = (ca_build_job_t *)n;
        r->cap = nc;
    }
    ca_build_job_t stored;
    if (!job_copy(&stored, &job)) return -1;
    r->jobs[r->count++] = stored;

    return job_copy(out, &job) ? 0 : -1;
}

bool ca_build_runner_get(const ca_build_runner_t *r, const char *job_id,
                      ca_build_job_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!r || cab_is_ws(job_id) || !out) return false;
    for (size_t i = 0; i < r->count; ++i)
        if (cab_ord_eq(r->jobs[i].job_id, job_id))
            return job_copy(out, &r->jobs[i]);
    return false;
}

int ca_build_runner_complete(ca_build_runner_t *r, const char *job_id, bool success) {
    if (!r || cab_is_ws(job_id)) return -1;
    for (size_t i = 0; i < r->count; ++i) {
        if (cab_ord_eq(r->jobs[i].job_id, job_id)) {
            r->jobs[i].phase = success ? CA_BUILD_PHASE_SUCCEEDED : CA_BUILD_PHASE_FAILED;
            return 0;
        }
    }
    return -1; /* InvalidOperationException: unknown job */
}

const char *ca_build_null_runner_backend_id(void) { return "null"; }

int ca_build_null_runner_start(const char *agent_id, const char *repo,
                            const char *branch, ca_build_job_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out) return -1;
    ca_build_job_t job;
    memset(&job, 0, sizeof(job));
    job.job_id = (char *)"00000000-0000-0000-0000-000000000000"; /* Guid.Empty */
    job.assigned_agent_id = (char *)(agent_id ? agent_id : "");
    job.repository = (char *)(repo ? repo : "");
    job.git_ref = (char *)(branch ? branch : "");
    job.phase = CA_BUILD_PHASE_FAILED;
    job.started_unix = INT64_MIN; /* DateTimeOffset.MinValue surrogate */
    return job_copy(out, &job) ? 0 : -1;
}

/* ── InMemoryBuildArtifactStore ─────────────────────────────────────────── */

struct ca_build_store {
    ca_build_artifact_t *items;
    size_t            count, cap;
};

ca_build_store_t *ca_build_store_create(void) {
    return (ca_build_store_t *)calloc(1, sizeof(ca_build_store_t));
}
void ca_build_store_destroy(ca_build_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_build_artifact_free(&s->items[i]);
    free(s->items);
    free(s);
}
const char *ca_build_store_backend_id(const ca_build_store_t *s) {
    (void)s; return "in-memory";
}

int ca_build_store_save(ca_build_store_t *s, const ca_build_artifact_t *artifact) {
    if (!s || !artifact || cab_is_ws(artifact->artifact_id)) return -1;
    for (size_t i = 0; i < s->count; ++i) {
        if (cab_ord_eq(s->items[i].artifact_id, artifact->artifact_id)) {
            ca_build_artifact_t copy;
            if (!artifact_copy(&copy, artifact)) return -1;
            ca_build_artifact_free(&s->items[i]);
            s->items[i] = copy;
            return 0;
        }
    }
    ca_build_artifact_t copy;
    if (!artifact_copy(&copy, artifact)) return -1;
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->items, nc * sizeof(*s->items));
        if (!n) { ca_build_artifact_free(&copy); return -1; }
        s->items = (ca_build_artifact_t *)n;
        s->cap = nc;
    }
    s->items[s->count++] = copy;
    return 0;
}

bool ca_build_store_get(const ca_build_store_t *s, const char *artifact_id,
                     ca_build_artifact_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || cab_is_ws(artifact_id) || !out) return false;
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->items[i].artifact_id, artifact_id))
            return artifact_copy(out, &s->items[i]);
    return false;
}

const char *ca_build_null_store_backend_id(void) { return "null"; }
