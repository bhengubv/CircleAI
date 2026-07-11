#ifndef CIRCLE_AI_TESTING_H
#define CIRCLE_AI_TESTING_H

/*
 * testing.h — CircleAI.Testing (C11 port).
 *
 * Ports src/CircleAI.Testing 1:1:
 *   Contracts.cs          — SnapshotDiff record; ISnapshotComparer / IGoldenStore.
 *   InMemoryTesting.cs    — InMemoryGoldenStore (BackendId "in-memory") +
 *                           LineDiffSnapshotComparer (BackendId "line-diff",
 *                           normalises line endings + trailing whitespace then
 *                           emits a -expected/+actual line diff).
 *   NullImplementations.cs— NullGoldenStore / NullSnapshotComparer (BackendId
 *                           "null").
 *   TestingHelpers.cs     — DeterministicIds.FromSeed (FNV-1a 32-bit) +
 *                           FrozenClock (advance/set an injected DateTimeOffset).
 *
 * The C# async methods complete synchronously (ValueTask), so the seams here are
 * plain synchronous calls. C# ArgumentException / ArgumentNullException guards map
 * to NULL / -1 / false returns (no throwing). SnapshotDiff.Diff (nullable) maps to
 * a possibly-NULL owned `diff` string. DateTimeOffset / TimeSpan carry as int64
 * Unix-ms / ms-delta.
 *
 * Conventions: ca_ prefix, _t types, opaque handles forward-declared here and
 * defined in the .c, strdup-owning fields with matching *_free, deep-copy getters,
 * errors via NULL / -1 / false. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * SnapshotDiff (Contracts.cs)
 * =========================================================================== */

/* record SnapshotDiff(bool Equal, string? Diff). `diff` is owned and is NULL when
 * the C# Diff is null (i.e. the snapshots matched). */
typedef struct {
    bool  equal;
    char *diff;   /* owned, or NULL */
} ca_snapshot_diff_t;

/* Free the owned `diff` field (does not free the struct). */
void ca_snapshot_diff_free(ca_snapshot_diff_t *d);

/* ===========================================================================
 * IGoldenStore — InMemoryGoldenStore + NullGoldenStore
 * =========================================================================== */

typedef struct ca_golden_store ca_golden_store_t;

/* InMemoryGoldenStore() (BackendId "in-memory"). NULL on OOM. */
ca_golden_store_t *ca_golden_store_inmemory_create(void);
/* NullGoldenStore (BackendId "null"; Read -> NULL; Write -> no-op). NULL on OOM. */
ca_golden_store_t *ca_golden_store_null_create(void);
void ca_golden_store_destroy(ca_golden_store_t *store);

/* BackendId ("in-memory" or "null"). */
const char *ca_golden_store_backend_id(const ca_golden_store_t *store);

/* ReadAsync(testId) -> a freshly-allocated copy of the stored golden (caller
 * frees), or NULL when absent, on the Null store, or on a bad arg (testId
 * null/whitespace — the C# ArgumentException). */
char *ca_golden_store_read(ca_golden_store_t *store, const char *test_id);

/* WriteAsync(testId, golden) — deep-copies; replaces an existing entry. testId
 * required (non-null / non-whitespace) and golden required (non-null) or the write
 * is rejected. Returns 0 on success (a no-op success on the Null store), -1 on a
 * bad arg / OOM. */
int ca_golden_store_write(ca_golden_store_t *store, const char *test_id,
                          const char *golden);

/* ===========================================================================
 * ISnapshotComparer — LineDiffSnapshotComparer + NullSnapshotComparer
 * =========================================================================== */

typedef struct ca_snapshot_comparer ca_snapshot_comparer_t;

/* LineDiffSnapshotComparer(store) (BackendId "line-diff"). `store` is borrowed
 * (the caller owns it for the comparer lifetime). NULL on NULL store / OOM. */
ca_snapshot_comparer_t *ca_snapshot_comparer_linediff_create(ca_golden_store_t *store);
/* NullSnapshotComparer (BackendId "null"). NULL on OOM. */
ca_snapshot_comparer_t *ca_snapshot_comparer_null_create(void);
void ca_snapshot_comparer_destroy(ca_snapshot_comparer_t *cmp);

/* BackendId ("line-diff" or "null"). */
const char *ca_snapshot_comparer_backend_id(const ca_snapshot_comparer_t *cmp);

/*
 * CompareAsync(testId, actual) -> fills *out (owned; free with
 * ca_snapshot_diff_free) and returns true. Returns false (leaving *out zeroed) on
 * a bad arg (testId null/whitespace, actual null — the C# guards) or OOM.
 *
 * line-diff: golden = store.Read(testId). No golden -> {false,"(no golden)"}.
 * Otherwise both sides are normalised (CRLF/CR -> LF, each line TrimEnd'd) and
 * compared ordinally: equal -> {true,NULL}; else -> {false, "-exp\n+act\n"...}.
 * null: always {false, "NullSnapshotComparer — no golden store wired."}.
 */
bool ca_snapshot_comparer_compare(ca_snapshot_comparer_t *cmp, const char *test_id,
                                  const char *actual, ca_snapshot_diff_t *out);

/* ===========================================================================
 * DeterministicIds (TestingHelpers.cs)
 * =========================================================================== */

/*
 * DeterministicIds.FromSeed(seed, prefix="test"). FNV-1a 32-bit over the seed's
 * bytes (unchecked uint wraparound), formatted "<prefix>-<h:x8>". prefix NULL uses
 * the C# default "test". Returns a freshly-allocated string (caller frees), or
 * NULL when seed is null/whitespace (the C# ArgumentException) or on OOM.
 */
char *ca_deterministic_id_from_seed(const char *seed, const char *prefix);

/* ===========================================================================
 * FrozenClock (TestingHelpers.cs)
 * =========================================================================== */

/* A frozen, manually-advanced clock. DateTimeOffset carried as Unix ms UTC;
 * TimeSpan deltas as ms. Plain struct — no allocation. */
typedef struct {
    int64_t now_ms;
} ca_frozen_clock_t;

/* FrozenClock(start). */
void    ca_frozen_clock_init(ca_frozen_clock_t *clk, int64_t start_ms);
/* Now getter. */
int64_t ca_frozen_clock_now(const ca_frozen_clock_t *clk);
/* Advance(by): Now += by. */
void    ca_frozen_clock_advance(ca_frozen_clock_t *clk, int64_t delta_ms);
/* SetTo(to): Now = to. */
void    ca_frozen_clock_set_to(ca_frozen_clock_t *clk, int64_t to_ms);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_TESTING_H */
