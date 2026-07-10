// SecurityOptions.kt
//
// Kotlin port of src/CircleAI.Security/SecurityOptions.cs.
//
// Configuration model for the AI Security Layer.
//
// All threshold values are trust scores in the [0, 1] range.
// Lower score = more compromised. Thresholds must satisfy:
//   quarantineThreshold < avoidNodeThreshold < elevateMonitoringThreshold

package com.bhengubv.circleai.security

import java.time.Duration

/**
 * Configures thresholds, decay rates, and event retention for the AI Security
 * Layer. Pass to [NodeTrustRegistry] and [SecurityLayerService].
 *
 * Mutable var properties mirror the C# settable POCO so hosts can tune values
 * before wiring the services.
 */
class SecurityOptions {
    /**
     * Trust score below which monitoring is elevated for the node.
     * Default: 0.75 — a 25 % trust loss triggers closer observation.
     */
    var elevateMonitoringThreshold: Double = 0.75

    /**
     * Trust score below which the node is excluded from routing.
     * Default: 0.50 — half trust lost -> route around the node.
     */
    var avoidNodeThreshold: Double = 0.50

    /**
     * Trust score at or below which the node is hard-blocked (quarantined).
     * Default: 0.25 — severe compromise -> no traffic to or from the node.
     */
    var quarantineThreshold: Double = 0.25

    /**
     * Passive trust recovery per second when no adverse events occur.
     * Default: 0.001 ≈ full recovery from zero in ~16 minutes of clean
     * behaviour.
     */
    var recoveryRatePerSecond: Double = 0.001

    /**
     * Sliding window used for pattern-based indicator detection (e.g. repeated
     * auth attempts). Events outside this window are ignored for pattern
     * analysis. Default: 5 minutes.
     */
    var eventWindow: Duration = Duration.ofMinutes(5)

    /**
     * Maximum security events retained per node. Oldest are dropped first.
     * Default: 100.
     */
    var maxEventsPerNode: Int = 100

    /**
     * Trust score assigned to nodes on first observation.
     * Default: 1.0 (full trust until evidence says otherwise).
     */
    var initialTrustScore: Double = 1.0
}
