// Testing.kt
//
// Kotlin port of CircleAI.Testing — the C# reference is the EXACT spec.
// Shipped snapshot-testing infrastructure: a golden store, a line-diff snapshot
// comparer that normalises platform churn, deterministic-id + frozen-clock
// helpers. (This is a runtime library, not xUnit test code.)
//
// Covers (C# file -> Kotlin type):
//   Contracts.cs          -> SnapshotDiff, ISnapshotComparer, IGoldenStore
//   InMemoryTesting.cs     -> InMemoryGoldenStore, LineDiffSnapshotComparer
//   NullImplementations.cs -> NullSnapshotComparer, NullGoldenStore
//   TestingHelpers.cs      -> DeterministicIds, FrozenClock
//
// Fidelity notes:
//   * C# `record` -> `data class`; `ValueTask`/`ValueTask<T>` -> `suspend fun`.
//   * `ConcurrentDictionary` (Ordinal) -> `ConcurrentHashMap`.
//   * Blank testId/key/seed -> `IllegalArgumentException` (C# ArgumentException).
//   * `Normalise` folds CRLF/CR to LF and trims trailing whitespace per line.
//   * `DeterministicIds.fromSeed` reproduces the C# FNV-1a 32-bit hash exactly:
//     32-bit unsigned arithmetic via `Int`, formatted as 8-hex-digit lowercase.
//   * `FrozenClock` uses `java.time.Instant` (+ `Duration` for advance).

package com.bhengubv.circleai.testing

import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** Result of comparing an actual snapshot against its golden. */
data class SnapshotDiff(val equal: Boolean, val diff: String?)

/** Compares an actual value against a stored golden and reports the diff. */
interface ISnapshotComparer {
    val backendId: String
    suspend fun compare(testId: String, actual: String): SnapshotDiff
}

/** Read/write backing store for golden snapshots. */
interface IGoldenStore {
    val backendId: String
    suspend fun read(testId: String): String?
    suspend fun write(testId: String, golden: String)
}

// =====================================================================
// InMemoryTesting (InMemoryTesting.cs)
// =====================================================================

/** In-memory [IGoldenStore]. */
class InMemoryGoldenStore : IGoldenStore {
    private val items = ConcurrentHashMap<String, String>()
    override val backendId: String get() = "in-memory"

    override suspend fun read(testId: String): String? {
        require(testId.isNotBlank()) { "testId required" }
        return items[testId]
    }

    override suspend fun write(testId: String, golden: String) {
        require(testId.isNotBlank()) { "testId required" }
        items[testId] = golden
    }
}

/**
 * [ISnapshotComparer] that normalises line endings + trailing whitespace before
 * diffing, so common platform churn doesn't false-positive.
 */
class LineDiffSnapshotComparer(private val store: IGoldenStore) : ISnapshotComparer {
    override val backendId: String get() = "line-diff"

    override suspend fun compare(testId: String, actual: String): SnapshotDiff {
        require(testId.isNotBlank()) { "testId required" }
        val golden = store.read(testId) ?: return SnapshotDiff(false, "(no golden)")
        val a = normalise(actual)
        val g = normalise(golden)
        return if (a == g) SnapshotDiff(true, null) else SnapshotDiff(false, buildDiff(g, a))
    }

    private companion object {
        fun normalise(s: String): String =
            s.replace("\r\n", "\n").replace('\r', '\n').split('\n')
                .joinToString("\n") { it.trimEnd() }

        fun buildDiff(expected: String, actual: String): String {
            val exp = expected.split('\n')
            val act = actual.split('\n')
            val sb = StringBuilder()
            val n = maxOf(exp.size, act.size)
            for (i in 0 until n) {
                val e = if (i < exp.size) exp[i] else ""
                val a = if (i < act.size) act[i] else ""
                if (e != a) {
                    sb.append('-').append(e).append('\n')
                    sb.append('+').append(a).append('\n')
                }
            }
            return sb.toString()
        }
    }
}

// =====================================================================
// NullImplementations (NullImplementations.cs)
// =====================================================================

/** No-op comparer — always reports a miss, since no golden store is wired. */
class NullSnapshotComparer private constructor() : ISnapshotComparer {
    override val backendId: String get() = "null"
    override suspend fun compare(testId: String, actual: String): SnapshotDiff =
        SnapshotDiff(false, "NullSnapshotComparer — no golden store wired.")

    companion object {
        val Instance = NullSnapshotComparer()
    }
}

/** No-op golden store — reads return null, writes are dropped. */
class NullGoldenStore private constructor() : IGoldenStore {
    override val backendId: String get() = "null"
    override suspend fun read(testId: String): String? = null
    override suspend fun write(testId: String, golden: String) {}

    companion object {
        val Instance = NullGoldenStore()
    }
}

// =====================================================================
// TestingHelpers (TestingHelpers.cs)
// =====================================================================

/** Deterministic id generator: a stable id from a seed via FNV-1a. */
object DeterministicIds {
    /**
     * Returns `"$prefix-XXXXXXXX"` where the suffix is the 8-hex-digit FNV-1a
     * 32-bit hash of [seed]. Reproduces the C# `unchecked` uint arithmetic.
     */
    fun fromSeed(seed: String, prefix: String = "test"): String {
        require(seed.isNotBlank()) { "seed required" }
        var h = -0x7ee3623b // 2166136261 (0x811C9DC5) as a signed 32-bit Int
        for (c in seed) {
            h = h xor c.code
            h *= 0x01000193 // 16777619 (FNV prime)
        }
        val hex = (h.toLong() and 0xFFFFFFFFL).toString(16).padStart(8, '0')
        return "$prefix-$hex"
    }
}

/** A manually-advanced clock for deterministic time in tests. */
class FrozenClock(start: Instant) {
    var now: Instant = start
        private set

    fun advance(by: Duration) {
        now = now.plus(by)
    }

    fun setTo(to: Instant) {
        now = to
    }
}
