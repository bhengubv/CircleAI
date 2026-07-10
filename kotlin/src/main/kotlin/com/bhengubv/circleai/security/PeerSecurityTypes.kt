// PeerSecurityTypes.kt
//
// Transport-agnostic security primitives for CircleAI.Security.
// Kotlin port of src/CircleAI.Security/PeerSecurityTypes.cs.
//
// These types are deliberately free of any transport dependency (Aether, WiFi,
// BLE, NearLink, HTTP, etc.). Every transport adapter translates its own event
// vocabulary into these types before feeding the security layer.
//
// Type map:
//   PeerSecurityEventKind  — what happened (transport-neutral event category)
//   PeerThreatLevel        — how severe (None -> Critical)
//   PeerSecurityEvent      — one security incident from any transport
//   PeerDirectiveKind      — what the security layer recommends
//   PeerDirective          — a directive issued to all IPeerDirectiveConsumer subscribers
//   PeerTrustScoreUpdate   — one change notification emitted by NodeTrustRegistry
//   PeerSecurityPosture    — aggregate snapshot of security state
//   PeerNetworkHealthReport— aggregate health across all observed peers
//   PeerThreatAssessment   — per-node threat confidence + indicators
//   PeerRoutingAdvice      — trust-aware path recommendation
//
// Interfaces:
//   IPeerDirectiveConsumer — receives PeerDirective instances from any security layer
//   IPeerSecurityLayer     — lifecycle + query surface for the transport-agnostic layer
//   IPeerIntelligence      — read-only intelligence queries (health, threat, routing)
//   IPeerSecurityEventFeed — implemented by transport adapters to register an event source
//
// CRITICAL: PeerThreatLevel values (0..4) are part of the wire/storage contract.
// Do NOT reorder or renumber. PeerSecurityEventKind / PeerDirectiveKind ordinals
// likewise mirror the C# declaration order for cross-port parity.

package com.bhengubv.circleai.security

import kotlinx.coroutines.flow.Flow
import java.time.Duration
import java.time.Instant

// -- Enumerations ------------------------------------------------------------

/**
 * Transport-neutral classification of a peer security event.
 *
 * Declaration order mirrors the C# reference so ordinals stay stable across
 * every language port.
 */
enum class PeerSecurityEventKind {
    /** Authentication attempt (login, handshake, re-auth). */
    AuthAttempt,

    /** Anomalous routing behaviour detected (loop, black-hole, etc.). */
    RoutingAnomaly,

    /** Peer behaviour changed unexpectedly (rate, pattern, protocol). */
    BehaviourChange,

    /** Encryption negotiation event (downgrade, cipher mismatch). */
    EncryptionEvent,

    /** Active intrusion probe or exploitation attempt. */
    IntrusionSignal,

    /** Privilege escalation or capability violation attempt. */
    PrivilegeAttempt,

    /** Unusual connection pattern (port scan, rapid reconnect). */
    ConnectionAnomaly,

    /** Suspected data exfiltration (volume, destination anomaly). */
    DataExfiltration,

    /** Denial-of-service signal (flooding, resource exhaustion). */
    DenialOfService,

    /** Catch-all for events that do not map to a specific category. */
    Unknown,
}

/**
 * Severity level for a peer security event or threat assessment.
 * Values match the intuitive ordering: [None] is safest, [Critical] is worst.
 *
 * The numeric [level] is the stable wire value — do NOT change it.
 */
enum class PeerThreatLevel(val level: Int) {
    /** No threat — event carries no security significance. */
    None(0),

    /** Low-level anomaly — monitor but no action required. */
    Low(1),

    /** Notable anomaly — elevated monitoring recommended. */
    Medium(2),

    /** Significant threat — routing around the peer recommended. */
    High(3),

    /** Active or confirmed attack — quarantine the peer. */
    Critical(4),
}

/**
 * The action recommended by the security layer for a given peer.
 */
enum class PeerDirectiveKind {
    /** Increase observation cadence; no traffic restriction yet. */
    ElevateMonitoring,

    /** Exclude the peer from routing; still accept inbound connections. */
    AvoidNode,

    /** Hard-block the peer — no traffic to or from it. */
    QuarantineNode,

    /**
     * Lift a previous directive; the peer has recovered sufficient trust.
     * Not issued automatically — requires explicit operator action.
     */
    ReleaseNode,
}

// -- Records -----------------------------------------------------------------

/**
 * One security incident observed on any transport.
 *
 * @property nodeId Stable identifier of the peer that generated the event.
 * @property kind Transport-neutral event category.
 * @property threatLevel Assessed severity at the time of observation.
 * @property description Human-readable description of the event.
 * @property transportId Identifier for the transport that produced the event
 *   (e.g. `"aether"`, `"wifi"`, `"ble"`, `"nearlink"`, `"http"`).
 * @property occurredAt UTC timestamp of the event.
 */
data class PeerSecurityEvent(
    val nodeId: String,
    val kind: PeerSecurityEventKind,
    val threatLevel: PeerThreatLevel,
    val description: String,
    val transportId: String,
    val occurredAt: Instant,
)

/**
 * A security directive issued to all registered [IPeerDirectiveConsumer]
 * subscribers when a peer's trust crosses a threshold.
 *
 * @property kind The recommended action.
 * @property targetNodeId The peer to which the directive applies.
 * @property trustScore Current trust score of the peer at time of issue.
 * @property threatLevel Threat level at time of issue.
 * @property reason Human-readable explanation for the directive.
 * @property duration Optional duration after which the directive should be
 *   re-evaluated. `null` means permanent until an explicit
 *   [PeerDirectiveKind.ReleaseNode] directive is issued.
 * @property issuedAt UTC timestamp of issue.
 */
data class PeerDirective(
    val kind: PeerDirectiveKind,
    val targetNodeId: String,
    val trustScore: Double,
    val threatLevel: PeerThreatLevel,
    val reason: String,
    val duration: Duration?,
    val issuedAt: Instant,
)

/**
 * Notification emitted by [NodeTrustRegistry] whenever a node's trust score
 * changes.
 *
 * @property nodeId The peer whose score changed.
 * @property previousScore Score before this change.
 * @property newScore Score after this change.
 * @property reason Short description of the cause (event description or
 *   "passive-recovery").
 * @property changedAt UTC timestamp of the change.
 */
data class PeerTrustScoreUpdate(
    val nodeId: String,
    val previousScore: Double,
    val newScore: Double,
    val reason: String,
    val changedAt: Instant,
)

/**
 * Snapshot of the overall security posture across all observed peers.
 *
 * @property overallThreatLevel Worst-case threat level in the current peer set.
 * @property quarantinedPeerCount Peers at or below
 *   [SecurityOptions.quarantineThreshold].
 * @property monitoredPeerCount Peers elevated beyond monitoring threshold but
 *   not yet quarantined.
 * @property isActive Whether the security layer is currently running.
 * @property generatedAt UTC timestamp of this snapshot.
 */
data class PeerSecurityPosture(
    val overallThreatLevel: PeerThreatLevel,
    val quarantinedPeerCount: Int,
    val monitoredPeerCount: Int,
    val isActive: Boolean,
    val generatedAt: Instant,
)

/**
 * Aggregate network health across all observed peers.
 *
 * @property overallScore Average trust score [0.0, 1.0] across all peers.
 * @property trustedPeerCount Peers above [SecurityOptions.avoidNodeThreshold].
 * @property suspiciousPeerCount Peers at or below
 *   [SecurityOptions.elevateMonitoringThreshold].
 * @property summary Human-readable health summary.
 * @property generatedAt UTC timestamp of this report.
 */
data class PeerNetworkHealthReport(
    val overallScore: Double,
    val trustedPeerCount: Int,
    val suspiciousPeerCount: Int,
    val summary: String,
    val generatedAt: Instant,
)

/**
 * Per-peer threat assessment: confidence score, threat level, and detected
 * indicators.
 *
 * @property nodeId The assessed peer.
 * @property confidence Likelihood that the peer is a genuine threat [0.0, 1.0].
 *   Derived from trust deficit + indicator count.
 * @property threatLevel Classified severity.
 * @property indicators Human-readable indicator tags
 *   (e.g. "brute-force-auth", "intrusion-signal").
 * @property assessedAt UTC timestamp of this assessment.
 */
data class PeerThreatAssessment(
    val nodeId: String,
    val confidence: Double,
    val threatLevel: PeerThreatLevel,
    val indicators: List<String>,
    val assessedAt: Instant,
)

/**
 * Trust-aware routing recommendation for reaching a destination peer.
 *
 * @property destinationNodeId The target peer.
 * @property recommendedPath Ordered list of peer IDs forming the recommended
 *   path. Empty when no safe path is available.
 * @property avoidNodeIds Peers that should be excluded from routing.
 * @property confidence Confidence in the recommendation [0.0, 1.0].
 * @property reasoning Human-readable explanation.
 * @property generatedAt UTC timestamp of this advice.
 */
data class PeerRoutingAdvice(
    val destinationNodeId: String,
    val recommendedPath: List<String>,
    val avoidNodeIds: List<String>,
    val confidence: Double,
    val reasoning: String,
    val generatedAt: Instant,
)

// -- Interfaces --------------------------------------------------------------

/**
 * Receives security directives from any [IPeerSecurityLayer] implementation.
 */
fun interface IPeerDirectiveConsumer {
    /** Called when the security layer issues a directive for a peer. */
    fun onDirective(directive: PeerDirective)
}

/**
 * Transport-agnostic security layer lifecycle and posture surface.
 */
interface IPeerSecurityLayer {
    /** Starts the background trust-recovery loop. */
    suspend fun start()

    /** Stops the recovery loop and releases resources. */
    suspend fun stop()

    /**
     * Feed a security event from any transport into the security layer.
     * The layer will degrade the peer's trust score and issue directives as
     * needed.
     */
    fun handlePeerEvent(e: PeerSecurityEvent)

    /**
     * Subscribe to receive directives. Close the returned handle to
     * unsubscribe.
     */
    fun subscribeToDirectives(consumer: IPeerDirectiveConsumer): AutoCloseable

    /** Returns a snapshot of the current security posture. */
    suspend fun getPosture(): PeerSecurityPosture
}

/**
 * Transport-agnostic intelligence queries over accumulated trust data.
 */
interface IPeerIntelligence {
    /** Returns aggregate network health across all observed peers. */
    suspend fun getNetworkHealth(): PeerNetworkHealthReport

    /** Returns a threat assessment for a specific peer. */
    suspend fun assessThreat(nodeId: String): PeerThreatAssessment

    /** Returns trust-aware routing advice toward a destination peer. */
    suspend fun getRoutingAdvice(destinationNodeId: String): PeerRoutingAdvice

    /**
     * Streams every trust score change as it occurs. The returned [Flow] is
     * cold; collecting it consumes updates from the registry channel.
     */
    fun streamTrustScores(): Flow<PeerTrustScoreUpdate>
}

/**
 * Implemented by transport adapters to register an event source with the
 * security layer. The security layer calls [start] once to begin pumping
 * events.
 */
interface IPeerSecurityEventFeed {
    /** Human-readable identifier for this transport (e.g. "wifi", "ble", "aether"). */
    val transportId: String

    /**
     * Begins feeding events into [handler] until the calling coroutine is
     * cancelled.
     */
    suspend fun start(handler: (PeerSecurityEvent) -> Unit)
}
