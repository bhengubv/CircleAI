// AetherNetAdapters.kt
//
// The join between CircleAI's Aether seam and a live AetherNet runtime.
//
// THE PAIR THAT MATTERS IS THE SYMMETRY. Directives flow both ways and each
// direction has its own adapter, which is the only way the round trip stays
// honest:
//
//   AetherNetDirectiveSink            CircleAI  →  AetherNet   (outbound)
//   AetherNetInboundDirectiveBridge   AetherNet →  CircleAI    (inbound)
//
// When the assistant decides a node is hostile that decision crosses the sink
// and lands on the mesh's policy engine, which decides whether to HONOUR it — a
// directive is a recommendation to a peer, not a command over it. When a peer
// publishes one, the bridge carries it back. Without both, security decisions
// travel in one direction and a device can be told nothing by the network it is
// part of.
//
// AGAINST A SEAM, NOT A DEPENDENCY. The C# references the AetherNet assembly
// directly; aether-protocol is its own repository, so the far side is an
// interface here. The TRANSLATION crosses, and a host binds it to the runtime.
//
// Ported from src/CircleAI.AetherNet/*.cs.

package com.bhengubv.circleai.aethernet

import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

// ─────────────────────────────────────────────────────────────────────────────
// The far side

/**
 * The mesh runtime's own directive consumer. Its own interface, not CircleAI's:
 * the two shapes are separate on purpose, and collapsing them would make the
 * translation invisible and then wrong.
 */
interface IMeshDirectiveConsumer {
    fun onMeshDirective(directive: SecurityDirective)
}

/** The mesh runtime's telemetry publisher. */
interface IMeshTelemetryPublisher {
    fun subscribe(observer: IAetherTelemetryObserver): AutoCloseable
}

// ─────────────────────────────────────────────────────────────────────────────
// Context

/**
 * Reports what the live AetherNet runtime is, to a caller that only knows the
 * Aether seam.
 *
 * The install level is fixed at App, deliberately: AetherNet runs as an
 * in-process library here, and an OS-managed instance is a different adapter on
 * a different platform. Reporting OS would make [requiresAuth] true and send a
 * caller looking for a permission prompt that will never appear.
 */
class AetherNetContextAdapter(
    protocolVersion: Int,
    val minimumRequired: SemanticVersion? = null,
    val isEnabled: Boolean = true
) : IAetherContext {

    // The mesh protocol version IS the major version. A mesh speaking protocol 4
    // and one speaking 5 are not the same runtime, and a caller comparing
    // versions has to see that in the number it is handed.
    override val runtimeVersion: SemanticVersion? = SemanticVersion(protocolVersion, 0, 0, 0)

    override val installLevel: AetherInstallLevel get() = AetherInstallLevel.App

    /** True: this adapter only exists when the runtime is linked in. */
    override val isAvailable: Boolean get() = true

    override val isSufficient: Boolean
        get() {
            val min = minimumRequired ?: return true
            val rv = runtimeVersion ?: return false
            return rv >= min
        }

    override val requiresAuth: Boolean get() = installLevel == AetherInstallLevel.OS
}

// ─────────────────────────────────────────────────────────────────────────────
// Directives, both ways

/**
 * CircleAI → AetherNet.
 *
 * The mesh's policy engine decides whether to honour what arrives. That is the
 * whole shape of the relationship: this device can say "I think that node is
 * hostile", and the network decides what to do about it.
 */
class AetherNetDirectiveSink(
    private val mesh: IMeshDirectiveConsumer
) : ISecurityDirectiveConsumer {
    override fun onDirective(directive: SecurityDirective) = mesh.onMeshDirective(directive)
}

/**
 * AetherNet → CircleAI.
 *
 * The inverse, and it is not optional. Without it a device issues security
 * decisions and receives none, so a node the rest of the mesh has already agreed
 * is hostile stays trusted here.
 */
class AetherNetInboundDirectiveBridge(
    private val circle: ISecurityDirectiveConsumer
) : IMeshDirectiveConsumer {
    override fun onMeshDirective(directive: SecurityDirective) = circle.onDirective(directive)
}

// ─────────────────────────────────────────────────────────────────────────────
// Telemetry

/**
 * Fans AetherNet's telemetry out to a CircleAI observer.
 *
 * The returned handle unhooks JUST that subscriber. A shared handle is how one
 * component shutting down takes the security layer's feed with it, and the
 * symptom is a mesh that silently stops being watched.
 */
class AetherNetTelemetryAdapter(
    private val mesh: IMeshTelemetryPublisher
) : IAetherTelemetry {
    override fun subscribe(observer: IAetherTelemetryObserver): AutoCloseable =
        mesh.subscribe(observer)
}

// ─────────────────────────────────────────────────────────────────────────────
// Companion state over the mesh

/** What a companion tells its other devices about itself. */
data class CompanionStateMessage(
    val deviceId: String,
    val payloadJson: String,
    val at: Instant
)

/**
 * Carries companion state between a person's own devices over the mesh.
 *
 * A DEVICE NEVER RECEIVES ITS OWN BROADCAST. Without that check a two-device
 * pairing echoes state back and forth forever, and each device treats its own
 * message as news from the other one.
 */
class AetherNetCompanionStateChannel(
    private val deviceId: String,
    private val send: (CompanionStateMessage) -> Unit
) {
    private val observers = ConcurrentHashMap<Int, (CompanionStateMessage) -> Unit>()
    private val nextId = AtomicInteger(0)
    private val seen = ConcurrentHashMap.newKeySet<String>()

    fun publish(payloadJson: String, at: Instant = Instant.now()) {
        send(CompanionStateMessage(deviceId, payloadJson, at))
    }

    /**
     * Called by the host when the mesh delivers a message. Returns whether it
     * was ACCEPTED — new, and not our own echo. Deliberately not "did an
     * observer hear it": whether anything is currently observing is the host's
     * business and changes minute to minute.
     */
    fun receive(message: CompanionStateMessage): Boolean {
        if (message.deviceId == deviceId) return false          // our own echo

        // A mesh FLOODS, so the same message legitimately arrives by more than
        // one route. Delivering it twice makes a companion apply the same state
        // change twice, which for anything non-idempotent is a real bug.
        val key = "${message.deviceId}|${message.at.toEpochMilli()}|${message.payloadJson}"
        if (!seen.add(key)) return false

        observers.values.forEach { it(message) }
        return true
    }

    fun observe(handler: (CompanionStateMessage) -> Unit): Int {
        val token = nextId.incrementAndGet()
        observers[token] = handler
        return token
    }

    fun stopObserving(token: Int) { observers.remove(token) }

    /** Clears the duplicate-suppression set, so a long-lived device does not
     *  grow it without bound. */
    fun forgetSeen() = seen.clear()

    val seenCount: Int get() = seen.size
}

// ─────────────────────────────────────────────────────────────────────────────
// AI over the mesh

class AetherNetNoPeerException(message: String) : Exception(message)

/**
 * Answers a prompt by asking a peer that has a model this device does not.
 *
 * The point of the whole arrangement: a cheap phone with no room for a
 * generalist can still get an answer from one on the same mesh, without either
 * device reaching the internet.
 */
class CircleAiAetherNetAiProvider(
    private val peers: () -> List<String>,
    private val ask: suspend (prompt: String, peerId: String) -> String
) {
    val hasPeer: Boolean get() = peers().isNotEmpty()

    /**
     * Asks each capable peer IN TURN, not in parallel. Every attempt costs the
     * peer's battery and the radio's airtime, and asking four phones a question
     * one of them will answer wastes three of them. The mesh is shared, and a
     * device that broadcasts every question to everybody is why mesh networks
     * get switched off.
     */
    suspend fun complete(prompt: String): String {
        val candidates = peers()
        if (candidates.isEmpty()) {
            throw AetherNetNoPeerException("No peer on the mesh is offering a model.")
        }

        var last: Throwable? = null
        for (peer in candidates) {
            try {
                val answer = ask(prompt, peer)
                if (answer.isNotBlank()) return answer
            } catch (t: Throwable) {
                // A peer that went out of range mid-question is ordinary on a
                // mesh, not an error worth ending on.
                last = t
            }
        }
        throw AetherNetNoPeerException(
            "Every peer that was asked failed" +
                (last?.let { ": $it" } ?: " or answered with nothing.")
        )
    }
}
