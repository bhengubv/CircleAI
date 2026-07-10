// Aether.kt
//
// Kotlin port of the CircleAI.Aether contract family (src/CircleAI.Aether/*.cs).
//
// The Aether module is the strictly one-way boundary between the Aether mesh
// protocol and BhenguAI / CircleAI:
//
//   Aether publishes telemetry  →  BhenguAI subscribes           (Contract 1)
//   BhenguAI reports presence    ←  IAetherContext               (Contract 2)
//   BhenguAI produces intelligence → apps + security layer       (Contract 3)
//   BhenguAI publishes directives → Aether policy engine         (Contract 4)
//   Bidirectional auth challenge  ↔  security gate               (Contract 5)
//
// Aether never calls into BhenguAI. Every enum ordinal / numeric value mirrors
// the C# declaration exactly for cross-port parity.
//
// C#→Kotlin conventions applied here:
//   record                → data class (computed members become val ... get())
//   DateTimeOffset        → java.time.Instant
//   TimeSpan              → java.time.Duration
//   Task<T>               → suspend fun
//   IAsyncEnumerable<T>   → kotlinx.coroutines.flow.Flow<T>
//   IDisposable (sub)     → AutoCloseable
//   System.Version        → AetherVersion (comparable value type, defined below)
//
// In addition to the contracts, this file ships working, deterministic
// in-memory implementations of the four named interfaces (IAetherContext,
// IAetherIntelligence, IAISecurityLayer, IAuthChallenge) — no stubs.

package com.bhengubv.circleai.aether

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.receiveAsFlow
import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.abs

// ═══════════════════════════════════════════════════════════════════════════
// AetherVersion — comparable version value type
//
// The C# contracts use System.Version. Kotlin/JVM has no built-in equivalent,
// so this small value type reproduces its comparison semantics: components are
// compared in order (major, minor, build, revision); an unspecified component
// compares as 0. A negative component is invalid (matches System.Version).
// ═══════════════════════════════════════════════════════════════════════════

/**
 * A four-component, dotted version number with the same ordering semantics as
 * .NET's `System.Version`. Unspecified trailing components default to 0.
 */
data class AetherVersion(
    val major: Int,
    val minor: Int = 0,
    val build: Int = 0,
    val revision: Int = 0,
) : Comparable<AetherVersion> {

    init {
        require(major >= 0 && minor >= 0 && build >= 0 && revision >= 0) {
            "Version components must be non-negative."
        }
    }

    override fun compareTo(other: AetherVersion): Int {
        major.compareTo(other.major).let { if (it != 0) return it }
        minor.compareTo(other.minor).let { if (it != 0) return it }
        build.compareTo(other.build).let { if (it != 0) return it }
        return revision.compareTo(other.revision)
    }

    override fun toString(): String = "$major.$minor.$build.$revision"

    companion object {
        /**
         * Parses a dotted version string ("1", "1.2", "1.2.3", "1.2.3.4").
         * Missing components default to 0. Throws on malformed input.
         */
        fun parse(text: String): AetherVersion {
            val parts = text.trim().split('.')
            require(parts.isNotEmpty() && parts.size <= 4) { "Invalid version string: '$text'." }
            val nums = parts.map { it.trim().toInt() }
            return AetherVersion(
                major = nums[0],
                minor = nums.getOrElse(1) { 0 },
                build = nums.getOrElse(2) { 0 },
                revision = nums.getOrElse(3) { 0 },
            )
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Contract 1 — Telemetry  (IAetherTelemetry.cs + Events/*.cs)
// ═══════════════════════════════════════════════════════════════════════════

// ── Node events ────────────────────────────────────────────────────────────

/** Kinds of node lifecycle transitions Aether can emit. */
enum class AetherNodeEventKind {
    Joined,
    Left,
    HealthChanged,
}

/**
 * Point-in-time health snapshot for a single mesh node.
 *
 * @property trustScore 0.0 (untrusted) to 1.0 (fully trusted). Maintained by
 *   the AI Security Layer when active; defaults to 1.0 for all nodes when the
 *   security layer is off.
 */
data class AetherNodeHealth(
    val trustScore: Double,
    val isReachable: Boolean,
    val latency: Duration,
    val hopCount: Int,
) {
    /** Returns true when [trustScore] is within the valid 0–1 range. */
    val isValid: Boolean get() = trustScore in 0.0..1.0
}

/**
 * Emitted by Aether whenever a node joins, leaves, or changes health.
 * Consumed by [IAetherTelemetry] subscribers — BhenguAI never writes back into
 * Aether directly.
 */
data class AetherNodeEvent(
    val nodeId: String,
    val kind: AetherNodeEventKind,
    val health: AetherNodeHealth,
    val occurredAt: Instant,
) {
    /** Convenience: true when this is a departure event. */
    val isExit: Boolean get() = kind == AetherNodeEventKind.Left
}

// ── Transport events ───────────────────────────────────────────────────────

/** Physical or logical transport medium Aether is using. */
enum class AetherTransportKind {
    WiFi,
    Bluetooth,
    LoRa,
    NFC,
    Cellular,
    Ethernet,
    Unknown,
}

/** Kinds of transport-layer observations Aether can emit. */
enum class AetherTransportEventKind {
    Selected,
    Changed,
    LatencyMeasured,
    PacketLoss,
}

/**
 * Emitted when Aether selects, changes, or measures quality on a transport
 * channel. The AI layer uses this to correlate transport behaviour with threat
 * patterns.
 */
data class AetherTransportEvent(
    val nodeId: String,
    val kind: AetherTransportEventKind,
    val transport: AetherTransportKind,
    val latency: Duration?,
    val packetLossRate: Double?,
    val occurredAt: Instant,
) {
    /**
     * Returns true when [packetLossRate] is set and exceeds the given threshold
     * (0.0–1.0).
     */
    fun exceedsLoss(threshold: Double): Boolean =
        packetLossRate != null && packetLossRate > threshold
}

// ── Route events ───────────────────────────────────────────────────────────

/** Kinds of routing changes Aether can emit. */
enum class AetherRouteEventKind {
    Discovered,
    Changed,
    Failed,
}

/**
 * Emitted when Aether discovers, updates, or loses a route between two nodes.
 * The [path] list describes the sequence of node IDs traversed.
 */
data class AetherRouteEvent(
    val sourceNodeId: String,
    val destinationNodeId: String,
    val path: List<String>,
    val kind: AetherRouteEventKind,
    val failureReason: String?,
    val occurredAt: Instant,
) {
    /** Number of hops in this route, including source and destination. */
    val hopCount: Int get() = path.size

    /** True when this event represents a routing failure. */
    val isFailed: Boolean get() = kind == AetherRouteEventKind.Failed
}

// ── Security events ────────────────────────────────────────────────────────

/**
 * Categories of security-relevant observations Aether can detect at the
 * protocol layer, without requiring AI. The AI Security Layer consumes these
 * events to produce threat assessments and directives.
 */
enum class AetherSecurityEventKind {
    /** A node attempted to authenticate into the mesh. */
    NodeAuthAttempt,

    /** Traffic was observed deviating from expected routing paths. */
    RoutingAnomaly,

    /** A node's behaviour deviated from its established baseline. */
    NodeBehaviourChange,

    /** A key exchange or certificate validation event occurred. */
    EncryptionEvent,

    /** Active attack signature detected (e.g. replay, spoofing). */
    IntrusionSignal,

    /** A node requested capabilities beyond its granted level. */
    PrivilegeAttempt,
}

/**
 * Protocol-level threat severity as assessed by Aether itself, before any AI
 * reasoning is applied. Ordinal order (None..Critical) is the wire contract.
 */
enum class AetherThreatLevel {
    None,
    Low,
    Medium,
    High,
    Critical,
}

/**
 * Emitted by Aether when a security-relevant event occurs at the protocol
 * layer. This is the primary feed for the AI Security Layer. Aether never calls
 * into BhenguAI — it only emits; BhenguAI subscribes.
 */
data class AetherSecurityEvent(
    val nodeId: String,
    val kind: AetherSecurityEventKind,
    val threatLevel: AetherThreatLevel,
    val description: String,
    val metadata: Map<String, String>,
    val occurredAt: Instant,
) {
    /** True when [threatLevel] is High or Critical. */
    val isHighSeverity: Boolean
        get() = threatLevel == AetherThreatLevel.High || threatLevel == AetherThreatLevel.Critical
}

// ── Network events ─────────────────────────────────────────────────────────

/** Mesh-wide topology and congestion observations. */
enum class AetherNetworkEventKind {
    TopologyChanged,
    CongestionDetected,
    PartitionDetected,
}

/**
 * Emitted when the mesh topology or overall network health changes. Provides
 * aggregate context that the AI layer uses alongside individual node events.
 */
data class AetherNetworkEvent(
    val kind: AetherNetworkEventKind,
    val nodeCount: Int,
    val activeRouteCount: Int,
    val congestionLevel: Double,
    val occurredAt: Instant,
) {
    /**
     * True when [congestionLevel] exceeds 0.75 — a useful default alert
     * threshold. Callers may apply their own thresholds.
     */
    val isHighCongestion: Boolean get() = congestionLevel > 0.75
}

// ── Telemetry observer + surface ───────────────────────────────────────────

/**
 * Receives events emitted by Aether. Implement this to react to mesh activity —
 * nodes, transports, routes, security signals, and topology.
 *
 * Default no-op bodies let observers override only the events they care about.
 */
interface IAetherTelemetryObserver {
    fun onNodeEvent(e: AetherNodeEvent) {}
    fun onTransportEvent(e: AetherTransportEvent) {}
    fun onRouteEvent(e: AetherRouteEvent) {}
    fun onSecurityEvent(e: AetherSecurityEvent) {}
    fun onNetworkEvent(e: AetherNetworkEvent) {}
}

/**
 * The outward-facing telemetry surface of Aether. The AI Security Layer and any
 * other BhenguAI component subscribes here. Aether owns this interface and
 * publishes; consumers subscribe and close the returned handle.
 */
interface IAetherTelemetry {
    /**
     * Subscribe to all Aether telemetry events. Close the returned handle to
     * unsubscribe.
     */
    fun subscribe(observer: IAetherTelemetryObserver): AutoCloseable
}

/**
 * No-op telemetry — useful for unit tests and environments where Aether is
 * absent. [subscribe] returns a no-op handle; no events are emitted.
 */
object NullAetherTelemetry : IAetherTelemetry {
    override fun subscribe(observer: IAetherTelemetryObserver): AutoCloseable =
        AutoCloseable { }
}

/**
 * In-memory telemetry publisher. Aether platform adapters (or tests) construct
 * one, register it with consumers, and call the `emit*` methods to fan events
 * out to every subscriber. Thread-safe; a snapshot of observers is taken under
 * the lock and callbacks fire outside it so a consumer that (un)subscribes from
 * within its own callback cannot self-deadlock.
 */
class InMemoryAetherTelemetry : IAetherTelemetry {

    private val lock = Any()
    private val observers = ArrayList<IAetherTelemetryObserver>()

    override fun subscribe(observer: IAetherTelemetryObserver): AutoCloseable {
        synchronized(lock) { observers.add(observer) }
        return Subscription(observer)
    }

    /** Number of currently attached observers. Useful in tests. */
    val observerCount: Int get() = synchronized(lock) { observers.size }

    fun emitNode(e: AetherNodeEvent) = fanOut { it.onNodeEvent(e) }
    fun emitTransport(e: AetherTransportEvent) = fanOut { it.onTransportEvent(e) }
    fun emitRoute(e: AetherRouteEvent) = fanOut { it.onRouteEvent(e) }
    fun emitSecurity(e: AetherSecurityEvent) = fanOut { it.onSecurityEvent(e) }
    fun emitNetwork(e: AetherNetworkEvent) = fanOut { it.onNetworkEvent(e) }

    private inline fun fanOut(action: (IAetherTelemetryObserver) -> Unit) {
        val snapshot = synchronized(lock) { observers.toList() }
        for (o in snapshot) action(o)
    }

    private inner class Subscription(private val observer: IAetherTelemetryObserver) : AutoCloseable {
        private val disposed = AtomicBoolean(false)
        override fun close() {
            if (disposed.compareAndSet(false, true)) {
                synchronized(lock) { observers.remove(observer) }
            }
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Contract 2 — Presence and Capability  (IAetherContext.cs)
// ═══════════════════════════════════════════════════════════════════════════

/** Indicates where Aether is installed and who manages it. */
enum class AetherInstallLevel {
    /** Aether is not present on this device. */
    None,

    /**
     * Aether was installed at app level — either bundled with the app or
     * downloaded at first launch. Updated independently by the app.
     */
    App,

    /**
     * Aether is a system service managed by the OS. Always present on TGN
     * devices. Updated with OS updates. Requires biometric + device admin auth
     * to toggle on or off.
     */
    OS,
}

/**
 * Reports the presence, version, and capability of the Aether runtime on this
 * device. Inject via DI; the platform adapter provides the concrete
 * implementation.
 */
interface IAetherContext {
    /** Where Aether is installed, if at all. */
    val installLevel: AetherInstallLevel

    /** True when Aether is installed and enabled. */
    val isAvailable: Boolean

    /** The installed Aether runtime version, or null when Aether is absent. */
    val runtimeVersion: AetherVersion?

    /**
     * The minimum Aether version declared as required by the consuming app. Set
     * this via configuration; the bootstrap checks it on startup.
     */
    val minimumRequired: AetherVersion?

    /**
     * True when [runtimeVersion] satisfies [minimumRequired]. Always true when
     * [minimumRequired] is null.
     */
    val isSufficient: Boolean

    /**
     * True when the install level is [AetherInstallLevel.OS]. OS-managed
     * instances require biometric + device admin auth before they can be
     * toggled.
     */
    val requiresAuth: Boolean

    /**
     * True when Aether is installed and currently enabled. An OS-managed
     * instance that has been toggled off returns false here.
     */
    val isEnabled: Boolean
}

/**
 * Immutable in-memory [IAetherContext]. Constructs the full contract from a
 * declared install level, runtime version, minimum requirement, and enabled
 * flag, computing every derived property exactly as the C# contract specifies.
 *
 * The [isAvailable] / [isEnabled] semantics honour the contract: when the
 * install level is [AetherInstallLevel.None], Aether is neither available nor
 * enabled regardless of the [enabled] flag.
 */
class InMemoryAetherContext(
    override val installLevel: AetherInstallLevel,
    override val runtimeVersion: AetherVersion? = null,
    override val minimumRequired: AetherVersion? = null,
    private val enabled: Boolean = true,
) : IAetherContext {

    override val isEnabled: Boolean
        get() = installLevel != AetherInstallLevel.None && enabled

    override val isAvailable: Boolean get() = isEnabled

    override val isSufficient: Boolean
        get() {
            val min = minimumRequired ?: return true
            val rt = runtimeVersion ?: return false
            return rt >= min
        }

    override val requiresAuth: Boolean get() = installLevel == AetherInstallLevel.OS

    companion object {
        /** A context reporting that Aether is absent. */
        fun absent(): InMemoryAetherContext = InMemoryAetherContext(AetherInstallLevel.None)
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Contract 3 — Intelligence Output  (IAetherIntelligence.cs)
// ═══════════════════════════════════════════════════════════════════════════

/** Aggregate health of the mesh as assessed by BhenguAI. */
data class NetworkHealthReport(
    val overallScore: Double,
    val trustedNodeCount: Int,
    val suspiciousNodeCount: Int,
    val summary: String,
    val generatedAt: Instant,
) {
    /** True when [overallScore] is within the valid 0–1 range. */
    val isValid: Boolean get() = overallScore in 0.0..1.0
}

/** BhenguAI's assessment of the threat posed by a specific node. */
data class ThreatAssessment(
    val nodeId: String,
    val threatConfidence: Double,
    val level: AetherThreatLevel,
    val indicators: List<String>,
    val assessedAt: Instant,
) {
    /** True when [threatConfidence] is within the valid 0–1 range. */
    val isValid: Boolean get() = threatConfidence in 0.0..1.0
}

/**
 * BhenguAI's recommendation for routing to a destination node, taking trust
 * scores and current threat assessments into account.
 */
data class RoutingAdvice(
    val destinationNodeId: String,
    val recommendedPath: List<String>,
    val avoidNodes: List<String>,
    val confidence: Double,
    val reasoning: String,
    val generatedAt: Instant,
)

/** Emitted when BhenguAI revises the trust score for a node. */
data class TrustScoreUpdate(
    val nodeId: String,
    val previousScore: Double,
    val currentScore: Double,
    val reason: String,
    val updatedAt: Instant,
) {
    /** True when the score moved in either direction. */
    val hasChanged: Boolean get() = abs(currentScore - previousScore) > 0.001

    /** True when the score decreased. */
    val isDegraded: Boolean get() = currentScore < previousScore
}

/**
 * The intelligence output surface produced by BhenguAI from Aether telemetry.
 * Consumed by apps and the Security Layer; never by Aether.
 */
interface IAetherIntelligence {
    /** Returns an aggregate health report for the current mesh state. */
    suspend fun getNetworkHealth(): NetworkHealthReport

    /**
     * Assesses the current threat level of a specific node. Returns a
     * zero-confidence assessment when the node is unknown.
     */
    suspend fun assessThreat(nodeId: String): ThreatAssessment

    /**
     * Returns a routing recommendation for reaching the given destination,
     * factoring out nodes with low trust scores.
     */
    suspend fun getRoutingAdvice(destinationNodeId: String): RoutingAdvice

    /**
     * Streams trust score updates as BhenguAI observes new telemetry. Useful
     * for live dashboards and security monitoring UIs.
     */
    fun streamTrustScores(): Flow<TrustScoreUpdate>
}

// ═══════════════════════════════════════════════════════════════════════════
// Contract 4 — Security Layer  (IAISecurityLayer.cs)
// ═══════════════════════════════════════════════════════════════════════════

/** The action BhenguAI is recommending to Aether's policy engine. */
enum class SecurityDirectiveKind {
    /** Adjust the recorded trust score for a node. */
    UpdateNodeTrust,

    /** Exclude the node from routing decisions (soft block). */
    AvoidNode,

    /** Hard block — no traffic to or from the node until released. */
    QuarantineNode,

    /** Lift an AvoidNode or QuarantineNode directive. */
    ReleaseNode,

    /** Request that the user re-authenticates before a sensitive operation. */
    RequestReauth,

    /** Increase telemetry verbosity for the target node. */
    ElevateMonitoring,
}

/**
 * An instruction published by the AI Security Layer to Aether's policy engine.
 * Aether is never required to honour a directive — adoption is a policy decision
 * for each deployment.
 */
data class SecurityDirective(
    val kind: SecurityDirectiveKind,
    val targetNodeId: String?,
    val trustScoreOverride: Double?,
    val threatLevel: AetherThreatLevel,
    val reason: String,
    val duration: Duration?,
    val issuedAt: Instant,
) {
    /** True when the directive targets a specific node. */
    val hasTarget: Boolean get() = !targetNodeId.isNullOrBlank()

    /** True when [duration] is null — the directive has no automatic expiry. */
    val isPermanent: Boolean get() = duration == null
}

/** Point-in-time summary of the AI Security Layer's current posture. */
data class SecurityPosture(
    val overallThreatLevel: AetherThreatLevel,
    val quarantinedNodeCount: Int,
    val monitoredNodeCount: Int,
    val isActive: Boolean,
    val assessedAt: Instant,
)

/**
 * Receives security directives from the AI Security Layer. Implement this on
 * Aether's policy engine to participate in AI-guided security decisions.
 */
fun interface ISecurityDirectiveConsumer {
    /**
     * Called each time BhenguAI issues a security directive. Implementations
     * decide whether and how to honour it.
     */
    fun onDirective(directive: SecurityDirective)
}

/**
 * The AI Security Layer contract. BhenguAI implements this by subscribing to
 * [IAetherTelemetry] and producing [SecurityDirective] outputs consumed by
 * Aether's policy engine via [ISecurityDirectiveConsumer].
 */
interface IAISecurityLayer {
    /**
     * Wire the security layer to an Aether telemetry feed and begin processing
     * events.
     */
    suspend fun start(telemetry: IAetherTelemetry)

    /** Stop processing and release all telemetry subscriptions. */
    suspend fun stop()

    /**
     * Subscribe a policy engine to receive security directives. Close the
     * returned handle to unsubscribe.
     */
    fun subscribeToDirectives(consumer: ISecurityDirectiveConsumer): AutoCloseable

    /** Returns the current security posture snapshot. */
    suspend fun getPosture(): SecurityPosture
}

// ═══════════════════════════════════════════════════════════════════════════
// Contract 5 — Auth Challenge  (IAuthChallenge.cs)
// ═══════════════════════════════════════════════════════════════════════════

/** Why an auth challenge is being issued. */
enum class AuthChallengeReason {
    /** The user is enabling or disabling the OS-level Aether service. */
    OsLevelToggle,

    /**
     * The AI Security Layer detected anomaly scores above the configured
     * threshold and requires the user to confirm their identity.
     */
    ThreatThresholdReached,

    /** The operation being attempted requires elevated auth. */
    PrivilegedOperation,

    /** Scheduled trust renewal — periodic re-validation. */
    PeriodicRevalidation,

    /** Explicitly triggered by the developer or admin. */
    ManualRequest,
}

/**
 * The authentication method used or required. Methods are ordered by strength;
 * higher numeric values are stronger. The numeric [strength] is the stable wire
 * value — do NOT change it.
 */
enum class AuthMethod(val strength: Int) {
    /** Fingerprint, face, or iris recognition. */
    Biometric(1),

    /** Device administrator credential (PIN, password, pattern). */
    DeviceAdmin(2),

    /** Biometric AND device admin — the minimum for any OS-level operation. */
    BiometricAndDeviceAdmin(3),

    /** Developer-defined method layered on top of BiometricAndDeviceAdmin. */
    Custom(4),
}

/** The outcome of an auth challenge. */
data class AuthChallengeResult(
    val succeeded: Boolean,
    val methodUsed: AuthMethod,
    val failureReason: String?,
    val completedAt: Instant,
) {
    companion object {
        /** Convenience: a successful result with no failure reason. */
        fun success(method: AuthMethod): AuthChallengeResult =
            AuthChallengeResult(true, method, null, Instant.now())

        /** Convenience: a failed result with an explanatory reason. */
        fun failure(method: AuthMethod, reason: String): AuthChallengeResult =
            AuthChallengeResult(false, method, reason, Instant.now())
    }
}

/**
 * Issues and resolves authentication challenges for security-sensitive
 * operations. Platform adapters implement this using native biometric and
 * device admin APIs.
 */
interface IAuthChallenge {
    /**
     * Presents an auth challenge to the user for the given reason. The platform
     * adapter enforces the minimum method requirement.
     *
     * @param reason Why auth is being requested.
     * @param minimumMethod The weakest method acceptable. Defaults to
     *   [AuthMethod.BiometricAndDeviceAdmin] when null.
     * @param prompt Human-readable message shown to the user.
     */
    suspend fun challenge(
        reason: AuthChallengeReason,
        minimumMethod: AuthMethod?,
        prompt: String,
    ): AuthChallengeResult

    /**
     * Presents the OS-level toggle challenge. Always requires
     * [AuthMethod.BiometricAndDeviceAdmin] at minimum.
     *
     * @param enable True to enable the service, false to disable.
     */
    suspend fun requestOsToggle(enable: Boolean): AuthChallengeResult
}

/**
 * Deterministic in-memory [IAuthChallenge] for tests, simulation, and headless
 * environments. It never touches real biometric hardware; instead a supplied
 * [authenticator] decides whether a presented challenge succeeds and with which
 * method.
 *
 * Contract enforcement matches the C# spec:
 *   • [challenge] applies [AuthMethod.BiometricAndDeviceAdmin] as the floor when
 *     `minimumMethod` is null, and never lets the caller drop below it.
 *   • The method the authenticator returns must be at least as strong as the
 *     effective minimum, otherwise the result is a failure.
 *   • [requestOsToggle] always enforces the OS-level floor
 *     ([AuthMethod.BiometricAndDeviceAdmin]).
 *
 * The default [authenticator] approves every challenge with exactly the required
 * minimum method — the simplest passing implementation. Inject a stricter one to
 * simulate failures, step-up, or user cancellation.
 */
class InMemoryAuthChallenge(
    private val authenticator: (AuthChallengeReason, AuthMethod, String) -> AuthChallengeResult =
        { _, required, _ -> AuthChallengeResult.success(required) },
) : IAuthChallenge {

    override suspend fun challenge(
        reason: AuthChallengeReason,
        minimumMethod: AuthMethod?,
        prompt: String,
    ): AuthChallengeResult {
        // Floor: never below BiometricAndDeviceAdmin — the OS-operation minimum.
        val floor = AuthMethod.BiometricAndDeviceAdmin
        val required =
            if (minimumMethod == null || minimumMethod.strength < floor.strength) floor
            else minimumMethod

        val result = authenticator(reason, required, prompt)

        // A successful result must have used a method at least as strong as the
        // required minimum, otherwise the challenge did not actually clear the bar.
        if (result.succeeded && result.methodUsed.strength < required.strength) {
            return AuthChallengeResult.failure(
                result.methodUsed,
                "Method ${result.methodUsed} is weaker than required minimum $required.",
            )
        }
        return result
    }

    override suspend fun requestOsToggle(enable: Boolean): AuthChallengeResult {
        val verb = if (enable) "enable" else "disable"
        return challenge(
            AuthChallengeReason.OsLevelToggle,
            AuthMethod.BiometricAndDeviceAdmin,
            "Authenticate to $verb the Aether service.",
        )
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// In-memory intelligence + security layer implementations
//
// The C# ships no reference in-memory impl of IAetherIntelligence / the AI
// security layer — platform / server adapters provide them (see
// CircleAI.Security.AetherNet.AetherSecurityBridge, ported separately). These
// two classes are self-contained, deterministic implementations that reason
// over Aether telemetry directly, so the Aether module's named interfaces have
// a working, no-stub backing out of the box.
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Per-node trust ledger shared by [InMemoryAISecurityLayer] and
 * [InMemoryAetherIntelligence]. Trust starts at 1.0 for every observed node and
 * degrades on security events by a fixed weight per [AetherThreatLevel]. Every
 * change is emitted on [updates] as a [TrustScoreUpdate].
 *
 * Thread-safe via a per-instance monitor. Scores are always clamped to [0,1].
 */
class AetherTrustLedger {

    private val lock = Any()
    private val scores = ConcurrentHashMap<String, Double>()
    private val indicators = ConcurrentHashMap<String, MutableList<String>>()

    // Unbounded channel so publish never blocks and writes are RETAINED UNTIL
    // READ — updates emitted before the first collector attaches are buffered
    // and delivered on first collection (matches NodeTrustRegistry's contract
    // and the wave's "no lost writes" guidance). trySend does not acquire a lock
    // the collector's cleanup path re-enters, so it is safe to call under [lock].
    private val channel = Channel<TrustScoreUpdate>(Channel.UNLIMITED)

    /**
     * Live stream of every trust score change; never completes during normal
     * operation. Single-consumer (channel-backed), mirroring
     * [com.bhengubv.circleai.security.NodeTrustRegistry.trustScoreUpdates].
     */
    val updates: Flow<TrustScoreUpdate> get() = channel.receiveAsFlow()

    /** Snapshot of every node id currently tracked. */
    val nodeIds: List<String> get() = scores.keys.toList()

    /** Ensures a node is tracked; returns its current score (1.0 if new). */
    fun ensure(nodeId: String): Double =
        scores.getOrPut(nodeId) { 1.0 }

    /** Current trust score for [nodeId]; 1.0 when unknown (never degraded). */
    fun trustOf(nodeId: String): Double = scores[nodeId] ?: 1.0

    /** True when [nodeId] has ever been observed. */
    fun isKnown(nodeId: String): Boolean = scores.containsKey(nodeId)

    /** Accumulated indicator tags for [nodeId]. */
    fun indicatorsOf(nodeId: String): List<String> =
        indicators[nodeId]?.toList() ?: emptyList()

    /**
     * Applies a security event: degrades the node's trust by the level weight,
     * records any indicator tag, and returns the (previous, current) pair. The
     * score is clamped to [0,1]. A change notification is emitted.
     */
    fun applyEvent(e: AetherSecurityEvent): Pair<Double, Double> {
        val weight = degradationWeight(e.threatLevel)
        val (previous, current) = synchronized(lock) {
            val prev = scores[e.nodeId] ?: 1.0
            val next = (prev - weight).coerceIn(0.0, 1.0)
            scores[e.nodeId] = next
            indicators.getOrPut(e.nodeId) { ArrayList() }.add(indicatorTag(e.kind))
            prev to next
        }
        if (abs(current - previous) > 0.0) {
            channel.trySend(
                TrustScoreUpdate(e.nodeId, previous, current, e.description, Instant.now()),
            )
        }
        return previous to current
    }

    /** Directly overrides a node's trust score (honours UpdateNodeTrust). */
    fun setTrust(nodeId: String, score: Double, reason: String) {
        val clamped = score.coerceIn(0.0, 1.0)
        val previous = synchronized(lock) {
            val prev = scores[nodeId] ?: 1.0
            scores[nodeId] = clamped
            prev
        }
        if (abs(clamped - previous) > 0.0) {
            channel.trySend(TrustScoreUpdate(nodeId, previous, clamped, reason, Instant.now()))
        }
    }

    private fun degradationWeight(level: AetherThreatLevel): Double = when (level) {
        AetherThreatLevel.None -> 0.0
        AetherThreatLevel.Low -> 0.10
        AetherThreatLevel.Medium -> 0.25
        AetherThreatLevel.High -> 0.50
        AetherThreatLevel.Critical -> 1.0
    }

    private fun indicatorTag(kind: AetherSecurityEventKind): String = when (kind) {
        AetherSecurityEventKind.NodeAuthAttempt -> "auth-attempt"
        AetherSecurityEventKind.RoutingAnomaly -> "routing-anomaly"
        AetherSecurityEventKind.NodeBehaviourChange -> "behaviour-change"
        AetherSecurityEventKind.EncryptionEvent -> "encryption-event"
        AetherSecurityEventKind.IntrusionSignal -> "intrusion-signal"
        AetherSecurityEventKind.PrivilegeAttempt -> "privilege-attempt"
    }

    companion object {
        /** Maps a trust score to a threat level using the standard bands. */
        fun scoreToThreatLevel(score: Double): AetherThreatLevel = when {
            score <= 0.25 -> AetherThreatLevel.Critical
            score <= 0.50 -> AetherThreatLevel.High
            score <= 0.75 -> AetherThreatLevel.Medium
            score <= 0.90 -> AetherThreatLevel.Low
            else -> AetherThreatLevel.None
        }
    }
}

/**
 * Deterministic in-memory [IAetherIntelligence] over an [AetherTrustLedger].
 * Produces network-health, per-node threat, and routing outputs from the ledger
 * state, and relays the ledger's trust-score change stream.
 */
class InMemoryAetherIntelligence(
    private val ledger: AetherTrustLedger = AetherTrustLedger(),
) : IAetherIntelligence {

    override suspend fun getNetworkHealth(): NetworkHealthReport {
        val ids = ledger.nodeIds
        if (ids.isEmpty()) {
            return NetworkHealthReport(1.0, 0, 0, "No nodes observed.", Instant.now())
        }
        val scores = ids.map { ledger.trustOf(it) }
        val overall = scores.average()
        val trusted = scores.count { it > 0.50 }
        val suspicious = scores.count { it <= 0.75 }
        val summary = when {
            overall > 0.90 -> "Network health is excellent."
            overall > 0.75 -> "Network health is good; minor anomalies detected."
            overall > 0.50 -> "Network health is degraded; elevated monitoring active."
            overall > 0.25 -> "Network health is poor; routing around compromised nodes."
            else -> "Network health is critical; quarantine directives in effect."
        }
        return NetworkHealthReport(overall, trusted, suspicious, summary, Instant.now())
    }

    override suspend fun assessThreat(nodeId: String): ThreatAssessment {
        if (!ledger.isKnown(nodeId)) {
            // Unknown node → zero-confidence assessment, per contract.
            return ThreatAssessment(nodeId, 0.0, AetherThreatLevel.None, emptyList(), Instant.now())
        }
        val score = ledger.trustOf(nodeId)
        val deficit = 1.0 - score
        val indicators = ledger.indicatorsOf(nodeId).distinct()
        val confidence = minOf(1.0, deficit + indicators.size * 0.1)
        return ThreatAssessment(
            nodeId,
            confidence,
            AetherTrustLedger.scoreToThreatLevel(score),
            indicators,
            Instant.now(),
        )
    }

    override suspend fun getRoutingAdvice(destinationNodeId: String): RoutingAdvice {
        val avoid = ledger.nodeIds.filter { ledger.trustOf(it) <= 0.50 }
        val destScore = ledger.trustOf(destinationNodeId)
        val recommended = if (destScore > 0.50) listOf(destinationNodeId) else emptyList()
        val reasoning = when {
            destScore > 0.75 -> "Direct path to $destinationNodeId is trusted."
            destScore > 0.50 -> "Destination $destinationNodeId is under monitoring; routing with caution."
            destScore > 0.25 -> "Destination $destinationNodeId has degraded trust; avoid recommended."
            else -> "Destination $destinationNodeId is quarantined; no safe path available."
        }
        return RoutingAdvice(destinationNodeId, recommended, avoid, destScore, reasoning, Instant.now())
    }

    override fun streamTrustScores(): Flow<TrustScoreUpdate> = ledger.updates
}

/**
 * Deterministic in-memory [IAISecurityLayer]. On [start] it subscribes to the
 * supplied [IAetherTelemetry], and for every [AetherSecurityEvent] it degrades
 * the node's trust in the shared [AetherTrustLedger] and issues at most one
 * [SecurityDirective] per event (most-severe threshold crossing wins). Directive
 * fan-out is snapshot-outside-the-lock so a consumer that (un)subscribes from
 * its own callback cannot self-deadlock.
 *
 * Thresholds (trust score crossing, from severe to mild):
 *   <= 0.25  → QuarantineNode  (Critical)
 *   <= 0.50  → AvoidNode       (High)
 *   <= 0.75  → ElevateMonitoring (Medium)
 */
class InMemoryAISecurityLayer(
    private val ledger: AetherTrustLedger = AetherTrustLedger(),
) : IAISecurityLayer {

    private val lock = Any()
    private val consumers = ArrayList<ISecurityDirectiveConsumer>()
    private var subscription: AutoCloseable? = null

    @Volatile
    private var active: Boolean = false

    /** Exposes the backing ledger so an [InMemoryAetherIntelligence] can share it. */
    val trustLedger: AetherTrustLedger get() = ledger

    override suspend fun start(telemetry: IAetherTelemetry) {
        synchronized(lock) {
            if (active) return
            active = true
            subscription = telemetry.subscribe(Observer())
        }
    }

    override suspend fun stop() {
        val sub = synchronized(lock) {
            active = false
            val s = subscription
            subscription = null
            s
        }
        sub?.close()
    }

    override fun subscribeToDirectives(consumer: ISecurityDirectiveConsumer): AutoCloseable {
        synchronized(lock) { consumers.add(consumer) }
        return Subscription(consumer)
    }

    override suspend fun getPosture(): SecurityPosture {
        val ids = ledger.nodeIds
        val quarantined = ids.count { ledger.trustOf(it) <= 0.25 }
        val monitored = ids.count {
            val s = ledger.trustOf(it)
            s <= 0.75 && s > 0.25
        }
        val worst = if (ids.isEmpty()) 1.0 else ids.minOf { ledger.trustOf(it) }
        return SecurityPosture(
            AetherTrustLedger.scoreToThreatLevel(worst),
            quarantined,
            monitored,
            active,
            Instant.now(),
        )
    }

    // ── Directive fan-out ──────────────────────────────────────────────────

    private fun publish(directive: SecurityDirective) {
        val snapshot = synchronized(lock) { consumers.toList() }
        for (c in snapshot) c.onDirective(directive)
    }

    private fun onSecurity(e: AetherSecurityEvent) {
        val (previous, current) = ledger.applyEvent(e)
        if (previous == current) return // AetherThreatLevel.None — no impact

        val directive: SecurityDirective? = when {
            previous > 0.25 && current <= 0.25 -> directiveFor(
                SecurityDirectiveKind.QuarantineNode, e, current, AetherThreatLevel.Critical,
            )
            previous > 0.50 && current <= 0.50 -> directiveFor(
                SecurityDirectiveKind.AvoidNode, e, current, AetherThreatLevel.High,
            )
            previous > 0.75 && current <= 0.75 -> directiveFor(
                SecurityDirectiveKind.ElevateMonitoring, e, current, AetherThreatLevel.Medium,
            )
            else -> null
        }
        directive?.let { publish(it) }
    }

    private fun directiveFor(
        kind: SecurityDirectiveKind,
        e: AetherSecurityEvent,
        trust: Double,
        level: AetherThreatLevel,
    ): SecurityDirective = SecurityDirective(
        kind = kind,
        targetNodeId = e.nodeId,
        trustScoreOverride = trust,
        threatLevel = level,
        reason = e.description,
        duration = null,
        issuedAt = Instant.now(),
    )

    private inner class Observer : IAetherTelemetryObserver {
        override fun onSecurityEvent(e: AetherSecurityEvent) = onSecurity(e)
    }

    private inner class Subscription(private val consumer: ISecurityDirectiveConsumer) : AutoCloseable {
        private val disposed = AtomicBoolean(false)
        override fun close() {
            if (disposed.compareAndSet(false, true)) {
                synchronized(lock) { consumers.remove(consumer) }
            }
        }
    }
}
