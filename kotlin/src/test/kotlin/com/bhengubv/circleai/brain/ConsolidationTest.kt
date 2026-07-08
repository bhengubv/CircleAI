// ConsolidationTest.kt
//
// Verifies the hierarchical memory-consolidation subsystem ported from
// CircleAI.Memory.Consolidation. Uses a fixed injected clock and hand-built
// EpisodicEntry lists so every deterministic formula can be asserted exactly.
// Covers: daily summary produced for a completed day + idempotency, today's
// episodes excluded, the salience/topicConcentration formula on a small example,
// weekly clustering's 2-day threshold, high-salience → core promotion, retention
// pruning, persona-delta new-topic detection, and full-cosine ranking in the
// in-memory stores. Mirrors the just-verified TS reference
// (tests/consolidation.test.ts) 1:1.

package com.bhengubv.circleai.brain

import com.bhengubv.circleai.memory.PersonaState
import com.bhengubv.circleai.memory.brain.CoreMemoryKind
import com.bhengubv.circleai.memory.brain.EpisodicEntry
import com.bhengubv.circleai.memory.brain.HeuristicSummarizer
import com.bhengubv.circleai.memory.brain.InMemoryCoreMemoryStore
import com.bhengubv.circleai.memory.brain.InMemoryDailyMemoryStore
import com.bhengubv.circleai.memory.brain.InMemoryEpisodicStore
import com.bhengubv.circleai.memory.brain.InMemoryPersonaDeltaStore
import com.bhengubv.circleai.memory.brain.InMemoryPersonaStore
import com.bhengubv.circleai.memory.brain.InMemorySemanticMemoryStore
import com.bhengubv.circleai.memory.brain.MemoryConsolidationOptions
import com.bhengubv.circleai.memory.brain.MemoryConsolidator
import com.bhengubv.circleai.memory.brain.SleepKind
import com.bhengubv.circleai.memory.brain.cosineFull
import com.bhengubv.circleai.memory.brain.createCoreMemory
import com.bhengubv.circleai.memory.brain.createDailySummary
import com.bhengubv.circleai.memory.brain.createSemanticCluster
import com.bhengubv.circleai.memory.brain.dayKeyOf
import com.bhengubv.circleai.memory.brain.mondayOf
import com.bhengubv.circleai.memory.brain.monthFirstDayOf
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import java.time.LocalDate
import kotlin.math.abs
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ConsolidationTest {

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private var idCounter = 0

    private fun entry(
        id: String? = null,
        recordedAtUtc: Instant = Instant.parse("2026-06-01T12:00:00Z"),
        userText: String = "u",
        assistantText: String = "a",
        embedding: FloatArray? = null,
        tags: Map<String, String>? = null,
    ): EpisodicEntry = EpisodicEntry(
        id = id ?: "e${idCounter++}",
        userText = userText,
        assistantText = assistantText,
        recordedAtUtc = recordedAtUtc,
        embedding = embedding,
        tags = tags,
    )

    /** Clock fixed at a given instant so time math is stable. */
    private fun fixedClock(iso: String): () -> Instant {
        val i = Instant.parse(iso)
        return { i }
    }

    private fun day(s: String): LocalDate = LocalDate.parse(s)

    private class Wired(
        val episodic: InMemoryEpisodicStore,
        val daily: InMemoryDailyMemoryStore,
        val semantic: InMemorySemanticMemoryStore,
        val personaDelta: InMemoryPersonaDeltaStore,
        val core: InMemoryCoreMemoryStore,
        val personaStore: InMemoryPersonaStore,
        val summarizer: HeuristicSummarizer,
        val consolidator: MemoryConsolidator,
    )

    /** Wires a consolidator over fresh in-memory stores; returns the parts. */
    private fun makeConsolidator(clock: () -> Instant, options: MemoryConsolidationOptions? = null): Wired {
        val episodic = InMemoryEpisodicStore(100000)
        val daily = InMemoryDailyMemoryStore()
        val semantic = InMemorySemanticMemoryStore()
        val personaDelta = InMemoryPersonaDeltaStore()
        val core = InMemoryCoreMemoryStore()
        val personaStore = InMemoryPersonaStore()
        val summarizer = HeuristicSummarizer(clock = clock)
        val consolidator = MemoryConsolidator(
            episodic,
            daily,
            semantic,
            personaDelta,
            core,
            personaStore,
            summarizer,
            options ?: MemoryConsolidationOptions(),
            clock,
        )
        return Wired(episodic, daily, semantic, personaDelta, core, personaStore, summarizer, consolidator)
    }

    // ── Day helpers ───────────────────────────────────────────────────────────

    @Test
    fun `dayKeyOf uses UTC calendar day`() {
        assertEquals(day("2026-06-08"), dayKeyOf(Instant.parse("2026-06-08T23:59:59Z")))
        assertEquals(day("2026-01-05"), dayKeyOf(Instant.parse("2026-01-05T00:00:00Z")))
    }

    @Test
    fun `mondayOf returns the Monday of the week Sunday zero`() {
        assertEquals(day("2026-06-08"), mondayOf(day("2026-06-08"))) // Monday → itself
        assertEquals(day("2026-06-08"), mondayOf(day("2026-06-14"))) // Sunday → prior Monday
        assertEquals(day("2026-06-08"), mondayOf(day("2026-06-10"))) // Wednesday → Monday
    }

    @Test
    fun `addDays crosses month boundaries`() {
        assertEquals(day("2026-05-31"), day("2026-06-01").minusDays(1))
        assertEquals(day("2026-07-01"), day("2026-06-30").plusDays(1))
    }

    @Test
    fun `monthFirstDayOf yields the first of the month`() {
        assertEquals(day("2026-06-01"), monthFirstDayOf(day("2026-06-17")))
    }

    // ── cosineFull ────────────────────────────────────────────────────────────

    @Test
    fun `cosineFull is 1 for identical direction 0 for orthogonal and normalises magnitude`() {
        assertEquals(1f, cosineFull(floatArrayOf(1f, 0f), floatArrayOf(1f, 0f)))
        assertEquals(0f, cosineFull(floatArrayOf(1f, 0f), floatArrayOf(0f, 1f)))
        // Not L2-normalised inputs: full cosine still yields 1 for same direction.
        assertTrue(abs(cosineFull(floatArrayOf(3f, 0f), floatArrayOf(7f, 0f)) - 1f) < 1e-6f)
    }

    @Test
    fun `cosineFull returns 0 on a length mismatch or a zero vector`() {
        assertEquals(0f, cosineFull(floatArrayOf(1f, 0f), floatArrayOf(1f, 0f, 0f)))
        assertEquals(0f, cosineFull(floatArrayOf(0f, 0f), floatArrayOf(1f, 0f)))
    }

    // ── Daily summarization formulas ────────────────────────────────────────────

    @Test
    fun `computes topic weights dispersion topicConcentration and salience exactly`() = runTest {
        val s = HeuristicSummarizer(clock = fixedClock("2026-06-02T00:00:00Z"))
        // 3 entries: finance×2 (topic tag) + health×1; embeddings [1,0],[0,1],[1,0].
        val entries = listOf(
            entry(id = "a", embedding = floatArrayOf(1f, 0f), tags = mapOf("topic" to "finance")),
            entry(id = "b", embedding = floatArrayOf(0f, 1f), tags = mapOf("topic" to "health")),
            entry(id = "c", embedding = floatArrayOf(1f, 0f), tags = mapOf("topic" to "finance")),
        )
        val summary = s.summarizeDayAsync(day("2026-06-01"), entries)

        assertEquals(3, summary.episodeCount)
        assertEquals(2f, summary.topicWeights["finance"])
        assertEquals(1f, summary.topicWeights["health"])
        // dispersion = mean(1-cos) over pairs = (1 + 0 + 1)/3 = 2/3
        assertTrue(abs(summary.topicDispersion - 2.0 / 3.0) < 1e-12)
        // salience = volume(3/30=0.1)*0.4 + disp(2/3)*0.3 + conc(2/3)*0.3 = 0.44
        assertTrue(abs(summary.salience - 0.44) < 1e-12)
        // summary text shape
        assertTrue(summary.summary.startsWith("On 2026-06-01 you had 3 exchanges."))
        assertTrue(summary.summary.contains("Top topics: finance, health."))
    }

    @Test
    fun `splits pipe-delimited topics and lowercases and trims`() = runTest {
        val s = HeuristicSummarizer()
        val summary = s.summarizeDayAsync(
            day("2026-06-01"),
            listOf(entry(tags = mapOf("topics" to "Finance | Health |finance"))),
        )
        assertEquals(2f, summary.topicWeights["finance"])
        assertEquals(1f, summary.topicWeights["health"])
    }

    @Test
    fun `uses topicConcentration 0_5 when there are no topics`() = runTest {
        val s = HeuristicSummarizer()
        // 1 entry, no tags, no embedding → dispersion 0, volume 1/30, conc 0.5
        val summary = s.summarizeDayAsync(day("2026-06-01"), listOf(entry()))
        val expected = (1.0 / 30.0) * 0.4 + 0.0 * 0.3 + 0.5 * 0.3
        assertTrue(abs(summary.salience - expected) < 1e-12)
        // A single entry is always a highlight, so the standout clause is appended
        // (userText defaults to "u"). No topics → no "Top topics" clause.
        assertEquals("On 2026-06-01 you had 1 exchange. Standout moment: \"u\".", summary.summary)
        assertTrue(!summary.summary.contains("Top topics"))
    }

    @Test
    fun `returns an empty-day summary for zero entries`() = runTest {
        val s = HeuristicSummarizer()
        val summary = s.summarizeDayAsync(day("2026-06-01"), emptyList())
        assertEquals(0, summary.episodeCount)
        assertEquals("No exchanges recorded on 2026-06-01.", summary.summary)
    }

    // ── Daily pass: production, idempotency, today-exclusion ─────────────────────

    @Test
    fun `produces a summary for a completed day and is idempotent on re-tick`() = runTest {
        val clock = fixedClock("2026-06-08T09:00:00Z") // "today" = 2026-06-08
        val w = makeConsolidator(clock)
        w.episodic.addAsync(entry(recordedAtUtc = Instant.parse("2026-06-06T10:00:00Z"), tags = mapOf("topic" to "x")))
        w.episodic.addAsync(entry(recordedAtUtc = Instant.parse("2026-06-06T11:00:00Z"), tags = mapOf("topic" to "x")))

        val r1 = w.consolidator.tickAsync(SleepKind.Daily)
        assertEquals(1, r1.dailySummariesProduced)
        val summary = w.daily.getAsync(day("2026-06-06"))
        assertNotNull(summary)
        assertEquals(2, summary.episodeCount)

        // Second tick with no new episodes → idempotent skip (episodeCount matches).
        val r2 = w.consolidator.tickAsync(SleepKind.Daily)
        assertEquals(0, r2.dailySummariesProduced)
        assertEquals(1, w.daily.countAsync())
    }

    @Test
    fun `does NOT summarise today's incomplete day`() = runTest {
        val clock = fixedClock("2026-06-08T09:00:00Z")
        val w = makeConsolidator(clock)
        // Episode recorded "today" → excluded (day is not < today).
        w.episodic.addAsync(entry(recordedAtUtc = Instant.parse("2026-06-08T08:00:00Z")))

        val r = w.consolidator.tickAsync(SleepKind.Daily)
        assertEquals(0, r.dailySummariesProduced)
        assertEquals(0, w.daily.countAsync())
    }

    @Test
    fun `re-summarises a day when new episodes arrive for it count mismatch`() = runTest {
        val clock = fixedClock("2026-06-08T09:00:00Z")
        val w = makeConsolidator(clock)
        w.episodic.addAsync(entry(id = "p1", recordedAtUtc = Instant.parse("2026-06-06T10:00:00Z")))
        w.consolidator.tickAsync(SleepKind.Daily)
        assertEquals(1, w.daily.getAsync(day("2026-06-06"))!!.episodeCount)

        w.episodic.addAsync(entry(id = "p2", recordedAtUtc = Instant.parse("2026-06-06T12:00:00Z")))
        val r = w.consolidator.tickAsync(SleepKind.Daily)
        assertEquals(1, r.dailySummariesProduced)
        assertEquals(2, w.daily.getAsync(day("2026-06-06"))!!.episodeCount)
    }

    // ── High-salience daily → core promotion (≥0.80) ────────────────────────────

    @Test
    fun `promotes a day whose salience is at least 0_80 to a HighSalience core memory`() = runTest {
        val clock = fixedClock("2026-06-08T09:00:00Z")
        val w = makeConsolidator(clock)

        // 30 entries, single topic 'finance' (conc=1); embeddings 15×[1,0] + 15×[0,1]
        // → dispersion ≈ 0.5172, salience ≈ 0.8552 (≥ 0.80).
        for (i in 0 until 30) {
            w.episodic.addAsync(
                entry(
                    id = "h$i",
                    recordedAtUtc = Instant.parse("2026-06-06T${(i % 24).toString().padStart(2, '0')}:00:00Z"),
                    embedding = if (i < 15) floatArrayOf(1f, 0f) else floatArrayOf(0f, 1f),
                    tags = mapOf("topic" to "finance"),
                ),
            )
        }

        val r = w.consolidator.tickAsync(SleepKind.Daily)
        assertEquals(1, r.dailySummariesProduced)
        assertEquals(1, r.corePromotions)

        val all = w.core.listAllAsync()
        assertEquals(1, all.size)
        assertEquals(CoreMemoryKind.HighSalience, all[0].kind)
        assertEquals("finance", all[0].topic)
        assertEquals("\"finance\" mattered enough on 2026-06-06 to be remembered.", all[0].statement)
        // Highlight embedding carried onto the core memory.
        assertNotNull(all[0].embedding)
    }

    @Test
    fun `does NOT promote a low-salience day`() = runTest {
        val clock = fixedClock("2026-06-08T09:00:00Z")
        val w = makeConsolidator(clock)
        w.episodic.addAsync(entry(recordedAtUtc = Instant.parse("2026-06-06T10:00:00Z"), tags = mapOf("topic" to "x")))
        val r = w.consolidator.tickAsync(SleepKind.Daily)
        assertEquals(0, r.corePromotions)
        assertEquals(0, w.core.countAsync())
    }

    // ── Weekly clustering + 2-day threshold ─────────────────────────────────────

    @Test
    fun `clusters only topics appearing in at least 2 days salience per formula`() = runTest {
        val s = HeuristicSummarizer(clock = fixedClock("2026-06-08T00:00:00Z"))
        // Day1: finance=1, health=1 ; Day2: finance=1.
        // finance → 2 days (weight 2) → cluster ; health → 1 day → excluded.
        val day1 = createDailySummary(
            day = day("2026-06-01"),
            episodeCount = 2,
            topicWeights = mapOf("finance" to 1f, "health" to 1f),
        )
        val day2 = createDailySummary(
            day = day("2026-06-02"),
            episodeCount = 1,
            topicWeights = mapOf("finance" to 1f),
        )

        val clusters = s.consolidateWeekAsync(day("2026-06-01"), listOf(day1, day2))
        assertEquals(1, clusters.size)
        assertEquals("finance", clusters[0].topic)
        assertEquals(2f, clusters[0].topicWeight)
        // salience = min(1, 2/3 + (2/7)*0.25) = 0.7380952…
        assertTrue(abs(clusters[0].salience - (2.0 / 3.0 + (2.0 / 7.0) * 0.25)) < 1e-12)
        assertEquals("Across 2 days this week you returned to \"finance\" — 3 exchanges in total.", clusters[0].summary)
        assertEquals(listOf(day1.id, day2.id).sorted(), clusters[0].sourceDailyIds.sorted())
    }

    @Test
    fun `returns no clusters when every topic is single-day`() = runTest {
        val s = HeuristicSummarizer()
        val clusters = s.consolidateWeekAsync(
            day("2026-06-01"),
            listOf(
                createDailySummary(day = day("2026-06-01"), topicWeights = mapOf("a" to 1f)),
                createDailySummary(day = day("2026-06-02"), topicWeights = mapOf("b" to 1f)),
            ),
        )
        assertEquals(0, clusters.size)
    }

    @Test
    fun `computes the centroid as the mean of highlight embeddings`() = runTest {
        val s = HeuristicSummarizer()
        val h1 = entry(id = "h1", embedding = floatArrayOf(2f, 0f))
        val h2 = entry(id = "h2", embedding = floatArrayOf(0f, 4f))
        val day1 = createDailySummary(
            day = day("2026-06-01"),
            topicWeights = mapOf("t" to 1f),
            highlightEntries = listOf(h1),
        )
        val day2 = createDailySummary(
            day = day("2026-06-02"),
            topicWeights = mapOf("t" to 1f),
            highlightEntries = listOf(h2),
        )
        val clusters = s.consolidateWeekAsync(day("2026-06-01"), listOf(day1, day2))
        assertEquals(1, clusters.size)
        // ([2,0]+[0,4])/2 = [1,2]
        assertNotNull(clusters[0].centroidEmbedding)
        assertTrue(clusters[0].centroidEmbedding!!.contentEquals(floatArrayOf(1f, 2f)))
    }

    @Test
    fun `clusters the last completed week and is idempotent`() = runTest {
        // "today" Monday 2026-06-08 → thisMonday 06-08, lastMonday 06-01..lastSunday 06-07.
        val clock = fixedClock("2026-06-08T09:00:00Z")
        val w = makeConsolidator(clock)
        w.daily.upsertAsync(
            createDailySummary(day = day("2026-06-01"), episodeCount = 2, topicWeights = mapOf("finance" to 1f)),
        )
        w.daily.upsertAsync(
            createDailySummary(day = day("2026-06-03"), episodeCount = 1, topicWeights = mapOf("finance" to 1f)),
        )

        val r1 = w.consolidator.tickAsync(SleepKind.Weekly)
        assertEquals(1, r1.semanticClustersProduced)
        assertEquals(1, w.semantic.countAsync())

        val r2 = w.consolidator.tickAsync(SleepKind.Weekly)
        assertEquals(0, r2.semanticClustersProduced) // getWeek non-empty → skip
        assertEquals(1, w.semantic.countAsync())
    }

    // ── Retention pruning ───────────────────────────────────────────────────────

    @Test
    fun `prunes episodic entries older than 7 days on the daily pass`() = runTest {
        val clock = fixedClock("2026-06-08T09:00:00Z")
        val w = makeConsolidator(clock)
        // cutoff = now - 7 days = 2026-06-01T09:00:00Z
        w.episodic.addAsync(entry(id = "old", recordedAtUtc = Instant.parse("2026-05-20T00:00:00Z")))
        w.episodic.addAsync(entry(id = "fresh", recordedAtUtc = Instant.parse("2026-06-06T00:00:00Z")))

        val r = w.consolidator.tickAsync(SleepKind.Daily)
        assertEquals(1, r.episodesPruned)
        assertEquals(1, w.episodic.countAsync())
        val remaining = w.episodic.getRecentAsync(10)
        assertEquals("fresh", remaining[0].id)
    }

    @Test
    fun `prunes daily summaries older than 30 days on the weekly pass`() = runTest {
        val clock = fixedClock("2026-06-08T09:00:00Z")
        val w = makeConsolidator(clock)
        // cutoff = 2026-06-08 - 30 = 2026-05-09. Older day is pruned.
        w.daily.upsertAsync(createDailySummary(day = day("2026-04-01"))) // < cutoff → pruned
        w.daily.upsertAsync(createDailySummary(day = day("2026-06-03"))) // kept

        val r = w.consolidator.tickAsync(SleepKind.Weekly)
        assertEquals(1, r.dailiesPruned)
        assertNull(w.daily.getAsync(day("2026-04-01")))
        assertNotNull(w.daily.getAsync(day("2026-06-03")))
    }

    @Test
    fun `prunes semantic clusters older than 365 days on the monthly pass`() = runTest {
        val clock = fixedClock("2026-06-08T09:00:00Z")
        val w = makeConsolidator(clock)
        // cutoff = 2026-06-08 - 365 = 2025-06-08.
        w.semantic.addAsync(createSemanticCluster(weekStartingMonday = day("2024-01-01"), topic = "t"))
        w.semantic.addAsync(createSemanticCluster(weekStartingMonday = day("2026-05-04"), topic = "t"))

        val r = w.consolidator.tickAsync(SleepKind.Monthly)
        assertEquals(1, r.semanticsPruned)
        assertEquals(1, w.semantic.countAsync())
    }

    // ── Monthly persona-delta ───────────────────────────────────────────────────

    @Test
    fun `derives a delta detecting a new topic and is idempotent by month`() = runTest {
        // "today" 2026-06-08 → previous month = May 2026 (2026-05-01..2026-05-31).
        val clock = fixedClock("2026-06-08T09:00:00Z")
        val w = makeConsolidator(clock)

        // A daily summary inside May so the month has data.
        w.daily.upsertAsync(createDailySummary(day = day("2026-05-15"), episodeCount = 4))

        // Persona "after" has a topic the fresh "before" lacks → newTopic.
        val after = PersonaState("default")
        after.topicWeights["finance"] = 3f
        after.totalInteractions = 10
        after.positiveSignals = 6
        after.negativeSignals = 1
        w.personaStore.saveAsync(after)

        val r1 = w.consolidator.tickAsync(SleepKind.Monthly)
        assertEquals(1, r1.personaDeltasProduced)
        val deltas = w.personaDelta.getForUserAsync("default")
        assertEquals(1, deltas.size)
        assertEquals(3f, deltas[0].newTopics["finance"])
        assertEquals(day("2026-05-15"), deltas[0].periodStart)
        assertEquals(day("2026-05-15"), deltas[0].periodEnd)
        assertTrue(deltas[0].narrative.contains("New interests appeared: finance."))

        // Second monthly tick → idempotent (delta already exists for May).
        val r2 = w.consolidator.tickAsync(SleepKind.Monthly)
        assertEquals(0, r2.personaDeltasProduced)
        assertEquals(1, w.personaDelta.getForUserAsync("default").size)
    }

    @Test
    fun `produces no delta when the previous month has no daily summaries`() = runTest {
        val clock = fixedClock("2026-06-08T09:00:00Z")
        val w = makeConsolidator(clock)
        val r = w.consolidator.tickAsync(SleepKind.Monthly)
        assertEquals(0, r.personaDeltasProduced)
        assertEquals(0, w.personaDelta.countAsync())
    }

    @Test
    fun `separates new topics from strengthened ones and computes signal deltas`() = runTest {
        val s = HeuristicSummarizer()
        val before = PersonaState()
        before.topicWeights["finance"] = 2f
        before.positiveSignals = 1
        before.negativeSignals = 1
        before.totalInteractions = 5
        before.verbosity = "balanced"

        val after = PersonaState()
        after.topicWeights["finance"] = 5f // strengthened (+3)
        after.topicWeights["travel"] = 3f // new
        after.positiveSignals = 7
        after.negativeSignals = 2
        after.totalInteractions = 20
        after.verbosity = "detailed"

        val d = createDailySummary(day = day("2026-05-10"))
        val delta = s.derivePersonaDeltaAsync(before, after, listOf(d))

        assertEquals(3f, delta.newTopics["travel"])
        assertEquals(false, delta.newTopics.containsKey("finance"))
        assertEquals(3f, delta.strengthenedTopics["finance"])
        // netSignals = (7-1) - (2-1) = 5 ; interactions = 20-5 = 15
        assertEquals(5, delta.netSignalDelta)
        assertEquals(15, delta.interactionsInPeriod)
        assertTrue(delta.narrative.contains("Preferred verbosity shifted from balanced to detailed."))
        assertTrue(delta.narrative.contains("Net feedback was positive (+5)."))
    }

    // ── OnDemand runs every tier ────────────────────────────────────────────────

    @Test
    fun `runs daily weekly and monthly passes in one tick`() = runTest {
        val clock = fixedClock("2026-06-08T09:00:00Z")
        val w = makeConsolidator(clock)

        // Daily fuel: a completed day earlier this week.
        w.episodic.addAsync(entry(recordedAtUtc = Instant.parse("2026-06-06T10:00:00Z"), tags = mapOf("topic" to "finance")))
        w.episodic.addAsync(entry(recordedAtUtc = Instant.parse("2026-06-06T11:00:00Z"), tags = mapOf("topic" to "finance")))
        // Weekly fuel: dailies inside last week (06-01..06-07) with a repeated topic.
        w.daily.upsertAsync(createDailySummary(day = day("2026-06-01"), episodeCount = 2, topicWeights = mapOf("finance" to 1f)))
        w.daily.upsertAsync(createDailySummary(day = day("2026-06-02"), episodeCount = 1, topicWeights = mapOf("finance" to 1f)))
        // Monthly fuel: a daily inside May + a persona.
        w.daily.upsertAsync(createDailySummary(day = day("2026-05-20"), episodeCount = 3))
        val p = PersonaState()
        p.topicWeights["finance"] = 2f
        p.totalInteractions = 6
        w.personaStore.saveAsync(p)

        val r = w.consolidator.tickAsync(SleepKind.OnDemand)
        assertEquals(SleepKind.OnDemand, r.kind)
        assertTrue(r.dailySummariesProduced >= 1)
        assertTrue(r.semanticClustersProduced >= 1)
        assertEquals(1, r.personaDeltasProduced)
        assertEquals(clock().toEpochMilli(), r.ranAtUtc.toEpochMilli())
        assertTrue(w.semantic.countAsync() >= 1)
        assertTrue(w.personaDelta.getForUserAsync("default").size == 1)
    }

    // ── In-memory store cosine ranking + ordering ───────────────────────────────

    @Test
    fun `CoreMemoryStore ranks by full cosine to the query centroid`() = runTest {
        val core = InMemoryCoreMemoryStore()
        core.addAsync(createCoreMemory(statement = "x", embedding = floatArrayOf(1f, 0f)))
        core.addAsync(createCoreMemory(statement = "y", embedding = floatArrayOf(0f, 1f)))
        core.addAsync(createCoreMemory(statement = "diag", embedding = floatArrayOf(1f, 1f)))

        val ranked = core.searchAsync(floatArrayOf(1f, 0f), 3)
        assertEquals("x", ranked[0].statement) // cos 1
        assertEquals("y", ranked[2].statement) // cos 0
        // 'diag' cos([1,1],[1,0]) = 0.707 → middle
        assertEquals("diag", ranked[1].statement)
    }

    @Test
    fun `CoreMemoryStore falls back to reinforcement order when query is null`() = runTest {
        val core = InMemoryCoreMemoryStore()
        val a = createCoreMemory(statement = "a")
        val b = createCoreMemory(statement = "b")
        core.addAsync(a)
        core.addAsync(b)
        core.reinforceAsync(b.id)
        core.reinforceAsync(b.id)

        val top = core.searchAsync(null, 2)
        assertEquals("b", top[0].statement) // more reinforced first
        assertEquals(2, top[0].reinforcementCount)
    }

    @Test
    fun `SemanticMemoryStore getWeek orders by topicWeight desc search ranks by centroid cosine`() = runTest {
        val sem = InMemorySemanticMemoryStore()
        sem.addAsync(createSemanticCluster(weekStartingMonday = day("2026-06-01"), topic = "low", topicWeight = 1f, centroidEmbedding = floatArrayOf(0f, 1f)))
        sem.addAsync(createSemanticCluster(weekStartingMonday = day("2026-06-01"), topic = "high", topicWeight = 5f, centroidEmbedding = floatArrayOf(1f, 0f)))

        val week = sem.getWeekAsync(day("2026-06-01"))
        assertEquals(listOf("high", "low"), week.map { it.topic })

        val ranked = sem.searchAsync(floatArrayOf(1f, 0f), 2)
        assertEquals("high", ranked[0].topic) // centroid [1,0] cos 1
    }

    @Test
    fun `DailyMemoryStore getRange returns day-ordered inclusive results`() = runTest {
        val daily = InMemoryDailyMemoryStore()
        daily.upsertAsync(createDailySummary(day = day("2026-06-03")))
        daily.upsertAsync(createDailySummary(day = day("2026-06-01")))
        daily.upsertAsync(createDailySummary(day = day("2026-06-10")))

        val range = daily.getRangeAsync(day("2026-06-01"), day("2026-06-05"))
        assertEquals(listOf(day("2026-06-01"), day("2026-06-03")), range.map { it.day })
    }
}
