// AgentHandoff.kt
//
// Passing a live call from one agent to another, and what the loop reports about
// itself.
//
// A HANDOFF IS THE MOMENT A CALL IS MOST LIKELY TO BE LOST. The caller is
// already mid-problem, the context lives in the agent being replaced, and every
// second of silence reads as a dropped call. So the handoff carries the
// transcript forward, and it either succeeds or says plainly that it did not —
// there is no silent failure mode here.
//
// Ported from src/CircleAI.Telephony/{AgentHandoff, Telemetry,
// SpeechLifecycleEvents}.cs.

package com.bhengubv.circleai.telephony

import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong

/** One agent a call can be handed to. */
data class CallAgent(
    val id: String,
    val displayName: String,
    /** What this agent is for, in words the router matches against. */
    val skills: List<String> = emptyList(),
    /** False takes it out of rotation without deleting it — an agent on leave
     *  should not be a configuration change somebody has to remember to undo. */
    val available: Boolean = true,
    val systemPrompt: String? = null
)

data class HandoffResult(
    val accepted: Boolean,
    val toAgentId: String?,
    /** Said to a PERSON, and read aloud on the call when the handoff fails.
     *  "No agent is available for that" is something a caller can act on. */
    val reason: String,
    val at: Instant = Instant.now()
)

interface IAgentHandoffOrchestrator {
    /** Every agent currently registered, available or not. */
    val agents: List<CallAgent>

    fun register(agent: CallAgent)

    /**
     * Hands [callId] to the best agent for [need], carrying [transcript]
     * forward. Never throws: a handoff that fails must still leave the caller
     * talking to somebody.
     */
    suspend fun handoff(callId: String, need: String, transcript: List<String>): HandoffResult
}

/**
 * Picks by skill overlap, then by who is free.
 *
 * DETERMINISTIC ON TIES, by agent id. Two agents equally suited must go to the
 * same one every time, or the same caller ringing twice gets two different
 * people and neither has the context.
 */
class DefaultAgentHandoffOrchestrator(
    private val onTransfer: suspend (callId: String, agent: CallAgent, transcript: List<String>) -> Boolean =
        { _, _, _ -> true }
) : IAgentHandoffOrchestrator {

    private val registered = ConcurrentHashMap<String, CallAgent>()

    override val agents: List<CallAgent> get() = registered.values.sortedBy { it.id }

    override fun register(agent: CallAgent) {
        registered[agent.id] = agent
    }

    override suspend fun handoff(
        callId: String, need: String, transcript: List<String>
    ): HandoffResult {
        val wanted = need.lowercase().split(Regex("[^a-z0-9]+")).filter { it.length > 2 }.toSet()

        val candidate = registered.values
            .filter { it.available }
            .map { agent ->
                agent to agent.skills.count { it.lowercase() in wanted }
            }
            .sortedWith(compareByDescending<Pair<CallAgent, Int>> { it.second }
                .thenBy { it.first.id })
            .firstOrNull()

        if (candidate == null) {
            return HandoffResult(false, null, "No agent is available to take this call.")
        }

        val (agent, overlap) = candidate
        // NO OVERLAP IS STILL A HANDOFF. A caller who asked for something nobody
        // is specialised in is better off with a generalist than with a refusal,
        // and the reason says which happened.
        val reason = if (overlap > 0) "matched on ${overlap} skill(s)"
        else "no specialist available; handed to ${agent.displayName}"

        return try {
            if (onTransfer(callId, agent, transcript)) {
                HandoffResult(true, agent.id, reason)
            } else {
                HandoffResult(false, agent.id, "${agent.displayName} did not pick up.")
            }
        } catch (t: Throwable) {
            // Never throws: the caller is on a live line, and an exception here
            // is silence at the other end.
            HandoffResult(false, agent.id, "The transfer failed: ${t.message}")
        }
    }
}

/**
 * A final transcript, versioned.
 *
 * The `_v2` name is kept exactly as the C# has it. It looks like a mistake and
 * is not: the original event shipped, callers deserialise it by name, and
 * renaming it would silently stop delivering to every one of them.
 */
data class TranscriptFinalEvent_v2(
    val callId: String,
    val at: Instant,
    val text: String
)

/**
 * What the loop reports about itself over a call.
 *
 * COUNTS, NOT A LOG. A per-call log of every turn is the thing nobody reads and
 * everybody ships; four numbers and two latencies are what actually tells
 * somebody whether the call went well.
 */
class VoiceLoopTelemetry {

    private val turns = AtomicLong(0)
    private val bargeIns = AtomicLong(0)
    private val faults = AtomicLong(0)
    private val silentTurns = AtomicLong(0)

    private val firstTokenMs = ArrayList<Double>()
    private val turnMs = ArrayList<Double>()

    fun recordTurn(firstTokenMillis: Double?, totalMillis: Double) {
        turns.incrementAndGet()
        synchronized(turnMs) { turnMs.add(totalMillis) }
        if (firstTokenMillis != null) synchronized(firstTokenMs) { firstTokenMs.add(firstTokenMillis) }
    }

    fun recordBargeIn() = bargeIns.incrementAndGet()
    fun recordFault() = faults.incrementAndGet()

    /** A turn where the caller said nothing usable. Counted separately from a
     *  fault: silence is the person, a fault is us. */
    fun recordSilentTurn() = silentTurns.incrementAndGet()

    val turnCount: Long get() = turns.get()
    val bargeInCount: Long get() = bargeIns.get()
    val faultCount: Long get() = faults.get()
    val silentTurnCount: Long get() = silentTurns.get()

    /**
     * The MEDIAN, not the mean. One 9-second turn while a model loaded drags a
     * mean far enough to hide thirty good ones, and the median is what a person
     * on the call actually experienced.
     */
    val medianFirstTokenMs: Double? get() = median(firstTokenMs)
    val medianTurnMs: Double? get() = median(turnMs)

    private fun median(values: List<Double>): Double? = synchronized(values) {
        if (values.isEmpty()) return null
        val sorted = values.sorted()
        val mid = sorted.size / 2
        return if (sorted.size % 2 == 1) sorted[mid] else (sorted[mid - 1] + sorted[mid]) / 2
    }

    fun reset() {
        turns.set(0); bargeIns.set(0); faults.set(0); silentTurns.set(0)
        synchronized(firstTokenMs) { firstTokenMs.clear() }
        synchronized(turnMs) { turnMs.clear() }
    }
}
