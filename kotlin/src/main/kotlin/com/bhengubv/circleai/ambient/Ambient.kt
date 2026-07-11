// Ambient.kt
//
// Kotlin port of CircleAI.Ambient (AmbientPrimitives.cs +
// AmbientCompanionMonitor.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory ambient board (environmental readings + comfort
// preferences) plus an always-on background companion monitor.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `DateTimeOffset` -> `Instant`.
//   * `Latest` = newest reading for the device (or null).
//   * `History` newest-first, capped at `limit` (default 50).
//   * `IsComfortable` = false unless both a preference (for the location) and a
//     latest reading (for the device) exist, then |ΔtempC| <= 2 AND |Δhumidity|
//     <= 10 AND noise <= max noise.
//   * `AmbientCompanionMonitor` mirrors the C# `IAsyncDisposable` monitor:
//     Start/Stop drive a background poll loop (default 5 min) that calls
//     `IProactiveReasoningService.checkAsync`; it re-surfaces the inner session's
//     proactive events via [proactiveMessages]. Concurrency: the inner session's
//     proactive Flow is subscribed on a dedicated coroutine; the poll loop
//     swallows all non-cancellation exceptions so the monitor never crashes the
//     host. `IAsyncDisposable` -> `AutoCloseable` + suspend `disposeAsync()`.

package com.bhengubv.circleai.ambient

import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.hosting.IProactiveReasoningService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import kotlin.math.abs

// =====================================================================
// Primitives (AmbientPrimitives.cs)
// =====================================================================

/** An environmental reading. Mirrors C# `AmbientReading`. */
data class AmbientReading(
    val deviceId: String,
    val temperatureC: Double,
    val humidity: Double,
    val luxLight: Double,
    val dbNoise: Double,
    val atUtc: Instant,
)

/** A comfort preference for a location. Mirrors C# `AmbientPreference`. */
data class AmbientPreference(val location: String, val targetTempC: Double, val targetHumidity: Double, val maxNoiseDb: Double)

/** Deterministic ambient board. Mirrors C# `IAmbientBoard`. */
interface IAmbientBoard {
    fun record(r: AmbientReading)
    fun latest(deviceId: String): AmbientReading?
    fun history(deviceId: String, limit: Int = 50): List<AmbientReading>
    fun setPreference(p: AmbientPreference)
    fun getPreference(location: String): AmbientPreference?
    fun isComfortable(deviceId: String, location: String): Boolean
}

/** In-memory [IAmbientBoard]. Mirrors C# `InMemoryAmbientBoard`. */
class InMemoryAmbientBoard : IAmbientBoard {
    private val readings = mutableListOf<AmbientReading>()
    private val prefs = ConcurrentHashMap<String, AmbientPreference>()
    private val lock = Any()

    override fun record(r: AmbientReading) { synchronized(lock) { readings.add(r) } }

    override fun latest(deviceId: String): AmbientReading? = synchronized(lock) {
        readings.filter { it.deviceId == deviceId }.maxByOrNull { it.atUtc }
    }

    override fun history(deviceId: String, limit: Int): List<AmbientReading> = synchronized(lock) {
        readings.filter { it.deviceId == deviceId }.sortedByDescending { it.atUtc }.take(limit)
    }

    override fun setPreference(p: AmbientPreference) { prefs[p.location] = p }
    override fun getPreference(location: String): AmbientPreference? = prefs[location]

    override fun isComfortable(deviceId: String, location: String): Boolean {
        val pref = getPreference(location) ?: return false
        val last = latest(deviceId) ?: return false
        return abs(last.temperatureC - pref.targetTempC) <= 2 &&
            abs(last.humidity - pref.targetHumidity) <= 10 &&
            last.dbNoise <= pref.maxNoiseDb
    }
}

// =====================================================================
// AmbientCompanionMonitor (AmbientCompanionMonitor.cs)
// =====================================================================

/**
 * Always-on background monitor. Periodically evaluates proactive triggers via
 * [IProactiveReasoningService] and re-surfaces the session's proactive messages.
 * Designed for ultra-low CPU budgets between checks. Mirrors C#
 * `AmbientCompanionMonitor` (which is `IAsyncDisposable`).
 */
class AmbientCompanionMonitor(
    private val session: ICompanionSession,
    private val proactive: IProactiveReasoningService? = null,
    pollInterval: Duration? = null,
) : AutoCloseable {

    private val pollInterval: Duration = pollInterval ?: Duration.ofMinutes(5)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val relay = MutableSharedFlow<CompanionProactiveEvent>(extraBufferCapacity = 64)
    private var loopJob: Job? = null
    private var disposed = false

    /**
     * Proactive messages to surface on the ambient display or speaker — a merge
     * of the inner session's events. Mirrors C# `ProactiveMessageReady`.
     */
    val proactiveMessages: Flow<CompanionProactiveEvent> get() = relay.asSharedFlow()

    init {
        // Re-surface the inner session's proactive events. Subscribed synchronously
        // on a dedicated coroutine before any poll loop starts (fan-out relay is
        // buffered, so a slow ambient consumer never blocks the session).
        scope.launch {
            session.proactiveEvents.collect { relay.emit(it) }
        }
    }

    /** Starts the background poll loop. Non-blocking. Mirrors C# `Start`. */
    fun start() {
        if (disposed) throw IllegalStateException("AmbientCompanionMonitor is disposed")
        if (loopJob != null) return // Already running.
        loopJob = scope.launch { runLoop() }
    }

    /** Stops the background poll loop. Mirrors C# `Stop`. */
    fun stop() {
        loopJob?.cancel()
        loopJob = null
    }

    private suspend fun runLoop() {
        while (currentIsActive()) {
            try {
                delay(pollInterval.toMillis())
                proactive?.checkAsync(session.identityId)
            } catch (_: kotlinx.coroutines.CancellationException) {
                break
            } catch (_: Throwable) {
                // Swallow — ambient monitor must never crash the host process.
            }
        }
    }

    private fun currentIsActive(): Boolean = scope.isActive

    /** Releases the monitor + underlying session. Mirrors C# `DisposeAsync`. */
    suspend fun disposeAsync() {
        if (disposed) return
        disposed = true
        scope.cancel()
        session.close()
    }

    /** [AutoCloseable] bridge — runs [disposeAsync] synchronously. */
    override fun close() {
        runBlocking { disposeAsync() }
    }
}
