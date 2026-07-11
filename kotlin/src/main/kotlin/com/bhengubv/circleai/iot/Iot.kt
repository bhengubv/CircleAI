// Iot.kt
//
// Kotlin port of CircleAI.IoT (IoTPrimitives.cs + IoTCompanionPipeline.cs)
// — the C# reference is the EXACT spec. A deterministic in-memory IoT
// board (devices, telemetry, commands) plus a voice-in → Companion →
// voice-out pipeline for IoT speakers.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `DateTimeOffset` -> `Instant`.
//   * `Devices` orders by Name ASC.
//   * `LatestValue` returns the newest telemetry value for the metric, `NaN`
//     when none; `History` returns newest-first capped at `limit`.
//   * `CommandsFor` returns newest-first (SentUtc DESC).
//   * The pipeline mirrors C#'s: on a final transcription it fires-and-forgets
//     a handler that sends the utterance to the session, synthesises the reply
//     via the optional TTS engine, and raises [onAudioReady]. All handler
//     exceptions are swallowed so the device process never crashes. `close`
//     unsubscribes and closes the underlying VoicePipeline + session.

package com.bhengubv.circleai.iot

import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.voice.IAudioCapture
import com.bhengubv.circleai.voice.ITtsEngine
import com.bhengubv.circleai.voice.IVoiceTranscriber
import com.bhengubv.circleai.voice.IWakeWordDetector
import com.bhengubv.circleai.voice.TranscribedEvent
import com.bhengubv.circleai.voice.TtsSynthesisResult
import com.bhengubv.circleai.voice.VoicePipeline
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList

// =====================================================================
// Primitives (IoTPrimitives.cs)
// =====================================================================

/** A registered IoT device. Mirrors C# `IoTDevice`. */
data class IoTDevice(
    val deviceId: String,
    val name: String,
    val kind: String,
    val firmwareVersion: String,
    val lastSeenUtc: Instant,
)

/** A telemetry reading. Mirrors C# `IoTTelemetry`. */
data class IoTTelemetry(val deviceId: String, val metric: String, val value: Double, val atUtc: Instant)

/** A command issued to a device. Mirrors C# `IoTCommand`. */
data class IoTCommand(
    val commandId: String,
    val deviceId: String,
    val action: String,
    val argumentsJson: String,
    val sentUtc: Instant,
)

/** Deterministic IoT board. Mirrors C# `IIoTBoard`. */
interface IIoTBoard {
    fun register(d: IoTDevice)
    fun getDevice(id: String): IoTDevice?
    val devices: List<IoTDevice>
    fun recordTelemetry(t: IoTTelemetry)
    fun latestValue(deviceId: String, metric: String): Double
    fun history(deviceId: String, metric: String, limit: Int = 100): List<IoTTelemetry>
    fun sendCommand(c: IoTCommand)
    fun commandsFor(deviceId: String): List<IoTCommand>
}

/** In-memory [IIoTBoard]. Mirrors C# `InMemoryIoTBoard`. */
class InMemoryIoTBoard : IIoTBoard {
    private val devices_ = ConcurrentHashMap<String, IoTDevice>()
    private val telemetry = mutableListOf<IoTTelemetry>()
    private val commands = mutableListOf<IoTCommand>()
    private val lock = Any()

    override fun register(d: IoTDevice) { devices_[d.deviceId] = d }
    override fun getDevice(id: String): IoTDevice? = devices_[id]
    override val devices: List<IoTDevice>
        get() = devices_.values.sortedBy { it.name }

    override fun recordTelemetry(t: IoTTelemetry) { synchronized(lock) { telemetry.add(t) } }

    override fun latestValue(deviceId: String, metric: String): Double = synchronized(lock) {
        telemetry.filter { it.deviceId == deviceId && it.metric == metric }
            .maxByOrNull { it.atUtc }?.value ?: Double.NaN
    }

    override fun history(deviceId: String, metric: String, limit: Int): List<IoTTelemetry> {
        if (limit <= 0) throw IllegalArgumentException("limit must be positive")
        synchronized(lock) {
            return telemetry.filter { it.deviceId == deviceId && it.metric == metric }
                .sortedByDescending { it.atUtc }
                .take(limit)
        }
    }

    override fun sendCommand(c: IoTCommand) { synchronized(lock) { commands.add(c) } }
    override fun commandsFor(deviceId: String): List<IoTCommand> = synchronized(lock) {
        commands.filter { it.deviceId == deviceId }.sortedByDescending { it.sentUtc }
    }
}

// =====================================================================
// CompanionPipeline (IoTCompanionPipeline.cs)
// =====================================================================

/**
 * Voice-in → Companion → voice-out pipeline for IoT devices. Wires the wake
 * word detector, transcriber, Companion session, and TTS engine into a single
 * listening loop via [VoicePipeline]. Mirrors C# `IoTCompanionPipeline`.
 */
class IoTCompanionPipeline(
    private val session: ICompanionSession,
    wakeWord: IWakeWordDetector,
    transcriber: IVoiceTranscriber,
    audioCapture: IAudioCapture? = null,
    private val tts: ITtsEngine? = null,
) : AutoCloseable {

    private val voicePipeline = VoicePipeline(wakeWord, transcriber, audioCapture, ttsEngine = tts)
    private val audioReadyListeners = CopyOnWriteArrayList<(TtsSynthesisResult) -> Unit>()
    // Owns fire-and-forget transcription handling; SupervisorJob so one failing
    // turn cannot cancel the pipeline. Handlers swallow their own exceptions too.
    private val scope = CoroutineScope(Dispatchers.Default + SupervisorJob())

    @Volatile
    private var disposed = false

    private val transcribedListener: (TranscribedEvent) -> Unit = { e -> onTranscribed(e) }

    init {
        voicePipeline.onTranscribed(transcribedListener)
    }

    /**
     * Register a listener raised when the Companion has synthesised a reply
     * audio buffer ready for playback on the IoT speaker. Mirrors the C#
     * `AudioReady` event.
     */
    fun onAudioReady(listener: (TtsSynthesisResult) -> Unit) {
        audioReadyListeners.add(listener)
    }

    /** Starts the wake-word listener. Non-blocking. */
    suspend fun startAsync() = voicePipeline.startAsync()

    /** Stops the wake-word listener. */
    suspend fun stopAsync() = voicePipeline.stopAsync()

    private fun onTranscribed(e: TranscribedEvent) {
        // Fire-and-forget on the pipeline scope so we don't block the event.
        scope.launch { handleTranscription(e.result.text) }
    }

    private suspend fun handleTranscription(utterance: String) {
        if (utterance.isBlank()) return
        try {
            val reply = session.sendAsync(utterance)
            val engine = tts
            if (engine != null) {
                val audio = engine.synthesiseAsync(reply)
                for (l in audioReadyListeners) l(audio)
            }
        } catch (_: Throwable) {
            // Swallow — IoT pipeline must never crash the device process.
        }
    }

    override fun close() {
        if (disposed) return
        disposed = true
        scope.cancel()
        voicePipeline.close()
        session.close()
    }
}
