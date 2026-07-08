// Consolidation.kt
//
// Hierarchical memory consolidation — the "sleep cycle" engine. Kotlin port of
// CircleAI.Memory.Consolidation (C#): SleepKind, CoreMemoryKind, CoreMemory,
// DailyMemorySummary, SemanticMemoryCluster, PersonaDeltaSnapshot, the four tier
// stores, the HeuristicSummarizer, and the MemoryConsolidator engine. Mirrors
// the just-verified TypeScript reference (memory/consolidation.ts) 1:1.
//
// Promotes episodic → daily → weekly (semantic) → monthly (persona delta) →
// core, and enforces retention. All time decisions go through an injectable
// clock so tests are deterministic. This is the in-memory port: identical
// algorithms and formulas to the C# reference, no persistence.
//
// C# `DateOnly` is represented here as `java.time.LocalDate` — it compares
// correctly with `<`/`<=`/`>=`, and its `toString()` renders "yyyy-MM-dd", so
// the range/idempotency/prune comparisons and the summary text carry over.

package com.bhengubv.circleai.memory.brain

import com.bhengubv.circleai.memory.IPersonaStore
import com.bhengubv.circleai.memory.PersonaState
import java.time.DayOfWeek
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset
import java.util.UUID
import kotlin.math.sqrt

// ─────────────────────────────────────────────────────────────────────────────
// SleepKind + CoreMemoryKind
// ─────────────────────────────────────────────────────────────────────────────

/** Which tier of hierarchical consolidation a tick should run. */
enum class SleepKind {
    /** End-of-day: collapse the day's episodic entries into a DailyMemorySummary. */
    Daily,
    /** End-of-week: cluster the week's daily summaries into semantic topic groups. */
    Weekly,
    /** End-of-month: compute the persona delta and write a PersonaDeltaSnapshot. */
    Monthly,
    /** Caller-initiated pass — runs whichever tiers have work pending. */
    OnDemand,
}

/** Why a memory was promoted to the core tier. */
enum class CoreMemoryKind {
    /** A fact the user explicitly asked the AI to remember. */
    UserAsserted,
    /** Inferred from interaction patterns — a long-standing preference / theme. */
    PatternInferred,
    /** Promoted because of extreme salience. */
    HighSalience,
    /** Promoted by the host directly (profile sync, identity bootstrap). */
    HostProvided,
}

// ─────────────────────────────────────────────────────────────────────────────
// Tier records
// ─────────────────────────────────────────────────────────────────────────────

/**
 * A core memory the AI will not forget. Compact by design. [lastReinforcedUtc]
 * and [reinforcementCount] are mutable (reinforcement bumps them in place),
 * matching the C# `set` accessors; the rest are immutable.
 */
class CoreMemory(
    /** Stable identifier. */
    val id: String = UUID.randomUUID().toString(),
    /** UTC time the memory was committed to core. */
    val createdAtUtc: Instant = Instant.now(),
    /** UTC time the memory was last reinforced (re-asserted, re-cited). Mutable. */
    var lastReinforcedUtc: Instant = Instant.now(),
    /** Short, dense statement of the memory, third-person from the AI's view. */
    val statement: String = "",
    /** How the memory came to be in core. */
    val kind: CoreMemoryKind = CoreMemoryKind.UserAsserted,
    /** Optional topic label (e.g. "family", "career", "health"). */
    val topic: String? = null,
    /** Embedding of the statement for retrieval; null when unavailable. */
    val embedding: FloatArray? = null,
    /** How many times this memory has been reinforced. Mutable. */
    var reinforcementCount: Int = 0,
    /** Trace back to the lower-tier source memory, if one exists. */
    val sourceMemoryId: String? = null,
)

/**
 * Builds a CoreMemory with C#-equivalent defaults (new id, now timestamps).
 * The optional [clock] fixes createdAt/lastReinforced for deterministic tests.
 */
fun createCoreMemory(
    statement: String = "",
    kind: CoreMemoryKind = CoreMemoryKind.UserAsserted,
    topic: String? = null,
    embedding: FloatArray? = null,
    sourceMemoryId: String? = null,
    clock: () -> Instant = { Instant.now() },
): CoreMemory {
    val now = clock()
    return CoreMemory(
        id = UUID.randomUUID().toString(),
        createdAtUtc = now,
        lastReinforcedUtc = now,
        statement = statement,
        kind = kind,
        topic = topic,
        embedding = embedding,
        reinforcementCount = 0,
        sourceMemoryId = sourceMemoryId,
    )
}

/** Compressed record of a single calendar day's worth of episodic memory. */
class DailyMemorySummary(
    /** Stable identifier. */
    val id: String = UUID.randomUUID().toString(),
    /** The calendar day this summary covers (UTC). */
    val day: LocalDate,
    /** UTC time the summary was produced. */
    val generatedAtUtc: Instant = Instant.now(),
    /** Short prose summary of the day's gist. */
    val summary: String = "",
    /** The most salient verbatim exchanges from the day (typically 3–5). */
    val highlightEntries: List<EpisodicEntry> = emptyList(),
    /** Total number of episodic entries collapsed into this summary. */
    val episodeCount: Int = 0,
    /** Aggregated topic weights across the day's exchanges (label → weight). */
    val topicWeights: Map<String, Float> = emptyMap(),
    /** Mean cosine-distance dispersion of the day's embeddings (0..1). */
    val topicDispersion: Double = 0.0,
    /** Salience score 0.0–1.0 assigned by the summariser. */
    val salience: Double = 0.0,
)

/** Builds a DailyMemorySummary with C#-equivalent defaults. */
fun createDailySummary(
    day: LocalDate,
    summary: String = "",
    highlightEntries: List<EpisodicEntry> = emptyList(),
    episodeCount: Int = 0,
    topicWeights: Map<String, Float> = emptyMap(),
    topicDispersion: Double = 0.0,
    salience: Double = 0.0,
    clock: () -> Instant = { Instant.now() },
): DailyMemorySummary = DailyMemorySummary(
    id = UUID.randomUUID().toString(),
    day = day,
    generatedAtUtc = clock(),
    summary = summary,
    highlightEntries = highlightEntries,
    episodeCount = episodeCount,
    topicWeights = topicWeights,
    topicDispersion = topicDispersion,
    salience = salience,
)

/** Topic-coherent cluster of daily summaries — the "semantic memory" tier. */
class SemanticMemoryCluster(
    /** Stable identifier. */
    val id: String = UUID.randomUUID().toString(),
    /** UTC time the cluster was produced. */
    val generatedAtUtc: Instant = Instant.now(),
    /** The week this cluster covers — Monday of that week (UTC). */
    val weekStartingMonday: LocalDate,
    /** Dominant topic label for this cluster. */
    val topic: String = "",
    /** Short prose summary of the cluster's gist. */
    val summary: String = "",
    /** Centroid embedding (mean of constituent embeddings); null when unavailable. */
    val centroidEmbedding: FloatArray? = null,
    /** IDs of the daily summaries that contributed to this cluster. */
    val sourceDailyIds: List<String> = emptyList(),
    /** Aggregate weight of the topic across constituent days. */
    val topicWeight: Float = 0f,
    /** Salience score 0.0–1.0. */
    val salience: Double = 0.0,
)

/** Builds a SemanticMemoryCluster with C#-equivalent defaults. */
fun createSemanticCluster(
    weekStartingMonday: LocalDate,
    topic: String = "",
    summary: String = "",
    centroidEmbedding: FloatArray? = null,
    sourceDailyIds: List<String> = emptyList(),
    topicWeight: Float = 0f,
    salience: Double = 0.0,
    clock: () -> Instant = { Instant.now() },
): SemanticMemoryCluster = SemanticMemoryCluster(
    id = UUID.randomUUID().toString(),
    generatedAtUtc = clock(),
    weekStartingMonday = weekStartingMonday,
    topic = topic,
    summary = summary,
    centroidEmbedding = centroidEmbedding,
    sourceDailyIds = sourceDailyIds,
    topicWeight = topicWeight,
    salience = salience,
)

/** Diff between a PersonaState at the start and end of a consolidation period. */
class PersonaDeltaSnapshot(
    /** Stable identifier. */
    val id: String = UUID.randomUUID().toString(),
    /** UTC time the delta was captured. */
    val generatedAtUtc: Instant = Instant.now(),
    /** Start of the period (UTC). */
    val periodStart: LocalDate,
    /** End of the period (UTC). */
    val periodEnd: LocalDate,
    /** User identifier. */
    val userId: String = "default",
    /** Verbosity at period start. */
    val verbosityBefore: String = "",
    /** Verbosity at period end. */
    val verbosityAfter: String = "",
    /** Formality at period start. */
    val formalityBefore: String = "",
    /** Formality at period end. */
    val formalityAfter: String = "",
    /** New topics that emerged in the period (label → accumulated weight). */
    val newTopics: Map<String, Float> = emptyMap(),
    /** Topics that gained the most weight (label → weight delta). */
    val strengthenedTopics: Map<String, Float> = emptyMap(),
    /** Topics the user explicitly down-voted during the period. */
    val newlyDisfavouredTopics: List<String> = emptyList(),
    /** Net positive minus negative signals across the period. */
    val netSignalDelta: Int = 0,
    /** Total interactions during the period. */
    val interactionsInPeriod: Int = 0,
    /** Short human-readable narrative of how the persona changed. */
    val narrative: String = "",
)

/** Builds a PersonaDeltaSnapshot with C#-equivalent defaults. */
fun createPersonaDelta(
    periodStart: LocalDate,
    periodEnd: LocalDate,
    userId: String = "default",
    verbosityBefore: String = "",
    verbosityAfter: String = "",
    formalityBefore: String = "",
    formalityAfter: String = "",
    newTopics: Map<String, Float> = emptyMap(),
    strengthenedTopics: Map<String, Float> = emptyMap(),
    newlyDisfavouredTopics: List<String> = emptyList(),
    netSignalDelta: Int = 0,
    interactionsInPeriod: Int = 0,
    narrative: String = "",
    clock: () -> Instant = { Instant.now() },
): PersonaDeltaSnapshot = PersonaDeltaSnapshot(
    id = UUID.randomUUID().toString(),
    generatedAtUtc = clock(),
    periodStart = periodStart,
    periodEnd = periodEnd,
    userId = userId,
    verbosityBefore = verbosityBefore,
    verbosityAfter = verbosityAfter,
    formalityBefore = formalityBefore,
    formalityAfter = formalityAfter,
    newTopics = newTopics,
    strengthenedTopics = strengthenedTopics,
    newlyDisfavouredTopics = newlyDisfavouredTopics,
    netSignalDelta = netSignalDelta,
    interactionsInPeriod = interactionsInPeriod,
    narrative = narrative,
)

/** Outcome of a single consolidator tick. */
data class ConsolidationOutcome(
    val kind: SleepKind,
    val dailySummariesProduced: Int,
    val semanticClustersProduced: Int,
    val personaDeltasProduced: Int,
    val corePromotions: Int,
    val episodesPruned: Int,
    val dailiesPruned: Int,
    val semanticsPruned: Int,
    val ranAtUtc: Instant,
)

/** Retention windows + core-promotion thresholds. Defaults mirror the C# reference. */
data class MemoryConsolidationOptions(
    /** Days of episodic entries to retain after they've been summarised. */
    val episodicRetentionDays: Int = 7,
    /** Days of daily summaries to retain after weekly consolidation. */
    val dailyRetentionDays: Int = 30,
    /** Days of semantic clusters to retain. */
    val semanticRetentionDays: Int = 365,
    /** Salience threshold above which daily summaries promote to core. */
    val dailyCorePromotionThreshold: Double = 0.80,
    /** Salience threshold above which weekly clusters promote to core. */
    val weeklyCorePromotionThreshold: Double = 0.75,
)

// ─────────────────────────────────────────────────────────────────────────────
// Day helpers — LocalDate arithmetic (mirrors the TS "YYYY-MM-DD" helpers)
// ─────────────────────────────────────────────────────────────────────────────

/** UTC calendar day of an Instant. */
fun dayKeyOf(instant: Instant): LocalDate = instant.atZone(ZoneOffset.UTC).toLocalDate()

/**
 * The Monday of the week containing [day]. Monday = d minus ((dow+6)%7) days with
 * Sunday=0 — faithful to the C# `((int)DayOfWeek + 6) % 7` (DayOfWeek.Sunday=0).
 * Java's DayOfWeek is Monday=1..Sunday=7, so `value % 7` maps Sunday→0, else 1..6.
 */
fun mondayOf(day: LocalDate): LocalDate {
    val dowSundayZero = day.dayOfWeek.value % 7 // Sun=0, Mon=1 … Sat=6
    val delta = (dowSundayZero + 6) % 7 // Sun=0..Sat=6 → Mon=0..Sun=6
    return day.minusDays(delta.toLong())
}

/** First day of the month containing [day]. */
fun monthFirstDayOf(day: LocalDate): LocalDate = day.withDayOfMonth(1)

// ─────────────────────────────────────────────────────────────────────────────
// Cosine — FULL cosine (differs from the episodic store's dot-only cosine).
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Full cosine similarity: dot / (‖a‖·‖b‖). Returns 0 on a length mismatch or a
 * near-zero denominator. Does NOT assume the vectors are L2-normalised, so it
 * differs from the episodic store's dot-product cosine — both are kept. Matches
 * the C# `CosineSimilarity.Score`: double accumulation, float result.
 */
fun cosineFull(a: FloatArray, b: FloatArray): Float {
    if (a.size != b.size) return 0f
    var dot = 0.0
    var magA = 0.0
    var magB = 0.0
    for (i in a.indices) {
        dot += a[i].toDouble() * b[i].toDouble()
        magA += a[i].toDouble() * a[i].toDouble()
        magB += b[i].toDouble() * b[i].toDouble()
    }
    val denom = sqrt(magA) * sqrt(magB)
    return if (denom < Double.MIN_VALUE) 0f else (dot / denom).toFloat()
}

// ─────────────────────────────────────────────────────────────────────────────
// Store interfaces
// ─────────────────────────────────────────────────────────────────────────────

/** Persistent store for tier-2 daily summaries. */
interface IDailyMemoryStore {
    /** Adds a daily summary. Replaces any existing entry for the same day. */
    suspend fun upsertAsync(summary: DailyMemorySummary)

    /** Returns the summary for the given day, or null when none exists. */
    suspend fun getAsync(day: LocalDate): DailyMemorySummary?

    /** Returns all summaries between fromInclusive and toInclusive (day-ordered). */
    suspend fun getRangeAsync(fromInclusive: LocalDate, toInclusive: LocalDate): List<DailyMemorySummary>

    /** Removes summaries older than cutoff. Returns count removed. */
    suspend fun pruneOlderThanAsync(cutoff: LocalDate): Int

    /** Total summaries currently stored. */
    suspend fun countAsync(): Int
}

/** Persistent store for tier-3 semantic memory clusters. */
interface ISemanticMemoryStore {
    /** Adds a cluster. */
    suspend fun addAsync(cluster: SemanticMemoryCluster)

    /** Returns all clusters for the given week, ordered by topicWeight desc. */
    suspend fun getWeekAsync(weekStartingMonday: LocalDate): List<SemanticMemoryCluster>

    /** Top-topK clusters by centroid cosine similarity; recency fallback when null. */
    suspend fun searchAsync(queryEmbedding: FloatArray?, topK: Int = 5): List<SemanticMemoryCluster>

    /** Removes clusters whose week start is before cutoff. Returns count removed. */
    suspend fun pruneOlderThanAsync(cutoff: LocalDate): Int

    /** Total clusters currently stored. */
    suspend fun countAsync(): Int
}

/** Persistent store for tier-4 persona-delta snapshots. Retained forever. */
interface IPersonaDeltaStore {
    /** Adds a delta snapshot. */
    suspend fun addAsync(snapshot: PersonaDeltaSnapshot)

    /** Returns all snapshots for the given user, ordered by periodStart. */
    suspend fun getForUserAsync(userId: String): List<PersonaDeltaSnapshot>

    /** Total snapshots currently stored. */
    suspend fun countAsync(): Int
}

/** Persistent store for tier-5 core memories — things the AI will not forget. */
interface ICoreMemoryStore {
    /** Adds a core memory. */
    suspend fun addAsync(memory: CoreMemory)

    /** Returns a core memory by id, or null when not found. */
    suspend fun getAsync(id: String): CoreMemory?

    /** Top-topK core memories by embedding cosine; reinforcement-order fallback when null. */
    suspend fun searchAsync(queryEmbedding: FloatArray?, topK: Int = 5): List<CoreMemory>

    /** All core memories in reinforcement order (most reinforced first). */
    suspend fun listAllAsync(): List<CoreMemory>

    /** Increments reinforcementCount and bumps lastReinforcedUtc. No-op when unknown. */
    suspend fun reinforceAsync(id: String)

    /** Removes a core memory. Returns true when one was removed. */
    suspend fun removeAsync(id: String): Boolean

    /** Total core memories currently stored. */
    suspend fun countAsync(): Int
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory store implementations
// ─────────────────────────────────────────────────────────────────────────────

/** In-memory [IDailyMemoryStore]. */
class InMemoryDailyMemoryStore : IDailyMemoryStore {
    private val lock = Any()
    private val store = LinkedHashMap<LocalDate, DailyMemorySummary>()

    override suspend fun upsertAsync(summary: DailyMemorySummary) {
        synchronized(lock) { store[summary.day] = summary }
    }

    override suspend fun getAsync(day: LocalDate): DailyMemorySummary? =
        synchronized(lock) { store[day] }

    override suspend fun getRangeAsync(
        fromInclusive: LocalDate,
        toInclusive: LocalDate,
    ): List<DailyMemorySummary> = synchronized(lock) {
        store.values
            .filter { !it.day.isBefore(fromInclusive) && !it.day.isAfter(toInclusive) }
            .sortedBy { it.day }
    }

    override suspend fun pruneOlderThanAsync(cutoff: LocalDate): Int = synchronized(lock) {
        val toRemove = store.keys.filter { it.isBefore(cutoff) }
        for (d in toRemove) store.remove(d)
        toRemove.size
    }

    override suspend fun countAsync(): Int = synchronized(lock) { store.size }
}

/** In-memory [ISemanticMemoryStore]. */
class InMemorySemanticMemoryStore : ISemanticMemoryStore {
    private val lock = Any()
    private val store = ArrayList<SemanticMemoryCluster>()

    override suspend fun addAsync(cluster: SemanticMemoryCluster) {
        synchronized(lock) { store.add(cluster) }
    }

    override suspend fun getWeekAsync(weekStartingMonday: LocalDate): List<SemanticMemoryCluster> =
        synchronized(lock) {
            store
                .filter { it.weekStartingMonday == weekStartingMonday }
                .sortedByDescending { it.topicWeight }
        }

    override suspend fun searchAsync(queryEmbedding: FloatArray?, topK: Int): List<SemanticMemoryCluster> =
        synchronized(lock) {
            if (queryEmbedding == null) {
                store
                    .sortedByDescending { it.generatedAtUtc }
                    .take(topK)
            } else {
                store
                    .filter { it.centroidEmbedding != null }
                    .map { it to cosineFull(queryEmbedding, it.centroidEmbedding!!) }
                    .sortedByDescending { it.second }
                    .take(topK)
                    .map { it.first }
            }
        }

    override suspend fun pruneOlderThanAsync(cutoff: LocalDate): Int = synchronized(lock) {
        val before = store.size
        store.removeAll { it.weekStartingMonday.isBefore(cutoff) }
        before - store.size
    }

    override suspend fun countAsync(): Int = synchronized(lock) { store.size }
}

/** In-memory [IPersonaDeltaStore]. */
class InMemoryPersonaDeltaStore : IPersonaDeltaStore {
    private val lock = Any()
    private val store = ArrayList<PersonaDeltaSnapshot>()

    override suspend fun addAsync(snapshot: PersonaDeltaSnapshot) {
        synchronized(lock) { store.add(snapshot) }
    }

    override suspend fun getForUserAsync(userId: String): List<PersonaDeltaSnapshot> =
        synchronized(lock) {
            store
                .filter { it.userId == userId }
                .sortedBy { it.periodStart }
        }

    override suspend fun countAsync(): Int = synchronized(lock) { store.size }
}

/** In-memory [ICoreMemoryStore]. */
class InMemoryCoreMemoryStore : ICoreMemoryStore {
    private val lock = Any()
    private val store = LinkedHashMap<String, CoreMemory>()

    override suspend fun addAsync(memory: CoreMemory) {
        synchronized(lock) { store[memory.id] = memory }
    }

    override suspend fun getAsync(id: String): CoreMemory? = synchronized(lock) { store[id] }

    override suspend fun searchAsync(queryEmbedding: FloatArray?, topK: Int): List<CoreMemory> =
        synchronized(lock) {
            if (queryEmbedding == null) {
                store.values.sortedWith(byReinforcement).take(topK)
            } else {
                store.values
                    .filter { it.embedding != null }
                    .map { it to cosineFull(queryEmbedding, it.embedding!!) }
                    .sortedByDescending { it.second }
                    .take(topK)
                    .map { it.first }
            }
        }

    override suspend fun listAllAsync(): List<CoreMemory> =
        synchronized(lock) { store.values.sortedWith(byReinforcement) }

    override suspend fun reinforceAsync(id: String) {
        synchronized(lock) {
            store[id]?.let {
                it.reinforcementCount++
                it.lastReinforcedUtc = Instant.now()
            }
        }
    }

    override suspend fun removeAsync(id: String): Boolean =
        synchronized(lock) { store.remove(id) != null }

    override suspend fun countAsync(): Int = synchronized(lock) { store.size }

    private companion object {
        /** Sort: reinforcementCount desc, then lastReinforcedUtc desc. */
        val byReinforcement: Comparator<CoreMemory> =
            compareByDescending<CoreMemory> { it.reinforcementCount }
                .thenByDescending { it.lastReinforcedUtc }
    }
}

/**
 * In-memory [IPersonaStore]. Keyed by userId; [loadAsync] returns a fresh default
 * PersonaState (stamped with the requested userId) when none has been persisted.
 * Mirrors the TS InMemoryPersonaStore.
 */
class InMemoryPersonaStore : IPersonaStore {
    private val lock = Any()
    private val store = LinkedHashMap<String, PersonaState>()

    override suspend fun loadAsync(userId: String): PersonaState =
        synchronized(lock) { store[userId] ?: PersonaState(userId) }

    override suspend fun saveAsync(persona: PersonaState) {
        synchronized(lock) { store[persona.userId] = persona }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IMemorySummarizer + HeuristicSummarizer
// ─────────────────────────────────────────────────────────────────────────────

/** Produces the text + scores for each consolidation tier. */
interface IMemorySummarizer {
    /** Produces a DailyMemorySummary from the day's episodic entries. */
    suspend fun summarizeDayAsync(day: LocalDate, entries: List<EpisodicEntry>): DailyMemorySummary

    /** Produces zero or more SemanticMemoryCluster records from a week's dailies. */
    suspend fun consolidateWeekAsync(
        weekStartingMonday: LocalDate,
        daysInWeek: List<DailyMemorySummary>,
    ): List<SemanticMemoryCluster>

    /** Computes the PersonaDeltaSnapshot across the period. */
    suspend fun derivePersonaDeltaAsync(
        before: PersonaState,
        after: PersonaState,
        daysInPeriod: List<DailyMemorySummary>,
    ): PersonaDeltaSnapshot
}

/**
 * Heuristic [IMemorySummarizer] that requires no LLM. Produces summaries entirely
 * from structural signals — embedding clustering, topic-weight aggregation,
 * length-and-recency salience. Formulas are identical to the C# HeuristicSummarizer.
 */
class HeuristicSummarizer(
    /** Max high-salience verbatim entries kept per DailyMemorySummary. */
    val highlightCount: Int = 5,
    /** Min contributing days a topic needs across a week to form a cluster. */
    val minDaysPerTopicForCluster: Int = 2,
    private val clock: () -> Instant = { Instant.now() },
) : IMemorySummarizer {

    // ── summarizeDayAsync ─────────────────────────────────────────────────────

    override suspend fun summarizeDayAsync(
        day: LocalDate,
        entries: List<EpisodicEntry>,
    ): DailyMemorySummary {
        if (entries.isEmpty()) {
            return createDailySummary(
                day = day,
                summary = "No exchanges recorded on $day.",
                episodeCount = 0,
                clock = clock,
            )
        }

        val topicWeights = aggregateTopicWeights(entries)
        val dispersion = meanPairwiseCosineDistance(entries)
        val highlights = selectHighlights(entries, highlightCount)
        val salience = computeDailySalience(entries.size, topicWeights, dispersion)
        val summary = buildDailySummaryText(day, entries.size, topicWeights, highlights)

        return createDailySummary(
            day = day,
            summary = summary,
            highlightEntries = highlights,
            episodeCount = entries.size,
            topicWeights = topicWeights,
            topicDispersion = dispersion,
            salience = salience,
            clock = clock,
        )
    }

    // ── consolidateWeekAsync ──────────────────────────────────────────────────

    override suspend fun consolidateWeekAsync(
        weekStartingMonday: LocalDate,
        daysInWeek: List<DailyMemorySummary>,
    ): List<SemanticMemoryCluster> {
        if (daysInWeek.isEmpty()) return emptyList()

        // Tally how many days each topic appeared in and its cumulative weight.
        // Topics compare case-insensitively (StringComparer.OrdinalIgnoreCase);
        // topic labels arrive already lowercased from aggregateTopicWeights.
        val topicToDays = LinkedHashMap<String, MutableList<DailyMemorySummary>>()
        val topicToWeight = LinkedHashMap<String, Float>()

        for (d in daysInWeek) {
            for ((topic, w) in d.topicWeights) {
                topicToDays.getOrPut(topic) { mutableListOf() }.add(d)
                topicToWeight[topic] = (topicToWeight[topic] ?: 0f) + w
            }
        }

        var totalWeight = 0f
        for (w in topicToWeight.values) totalWeight += w
        if (totalWeight <= 0f) totalWeight = 1f

        val clusters = ArrayList<SemanticMemoryCluster>()
        val topicsByWeightDesc = topicToWeight.keys.sortedByDescending { topicToWeight[it]!! }
        for (topic in topicsByWeightDesc) {
            val contributingDays = topicToDays[topic]!!
            if (contributingDays.size < minDaysPerTopicForCluster) continue

            val centroid = centroidOfHighlights(contributingDays)
            val weight = topicToWeight[topic]!!
            val clusterSalience = minOf(
                1.0,
                weight.toDouble() / totalWeight.toDouble() + (contributingDays.size / 7.0) * 0.25,
            )

            clusters.add(
                createSemanticCluster(
                    weekStartingMonday = weekStartingMonday,
                    topic = topic,
                    summary = buildWeeklyClusterText(topic, contributingDays),
                    centroidEmbedding = centroid,
                    sourceDailyIds = contributingDays.map { it.id },
                    topicWeight = weight,
                    salience = clusterSalience,
                    clock = clock,
                ),
            )
        }
        return clusters
    }

    // ── derivePersonaDeltaAsync ───────────────────────────────────────────────

    override suspend fun derivePersonaDeltaAsync(
        before: PersonaState,
        after: PersonaState,
        daysInPeriod: List<DailyMemorySummary>,
    ): PersonaDeltaSnapshot {
        val newTopics = LinkedHashMap<String, Float>()
        val strengthened = LinkedHashMap<String, Float>()
        for ((topic, afterW) in after.topicWeights) {
            val beforeW = before.topicWeights[topic] ?: 0f
            val delta = afterW - beforeW
            if (beforeW <= 0f && afterW > 0f) {
                newTopics[topic] = afterW
            } else if (delta > 0f) {
                strengthened[topic] = delta
            }
        }

        val disfavouredNew = after.disfavouredTopics.filter { it !in before.disfavouredTopics }

        val netSignals = (after.positiveSignals - before.positiveSignals) -
            (after.negativeSignals - before.negativeSignals)
        val interactions = after.totalInteractions - before.totalInteractions

        val periodStart = if (daysInPeriod.isNotEmpty()) {
            daysInPeriod.minOf { it.day }
        } else {
            dayKeyOf(after.lastUpdatedAt)
        }
        val periodEnd = if (daysInPeriod.isNotEmpty()) {
            daysInPeriod.maxOf { it.day }
        } else {
            dayKeyOf(after.lastUpdatedAt)
        }

        val narrative = buildPersonaNarrative(
            before, after, newTopics, strengthened, disfavouredNew,
            netSignals, interactions, periodStart, periodEnd,
        )

        return createPersonaDelta(
            userId = after.userId,
            periodStart = periodStart,
            periodEnd = periodEnd,
            verbosityBefore = before.verbosity,
            verbosityAfter = after.verbosity,
            formalityBefore = before.formality,
            formalityAfter = after.formality,
            newTopics = newTopics,
            strengthenedTopics = strengthened,
            newlyDisfavouredTopics = disfavouredNew,
            netSignalDelta = netSignals,
            interactionsInPeriod = interactions,
            narrative = narrative,
            clock = clock,
        )
    }
}

// ── Summarizer helpers — topic + dispersion ─────────────────────────────────

/** Topic weights from "topic" (+1) and pipe-split "topics" (each +1), lowercased. */
private fun aggregateTopicWeights(entries: List<EpisodicEntry>): Map<String, Float> {
    val weights = LinkedHashMap<String, Float>()
    for (e in entries) {
        val tags = e.tags ?: continue
        val t = tags["topic"]
        if (t != null && t.isNotBlank()) accumulateTopic(weights, t, 1f)
        val multi = tags["topics"]
        if (multi != null && multi.isNotBlank()) {
            for (p in multi.split("|")) {
                if (p.isEmpty()) continue // RemoveEmptyEntries
                accumulateTopic(weights, p, 1f)
            }
        }
    }
    return weights
}

private fun accumulateTopic(dict: MutableMap<String, Float>, topic: String, weight: Float) {
    val key = topic.trim().lowercase()
    if (key.isEmpty()) return
    dict[key] = (dict[key] ?: 0f) + weight
}

/** Mean over all pairs of (1 - clamp(fullCosine,-1,1)); 0 when <2 embedded entries. */
private fun meanPairwiseCosineDistance(entries: List<EpisodicEntry>): Double {
    val withEmbeddings = entries.filter { hasEmbedding(it) }
    if (withEmbeddings.size < 2) return 0.0

    var total = 0.0
    var pairs = 0
    for (i in withEmbeddings.indices) {
        for (j in i + 1 until withEmbeddings.size) {
            val sim = cosineFull(withEmbeddings[i].embedding!!, withEmbeddings[j].embedding!!)
            total += 1.0 - clampD(sim.toDouble(), -1.0, 1.0)
            pairs++
        }
    }
    return if (pairs == 0) 0.0 else clampD(total / pairs, 0.0, 1.0)
}

/** Top-[count] entries by salience proxy (or all when ≤count), re-sorted by time. */
private fun selectHighlights(entries: List<EpisodicEntry>, count: Int): List<EpisodicEntry> {
    if (entries.size <= count) {
        return entries.sortedBy { it.recordedAtUtc }
    }
    return entries
        .map { it to entrySalienceProxy(it, entries) }
        // OrderByDescending(score).ThenByDescending(recordedAt)
        .sortedWith(
            compareByDescending<Pair<EpisodicEntry, Double>> { it.second }
                .thenByDescending { it.first.recordedAtUtc },
        )
        .take(count)
        .map { it.first }
        .sortedBy { it.recordedAtUtc }
}

private fun entrySalienceProxy(entry: EpisodicEntry, all: List<EpisodicEntry>): Double {
    val lengthScore = minOf(1.0, (entry.userText.length + entry.assistantText.length) / 800.0)
    var uniquenessScore = 0.5
    if (hasEmbedding(entry)) {
        val others = all.filter { it.id != entry.id && hasEmbedding(it) }
        if (others.isNotEmpty()) {
            var sum = 0.0
            for (e in others) sum += cosineFull(entry.embedding!!, e.embedding!!).toDouble()
            val meanSim = sum / others.size
            uniquenessScore = 1.0 - clampD(meanSim, -1.0, 1.0)
        }
    }
    return lengthScore * 0.6 + uniquenessScore * 0.4
}

/** Daily salience = volume·0.4 + dispersion·0.3 + topicConcentration·0.3. */
private fun computeDailySalience(
    episodeCount: Int,
    topicWeights: Map<String, Float>,
    dispersion: Double,
): Double {
    val volumeScore = minOf(1.0, episodeCount / 30.0)
    val topicConcentration: Double = if (topicWeights.isEmpty()) {
        0.5
    } else {
        var maxW = Float.NEGATIVE_INFINITY
        var sumW = 0f
        for (w in topicWeights.values) {
            if (w > maxW) maxW = w
            sumW += w
        }
        minOf(1.0, maxW.toDouble() / maxOf(1.0, sumW.toDouble()))
    }
    return volumeScore * 0.4 + dispersion * 0.3 + topicConcentration * 0.3
}

/** Mean of all highlight embeddings across contributing days; null when none. */
private fun centroidOfHighlights(days: List<DailyMemorySummary>): FloatArray? {
    val allEmbeddings = ArrayList<FloatArray>()
    for (d in days) {
        for (e in d.highlightEntries) {
            if (hasEmbedding(e)) allEmbeddings.add(e.embedding!!)
        }
    }
    if (allEmbeddings.isEmpty()) return null
    val dim = allEmbeddings[0].size
    val centroid = FloatArray(dim)
    for (e in allEmbeddings) {
        var i = 0
        while (i < dim && i < e.size) {
            centroid[i] += e[i]
            i++
        }
    }
    for (i in 0 until dim) centroid[i] /= allEmbeddings.size
    return centroid
}

// ── Summarizer helpers — text builders ──────────────────────────────────────

private fun buildDailySummaryText(
    day: LocalDate,
    count: Int,
    topics: Map<String, Float>,
    highlights: List<EpisodicEntry>,
): String {
    val topTopics = topics.entries
        .sortedByDescending { it.value }
        .take(3)
        .map { it.key }

    val topicsClause = if (topTopics.isNotEmpty()) " Top topics: ${topTopics.joinToString(", ")}." else ""

    val highlightClause = if (highlights.isNotEmpty()) {
        " Standout moment: \"${truncate(highlights[0].userText, 120)}\"."
    } else {
        ""
    }

    return "On $day you had $count " +
        (if (count == 1) "exchange." else "exchanges.") +
        topicsClause + highlightClause
}

private fun buildWeeklyClusterText(topic: String, contributingDays: List<DailyMemorySummary>): String {
    var totalEpisodes = 0
    for (d in contributingDays) totalEpisodes += d.episodeCount
    return "Across ${contributingDays.size} days this week you returned to " +
        "\"$topic\" — $totalEpisodes exchanges in total."
}

private fun buildPersonaNarrative(
    before: PersonaState,
    after: PersonaState,
    newTopics: Map<String, Float>,
    strengthened: Map<String, Float>,
    disfavoured: List<String>,
    netSignals: Int,
    interactions: Int,
    periodStart: LocalDate,
    periodEnd: LocalDate,
): String {
    val parts = ArrayList<String>()
    parts.add("Between $periodStart and $periodEnd, $interactions interactions were recorded.")
    if (newTopics.isNotEmpty()) {
        parts.add("New interests appeared: " + topNKeys(newTopics, 3).joinToString(", ") + ".")
    }
    if (strengthened.isNotEmpty()) {
        parts.add("Existing interests deepened around " + topNKeys(strengthened, 3).joinToString(", ") + ".")
    }
    if (disfavoured.isNotEmpty()) {
        parts.add("Topics now avoided: " + disfavoured.joinToString(", ") + ".")
    }
    if (before.verbosity != after.verbosity) {
        parts.add("Preferred verbosity shifted from ${before.verbosity} to ${after.verbosity}.")
    }
    if (before.formality != after.formality) {
        parts.add("Preferred tone shifted from ${before.formality} to ${after.formality}.")
    }
    if (netSignals != 0) {
        parts.add(
            if (netSignals > 0) "Net feedback was positive (+$netSignals)." else "Net feedback was negative ($netSignals).",
        )
    }
    return parts.joinToString(" ")
}

/** Keys of [map] ordered by value desc, top-n. */
private fun topNKeys(map: Map<String, Float>, n: Int): List<String> =
    map.entries.sortedByDescending { it.value }.take(n).map { it.key }

private fun truncate(s: String, max: Int): String {
    if (s.isEmpty()) return ""
    if (s.length <= max) return s
    return s.substring(0, max).trimEnd() + "…"
}

// ── Shared small helpers ────────────────────────────────────────────────────

private fun hasEmbedding(e: EpisodicEntry): Boolean = e.embedding != null && e.embedding.isNotEmpty()

private fun clampD(x: Double, lo: Double, hi: Double): Double = maxOf(lo, minOf(hi, x))

// ─────────────────────────────────────────────────────────────────────────────
// IMemoryConsolidator + MemoryConsolidator
// ─────────────────────────────────────────────────────────────────────────────

/** Promotes lower-tier memory into higher tiers and enforces retention. */
interface IMemoryConsolidator {
    /**
     * Runs the consolidation pass for the given kind. OnDemand runs every tier
     * with work pending. Returns the breakdown of what was produced and pruned.
     */
    suspend fun tickAsync(kind: SleepKind): ConsolidationOutcome
}

/** Default [IMemoryConsolidator] implementation. */
class MemoryConsolidator(
    private val episodic: IEpisodicStore,
    private val daily: IDailyMemoryStore,
    private val semantic: ISemanticMemoryStore,
    private val personaDelta: IPersonaDeltaStore,
    private val core: ICoreMemoryStore,
    private val personaStore: IPersonaStore,
    private val summarizer: IMemorySummarizer,
    options: MemoryConsolidationOptions = MemoryConsolidationOptions(),
    private val clock: () -> Instant = { Instant.now() },
    private val userId: String = "default",
) : IMemoryConsolidator {

    private val options: MemoryConsolidationOptions = options

    override suspend fun tickAsync(kind: SleepKind): ConsolidationOutcome {
        val now = clock()
        var dailies = 0
        var clusters = 0
        var deltas = 0
        var corePromoted = 0
        var episodesPruned = 0
        var dailiesPruned = 0
        var semanticsPruned = 0

        if (kind == SleepKind.Daily || kind == SleepKind.OnDemand) {
            val (produced, promotedFromDaily) = runDaily(now)
            dailies = produced
            corePromoted += promotedFromDaily
            episodesPruned += pruneEpisodic(now)
        }

        if (kind == SleepKind.Weekly || kind == SleepKind.OnDemand) {
            val (produced, promotedFromWeekly) = runWeekly(now)
            clusters = produced
            corePromoted += promotedFromWeekly
            dailiesPruned += pruneDailies(now)
        }

        if (kind == SleepKind.Monthly || kind == SleepKind.OnDemand) {
            deltas = runMonthly(now)
            semanticsPruned += pruneSemantics(now)
        }

        return ConsolidationOutcome(
            kind = kind,
            dailySummariesProduced = dailies,
            semanticClustersProduced = clusters,
            personaDeltasProduced = deltas,
            corePromotions = corePromoted,
            episodesPruned = episodesPruned,
            dailiesPruned = dailiesPruned,
            semanticsPruned = semanticsPruned,
            ranAtUtc = now,
        )
    }

    // ── Daily pass ─────────────────────────────────────────────────────────────

    private suspend fun runDaily(now: Instant): Pair<Int, Int> {
        val recent = episodic.getRecentAsync(Int.MAX_VALUE)
        if (recent.isEmpty()) return 0 to 0

        // Group episodes by their calendar day (UTC).
        val today = dayKeyOf(now)
        val byDay = LinkedHashMap<LocalDate, MutableList<EpisodicEntry>>()
        for (e in recent) {
            val key = dayKeyOf(e.recordedAtUtc)
            byDay.getOrPut(key) { mutableListOf() }.add(e)
        }

        var produced = 0
        var promoted = 0
        for ((day, group) in byDay) {
            if (!day.isBefore(today)) continue // only fully completed days

            val existing = daily.getAsync(day)
            if (existing != null && existing.episodeCount == group.size) {
                continue // idempotent skip — already consolidated this day
            }

            val ordered = group.sortedBy { it.recordedAtUtc }
            val summary = summarizer.summarizeDayAsync(day, ordered)
            daily.upsertAsync(summary)
            produced++

            if (summary.salience >= options.dailyCorePromotionThreshold) {
                promoted += promoteDailyToCore(summary)
            }
        }
        return produced to promoted
    }

    // ── Weekly pass ────────────────────────────────────────────────────────────

    private suspend fun runWeekly(now: Instant): Pair<Int, Int> {
        val today = dayKeyOf(now)
        val thisMonday = mondayOf(today)
        val lastMonday = thisMonday.minusDays(7)
        val lastSunday = lastMonday.plusDays(6)

        val lastWeek = daily.getRangeAsync(lastMonday, lastSunday)
        if (lastWeek.isEmpty()) return 0 to 0

        // Idempotency: if we already have clusters for this week, skip.
        val existing = semantic.getWeekAsync(lastMonday)
        if (existing.isNotEmpty()) return 0 to 0

        val clusters = summarizer.consolidateWeekAsync(lastMonday, lastWeek)
        var promoted = 0
        for (c in clusters) {
            semantic.addAsync(c)
            if (c.salience >= options.weeklyCorePromotionThreshold) {
                promoted += promoteClusterToCore(c)
            }
        }
        return clusters.size to promoted
    }

    // ── Monthly pass ───────────────────────────────────────────────────────────

    private suspend fun runMonthly(now: Instant): Int {
        val today = dayKeyOf(now)
        // Consider the most recently completed full month.
        val firstOfThisMonth = monthFirstDayOf(today)
        val lastMonthEnd = firstOfThisMonth.minusDays(1)
        val lastMonthStart = monthFirstDayOf(lastMonthEnd)

        // Idempotency: skip if we already have a delta whose PeriodStart falls in
        // the previous month (compared by year+month, not exact dates).
        val existingDeltas = personaDelta.getForUserAsync(userId)
        if (existingDeltas.any {
                it.periodStart.year == lastMonthStart.year &&
                    it.periodStart.monthValue == lastMonthStart.monthValue
            }
        ) {
            return 0
        }

        val days = daily.getRangeAsync(lastMonthStart, lastMonthEnd)
        if (days.isEmpty()) return 0

        val after = personaStore.loadAsync(userId)

        // For "before", reconstruct from the most recent prior delta if one exists;
        // otherwise treat as a fresh persona.
        val prior = existingDeltas
            .filter { it.periodEnd.isBefore(lastMonthStart) }
            .maxByOrNull { it.periodEnd }
        val before = if (prior == null) newPersona(userId) else reconstructPersonaBefore(after, days, prior)

        val delta = summarizer.derivePersonaDeltaAsync(before, after, days)
        personaDelta.addAsync(delta)
        return 1
    }

    // ── Core promotions ──────────────────────────────────────────────────────

    private suspend fun promoteDailyToCore(summary: DailyMemorySummary): Int {
        // FirstOrDefault on TopicWeights.OrderByDescending — null topic when empty.
        var topTopic: String? = null
        var topWeight = Float.NEGATIVE_INFINITY
        for ((k, v) in summary.topicWeights) {
            if (v > topWeight) {
                topWeight = v
                topTopic = k
            }
        }

        val statement = if (topTopic == null) {
            "On ${summary.day} an unusually meaningful day was recorded."
        } else {
            "\"$topTopic\" mattered enough on ${summary.day} to be remembered."
        }

        var embedding: FloatArray? = null
        for (h in summary.highlightEntries) {
            if (h.embedding != null && h.embedding.isNotEmpty()) {
                embedding = h.embedding
                break
            }
        }

        val memory = createCoreMemory(
            statement = statement,
            kind = CoreMemoryKind.HighSalience,
            topic = topTopic,
            embedding = embedding,
            sourceMemoryId = summary.id,
            clock = clock,
        )
        core.addAsync(memory)
        return 1
    }

    private suspend fun promoteClusterToCore(cluster: SemanticMemoryCluster): Int {
        val memory = createCoreMemory(
            statement = "\"${cluster.topic}\" has been a recurring theme " +
                "(week of ${cluster.weekStartingMonday}).",
            kind = CoreMemoryKind.PatternInferred,
            topic = cluster.topic,
            embedding = cluster.centroidEmbedding,
            sourceMemoryId = cluster.id,
            clock = clock,
        )
        core.addAsync(memory)
        return 1
    }

    // ── Retention ────────────────────────────────────────────────────────────

    private suspend fun pruneEpisodic(now: Instant): Int {
        val cutoff = now.minus(Duration.ofDays(options.episodicRetentionDays.toLong()))
        return episodic.pruneOlderThanAsync(cutoff)
    }

    private suspend fun pruneDailies(now: Instant): Int {
        val cutoff = dayKeyOf(now).minusDays(options.dailyRetentionDays.toLong())
        return daily.pruneOlderThanAsync(cutoff)
    }

    private suspend fun pruneSemantics(now: Instant): Int {
        val cutoff = dayKeyOf(now).minusDays(options.semanticRetentionDays.toLong())
        return semantic.pruneOlderThanAsync(cutoff)
    }
}

/**
 * Approximates the persona at the start of the period by subtracting the
 * in-period gains from the current persona. Conservative — when in doubt it shows
 * no change. Faithful port of ReconstructPersonaBeforeAsync.
 */
private fun reconstructPersonaBefore(
    after: PersonaState,
    daysInPeriod: List<DailyMemorySummary>,
    prior: PersonaDeltaSnapshot,
): PersonaState {
    val before = PersonaState(after.userId)
    before.verbosity = prior.verbosityAfter
    before.formality = prior.formalityAfter
    before.preferredLocale = after.preferredLocale
    var episodeSum = 0
    for (d in daysInPeriod) episodeSum += d.episodeCount
    before.totalInteractions = after.totalInteractions - episodeSum
    before.positiveSignals = maxOf(0, after.positiveSignals - clampPositive(prior.netSignalDelta))
    before.negativeSignals = after.negativeSignals

    // Carry over topic weights minus the strongest in-period gains.
    for ((topic, w) in after.topicWeights) {
        val delta = prior.strengthenedTopics[topic]
        before.topicWeights[topic] = if (delta != null) maxOf(0f, w - delta) else w
    }
    before.disfavouredTopics.addAll(after.disfavouredTopics)
    return before
}

private fun newPersona(userId: String): PersonaState = PersonaState(userId)

private fun clampPositive(v: Int): Int = if (v < 0) 0 else v
