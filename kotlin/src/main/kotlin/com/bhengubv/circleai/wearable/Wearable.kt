// Wearable.kt
//
// Kotlin port of CircleAI.Wearable (WearablePrimitives.cs + WearableContext.cs +
// WearableCompanionAdapter.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory wearable board (devices + telemetry) plus a
// biometric-context-injecting companion adapter.
//
// Fidelity notes:
//   * C# `enum` (WearableKind, WearableTelemetryKind) -> Kotlin `enum class`.
//   * C# `record` -> Kotlin `data class`; `DateTimeOffset` -> `Instant`.
//   * `Devices` ordered by Vendor ASC.
//   * `Record` rejects samples for unknown devices (throws).
//   * `ReadSince` = samples (device, kind) at/after `since`, ASC.
//   * `LatestValue` = newest sample's value (or null).
//   * `AverageValue` = mean value over the window, NaN when empty.
//   * The adapter fixes Interface to Wearable and injects a "[Biometrics] …"
//     line (HR:%.0fbpm, Steps:, SpO₂:%.0f%, Workout:active) when a
//     [WearableContext] is set — reproduced field-for-field incl. trailing trim.

package com.bhengubv.circleai.wearable

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.util.Locale
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (WearablePrimitives.cs)
// =====================================================================

/** Kind of wearable device. Mirrors C# `WearableKind`. */
enum class WearableKind { Smartwatch, FitnessBand, ChestStrap, Patch, Headset }

/** Kind of wearable telemetry. Mirrors C# `WearableTelemetryKind`. */
enum class WearableTelemetryKind { HeartRate, Steps, Calories, SleepStage, SkinTempC, Stress, OxygenPct }

/** A wearable device descriptor. Mirrors C# `WearableDevice`. */
data class WearableDevice(val deviceId: String, val kind: WearableKind, val vendor: String, val firmwareVersion: String, val batteryPct: Double)

/** A telemetry sample. Mirrors C# `WearableSample`. */
data class WearableSample(val deviceId: String, val kind: WearableTelemetryKind, val value: Double, val atUtc: Instant)

/** Deterministic wearable board. Mirrors C# `IWearableBoard`. */
interface IWearableBoard {
    fun add(d: WearableDevice)
    fun getDevice(id: String): WearableDevice?
    val devices: List<WearableDevice>
    fun record(s: WearableSample)
    fun readSince(deviceId: String, kind: WearableTelemetryKind, since: Instant): List<WearableSample>
    fun latestValue(deviceId: String, kind: WearableTelemetryKind): Double?
    fun averageValue(deviceId: String, kind: WearableTelemetryKind, since: Instant): Double
}

/** In-memory [IWearableBoard]. Mirrors C# `InMemoryWearableBoard`. */
class InMemoryWearableBoard : IWearableBoard {
    private val devicesMap = ConcurrentHashMap<String, WearableDevice>()
    private val samples = mutableListOf<WearableSample>()
    private val lock = Any()

    override fun add(d: WearableDevice) { devicesMap[d.deviceId] = d }
    override fun getDevice(id: String): WearableDevice? = devicesMap[id]
    override val devices: List<WearableDevice>
        get() = devicesMap.values.sortedBy { it.vendor }

    override fun record(s: WearableSample) {
        if (!devicesMap.containsKey(s.deviceId)) throw IllegalStateException("Unknown device ${s.deviceId}")
        synchronized(lock) { samples.add(s) }
    }

    override fun readSince(deviceId: String, kind: WearableTelemetryKind, since: Instant): List<WearableSample> =
        synchronized(lock) {
            samples.filter { it.deviceId == deviceId && it.kind == kind && !it.atUtc.isBefore(since) }
                .sortedBy { it.atUtc }
        }

    override fun latestValue(deviceId: String, kind: WearableTelemetryKind): Double? = synchronized(lock) {
        samples.filter { it.deviceId == deviceId && it.kind == kind }.maxByOrNull { it.atUtc }?.value
    }

    override fun averageValue(deviceId: String, kind: WearableTelemetryKind, since: Instant): Double {
        val items = readSince(deviceId, kind, since)
        return if (items.isEmpty()) Double.NaN else items.map { it.value }.average()
    }
}

// =====================================================================
// WearableContext (WearableContext.cs)
// =====================================================================

/**
 * Biometric snapshot injected into the Companion context on wearable surfaces.
 * Values are optional. Mirrors C# `WearableContext`.
 */
data class WearableContext(
    val heartRateBpm: Double?,
    val stepCountToday: Int?,
    val spO2Percent: Double?,
    val skinTempCelsius: Double?,
    val isWorkoutActive: Boolean,
    val capturedAt: Instant,
)

// =====================================================================
// CompanionAdapter (WearableCompanionAdapter.cs)
// =====================================================================

/**
 * Wraps an [ICompanionSession] with wearable-specific biometric context.
 * Mirrors C# `WearableCompanionAdapter`.
 */
class WearableCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {

    /** Current biometric snapshot injected into each message; null = no injection. */
    var currentContext: WearableContext? = null

    override val sessionId: String get() = inner.sessionId
    override val identityId: String get() = inner.identityId
    override val interfaceKind: InterfaceKind get() = InterfaceKind.Wearable
    override val history: List<CompanionTurn> get() = inner.history
    override val proactiveEvents: Flow<CompanionProactiveEvent> get() = inner.proactiveEvents

    override fun getContext(): CompanionContext = inner.getContext()
    override suspend fun refreshContextAsync() = inner.refreshContextAsync()
    override suspend fun signalFeedbackAsync(positive: Boolean, note: String?) =
        inner.signalFeedbackAsync(positive, note)
    override fun close() = inner.close()

    override suspend fun sendAsync(message: String): String = inner.sendAsync(enrichMessage(message))
    override fun streamAsync(message: String): Flow<String> = inner.streamAsync(enrichMessage(message))
    override suspend fun agentAsync(instruction: String): String = inner.agentAsync(enrichMessage(instruction))

    private fun enrichMessage(message: String): String {
        val ctx = currentContext ?: return message
        val sb = StringBuilder(message)
        sb.append('\n')
        sb.append("[Biometrics] ")
        ctx.heartRateBpm?.let { sb.append("HR:${String.format(Locale.US, "%.0f", it)}bpm ") }
        ctx.stepCountToday?.let { sb.append("Steps:$it ") }
        ctx.spO2Percent?.let { sb.append("SpO₂:${String.format(Locale.US, "%.0f", it)}% ") }
        if (ctx.isWorkoutActive) sb.append("Workout:active ")
        return sb.toString().trimEnd()
    }

    suspend fun interpretReadingsAsync(metric: String, sampleData: String, baseline: String): String =
        inner.agentAsync("Interpret wearable $metric from samples: $sampleData vs baseline: $baseline. Signal vs noise, what to do.")

    suspend fun correlateWithBehaviourAsync(metric: String, behaviourLog: String): String =
        inner.agentAsync("Correlate $metric trend with behaviour log: $behaviourLog. Hypotheses + experiment to test the strongest one.")

    suspend fun suggestTrackingExperimentAsync(goal: String, availableMetrics: String): String =
        inner.agentAsync("Suggest a 2-week tracking experiment for goal '$goal' using metrics: $availableMetrics. Protocol + success criteria.")

    suspend fun explainBatterySavingsAsync(deviceModel: String, currentBatteryPct: String, usagePattern: String): String =
        inner.agentAsync("Suggest battery savings for $deviceModel at $currentBatteryPct% with usage: $usagePattern. Ranked by impact.")
}
