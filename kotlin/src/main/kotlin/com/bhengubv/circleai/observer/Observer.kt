// Observer.kt
//
// Kotlin port of CircleAI.Observer (Contracts.cs + InMemoryObserver.cs +
// NullImplementations.cs) — the C# reference is the EXACT spec. The
// perceive-reason-act observation loop: sensors (each a subscribable perception
// source), a tool registry, and a loop that ticks at a configured interval,
// reasons over the latest readings, invokes tools, and fans a tick out to
// subscribers.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `DateTimeOffset` -> `java.time.Instant`; C# `TimeSpan` -> `kotlin Duration`.
//   * C# `ReadOnlyMemory<byte>` -> `ByteArray`.
//   * C# `Task`/`ValueTask` -> `suspend fun`; the reasoner + tool-invoke +
//     subscriber handlers are suspend function seams.
//   * The loop runs on an injected [CoroutineScope] (default: a fresh
//     Dispatchers.Default scope). Start/Stop mirror the C# CancellationTokenSource
//     lifecycle: Start launches the run job, Stop cancels + joins.
//
// CONCURRENCY SAFETY: the subscriber list is snapshotted under the lock and the
// snapshot is iterated OUTSIDE the lock, so a handler that (un)subscribes cannot
// deadlock or mutate-during-iterate — this mirrors the C# `lock (_lock) snap =
// _subs.ToArray();` pattern exactly.

package com.bhengubv.circleai.observer

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import kotlin.coroutines.cancellation.CancellationException
import kotlin.time.Duration

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** One snapshot from one sensor. Mirrors C# `SensorReading`. */
data class SensorReading(
    val sensorId: String,
    val kind: String,
    val capturedAtUtc: Instant,
    val values: Map<String, String>,
    val payload: ByteArray? = null,
)

/** A single perception source. Mirrors C# `ISensor`. */
interface ISensor : AutoCloseable {
    val sensorId: String
    val kind: String
    val backendId: String

    suspend fun startAsync()
    suspend fun stopAsync()

    /** Subscribe to readings; dispose the returned handle to unsubscribe. */
    fun subscribe(handler: suspend (SensorReading) -> Unit): AutoCloseable
}

/** One tool the observer can invoke during its act tick. Mirrors C# `ObservationTool`. */
data class ObservationTool(
    val toolId: String,
    val description: String,
    val tags: List<String>,
    val invoke: suspend (Map<String, String>) -> String,
)

/** Registry of tools available to the observation loop. Mirrors C# `IObservationToolbox`. */
interface IObservationToolbox {
    val backendId: String
    fun registerTool(tool: ObservationTool)
    fun tryGet(toolId: String): ObservationTool?
    fun listTools(): List<ObservationTool>
}

/** One loop tick. Mirrors C# `ObservationTick`. */
data class ObservationTick(
    val atUtc: Instant,
    val perceived: List<SensorReading>,
    val reasoning: String,
    val toolsInvoked: List<String>,
)

/** The perceive-reason-act loop. Mirrors C# `IObservationLoop`. */
interface IObservationLoop : AutoCloseable {
    val backendId: String
    suspend fun startAsync(tickInterval: Duration)
    suspend fun stopAsync()
    fun subscribe(handler: suspend (ObservationTick) -> Unit): AutoCloseable
}

// =====================================================================
// In-memory implementations (InMemoryObserver.cs)
// =====================================================================

/** Captures the latest reading from a sensor. Mirrors C# `SensorRecorder`. */
class SensorRecorder(sensor: ISensor) : AutoCloseable {
    private val sub: AutoCloseable

    @Volatile
    var latest: SensorReading? = null
        private set

    init {
        sub = sensor.subscribe { latest = it }
    }

    override fun close() = sub.close()
}

/** Decision shape returned by the reasoner. Mirrors C# `ObserverDecision`. */
data class ObserverDecision(
    val reasoning: String,
    val toolsToInvoke: List<String>,
    val toolArgs: Map<String, String>? = null,
)

/**
 * The perceive-reason-act loop. Mirrors C# `InMemoryObservationLoop`.
 *
 * @param scope the coroutine scope the run loop is launched on. Defaults to a
 *   fresh [Dispatchers.Default]-backed scope owned by this loop.
 */
class InMemoryObservationLoop(
    sensors: Iterable<ISensor>,
    private val toolbox: IObservationToolbox,
    private val reason: suspend (List<SensorReading>) -> ObserverDecision,
    private val scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.Default),
) : IObservationLoop {

    private val recorders: List<SensorRecorder> = sensors.map { SensorRecorder(it) }
    private val subs = ArrayList<suspend (ObservationTick) -> Unit>()
    private val lock = Any()

    @Volatile
    private var runJob: Job? = null

    override val backendId: String get() = "in-memory"

    override suspend fun startAsync(tickInterval: Duration) {
        if (runJob != null) throw IllegalStateException("already started")
        runJob = scope.launch { runLoop(tickInterval) }
    }

    override suspend fun stopAsync() {
        val job = runJob ?: return
        runJob = null
        try {
            job.cancelAndJoin()
        } catch (_: CancellationException) {
            // expected
        }
    }

    override fun subscribe(handler: suspend (ObservationTick) -> Unit): AutoCloseable {
        synchronized(lock) { subs.add(handler) }
        return Token(handler)
    }

    override fun close() {
        // Best-effort synchronous stop: cancel the job without awaiting.
        runJob?.cancel()
        runJob = null
        recorders.forEach { it.close() }
    }

    private suspend fun runLoop(interval: Duration) {
        while (scope.isActive) {
            try {
                val readings = recorders.mapNotNull { it.latest }
                val decision = reason(readings)
                val invoked = ArrayList<String>()
                for (toolId in decision.toolsToInvoke) {
                    val tool = toolbox.tryGet(toolId)
                    if (tool != null) {
                        try {
                            tool.invoke(decision.toolArgs ?: emptyMap())
                            invoked.add(toolId)
                        } catch (ex: Exception) {
                            // tool threw — skip, keep ticking (matches C# Debug.WriteLine).
                        }
                    }
                }
                val tick = ObservationTick(Instant.now(), readings, decision.reasoning, invoked)
                // Snapshot subscribers UNDER the lock, invoke OUTSIDE it.
                val snap: List<suspend (ObservationTick) -> Unit>
                synchronized(lock) { snap = subs.toList() }
                for (s in snap) {
                    try {
                        s(tick)
                    } catch (ex: Exception) {
                        // subscriber threw — skip (matches C#).
                    }
                }
            } catch (ex: CancellationException) {
                break
            } catch (ex: Exception) {
                // reasoner threw — skip this tick (matches C#).
            }
            try {
                delay(interval)
            } catch (ex: CancellationException) {
                break
            }
        }
    }

    private inner class Token(private val handler: suspend (ObservationTick) -> Unit) : AutoCloseable {
        override fun close() {
            synchronized(lock) { subs.remove(handler) }
        }
    }
}

// =====================================================================
// Null / registry implementations (NullImplementations.cs)
// =====================================================================

/** No-op [ISensor]. Mirrors C# `NullSensor`. */
class NullSensor : ISensor {
    override val sensorId: String = "null"
    override val kind: String = "null"
    override val backendId: String get() = "null"

    override suspend fun startAsync() {}
    override suspend fun stopAsync() {}
    override fun subscribe(handler: suspend (SensorReading) -> Unit): AutoCloseable = EmptyDisposable
    override fun close() {}

    private object EmptyDisposable : AutoCloseable {
        override fun close() {}
    }
}

/** In-memory tool registry. Mirrors C# `InMemoryObservationToolbox`. */
class InMemoryObservationToolbox : IObservationToolbox {
    private val tools = ConcurrentHashMap<String, ObservationTool>()

    override val backendId: String get() = "in-memory"

    override fun registerTool(tool: ObservationTool) {
        tools[tool.toolId] = tool
    }

    override fun tryGet(toolId: String): ObservationTool? = tools[toolId]

    override fun listTools(): List<ObservationTool> = tools.values.toList()
}

/** No-op [IObservationLoop]. Mirrors C# `NullObservationLoop`. */
class NullObservationLoop private constructor() : IObservationLoop {
    override val backendId: String get() = "null"
    override suspend fun startAsync(tickInterval: Duration) {}
    override suspend fun stopAsync() {}
    override fun subscribe(handler: suspend (ObservationTick) -> Unit): AutoCloseable = EmptyDisposable
    override fun close() {}

    private object EmptyDisposable : AutoCloseable {
        override fun close() {}
    }

    companion object {
        val Instance = NullObservationLoop()
    }
}
