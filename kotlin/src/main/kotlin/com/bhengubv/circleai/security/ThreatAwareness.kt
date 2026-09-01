// ThreatAwareness.kt
//
// The three awareness seams, and the sink that carries a verdict to the
// watchdog.
//
// AWARENESS IS NOT ENFORCEMENT, and the split is deliberate. These report what
// they SEE; something else decides what to do about it. Collapsing the two would
// put a component that can read your files in charge of blocking them, and the
// blast radius of a false positive goes from a notification to a device that
// will not open its owner's documents.
//
// Ported from src/CircleAI.Security.Antibodies/Awareness/*.cs and
// src/CircleAI.Security.Defense/Integration/WatchdogThreatSink.cs.

package com.bhengubv.circleai.security

import java.time.Instant

/** How bad, on one scale, so three sources can be compared. */
enum class ThreatSeverity { Informational, Low, Medium, High, Critical }

data class ThreatObservation(
    val source: String,
    val severity: ThreatSeverity,
    /** Said to a PERSON. This is the line that appears in a notification, so it
     *  names the thing and not the rule that fired. */
    val summary: String,
    val detail: String? = null,
    val at: Instant = Instant.now()
)

/**
 * Whether an address of the person's has turned up in a known breach.
 *
 * ADDRESSES ARE NEVER SENT WHOLE. A breach-check service that receives an email
 * address learns that the address exists and that its owner is worried —
 * implementations use a k-anonymity prefix, and this contract exists so that
 * choice sits with the implementation rather than being assumed away.
 */
interface IBreachExposureAwareness {
    suspend fun check(identifier: String): List<ThreatObservation>
}

/** Whether a file on this device looks dangerous. */
interface IFileThreatAwareness {
    /**
     * Examines a file and reports. Returns empty when it has nothing to say —
     * "no observations" and "clean" are the same answer here, and pretending to
     * certify a file as safe is a promise no local check can keep.
     */
    suspend fun examine(path: String): List<ThreatObservation>
}

/** Whether the network this device is on looks dangerous. */
interface INetworkThreatAwareness {
    suspend fun observe(): List<ThreatObservation>
}

/**
 * Carries observations to the watchdog.
 *
 * DEDUPLICATED BY (source, summary) within a window, because the same condition
 * observed every thirty seconds is one problem, not a hundred. A person who gets
 * a hundred notifications turns notifications off, and then gets none of the
 * ones that mattered.
 */
class WatchdogThreatSink(
    private val forward: (ThreatObservation) -> Unit,
    private val windowMillis: Long = 5 * 60 * 1000,
    private val now: () -> Long = { System.currentTimeMillis() }
) {
    private val lastSeen = HashMap<String, Long>()

    /** Returns whether it was forwarded, so a caller can tell "nothing new" from
     *  "nothing observed" — which look identical from outside. */
    @Synchronized
    fun submit(observation: ThreatObservation): Boolean {
        val key = "${observation.source}|${observation.summary}"
        val t = now()
        val previous = lastSeen[key]

        // CRITICAL IS NEVER SUPPRESSED. Everything else can wait for the window;
        // something that needs acting on now must not be silenced because a
        // similar thing happened four minutes ago.
        if (observation.severity != ThreatSeverity.Critical &&
            previous != null && t - previous < windowMillis
        ) return false

        lastSeen[key] = t
        forward(observation)
        return true
    }

    @Synchronized
    fun reset() = lastSeen.clear()
}
