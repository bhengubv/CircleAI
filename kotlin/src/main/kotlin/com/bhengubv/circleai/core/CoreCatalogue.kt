// CoreCatalogue.kt
//
// What a model IS, where it comes from, where it lives, what a download is
// doing, how far a component has been proven, and where the RAM figure came
// from.
//
// Ported from src/CircleAI.Core/{ModelModality, ModelSource, ModelPaths,
// IModelSource, Diagnostics/CircleAIDiagnostics,
// Validation/CircleAIVerificationStatusAttribute, SystemInfoDeviceContext,
// Models/EmbeddedVoiceConfigs}.cs and the platform-memory half of DeviceProbe.cs.

package com.bhengubv.circleai.core

import java.io.File
import java.util.Locale
import java.util.concurrent.atomic.AtomicReference

/** What a model DOES. Kept separate from its size or its backend, because those
 *  change with the build and this does not. */
enum class ModelModality { Chat, Asr, Tts, Vad, WakeWord, Vision, Music, Video, Coding, Phonemizer }

/**
 * Where a model's bytes come from.
 *
 * `HuggingFaceBucket` is a bucket we hold no token for, which is why it is a
 * separate case rather than a URL detail: a 401 from a bucket is not the same
 * problem as a 404 from a repo, and treating them alike sends somebody looking
 * for a file that is there.
 */
enum class ModelSource { ModelScope, HuggingFace, HuggingFaceBucket, GitHubRelease }

/**
 * What a download is doing right now — not all of it is transfer.
 *
 * A 433 MB bundle spends real time hashing and, on a bad link, retrying.
 * Without a phase those look identical to a stalled download, and the person
 * watching concludes the app has hung.
 */
enum class DownloadPhase { Downloading, Resuming, Retrying, Verifying, Cached, Complete }

/**
 * The ONE place the model directory is decided.
 *
 * IT WAS DECIDED IN FOUR PLACES AND THEY DISAGREED ON A PHONE. Three loaders
 * used the application-data folder and the mobile head used the app's own data
 * directory; on Android the first is a SUBDIRECTORY of the second. Nothing
 * failed — both existed, both were writable, both looked right in a log. What
 * happened instead is that a 523 MB chat model was downloaded twice onto a
 * phone with 890 MB of app data, and it was found by looking at the disk.
 */
object ModelPaths {

    /**
     * The platform's per-user data directory.
     *
     * Deliberately NOT a cache directory: a system is free to evict a cache
     * under pressure, and a half-evicted 400 MB bundle fails its hash on the
     * next launch with no explanation.
     */
    val root: String
        get() {
            System.getenv("CIRCLEAI_DATA_HOME")?.takeIf { it.isNotBlank() }?.let { return it }
            val os = System.getProperty("os.name").orEmpty().lowercase(Locale.ROOT)
            val home = System.getProperty("user.home").orEmpty()
            return when {
                os.contains("win") ->
                    System.getenv("LOCALAPPDATA")?.takeIf { it.isNotBlank() }
                        ?: File(home, "AppData/Local").path
                os.contains("mac") -> File(home, "Library/Application Support").path
                else ->
                    System.getenv("XDG_DATA_HOME")?.takeIf { it.isNotBlank() }
                        ?: File(home, ".local/share").path
            }
        }

    val default: String get() = File(File(root, "CircleAI"), "Models").path

    /**
     * The directory to use, created if it is not there.
     *
     * Blank means "the default" rather than the working directory: a relative
     * path here puts a 400 MB download wherever the process was started from.
     */
    fun resolve(requested: String?): String {
        val dir = if (requested.isNullOrBlank()) default else requested
        File(dir).mkdirs()
        return dir
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// How far this has actually been proven

/**
 * How far a component has been proven, which is NOT whether it compiles.
 *
 * The distinction is the point. Something marked [Reference] may be complete,
 * tested and green and still never have had a byte cross a wire. Recording it
 * is what stops a green test suite reading as a shipped feature.
 */
enum class VerificationLevel { Reference, WireProven, ProductionDeployed }

/**
 * Kotlin has annotations, so this stays an annotation as it is in C# — a caller
 * reads it off the class rather than having to be told separately.
 */
@Target(AnnotationTarget.CLASS)
@Retention(AnnotationRetention.RUNTIME)
annotation class CircleAIVerificationStatus(
    val status: VerificationLevel,
    val notes: String = ""
)

// ─────────────────────────────────────────────────────────────────────────────
// Diagnostics

/** Where a measurement goes. A host wires this to whatever it has; unset,
 *  nothing is recorded and nothing is allocated. */
interface CircleAIMetricSink {
    fun count(name: String, amount: Long, tags: Map<String, String>)
    fun record(name: String, milliseconds: Double, tags: Map<String, String>)
}

/**
 * The instrument names and the outcome vocabulary, in one place so two
 * components cannot report the same thing under two spellings.
 *
 * The C# is System.Diagnostics.Metrics, which an OpenTelemetry exporter picks
 * up with no code. The JVM equivalent would be a dependency, and this package
 * has none — so the SHAPE crosses behind a sink a host points anywhere.
 */
object CircleAIDiagnostics {

    const val ACTIVITY_SOURCE_NAME = "CircleAI"
    const val METER_NAME = "CircleAI"
    const val VERSION = "1.1.0"

    // The instrument names are the C# ones EXACTLY. A dashboard is built on
    // these strings, so renaming one silently splits a metric in two.
    const val OPERATIONS_TOTAL = "circleai.operations.total"
    const val OPERATION_DURATION_MS = "circleai.operation.duration"
    const val ANOMALY_SIGNALS_TOTAL = "circleai.anomaly.signals.total"
    const val INFERENCE_REQUESTS_TOTAL = "circleai.inference.requests.total"

    /** How an operation ended. A CLOSED vocabulary: "failed", "error" and "err"
     *  in three components make a chart nobody can read. */
    object Outcomes {
        const val SUCCESS = "success"
        const val CANCELLED = "cancelled"
        const val UNAVAILABLE = "unavailable"
        const val RATE_LIMITED = "rate_limited"
        const val INVALID = "invalid"
        const val ERROR = "error"

        val all = listOf(SUCCESS, CANCELLED, UNAVAILABLE, RATE_LIMITED, INVALID, ERROR)
    }

    private val sink = AtomicReference<CircleAIMetricSink?>(null)

    /** Null by default. Nothing is measured until a host says where to put it. */
    var metricSink: CircleAIMetricSink?
        get() = sink.get()
        set(value) = sink.set(value)

    fun count(name: String, amount: Long = 1, tags: Map<String, String> = emptyMap()) {
        sink.get()?.count(name, amount, tags)
    }

    fun record(name: String, milliseconds: Double, tags: Map<String, String> = emptyMap()) {
        sink.get()?.record(name, milliseconds, tags)
    }

    fun startOperation(component: String, operation: String): CircleAIOperation =
        CircleAIOperation(component, operation)
}

/**
 * One operation being measured.
 *
 * The outcome is recorded when [finish] is called, not when this is collected:
 * a span that ends on finalisation reports every abandoned operation as a
 * success, which is exactly backwards.
 */
class CircleAIOperation internal constructor(
    val component: String,
    val operation: String
) {
    private val startedAt = System.nanoTime()
    private var done = false

    val elapsedMs: Double get() = (System.nanoTime() - startedAt) / 1_000_000.0

    val isFinished: Boolean @Synchronized get() = done

    /** Idempotent: a caller that finishes in both a success path and a `finally`
     *  must not double-count. */
    @Synchronized
    fun finish(outcome: String = CircleAIDiagnostics.Outcomes.SUCCESS) {
        if (done) return
        done = true
        val tags = mapOf(
            "circleai.component" to component,
            "circleai.operation" to operation,
            "circleai.outcome" to outcome
        )
        CircleAIDiagnostics.record(CircleAIDiagnostics.OPERATION_DURATION_MS, elapsedMs, tags)
        CircleAIDiagnostics.count(CircleAIDiagnostics.OPERATIONS_TOTAL, 1, tags)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Where the RAM figure came from

/**
 * Real device memory, supplied by a platform head that can read it.
 *
 * Two numbers on purpose: [ramTotalBytes] is the device CLASS, [ramAvailableBytes]
 * is what is free right now. Collapsing them makes a busy 8 GB phone look like a
 * 2 GB one.
 */
data class PlatformMemory(
    val ramAvailableBytes: Long? = null,
    val storageFreeBytes: Long? = null,
    val ramTotalBytes: Long? = null
)

/** Where the RAM figure actually came from. */
enum class RamMeasurement {
    /** A caller stated it outright (tests, hosts that already know). */
    Explicit,

    /** Read from the device by a platform head. */
    PlatformMeasured,

    /** Nobody supplied one, so it was inferred. On mobile that is a guess. */
    Heuristic
}

/**
 * The platform memory hook and the honesty it buys.
 *
 * A PROBE THAT GUESSED WAS INDISTINGUISHABLE FROM ONE THAT MEASURED, and every
 * verdict downstream was stated with full confidence about a number that is the
 * JVM heap limit — a few hundred megabytes inside an Android sandbox. The device
 * reads as a wearable, every model comes back as not fitting, and nothing
 * anywhere says the input was invented.
 */
object DeviceMemory {

    private val hook = AtomicReference<(() -> PlatformMemory)?>(null)

    /**
     * Optional platform hook. A JVM cannot read a phone's real RAM: the runtime
     * reports its own heap limit and the sandboxed data partition denies a
     * free-space query. An Android head sets this once at startup. Left null on
     * desktop and server, where the heuristics are accurate.
     */
    var platformMemoryProbe: (() -> PlatformMemory)?
        get() = hook.get()
        set(value) = hook.set(value)

    /**
     * A plain-language warning when the RAM figure is a guess that looks wrong,
     * or null when there is nothing to say.
     *
     * Deliberately NARROW. The heuristic is fine on desktop and server, where it
     * returns GB-scale numbers, and warning there would be noise nobody reads.
     * It fires only on the actual signature of the bug: an inferred figure too
     * small for any real device.
     */
    fun measurementWarning(ramAvailableBytes: Long, source: RamMeasurement): String? {
        if (source != RamMeasurement.Heuristic) return null
        if (ramAvailableBytes >= 512L * 1024 * 1024) return null
        val mb = ramAvailableBytes / (1024.0 * 1024)
        return "this device's RAM was not measured — %.0f MB is the managed heap limit, ".format(mb) +
            "not the hardware. The platform head has not set " +
            "DeviceMemory.platformMemoryProbe, so every size decision here is based on a guess"
    }

    /**
     * The RAM figure and where it came from.
     *
     * The hook is asked ONLY when the caller did not state a figure — a test
     * that passes an explicit number must not have it overwritten by whatever
     * hardware happens to be running the test.
     */
    fun resolve(ramAvailableBytes: Long? = null): Pair<Long, RamMeasurement> {
        if (ramAvailableBytes != null) return ramAvailableBytes to RamMeasurement.Explicit

        hook.get()?.invoke()?.let { m ->
            (m.ramAvailableBytes ?: m.ramTotalBytes)?.let {
                return it to RamMeasurement.PlatformMeasured
            }
        }
        return Runtime.getRuntime().maxMemory() to RamMeasurement.Heuristic
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// A device context built only from what the runtime already knows

/**
 * Cross-platform device context using nothing but the JDK.
 *
 * Everything it cannot honestly answer is null rather than zero. A zero battery
 * level and an unknown battery level are different facts, and reporting 0% tells
 * the assistant the phone is about to die.
 */
class SystemInfoDeviceContext(
    val activeAppId: String? = null
) {
    @Volatile
    var lastActiveUtc: java.time.Instant = java.time.Instant.now()
        private set

    val locale: String get() = Locale.getDefault().toLanguageTag()
    val timeZoneId: String get() = java.util.TimeZone.getDefault().id
    val localTime: java.time.OffsetDateTime get() = java.time.OffsetDateTime.now()

    // Unavailable without platform APIs, and never guessed.
    val latitude: Double? = null
    val longitude: Double? = null
    val locationHint: String? = null
    val batteryLevel: Float? = null
    val isCharging: Boolean? = null
    val networkType: String? = null
    val cpuUsagePercent: Float? = null
    val availableMemoryBytes: Long? = null
    val thermalState: String? = null
    val storageFreeBytes: Long? = null

    fun recordInteraction() {
        lastActiveUtc = java.time.Instant.now()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The MMS voice sidecars

/**
 * The voices' `model.onnx.json` sidecars.
 *
 * WHY THEY ARE NOT DOWNLOADED. The registry pinned each sidecar as a remote
 * bundle file with a SHA-256, exactly like the 114 MB model beside it. Measured
 * 2026-08-23, 43 of 47 returned 404: they were generated once by a script that
 * was never committed and the bytes were lost, so the registry promised files
 * that no longer existed. Every one of those voices downloaded its model and
 * then failed on a 2 KB sidecar.
 *
 * The SHA in the registry still governs — these bytes go through the ordinary
 * verify-then-skip path, so a sidecar that does not match its pin fails exactly
 * the way a corrupt download would.
 */
object EmbeddedVoiceConfigs {

    /** The two files a voice can carry. Both are ours; neither is downloadable. */
    val companions = listOf("model.onnx.json", "language_ids.json")

    @Volatile
    private var overrideDirectory: String? = null

    @Volatile
    private var cached: Map<String, File>? = null

    /** Point at a directory of `<voice>/model.onnx.json` files. */
    var resourceDirectory: String?
        get() = overrideDirectory
        set(value) {
            overrideDirectory = value
            cached = null                      // the map is derived from it
        }

    val names: List<String> get() = map().keys.sorted()

    val voices: List<String>
        get() = map().keys.mapNotNull { it.substringBefore('/').takeIf(String::isNotEmpty) }
            .distinct().sorted()

    /**
     * The bytes for one bundle file, or null when it is not one of ours.
     *
     * Backslashes fold to forward slashes first: a bundle manifest written on
     * Windows names the same file a different way, and a miss here falls through
     * to downloading a file that does not exist.
     */
    fun bytes(bundleFileName: String?): ByteArray? {
        if (bundleFileName.isNullOrBlank()) return null
        val key = bundleFileName.replace('\\', '/')
        return map()[key]?.takeIf { it.isFile }?.readBytes()
    }

    private fun map(): Map<String, File> {
        cached?.let { return it }
        val built = build(overrideDirectory)
        cached = built
        return built
    }

    private fun build(override: String?): Map<String, File> {
        val roots = buildList {
            override?.let { add(File(it)) }
            add(File("VoiceConfigs"))
        }

        val out = LinkedHashMap<String, File>()
        for (root in roots) {
            if (!root.isDirectory) continue
            root.walkTopDown().filter { it.isFile && it.name in companions }.forEach { f ->
                // The voice id is the DIRECTORY the sidecar sits in, not a prefix
                // trimmed off the file name: both layouts exist upstream and
                // guessing from the file name keys the second one under nothing.
                val voice = f.parentFile?.name.orEmpty()
                if (voice.isNotEmpty() && voice != root.name) {
                    out.putIfAbsent("$voice/${f.name}", f)   // first root wins
                }
            }
        }
        return out
    }
}
