// SecurityAetherNet.kt
//
// Kotlin port of the CircleAI.Security.AetherNet module — the AetherNet-specific
// security bindings (src/CircleAI.Security.AetherNet/*.cs).
//
// This module bridges the Aether contract family (com.bhengubv.circleai.aether)
// to the transport-agnostic CircleAI.Security layer (PeerIntelligenceService,
// SecurityLayerService, PeerSecurityTypes) already ported in the security
// package, plus the Companion session contract.
//
// Ported types:
//   AetherMapper                — static enum translations Aether ↔ Peer
//   AetherIntelligenceAdapter   — IAetherIntelligence over PeerIntelligenceService
//   AetherSecurityBridge        — IAISecurityLayer over SecurityLayerService
//   MeshDirectiveStore          — ISecurityDirectiveConsumer + block-state store
//   MeshSecurityGate            — read-only "is this id blocked?" query view
//   MeshSecurityBlockedException
//   MeshGatedCompanionSession   — ICompanionSession decorator enforcing the gate
//
// The two DI ServiceCollectionExtensions files are Microsoft.Extensions.DI
// wire-up with no behavioural logic; on the JVM the equivalent is direct
// construction, so they are intentionally not ported. Everything they wire is
// available by constructing the classes below.

package com.bhengubv.circleai.security.aethernet

import com.bhengubv.circleai.aether.AetherNetworkEvent
import com.bhengubv.circleai.aether.AetherNodeEvent
import com.bhengubv.circleai.aether.AetherRouteEvent
import com.bhengubv.circleai.aether.AetherSecurityEvent
import com.bhengubv.circleai.aether.AetherSecurityEventKind
import com.bhengubv.circleai.aether.AetherThreatLevel
import com.bhengubv.circleai.aether.AetherTransportEvent
import com.bhengubv.circleai.aether.IAISecurityLayer
import com.bhengubv.circleai.aether.IAetherIntelligence
import com.bhengubv.circleai.aether.IAetherTelemetry
import com.bhengubv.circleai.aether.IAetherTelemetryObserver
import com.bhengubv.circleai.aether.ISecurityDirectiveConsumer
import com.bhengubv.circleai.aether.NetworkHealthReport
import com.bhengubv.circleai.aether.RoutingAdvice
import com.bhengubv.circleai.aether.SecurityDirective
import com.bhengubv.circleai.aether.SecurityDirectiveKind
import com.bhengubv.circleai.aether.SecurityPosture
import com.bhengubv.circleai.aether.ThreatAssessment
import com.bhengubv.circleai.aether.TrustScoreUpdate
import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import com.bhengubv.circleai.security.IPeerDirectiveConsumer
import com.bhengubv.circleai.security.PeerDirective
import com.bhengubv.circleai.security.PeerDirectiveKind
import com.bhengubv.circleai.security.PeerIntelligenceService
import com.bhengubv.circleai.security.PeerSecurityEvent
import com.bhengubv.circleai.security.PeerSecurityEventKind
import com.bhengubv.circleai.security.PeerThreatLevel
import com.bhengubv.circleai.security.SecurityLayerService
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emitAll
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.map
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// ═══════════════════════════════════════════════════════════════════════════
// AetherMapper — static translation helpers between Aether and Peer types.
//
// All mappings are explicit `when` expressions so a new enum value on either
// side surfaces at the exhaustiveness check. Mirrors AetherMapper.cs exactly.
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Translations between Aether-specific types and the transport-agnostic Peer
 * types defined in `com.bhengubv.circleai.security`.
 */
internal object AetherMapper {

    // ── AetherSecurityEventKind → PeerSecurityEventKind ─────────────────────

    fun toPeerEventKind(kind: AetherSecurityEventKind): PeerSecurityEventKind = when (kind) {
        AetherSecurityEventKind.NodeAuthAttempt -> PeerSecurityEventKind.AuthAttempt
        AetherSecurityEventKind.RoutingAnomaly -> PeerSecurityEventKind.RoutingAnomaly
        AetherSecurityEventKind.NodeBehaviourChange -> PeerSecurityEventKind.BehaviourChange
        AetherSecurityEventKind.EncryptionEvent -> PeerSecurityEventKind.EncryptionEvent
        AetherSecurityEventKind.IntrusionSignal -> PeerSecurityEventKind.IntrusionSignal
        AetherSecurityEventKind.PrivilegeAttempt -> PeerSecurityEventKind.PrivilegeAttempt
    }

    // ── AetherThreatLevel ↔ PeerThreatLevel ─────────────────────────────────

    fun toPeerThreatLevel(level: AetherThreatLevel): PeerThreatLevel = when (level) {
        AetherThreatLevel.None -> PeerThreatLevel.None
        AetherThreatLevel.Low -> PeerThreatLevel.Low
        AetherThreatLevel.Medium -> PeerThreatLevel.Medium
        AetherThreatLevel.High -> PeerThreatLevel.High
        AetherThreatLevel.Critical -> PeerThreatLevel.Critical
    }

    fun toAetherThreatLevel(level: PeerThreatLevel): AetherThreatLevel = when (level) {
        PeerThreatLevel.None -> AetherThreatLevel.None
        PeerThreatLevel.Low -> AetherThreatLevel.Low
        PeerThreatLevel.Medium -> AetherThreatLevel.Medium
        PeerThreatLevel.High -> AetherThreatLevel.High
        PeerThreatLevel.Critical -> AetherThreatLevel.Critical
    }

    // ── PeerDirectiveKind → SecurityDirectiveKind ───────────────────────────
    //
    // The C# reference folds the unmapped values (there is no Peer analogue of
    // UpdateNodeTrust / RequestReauth) to ElevateMonitoring via its default arm.
    // Kotlin's `when` on an enum is exhaustive over the four Peer values.

    fun toSecurityDirectiveKind(kind: PeerDirectiveKind): SecurityDirectiveKind = when (kind) {
        PeerDirectiveKind.ElevateMonitoring -> SecurityDirectiveKind.ElevateMonitoring
        PeerDirectiveKind.AvoidNode -> SecurityDirectiveKind.AvoidNode
        PeerDirectiveKind.QuarantineNode -> SecurityDirectiveKind.QuarantineNode
        PeerDirectiveKind.ReleaseNode -> SecurityDirectiveKind.ReleaseNode
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// AetherIntelligenceAdapter
//
// Implements IAetherIntelligence by delegating to the transport-agnostic
// PeerIntelligenceService and mapping result types.
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Implements [IAetherIntelligence] by wrapping [PeerIntelligenceService] and
 * mapping transport-agnostic result types to their Aether equivalents. Callers
 * that only need transport-agnostic intelligence should use
 * [PeerIntelligenceService] directly.
 */
class AetherIntelligenceAdapter(
    private val inner: PeerIntelligenceService,
) : IAetherIntelligence {

    override suspend fun getNetworkHealth(): NetworkHealthReport {
        val r = inner.getNetworkHealth()
        return NetworkHealthReport(
            overallScore = r.overallScore,
            trustedNodeCount = r.trustedPeerCount,
            suspiciousNodeCount = r.suspiciousPeerCount,
            summary = r.summary,
            generatedAt = r.generatedAt,
        )
    }

    override suspend fun assessThreat(nodeId: String): ThreatAssessment {
        val a = inner.assessThreat(nodeId)
        return ThreatAssessment(
            nodeId = a.nodeId,
            threatConfidence = a.confidence,
            level = AetherMapper.toAetherThreatLevel(a.threatLevel),
            indicators = a.indicators,
            assessedAt = a.assessedAt,
        )
    }

    override suspend fun getRoutingAdvice(destinationNodeId: String): RoutingAdvice {
        val r = inner.getRoutingAdvice(destinationNodeId)
        return RoutingAdvice(
            destinationNodeId = r.destinationNodeId,
            recommendedPath = r.recommendedPath,
            avoidNodes = r.avoidNodeIds,
            confidence = r.confidence,
            reasoning = r.reasoning,
            generatedAt = r.generatedAt,
        )
    }

    override fun streamTrustScores(): Flow<TrustScoreUpdate> =
        inner.streamTrustScores().map { u ->
            TrustScoreUpdate(
                nodeId = u.nodeId,
                previousScore = u.previousScore,
                currentScore = u.newScore,
                reason = u.reason,
                updatedAt = u.changedAt,
            )
        }
}

// ═══════════════════════════════════════════════════════════════════════════
// AetherSecurityBridge
//
// Bridges the Aether telemetry feed (IAetherTelemetry / IAetherTelemetryObserver)
// into the transport-agnostic SecurityLayerService. Implements IAISecurityLayer
// so existing Aether callers can wire it up unchanged. Pure translation.
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Connects an Aether mesh telemetry feed to the transport-agnostic
 * [SecurityLayerService]. Implements [IAISecurityLayer] so it can be used as a
 * drop-in replacement for the old Aether-coupled layer.
 */
class AetherSecurityBridge(
    private val layer: SecurityLayerService,
) : IAISecurityLayer {

    private var telemetrySubscription: AutoCloseable? = null

    override suspend fun start(telemetry: IAetherTelemetry) {
        telemetrySubscription = telemetry.subscribe(Observer(this))
        layer.start()
    }

    override suspend fun stop() {
        telemetrySubscription?.close()
        telemetrySubscription = null
        layer.stop()
    }

    override fun subscribeToDirectives(consumer: ISecurityDirectiveConsumer): AutoCloseable =
        layer.subscribeToDirectives(DirectiveAdapter(consumer))

    override suspend fun getPosture(): SecurityPosture {
        val posture = layer.getPosture()
        return SecurityPosture(
            overallThreatLevel = AetherMapper.toAetherThreatLevel(posture.overallThreatLevel),
            quarantinedNodeCount = posture.quarantinedPeerCount,
            monitoredNodeCount = posture.monitoredPeerCount,
            isActive = posture.isActive,
            assessedAt = posture.generatedAt,
        )
    }

    // ── Telemetry observer ──────────────────────────────────────────────────

    private class Observer(private val bridge: AetherSecurityBridge) : IAetherTelemetryObserver {
        override fun onSecurityEvent(e: AetherSecurityEvent) {
            val peer = PeerSecurityEvent(
                nodeId = e.nodeId,
                kind = AetherMapper.toPeerEventKind(e.kind),
                threatLevel = AetherMapper.toPeerThreatLevel(e.threatLevel),
                description = e.description,
                transportId = "aether",
                occurredAt = e.occurredAt,
            )
            bridge.layer.handlePeerEvent(peer)
        }

        override fun onNodeEvent(e: AetherNodeEvent) {
            if (e.isExit) bridge.layer.handlePeerLeft(e.nodeId)
        }

        // Not relevant to security scoring — ignore.
        override fun onTransportEvent(e: AetherTransportEvent) {}
        override fun onRouteEvent(e: AetherRouteEvent) {}
        override fun onNetworkEvent(e: AetherNetworkEvent) {}
    }

    // ── Directive adapter ─────────────────────────────────────────────────

    /**
     * Adapts an Aether [ISecurityDirectiveConsumer] so it can receive
     * [PeerDirective] instances from the transport-agnostic layer, translating
     * them back to [SecurityDirective] before delivery.
     */
    private class DirectiveAdapter(
        private val consumer: ISecurityDirectiveConsumer,
    ) : IPeerDirectiveConsumer {
        override fun onDirective(directive: PeerDirective) {
            val aether = SecurityDirective(
                kind = AetherMapper.toSecurityDirectiveKind(directive.kind),
                targetNodeId = directive.targetNodeId,
                trustScoreOverride = directive.trustScore,
                threatLevel = AetherMapper.toAetherThreatLevel(directive.threatLevel),
                reason = directive.reason,
                duration = directive.duration,
                issuedAt = directive.issuedAt,
            )
            consumer.onDirective(aether)
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// MeshDirectiveStore
//
// In-memory record of every active SecurityDirective the mesh has issued against
// a node. Implements ISecurityDirectiveConsumer so it can be plugged in as the
// directive sink. Expiry is handled lazily on read — no background timer.
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Thread-safe in-memory registry of security directives received from the mesh.
 * Acts as both the directive sink and the query surface other CircleAI
 * components consult before serving a request.
 *
 * @param clock Wall clock; defaults to [Instant.now]. Inject for testing expiry.
 */
class MeshDirectiveStore(
    private val clock: () -> Instant = { Instant.now() },
) : ISecurityDirectiveConsumer {

    private val byNode = ConcurrentHashMap<String, MutableList<SecurityDirective>>()

    override fun onDirective(directive: SecurityDirective) {
        if (!directive.hasTarget) return
        val nodeId = directive.targetNodeId!!

        if (directive.kind == SecurityDirectiveKind.ReleaseNode) {
            // Release lifts every Avoid/Quarantine for the node.
            byNode.remove(nodeId)
            return
        }

        byNode.compute(nodeId) { _, existing ->
            val list = existing ?: ArrayList()
            synchronized(list) { list.add(directive) }
            list
        }
    }

    /**
     * Returns a [BlockCheck] describing whether an unexpired Avoid or Quarantine
     * directive is active for the node, carrying the most recent block's reason.
     * Expired entries are swept while walking the list.
     */
    fun isBlocked(nodeId: String): BlockCheck {
        if (nodeId.isBlank()) return BlockCheck.notBlocked()
        val list = byNode[nodeId] ?: return BlockCheck.notBlocked()

        val now = clock()
        var latestBlock: SecurityDirective? = null

        synchronized(list) {
            // Drop expired entries while we walk the list (reverse for safe removal).
            for (i in list.indices.reversed()) {
                val d = list[i]
                if (isExpired(d, now)) {
                    list.removeAt(i)
                    continue
                }
                if (isBlockKind(d.kind) &&
                    (latestBlock == null || d.issuedAt.isAfter(latestBlock!!.issuedAt))
                ) {
                    latestBlock = d
                }
            }
            if (list.isEmpty()) byNode.remove(nodeId)
        }

        val block = latestBlock ?: return BlockCheck.notBlocked()
        return BlockCheck(blocked = true, reason = block.reason)
    }

    /** Lists every unexpired directive for the node — useful for audit/diagnostics. */
    fun getActiveDirectives(nodeId: String): List<SecurityDirective> {
        if (nodeId.isBlank()) return emptyList()
        val list = byNode[nodeId] ?: return emptyList()
        val now = clock()
        synchronized(list) {
            return list.filter { !isExpired(it, now) }
        }
    }

    /** Number of nodes with at least one tracked directive. */
    val trackedNodeCount: Int get() = byNode.size

    private fun isBlockKind(k: SecurityDirectiveKind): Boolean =
        k == SecurityDirectiveKind.AvoidNode || k == SecurityDirectiveKind.QuarantineNode

    private fun isExpired(d: SecurityDirective, now: Instant): Boolean {
        val duration = d.duration ?: return false
        return !d.issuedAt.plus(duration).isAfter(now) // (issuedAt + duration) <= now
    }

    /** Outcome of an [isBlocked] check. */
    data class BlockCheck(val blocked: Boolean, val reason: String) {
        companion object {
            fun notBlocked(): BlockCheck = BlockCheck(false, "")
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// MeshSecurityGate + MeshSecurityBlockedException
//
// Read-only fast-path query surface over MeshDirectiveStore. The gate is what
// CircleAI features inject when they want to consult mesh-issued directives
// before serving a request — e.g. chat refusing a blocked user.
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Query surface for asking "is this user/node currently blocked by the mesh?"
 * Backed by a [MeshDirectiveStore].
 */
class MeshSecurityGate(
    private val store: MeshDirectiveStore,
) {
    /** Decision returned from [decide]. */
    data class GateDecision(val isBlocked: Boolean, val reason: String) {
        companion object {
            /** Convenience: allow with no reason text. */
            val Allowed: GateDecision = GateDecision(false, "")
        }
    }

    /**
     * Returns a single-shot decision for the given user/node id. The reason text
     * comes from the most recent active block directive.
     */
    fun decide(userOrNodeId: String): GateDecision {
        if (userOrNodeId.isBlank()) return GateDecision.Allowed
        val check = store.isBlocked(userOrNodeId)
        return if (check.blocked) GateDecision(true, check.reason) else GateDecision.Allowed
    }

    /**
     * Throws when a request from a blocked id would proceed. Use in service code
     * that wants a one-line guard at the top of a method.
     */
    fun enforce(userOrNodeId: String) {
        val decision = decide(userOrNodeId)
        if (decision.isBlocked) {
            throw MeshSecurityBlockedException(userOrNodeId, decision.reason)
        }
    }
}

/**
 * Thrown by [MeshSecurityGate.enforce] when the mesh has issued a block
 * directive against the requesting id.
 */
class MeshSecurityBlockedException(
    val blockedId: String,
    reason: String,
) : RuntimeException("Mesh has blocked '$blockedId': $reason")

// ═══════════════════════════════════════════════════════════════════════════
// MeshGatedCompanionSession
//
// Decorator over ICompanionSession that consults MeshSecurityGate before every
// message-producing call (sendAsync, streamAsync, agentAsync). When the gate
// says the session's identityId is blocked, it throws
// MeshSecurityBlockedException instead of reaching the underlying generator.
//
// Diagnostic / metadata calls (context, history, feedback) pass through
// unguarded — gating them would go beyond "stop the chat" into "punish".
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Wraps an inner [ICompanionSession] and enforces the mesh's "block this user"
 * directives via [MeshSecurityGate] on every message-producing call.
 */
class MeshGatedCompanionSession(
    private val inner: ICompanionSession,
    private val gate: MeshSecurityGate,
) : ICompanionSession {

    // ── Pass-through identity / properties ──────────────────────────────────

    override val sessionId: String get() = inner.sessionId
    override val identityId: String get() = inner.identityId
    override val interfaceKind: InterfaceKind get() = inner.interfaceKind
    override val history: List<CompanionTurn> get() = inner.history
    override val proactiveEvents: Flow<CompanionProactiveEvent> get() = inner.proactiveEvents

    // ── Guarded entry points ────────────────────────────────────────────────

    override suspend fun sendAsync(message: String): String {
        gate.enforce(identityId)
        return inner.sendAsync(message)
    }

    override fun streamAsync(message: String): Flow<String> = flow {
        // Enforce at collection time — mirrors the C# guard placed at the top of
        // the async-iterator body, which runs when enumeration first begins, so
        // a blocked user is rejected before any chunk is produced.
        gate.enforce(identityId)
        emitAll(inner.streamAsync(message))
    }

    override suspend fun agentAsync(instruction: String): String {
        gate.enforce(identityId)
        return inner.agentAsync(instruction)
    }

    // ── Unguarded pass-through ──────────────────────────────────────────────

    override fun getContext(): CompanionContext = inner.getContext()

    override suspend fun refreshContextAsync() = inner.refreshContextAsync()

    override suspend fun signalFeedbackAsync(positive: Boolean, note: String?) =
        inner.signalFeedbackAsync(positive, note)

    override fun close() = inner.close()
}
