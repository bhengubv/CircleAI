// Thermal.kt
//
// Kotlin port of the CircleAI.Hosting thermal + memory-pressure + background
// worker surface — the C# reference is the EXACT spec (IThermalThrottleService.cs,
// ThermalThrottleService.cs, IMemoryPressureSource.cs, BackgroundInferenceWorker.cs).
//
// The C# ThermalThrottleService samples platform thermal APIs behind #if guards
// (Android PowerManager / iOS NSProcessInfo / Windows WMI / Linux sysfs). The
// portable Kotlin core injects the sampler behind [IThermalSampler] so hosts wire
// a platform sampler while the default reads Linux sysfs and tests use a fake.
// State machine, thresholds, ShouldPauseInference, and StateChanged semantics are
// identical.

package com.bhengubv.circleai.hosting

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.File
import java.time.Duration
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference

// =====================================================================
// ThermalState + IThermalThrottleService (IThermalThrottleService.cs)
// =====================================================================

/**
 * Coarse thermal state, ordered from coolest to hottest so numeric comparisons
 * (e.g. `>= ThermalState.Serious`) are meaningful. Ordinal matches the C# enum
 * values (Unknown=0..Critical=4). Mirrors C# `ThermalState`.
 */
enum class ThermalState {
    /** State could not be determined. */
    Unknown,
    /** Device is within normal operating temperature. */
    Normal,
    /** Device is slightly warm. */
    Fair,
    /** Device is hot; OS may have begun throttling. */
    Serious,
    /** Device is critically hot. */
    Critical,
}

/**
 * The platform thermal sampler seam. The C# service samples OS APIs directly
 * behind #if; here the host injects a sampler. Returns the current coarse state.
 */
interface IThermalSampler {
    fun sample(): ThermalState
}

/**
 * Default sampler that reads Linux sysfs (`/sys/class/thermal/thermal_zone0/temp`,
 * millidegrees Celsius). Returns [ThermalState.Unknown] when the path is absent
 * (e.g. Windows/macOS host without a platform sampler injected). Byte-identical
 * thresholds to the C# Linux branch.
 */
class LinuxSysfsThermalSampler : IThermalSampler {
    override fun sample(): ThermalState {
        val f = File(LINUX_THERMAL_PATH)
        if (!f.exists()) return ThermalState.Unknown
        return try {
            val text = f.readText().trim()
            val milliCelsius = text.toIntOrNull() ?: return ThermalState.Unknown
            when {
                milliCelsius > MILLI_CELSIUS_CRITICAL -> ThermalState.Critical
                milliCelsius > MILLI_CELSIUS_SERIOUS -> ThermalState.Serious
                else -> ThermalState.Normal
            }
        } catch (_: Exception) {
            ThermalState.Unknown
        }
    }

    private companion object {
        const val LINUX_THERMAL_PATH = "/sys/class/thermal/thermal_zone0/temp"
        const val MILLI_CELSIUS_SERIOUS = 75_000
        const val MILLI_CELSIUS_CRITICAL = 90_000
    }
}

/**
 * Polls platform thermal APIs and exposes the current device temperature state.
 * Mirrors C# `IThermalThrottleService`.
 */
interface IThermalThrottleService : AutoCloseable {
    /** Most-recently sampled thermal state. */
    val currentState: ThermalState

    /**
     * True when [currentState] is [ThermalState.Serious] or [ThermalState.Critical].
     * Inference workers should pause when this returns true.
     */
    val shouldPauseInference: Boolean

    /** Invoked whenever [currentState] changes. Mirrors the C# StateChanged event. */
    var stateChanged: ((ThermalState) -> Unit)?

    /** Starts the background polling loop. Safe to call multiple times. */
    fun startMonitoring()

    /** Stops the polling loop. The current state is retained. */
    fun stopMonitoring()
}

/**
 * Cross-platform thermal state poller. Detects device temperature via the
 * injected [IThermalSampler] and surfaces it as a [ThermalState]. Mirrors C#
 * `ThermalThrottleService` — 10-second poll, StateChanged fired on transitions,
 * ShouldPauseInference at Serious+.
 */
class ThermalThrottleService(
    private val sampler: IThermalSampler = LinuxSysfsThermalSampler(),
) : IThermalThrottleService {

    private val currentStateRaw = AtomicInteger(ThermalState.Unknown.ordinal)
    private var scope: CoroutineScope? = null
    private var pollJob: Job? = null
    private var disposed = false

    // 0 = not running, 1 = running.
    private val running = AtomicInteger(0)

    override val currentState: ThermalState get() = ThermalState.entries[currentStateRaw.get()]

    override val shouldPauseInference: Boolean get() = currentState >= ThermalState.Serious

    override var stateChanged: ((ThermalState) -> Unit)? = null

    override fun startMonitoring() {
        check(!disposed) { "ThermalThrottleService is disposed." }
        if (!running.compareAndSet(0, 1)) return

        val s = CoroutineScope(Dispatchers.Default + Job())
        scope = s
        pollJob = s.launch { pollLoop() }
    }

    override fun stopMonitoring() {
        pollJob?.cancel()
        scope?.coroutineContext?.get(Job)?.cancel()
        pollJob = null
        scope = null
        running.set(0)
    }

    private suspend fun pollLoop() {
        // Sample immediately so callers get a valid state before the first tick.
        applyNewState(sampleThermalState())

        val self = scope ?: return
        while (self.isActive) {
            try {
                delay(POLL_INTERVAL.toMillis())
            } catch (_: CancellationException) {
                return
            }
            applyNewState(sampleThermalState())
        }
    }

    private fun applyNewState(newState: ThermalState) {
        val newRaw = newState.ordinal
        val previousRaw = currentStateRaw.getAndSet(newRaw)
        if (previousRaw != newRaw) {
            try {
                stateChanged?.invoke(newState)
            } catch (_: Exception) {
                // Handler threw — non-fatal.
            }
        }
    }

    private fun sampleThermalState(): ThermalState =
        try {
            sampler.sample()
        } catch (_: Exception) {
            ThermalState.Unknown
        }

    override fun close() {
        if (disposed) return
        disposed = true
        stopMonitoring()
    }

    private companion object {
        val POLL_INTERVAL: Duration = Duration.ofSeconds(10)
    }
}

// =====================================================================
// IMemoryPressureSource (IMemoryPressureSource.cs)
// =====================================================================

/**
 * Coarse memory-pressure level. Mirrors Android's onTrimMemory contract and
 * iOS's memory-warning notification. Mirrors C# `MemoryPressureLevel`.
 */
enum class MemoryPressureLevel {
    /** Plenty of headroom; no action. */
    Normal,
    /** OS asked apps to release optional caches. Drop prefix cache. */
    Trim,
    /** OS is about to kill the process. Drop everything; consider downshifting. */
    Critical,
}

/**
 * A platform-published memory-pressure signal. Mirrors C# `IMemoryPressureSource`.
 * The subscribe handler receives (oldLevel, newLevel); [subscribe] returns an
 * unsubscribe handle.
 */
interface IMemoryPressureSource {
    /** Current pressure level as last observed. */
    val current: MemoryPressureLevel

    /** Subscribe to pressure-level transitions. Returns an [AutoCloseable] unsubscribe handle. */
    fun subscribe(handler: suspend (MemoryPressureLevel, MemoryPressureLevel) -> Unit): AutoCloseable
}

/**
 * Default [IMemoryPressureSource] that always reports Normal pressure and never
 * raises events. Mirrors C# `NullMemoryPressureSource`.
 */
object NullMemoryPressureSource : IMemoryPressureSource {
    override val current: MemoryPressureLevel get() = MemoryPressureLevel.Normal
    override fun subscribe(handler: suspend (MemoryPressureLevel, MemoryPressureLevel) -> Unit): AutoCloseable =
        AutoCloseable { }
}

/**
 * Manually-driven [IMemoryPressureSource]. Hosting layers (or tests) construct
 * one and call [raise] when the platform publishes a pressure event. Thread-safe.
 * Mirrors C# `ManualMemoryPressureSource`.
 */
class ManualMemoryPressureSource : IMemoryPressureSource {
    private val gate = Any()
    private var currentLevel = MemoryPressureLevel.Normal
    private val handlers = ArrayList<suspend (MemoryPressureLevel, MemoryPressureLevel) -> Unit>()

    override val current: MemoryPressureLevel
        get() = synchronized(gate) { currentLevel }

    override fun subscribe(handler: suspend (MemoryPressureLevel, MemoryPressureLevel) -> Unit): AutoCloseable {
        synchronized(gate) { handlers.add(handler) }
        return AutoCloseable { synchronized(gate) { handlers.remove(handler) } }
    }

    /**
     * Publish a new pressure level. Idempotent for the same level — only
     * transitions fire handlers. Mirrors C# `Raise`.
     */
    suspend fun raise(level: MemoryPressureLevel) {
        val previous: MemoryPressureLevel
        val snapshot: List<suspend (MemoryPressureLevel, MemoryPressureLevel) -> Unit>
        synchronized(gate) {
            if (currentLevel == level) return
            previous = currentLevel
            currentLevel = level
            snapshot = handlers.toList()
        }
        for (h in snapshot) {
            try {
                h(previous, level)
            } catch (_: Exception) {
                // error-isolated; pressure handlers must not break the source
            }
        }
    }
}

// =====================================================================
// BackgroundInferenceWorker (BackgroundInferenceWorker.cs)
// =====================================================================

/**
 * Wraps a [IAIService] in a hosted-service lifecycle so it participates in a
 * generic host (start/stop). Honours [IThermalThrottleService] when one is
 * supplied: sets [isPaused] while the device is thermally throttled. Mirrors C#
 * `BackgroundInferenceWorker` (IHostedService).
 */
class BackgroundInferenceWorker(
    private val butler: IAIService,
    private val thermal: IThermalThrottleService? = null,
) : AutoCloseable {

    private val paused = AtomicReference(false)
    // 0 = running, 1 = stopped.
    private val stopped = AtomicInteger(0)
    private val thermalHandler: (ThermalState) -> Unit = ::onThermalStateChanged

    /**
     * True while the device is in a thermally-throttled state. Callers that queue
     * inference work should check this before submitting.
     */
    val isPaused: Boolean get() = paused.get()

    /** Starts the butler service and, if available, begins thermal monitoring. */
    suspend fun startAsync() {
        if (thermal != null) {
            thermal.stateChanged = thermalHandler
            thermal.startMonitoring()
        }
        butler.startAsync()
    }

    /** Stops the butler service and thermal monitoring. Safe to call multiple times. */
    suspend fun stopAsync() {
        if (!stopped.compareAndSet(0, 1)) return
        if (thermal != null) {
            if (thermal.stateChanged === thermalHandler) thermal.stateChanged = null
            thermal.stopMonitoring()
        }
        butler.stopAsync()
    }

    override fun close() {
        // Best-effort synchronous teardown.
        if (stopped.compareAndSet(0, 1)) {
            if (thermal != null) {
                if (thermal.stateChanged === thermalHandler) thermal.stateChanged = null
                thermal.stopMonitoring()
            }
        }
    }

    private fun onThermalStateChanged(newState: ThermalState) {
        val shouldPause = newState >= ThermalState.Serious
        if (shouldPause && !paused.get()) {
            paused.set(true)
        } else if (!shouldPause && paused.get()) {
            paused.set(false)
        }
    }
}
