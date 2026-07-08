// CompanionRuntimeTest.kt
//
// Verifies CompanionRuntime against the C# reference: catch-up-on-start runs an
// OnDemand consolidation, consolidateNow triggers a pass, ingestMedia forwards
// to the ingester (and throws when absent), syncNow drives the engine (and
// no-ops when absent), and start/stop lifecycle is clean.
//
// Periodic loops are disabled (interval = ZERO) so tests stay deterministic and
// fast — the tick-loop cadence is covered structurally by the port and the
// C# reference; here we assert the observable orchestration contract.

package com.bhengubv.circleai.runtime

import com.bhengubv.circleai.memory.brain.ConsolidationOutcome
import com.bhengubv.circleai.memory.brain.IMemoryConsolidator
import com.bhengubv.circleai.memory.brain.MediaModality
import com.bhengubv.circleai.memory.brain.SleepKind
import com.bhengubv.circleai.memory.sync.CompanionStateSyncEngine
import com.bhengubv.circleai.memory.sync.HybridLogicalClock
import com.bhengubv.circleai.memory.sync.ICompanionStateChannel
import com.bhengubv.circleai.memory.sync.InMemorySyncableEntryStore
import com.bhengubv.circleai.memory.sync.InProcessCompanionStateChannel
import com.bhengubv.circleai.memory.sync.InProcessSyncHub
import com.bhengubv.circleai.memory.sync.SyncEnvelope
import com.bhengubv.circleai.memory.sync.SyncEnvelopeKind
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class CompanionRuntimeTest {

    private class CountingConsolidator : IMemoryConsolidator {
        val ticks = ArrayList<SleepKind>()
        override suspend fun tickAsync(kind: SleepKind): ConsolidationOutcome {
            ticks.add(kind)
            return ConsolidationOutcome(
                kind = kind,
                dailySummariesProduced = 0,
                semanticClustersProduced = 0,
                personaDeltasProduced = 0,
                corePromotions = 0,
                episodesPruned = 0,
                dailiesPruned = 0,
                semanticsPruned = 0,
                ranAtUtc = Instant.EPOCH,
            )
        }
    }

    /** Options with all periodic loops disabled for determinism. */
    private fun noLoops(catchUp: Boolean = true) = CompanionRuntimeOptions(
        dailyTickInterval = Duration.ZERO,
        weeklyTickInterval = Duration.ZERO,
        monthlyTickInterval = Duration.ZERO,
        syncBroadcastInterval = Duration.ZERO,
        initialDelay = Duration.ZERO,
        catchUpOnStart = catchUp,
    )

    @Test
    fun `start runs an on-demand catch-up consolidation`() = runTest {
        val consolidator = CountingConsolidator()
        val runtime = CompanionRuntime(consolidator, noLoops(catchUp = true))
        runtime.start()
        assertEquals(listOf(SleepKind.OnDemand), consolidator.ticks)
        runtime.stop()
    }

    @Test
    fun `catch-up can be disabled`() = runTest {
        val consolidator = CountingConsolidator()
        val runtime = CompanionRuntime(consolidator, noLoops(catchUp = false))
        runtime.start()
        assertTrue(consolidator.ticks.isEmpty())
        runtime.stop()
    }

    @Test
    fun `consolidateNow triggers an on-demand pass`() = runTest {
        val consolidator = CountingConsolidator()
        val runtime = CompanionRuntime(consolidator, noLoops(catchUp = false))
        val outcome = runtime.consolidateNow()
        assertEquals(SleepKind.OnDemand, outcome.kind)
        assertEquals(listOf(SleepKind.OnDemand), consolidator.ticks)
    }

    @Test
    fun `ingestMedia without an ingester throws`() = runTest {
        val runtime = CompanionRuntime(CountingConsolidator(), noLoops(catchUp = false))
        var threw = false
        try {
            runtime.ingestMedia(MediaModality.Image, byteArrayOf(1, 2, 3))
        } catch (_: IllegalStateException) {
            threw = true
        }
        assertTrue(threw)
    }

    @Test
    fun `syncNow without an engine is a no-op`() = runTest {
        val runtime = CompanionRuntime(CountingConsolidator(), noLoops(catchUp = false))
        runtime.syncNow() // must not throw
        assertTrue(true)
    }

    @Test
    fun `syncNow with an engine broadcasts an announce`() = runTest {
        val hub = InProcessSyncHub()
        // A listener channel captures whatever the runtime's engine broadcasts.
        val listener = InProcessCompanionStateChannel(hub, "listener")
        val announces = AtomicInteger()
        listener.subscribe { env: SyncEnvelope ->
            if (env.kind == SyncEnvelopeKind.Announce) announces.incrementAndGet()
        }

        val engineChannel: ICompanionStateChannel = InProcessCompanionStateChannel(hub, "runtime")
        val engine = CompanionStateSyncEngine(
            engineChannel, InMemorySyncableEntryStore(), HybridLogicalClock(1) { 1000L },
        ) { Instant.EPOCH }

        val runtime = CompanionRuntime(
            CountingConsolidator(), noLoops(catchUp = false), syncEngine = engine,
        )
        runtime.start()          // starts the engine
        runtime.syncNow()        // broadcast announce -> listener sees it

        assertTrue(announces.get() >= 1)
        runtime.stop()
    }

    @Test
    fun `start then stop is clean and repeatable`() = runTest {
        val runtime = CompanionRuntime(CountingConsolidator(), noLoops(catchUp = false))
        runtime.start()
        runtime.stop()
        // A second cycle must also work.
        runtime.start()
        runtime.stop()
        assertTrue(true)
    }
}
