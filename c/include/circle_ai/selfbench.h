#ifndef CIRCLE_AI_SELFBENCH_H
#define CIRCLE_AI_SELFBENCH_H

/*
 * selfbench.h — CircleAI.SelfBench (C11 port).
 *
 * Ports src/CircleAI.SelfBench 1:1:
 *   BenchContracts.cs    — BenchScoring enum; BenchTask / BenchResult /
 *                          BenchSummary records; IBenchScorer + BuiltInScorers
 *                          (exact / substring / regex / numeric-tolerance).
 *   BenchRunner.cs       — BenchRunner: run a suite against an IAIService
 *                          (ca_ai_service_t, host_ai.h), score each task, and
 *                          aggregate pass-count / mean score / p50 / p95 latency.
 *   AbBenchRunner.cs     — AbBenchRunner: run a suite against a baseline + a
 *                          candidate service and emit a promote/reject verdict
 *                          gated by RegressionGateConfig (critical-task guard).
 *   BenchSuiteRegistry.cs— BenchSuiteRegistry: named suites + a built-in
 *                          "default" 10-task suite.
 *
 * The bench harness drives the single butler contract IAIService, which in this
 * C port is `ca_ai_service_t` (host_ai.h): ca_ai_service_is_ready / _start /
 * _ask are the seam RunOne uses. Async ValueTask/Task collapse to synchronous
 * returns; DateTimeOffset carries as Unix ms UTC.
 *
 * The C# RegexScorer uses System.Text.RegularExpressions; C has no guaranteed
 * <regex.h>, so this port ships a small, self-contained, case-insensitive
 * backtracking matcher (literals, '.', char classes [...] with ranges + the
 * \s \d \w escapes, anchors ^ $, quantifiers * + and {m,}, groups (...), and
 * alternation | at top level and within groups). It covers exactly the constructs
 * the default suite's four regex patterns use and fails gracefully (score 0)
 * on anything unsupported rather than crashing.
 *
 * TWO intentional omissions relative to the C# (see the .c for detail):
 *   - BenchSuiteRegistry.RegisterFromFile (JSON-file suite loading) is NOT ported:
 *     it needs a JSON dependency this pure-libc port does not carry. In-code
 *     registration via ca_bench_suite_registry_register is the supported path.
 *   - Per-task MaxLatencyMs cancellation is NOT enforced: the ca_ai_service_ask
 *     seam has no per-call timeout/cancellation token, so latency is measured but
 *     a task is never cancelled mid-generation. MaxLatencyMs is still carried on
 *     ca_bench_task_t for fidelity.
 *
 * Conventions: ca_ prefix, _t types, opaque handles forward-declared here and
 * defined in the .c, strdup-owning fields with matching *_free / *_free_array,
 * deep-copy helpers, errors via NULL / SIZE_MAX / false. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "host_ai.h"   /* ca_ai_service_t + is_ready/start/ask seam */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * BenchScoring (BenchContracts.cs)
 * =========================================================================== */

typedef enum {
    CA_BENCH_EXACT_MATCH       = 0,
    CA_BENCH_SUBSTRING         = 1,
    CA_BENCH_REGEX             = 2,
    CA_BENCH_NUMERIC_TOLERANCE = 3,
    CA_BENCH_CUSTOM_SCORER     = 4
} ca_bench_scoring_t;

/* ===========================================================================
 * BenchTask (BenchContracts.cs)
 * =========================================================================== */

/* record BenchTask(Id, Suite, Prompt, Expected, Scoring=ExactMatch,
 * NumericTolerance=0.0, CustomScorerName=null, MaxLatencyMs=30000,
 * IsCritical=false). All strings owned; custom_scorer_name may be NULL. */
typedef struct {
    char              *id;                  /* owned */
    char              *suite;               /* owned */
    char              *prompt;              /* owned */
    char              *expected;            /* owned */
    ca_bench_scoring_t scoring;
    double             numeric_tolerance;
    char              *custom_scorer_name;  /* owned, or NULL */
    double             max_latency_ms;      /* carried; not enforced (see header) */
    bool               is_critical;
} ca_bench_task_t;

/* Deep-free one task's owned strings (not the struct). */
void ca_bench_task_free(ca_bench_task_t *t);
/* Free an owned array of tasks (each task's strings + the block). */
void ca_bench_task_free_array(ca_bench_task_t *arr, size_t count);
/* Deep-copy src into dst (dst assumed uninitialised). Returns false on OOM. */
bool ca_bench_task_copy(ca_bench_task_t *dst, const ca_bench_task_t *src);

/* ===========================================================================
 * BenchResult (BenchContracts.cs)
 * =========================================================================== */

typedef struct {
    char  *task_id;         /* owned */
    char  *suite;           /* owned */
    char  *actual_answer;   /* owned */
    double score;           /* 0..1 */
    double latency_ms;
    bool   passed;
    char  *failure_reason;  /* owned, or NULL */
} ca_bench_result_t;

void ca_bench_result_free(ca_bench_result_t *r);
void ca_bench_result_free_array(ca_bench_result_t *arr, size_t count);

/* ===========================================================================
 * BenchSummary (BenchContracts.cs)
 * ===========================================================================
 *
 * PerTaskScore (IReadOnlyDictionary<string,double>) is modelled as parallel
 * owned arrays per_task_id[i] -> per_task_score[i]. completed_at_utc is Unix ms.
 */
typedef struct {
    char   *run_id;            /* owned */
    char   *suite_id;          /* owned */
    int     task_count;
    int     pass_count;
    double  mean_score;
    double  p50_latency_ms;
    double  p95_latency_ms;
    char  **per_task_id;       /* owned array of owned strings */
    double *per_task_score;    /* owned parallel array */
    size_t  per_task_count;
    int64_t completed_at_utc;  /* Unix ms UTC */
} ca_bench_summary_t;

/* Free the summary and all its owned members (frees the struct itself — summaries
 * are returned by pointer from the runners). */
void ca_bench_summary_free(ca_bench_summary_t *s);

/* ===========================================================================
 * Scorers (BuiltInScorers + custom-scorer seam)
 * ===========================================================================
 *
 * Each built-in returns a double in 0..1. Extra scorers are registered by name
 * on the runner and invoked via this callback seam (expected/actual/task borrowed
 * for the call; user is the value passed at registration).
 */
typedef double (*ca_bench_scorer_fn)(const char *expected, const char *actual,
                                     const ca_bench_task_t *task, void *user);

/* The four built-ins, exposed for direct use / testing. `task` supplies
 * numeric_tolerance for the numeric scorer; it may be NULL for the others. */
double ca_bench_scorer_exact(const char *expected, const char *actual,
                             const ca_bench_task_t *task, void *user);
double ca_bench_scorer_substring(const char *expected, const char *actual,
                                 const ca_bench_task_t *task, void *user);
double ca_bench_scorer_regex(const char *expected, const char *actual,
                             const ca_bench_task_t *task, void *user);
double ca_bench_scorer_numeric_tolerance(const char *expected, const char *actual,
                                         const ca_bench_task_t *task, void *user);

/* Case-insensitive regex match over the supported subset (see header comment).
 * Returns true on a match, false on no-match OR an unsupported/invalid pattern
 * (mirrors RegexScorer catching ArgumentException -> 0). Exposed for tests. */
bool ca_bench_regex_is_match(const char *pattern, const char *input);

/* ===========================================================================
 * BenchRunner (BenchRunner.cs)
 * =========================================================================== */

typedef struct ca_bench_runner ca_bench_runner_t;

/* BenchRunner(extraScorers?). Registers the four built-ins; extra scorers (name +
 * fn + user, parallel arrays of length extra_count) override built-ins on a name
 * clash (StringComparer.OrdinalIgnoreCase). Pass extra_count 0 / NULLs for none.
 * NULL on OOM. */
ca_bench_runner_t *ca_bench_runner_create(const char *const *extra_names,
                                          const ca_bench_scorer_fn *extra_fns,
                                          void *const *extra_users,
                                          size_t extra_count);
void ca_bench_runner_destroy(ca_bench_runner_t *runner);

/*
 * RunAsync(suiteId, tasks, ai). Starts the service when not ready, runs each task
 * (measuring latency, applying the resolved scorer, passed = score >= 1-1e-9),
 * and aggregates. A NULL ask result fails the task ("generation returned null");
 * a CustomScorer whose name is unregistered fails the task ("Custom scorer not
 * registered: <name>"). Returns a freshly-allocated summary (free with
 * ca_bench_summary_free), or NULL on a NULL arg / OOM.
 */
ca_bench_summary_t *ca_bench_runner_run(ca_bench_runner_t *runner,
                                        const char *suite_id,
                                        const ca_bench_task_t *tasks, size_t count,
                                        ca_ai_service_t *ai);

/* ===========================================================================
 * AbBenchRunner (AbBenchRunner.cs)
 * =========================================================================== */

/* record RegressionGateConfig(MinMeanScoreImprovement=0.01,
 * MaxP95LatencyRegressionMs=250.0, MaxCriticalRegressions=0). */
typedef struct {
    double min_mean_score_improvement;
    double max_p95_latency_regression_ms;
    int    max_critical_regressions;
} ca_regression_gate_config_t;

/* Fill with the C# defaults. */
void ca_regression_gate_config_init(ca_regression_gate_config_t *gate);

/* record AbVerdict(ShouldPromote, BaselineSummary, CandidateSummary,
 * MeanScoreDelta, P95LatencyDeltaMs, CriticalRegressions, Reason). Owns both
 * summaries, the criticals array, and the reason string. */
typedef struct {
    bool                should_promote;
    ca_bench_summary_t *baseline_summary;    /* owned */
    ca_bench_summary_t *candidate_summary;   /* owned */
    double              mean_score_delta;
    double              p95_latency_delta_ms;
    char              **critical_regressions; /* owned array of owned strings */
    size_t              critical_regression_count;
    char               *reason;               /* owned */
} ca_bench_ab_verdict_t;

/* Free the verdict and all its owned members (frees the struct itself). */
void ca_bench_ab_verdict_free(ca_bench_ab_verdict_t *v);

typedef struct ca_ab_bench_runner ca_ab_bench_runner_t;

/* AbBenchRunner(runner). `runner` is borrowed (caller owns it for the AB runner's
 * lifetime). NULL on NULL runner / OOM. */
ca_ab_bench_runner_t *ca_ab_bench_runner_create(ca_bench_runner_t *runner);
void ca_ab_bench_runner_destroy(ca_ab_bench_runner_t *ab);

/*
 * CompareAsync(suiteId, tasks, baseline, candidate, gate?). Runs the suite against
 * "<suiteId>@baseline" and "<suiteId>@candidate", computes the mean/p95 deltas and
 * the per-critical-task regression list, and applies the gate. gate may be NULL
 * (C# defaults). Returns a freshly-allocated verdict (free with ca_bench_ab_verdict_free)
 * or NULL on a NULL arg / OOM.
 */
ca_bench_ab_verdict_t *ca_ab_bench_runner_compare(ca_ab_bench_runner_t *ab,
                                            const char *suite_id,
                                            const ca_bench_task_t *tasks, size_t count,
                                            ca_ai_service_t *baseline,
                                            ca_ai_service_t *candidate,
                                            const ca_regression_gate_config_t *gate);

/* ===========================================================================
 * BenchSuiteRegistry (BenchSuiteRegistry.cs)
 * =========================================================================== */

typedef struct ca_bench_suite_registry ca_bench_suite_registry_t;

/* BenchSuiteRegistry() — registers "default" -> the built-in 10-task suite.
 * NULL on OOM. */
ca_bench_suite_registry_t *ca_bench_suite_registry_create(void);
void ca_bench_suite_registry_destroy(ca_bench_suite_registry_t *reg);

/* Register(suiteId, tasks) — deep-copies; replaces an existing suite. suiteId
 * required (non-null / non-whitespace). Returns 0 on success, -1 on bad args/OOM.
 * (RegisterFromFile is intentionally omitted — see the header comment.) */
int ca_bench_suite_registry_register(ca_bench_suite_registry_t *reg,
                                     const char *suite_id,
                                     const ca_bench_task_t *tasks, size_t count);

/* Get(suiteId) -> a freshly-allocated deep-copied task array (*out_count) for the
 * suite, or NULL + *out_count 0 when absent (Array.Empty). NULL + SIZE_MAX on
 * error/OOM. Caller frees with ca_bench_task_free_array. */
ca_bench_task_t *ca_bench_suite_registry_get(const ca_bench_suite_registry_t *reg,
                                             const char *suite_id,
                                             size_t *out_count);

/* SuiteIds -> a freshly-allocated array of owned suite-id strings (*out_count).
 * NULL + *out_count 0 when empty; NULL + SIZE_MAX on error. Free each string then
 * the block (or use ca_bench_suite_ids_free). */
char **ca_bench_suite_registry_suite_ids(const ca_bench_suite_registry_t *reg,
                                         size_t *out_count);
/* Free an id array from ca_bench_suite_registry_suite_ids. */
void ca_bench_suite_ids_free(char **ids, size_t count);

/* Build the built-in "default" suite as a freshly-allocated task array
 * (*out_count == 10). Exposed for direct use / testing. NULL + SIZE_MAX on OOM. */
ca_bench_task_t *ca_bench_build_default_suite(size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SELFBENCH_H */
