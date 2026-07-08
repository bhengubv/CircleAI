// CompanionRuntime.kt
//
// Kotlin port of CircleAI.Memory.Runtime — the C# reference is the EXACT spec
// (CompanionRuntime.cs, CompanionRuntimeOptions.cs).
//
// CompanionRuntime owns the lifecycle of the memory pipeline (consolidator,
// sync engine, multimodal ingester) and ticks the consolidation passes on a
// configurable schedule. In C# it is an IHostedService; here it exposes
// start()/stop() coroutine loops mirroring the established
// ProactiveSchedulerBackgroundService port pattern, plus a single ingestion
// entry point and an immediate-consolidation trigger.

package com.bhengubv.circleai.runtime

import com.bhengubv.circleai.memory.brain.ConsolidationOutcome
import com.bhengubv.circleai.memory.brain.IMemoryConsolidator
import com.bhengubv.circleai.memory.brain.IngestOptions
import com.bhengubv.circleai.memory.brain.IngestionResult
import com.bhengubv.circleai.memory.brain.MediaModality
import com.bhengubv.circleai.memory.brain.MultimodalMemoryIngester
import com.bhengubv.circleai.memory.brain.SleepKind
import com.bhengubv.circleai.memory.sync.ICompanionStateSyncEngine
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.time.Duration

// ===========================================================================
// CompanionRuntimeOptions  (CompanionRuntimeOptions.cs)
// ===========================================================================

/**
 * Configuration for [CompanionRuntime]. All values have sensible defaults so a
 * host can construct the runtime and get a working pipeline out of the box.
 */
data class CompanionRuntimeOptions(
    /**
     * Cadence for the daily-tier consolidation pass. Default: every 6 hours.
     * [Duration.ZERO] disables automatic daily ticks.
     */
    val dailyTickInterval: Duration = Duration.ofHours(6),

    /** Cadence for the weekly-tier consolidation pass. Default: every 24 hours. */
    val weeklyTickInterval: Duration = Duration.ofHours(24),

    /**
     * Cadence for the monthly-tier (persona-delta) consolidation pass.
     * Default: every 48 hours.
     */
    val monthlyTickInterval: Duration = Duration.ofHours(48),

    /**
     * Cadence at which the runtime broadcasts its sync state vector to peers.
     * Default: every 5 minutes. [Duration.ZERO] disables periodic sync (the
     * engine still responds to inbound envelopes; only the initiating Announce
     * broadcasts are suppressed).
     */
    val syncBroadcastInterval: Duration = Duration.ofMinutes(5),

    /**
     * Initial delay before the first consolidator tick after [CompanionRuntime.start].
     * Default: 30 seconds. Keeps startup quiet.
     */
    val initialDelay: Duration = Duration.ofSeconds(30),

    /**
     * When true, the runtime runs an OnDemand consolidation pass during
     * [CompanionRuntime.start] to catch up anything pending before the timer
     * cadence kicks in. Default: true.
     */
    val catchUpOnStart: Boolean = true,
)

// ===========================================================================
// CompanionRuntime  (CompanionRuntime.cs)
// ===========================================================================

/**
 * Owns the lifecycle of the memory pipeline (consolidator, sync engine,
 * multimodal ingester) and ticks the consolidation passes on a configurable
 * schedule.
 *
 * @param consolidator the memory consolidator (required).
 * @param options tunable schedule/behaviour; defaults to [CompanionRuntimeOptions].
 * @param syncEngine optional companion-state sync engine; skipped when null.
 * @param ingester optional multimodal ingester; [ingestMedia] throws when null.
 * @param logger optional structured log sink; defaults to a no-op.
 */
class CompanionRuntime(
    private val consolidator: IMemoryConsolidator,
    private val options: CompanionRuntimeOptions = CompanionRuntimeOptions(),
    private val syncEngine: ICompanionStateSyncEngine? = null,
    private val ingester: MultimodalMemoryIngester? = null,
    private val logger: (String) -> Unit = {},
) : AutoCloseable {

    private var scope: CoroutineScope? = null
    private var dailyLoop: Job? = null
    private var weeklyLoop: Job? = null
    private var monthlyLoop: Job? = null
    private var syncLoop: Job? = null

    // -- Lifecycle ----------------------------------------------------------

    /** Starts the sync engine (if any), runs optional catch-up, and launches the tick loops. */
    suspend fun start() {
        logger("CompanionRuntime starting.")
        val s = CoroutineScope(Dispatchers.Default + SupervisorJob())
        scope = s

        syncEngine?.let {
            it.start()
            logger("Sync engine started.")
        }

        if (options.catchUpOnStart) {
            try {
                val outcome = consolidator.tickAsync(SleepKind.OnDemand)
                logger(
                    "Catch-up consolidation: daily=${outcome.dailySummariesProduced} " +
                        "weekly=${outcome.semanticClustersProduced} " +
                        "monthly=${outcome.personaDeltasProduced} core=${outcome.corePromotions}."
                )
            } catch (ex: Exception) {
                logger("Catch-up consolidation failed (non-fatal): ${ex.message}")
            }
        }

        if (options.dailyTickInterval > Duration.ZERO)
            dailyLoop = s.launch { runPeriodic(SleepKind.Daily, options.dailyTickInterval) }
        if (options.weeklyTickInterval > Duration.ZERO)
            weeklyLoop = s.launch { runPeriodic(SleepKind.Weekly, options.weeklyTickInterval) }
        if (options.monthlyTickInterval > Duration.ZERO)
            monthlyLoop = s.launch { runPeriodic(SleepKind.Monthly, options.monthlyTickInterval) }
        if (syncEngine != null && options.syncBroadcastInterval > Duration.ZERO)
            syncLoop = s.launch { runSyncBroadcasts(options.syncBroadcastInterval) }

        logger("CompanionRuntime started.")
    }

    /** Cancels the tick loops, awaits them, and disposes the sync engine. */
    suspend fun stop() {
        logger("CompanionRuntime stopping.")
        safeCancel(dailyLoop)
        safeCancel(weeklyLoop)
        safeCancel(monthlyLoop)
        safeCancel(syncLoop)
        dailyLoop = null
        weeklyLoop = null
        monthlyLoop = null
        syncLoop = null
        scope = null

        syncEngine?.close()
        logger("CompanionRuntime stopped.")
    }

    // -- Public helpers -----------------------------------------------------

    /**
     * Triggers an OnDemand consolidation pass. Hosts call this after large
     * chunks of new activity when they don't want to wait for the timer.
     */
    suspend fun consolidateNow(): ConsolidationOutcome =
        consolidator.tickAsync(SleepKind.OnDemand)

    /**
     * Forwards multimodal ingestion to the registered ingester. Throws
     * [IllegalStateException] when no ingester was wired.
     */
    suspend fun ingestMedia(
        modality: MediaModality,
        sourceBytes: ByteArray,
        mimeType: String? = null,
        sourceUri: String? = null,
        tags: Map<String, String>? = null,
    ): IngestionResult {
        val ing = ingester
            ?: throw IllegalStateException(
                "CompanionRuntime was constructed without a MultimodalMemoryIngester."
            )
        return ing.ingestAsync(
            modality,
            sourceBytes,
            IngestOptions(mimeType = mimeType, sourceUri = sourceUri, tags = tags),
        )
    }

    /** Forces an immediate sync broadcast. No-op when sync isn't wired. */
    suspend fun syncNow() {
        syncEngine?.syncNow()
    }

    // -- AutoCloseable ------------------------------------------------------

    /** Best-effort synchronous close — cancels loops and closes the sync engine. */
    override fun close() {
        dailyLoop?.cancel()
        weeklyLoop?.cancel()
        monthlyLoop?.cancel()
        syncLoop?.cancel()
        dailyLoop = null
        weeklyLoop = null
        monthlyLoop = null
        syncLoop = null
        scope = null
        syncEngine?.close()
    }

    // -- Internals ----------------------------------------------------------

    private suspend fun runPeriodic(kind: SleepKind, interval: Duration) {
        val self = scope ?: return
        try {
            delay(options.initialDelay.toMillis())
            while (self.isActive) {
                try {
                    val outcome = consolidator.tickAsync(kind)
                    if (outcome.dailySummariesProduced + outcome.semanticClustersProduced +
                        outcome.personaDeltasProduced + outcome.corePromotions > 0
                    ) {
                        logger(
                            "Consolidation tick $kind: daily=${outcome.dailySummariesProduced} " +
                                "weekly=${outcome.semanticClustersProduced} " +
                                "monthly=${outcome.personaDeltasProduced} core=${outcome.corePromotions}."
                        )
                    }
                } catch (ex: Exception) {
                    logger("Consolidation tick $kind failed: ${ex.message}")
                }
                delay(interval.toMillis())
            }
        } catch (_: Exception) {
            // graceful — cancellation surfaces here
        }
    }

    private suspend fun runSyncBroadcasts(interval: Duration) {
        val self = scope ?: return
        try {
            delay(options.initialDelay.toMillis())
            while (self.isActive) {
                try {
                    syncEngine?.syncNow()
                } catch (ex: Exception) {
                    logger("Sync broadcast failed: ${ex.message}")
                }
                delay(interval.toMillis())
            }
        } catch (_: Exception) {
            // graceful
        }
    }

    private companion object {
        suspend fun safeCancel(job: Job?) {
            if (job == null) return
            try {
                job.cancelAndJoin()
            } catch (_: Exception) {
                // already logged / graceful
            }
        }
    }
}
