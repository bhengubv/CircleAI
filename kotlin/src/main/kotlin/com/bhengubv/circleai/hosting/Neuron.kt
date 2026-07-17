// Neuron.kt
//
// The Neuron — Kotlin port of CircleAI.Hosting.Neuron. The Kotlin hosting
// AIService is a thin port (it owns an injected generatorFactory, not a
// selector/loader), so the two-slot residency is adapted to that idiom: the
// concierge routes a turn, an injected selector resolves the capability to a
// model id, and an injected specialistFactory builds the specialist generator.
//
// Contents: the concierge router + gate, the RAM admission gate
// (ResidentSlotManager), and the host-neutral NeuronNode facade.

package com.bhengubv.circleai.hosting

import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.models.ChatMessage
import com.bhengubv.circleai.selector.ChatCapability
import com.bhengubv.circleai.selector.ModelSelection
import kotlinx.coroutines.flow.Flow
import java.util.UUID

// =====================================================================
// Concierge router + gate
// =====================================================================

/** Which resident organ should serve a turn. Mirrors the Organ enum. */
enum class Organ { GENERALIST, SPECIALIST }

/** What the router sees for one turn. Mirrors RouteContext. */
data class RouteContext(
    val query: String,
    val hasImage: Boolean = false,
    val promptChars: Int? = null,
)

/** The router's per-turn verdict. Mirrors RouteDecision. */
data class RouteDecision(
    val organ: Organ,
    /** ChatCapability flags the specialist must satisfy (ignored for the generalist). */
    val capability: Int,
    val reason: String,
)

/** Per-turn router contract. Mirrors INeuronRouter. */
fun interface INeuronRouter {
    fun route(context: RouteContext): RouteDecision
}

/**
 * Safety/capability veto over a specialist pick. Mirrors NeuronGate: when the
 * predicate rejects a decision the router demotes it to the generalist, so a
 * turn is never blocked — the floor always answers.
 */
class NeuronGate(
    private val allowSpecialist: (RouteDecision) -> Boolean = { true },
) {
    fun allows(decision: RouteDecision): Boolean = allowSpecialist(decision)
}

/**
 * The default concierge router. Cheap heuristics, in priority order:
 *   image bytes     → Specialist(VISION)
 *   long prompt     → Specialist(LONG_CONTEXT)   (≥ longContextChars)
 *   reasoning cues  → Specialist(REASONING)
 *   otherwise       → Generalist(DEFAULT)
 * A NeuronGate veto demotes any specialist pick back to the generalist.
 * Mirrors HeuristicNeuronRouter.
 */
class HeuristicNeuronRouter(
    private val longContextChars: Int = 4000,
    private val gate: NeuronGate = NeuronGate(),
) : INeuronRouter {
    override fun route(context: RouteContext): RouteDecision {
        val d = classify(context)
        return if (d.organ == Organ.SPECIALIST && !gate.allows(d)) {
            RouteDecision(Organ.GENERALIST, ChatCapability.DEFAULT, "gate-vetoed → generalist")
        } else {
            d
        }
    }

    private fun classify(context: RouteContext): RouteDecision {
        if (context.hasImage) {
            return RouteDecision(Organ.SPECIALIST, ChatCapability.VISION, "image present")
        }
        val chars = context.promptChars ?: context.query.length
        if (chars >= longContextChars) {
            return RouteDecision(Organ.SPECIALIST, ChatCapability.LONG_CONTEXT, "long prompt ($chars chars)")
        }
        val q = context.query.lowercase()
        if (REASONING_CUES.any { q.contains(it) }) {
            return RouteDecision(Organ.SPECIALIST, ChatCapability.REASONING, "reasoning cue")
        }
        return RouteDecision(Organ.GENERALIST, ChatCapability.DEFAULT, "default")
    }

    private companion object {
        val REASONING_CUES = listOf(
            "debug", "stack trace", "solve", "prove", "reason", "analy",
            "calculate", "equation", "step by step", "algorithm",
            "why does", "derive", "diagnose",
        )
    }
}

// =====================================================================
// RAM admission gate — the specialist slot beside the generalist floor
// =====================================================================

/** Outcome of an admission attempt. Mirrors SlotOutcome. */
enum class SlotOutcome { ADMITTED, ALREADY_RESIDENT, INSUFFICIENT_RAM, BUILD_FAILED }

/** Result of ensureSpecialist. Mirrors SlotAdmission. */
data class SlotAdmission(val outcome: SlotOutcome, val generator: IChatGenerator?)

/**
 * Owns the single hot-swappable specialist slot beside the always-warm
 * generalist floor. Admission = generalistReserved + estimatedBytes ≤ ceiling.
 * A different pick evicts the incumbent first (one specialist at a time), and
 * the specialist is evicted first under memory pressure so the generalist never
 * drops. Mirrors ResidentSlotManager (modeled on the server's
 * ModelLifecycleManager).
 */
class ResidentSlotManager(
    private val generalistReservedBytes: Long,
    private val ramAvailableBytes: () -> Long,
) {
    private var specialist: IChatGenerator? = null
    private var specialistModelId: String? = null

    /** The resident specialist's model id, or null if the slot is empty. */
    val residentSpecialistModelId: String? get() = specialistModelId

    /** The resident specialist generator, or null. */
    val residentSpecialist: IChatGenerator? get() = specialist

    /**
     * Ensure [selection] is the resident specialist, building it via [build] if
     * needed. Admission-gated on RAM; a different pick evicts the incumbent.
     * Never throws on denial — returns the outcome so the caller can fall back
     * to the generalist floor.
     */
    fun ensureSpecialist(
        selection: ModelSelection,
        build: (String) -> IChatGenerator?,
    ): SlotAdmission {
        val id = selection.modelId
        val current = specialistModelId
        if (current != null && current.equals(id, ignoreCase = true)) {
            return SlotAdmission(SlotOutcome.ALREADY_RESIDENT, specialist)
        }

        // RAM admission gate: reserve the floor, then check the specialist fits.
        val needed = generalistReservedBytes + maxOf(0L, selection.estimatedBytes)
        if (needed > ramAvailableBytes()) {
            return SlotAdmission(SlotOutcome.INSUFFICIENT_RAM, null)
        }

        // Evict the incumbent (one specialist at a time) before building the new.
        evictSpecialist()

        val built = build(id) ?: return SlotAdmission(SlotOutcome.BUILD_FAILED, null)
        specialist = built
        specialistModelId = id
        return SlotAdmission(SlotOutcome.ADMITTED, built)
    }

    /** Drop the resident specialist (the generalist floor is untouched). */
    fun evictSpecialist() {
        val g = specialist
        specialist = null
        specialistModelId = null
        if (g is AutoCloseable) runCatching { g.close() }
    }
}

// =====================================================================
// NeuronNode facade
// =====================================================================

/**
 * A complete, host-neutral Neuron node over an [IAIService] brain. Composes the
 * concierge (a router-gated two-slot AIService) behind the host-neutral
 * [IChatRuntime] / [IPersistableChatRuntime] seam, and exposes the underlying
 * brain so a CompanionSession can still sit on top. Persists the generalist
 * floor's session for OOM/restart survival (the specialist is rebuildable from
 * the registry). Mirrors NeuronNode : IChatRuntime, IPersistableChatRuntime.
 */
class NeuronNode(
    private val brain: IAIService,
    sessionSnapshotPath: String? = null,
) : IPersistableChatRuntime {

    private val snapshotPathValue: String? =
        sessionSnapshotPath
            ?: System.getProperty("java.io.tmpdir")?.let { dir ->
                (if (dir.endsWith("/") || dir.endsWith("\\")) dir else "$dir/") +
                    "circleai-neuron-session.bin"
            }

    /** The underlying brain, so CompanionSession can compose over the Neuron. */
    val brainService: IAIService get() = brain

    override val id: String get() = "circleai-neuron"

    override val engineLabel: String
        get() {
            val mid = (brain as? AIService)?.resolvedModelIdValue
            return if (!mid.isNullOrEmpty()) "circleai-neuron:$mid" else "circleai-neuron"
        }

    override val isReady: Boolean get() = brain.isReady

    override val statusMessage: String get() = if (brain.isReady) "ready" else "loading model…"

    override val sessionSnapshotPath: String? get() = snapshotPathValue

    override fun streamAsync(turns: List<ChatTurn>): Flow<String> {
        val messages = turns.map { t ->
            ChatMessage(id = UUID.randomUUID().toString(), role = t.role, content = t.content)
        }
        return brain.streamAsync(messages)
    }

    override suspend fun saveSessionAsync(path: String): Boolean = brain.saveSessionAsync(path)

    override suspend fun loadSessionAsync(path: String): Boolean = brain.loadSessionAsync(path)
}
