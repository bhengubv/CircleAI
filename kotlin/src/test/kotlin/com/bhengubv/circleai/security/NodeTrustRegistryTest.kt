// NodeTrustRegistryTest.kt
//
// Verifies the per-peer trust store: initial score, degradation clamps to
// [0,1], event history bounding + windowing, passive recovery, and that the
// trust-score-update Flow buffers pre-subscription writes (unbounded channel).

package com.bhengubv.circleai.security

import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class NodeTrustRegistryTest {

    private fun opts(block: SecurityOptions.() -> Unit = {}) = SecurityOptions().apply(block)

    private fun event(
        node: String,
        kind: PeerSecurityEventKind = PeerSecurityEventKind.AuthAttempt,
        level: PeerThreatLevel = PeerThreatLevel.Medium,
        at: Instant = Instant.now(),
        desc: String = "e",
    ) = PeerSecurityEvent(node, kind, level, desc, "test", at)

    @Test
    fun `unknown peer reports initial trust score`() {
        val reg = NodeTrustRegistry(opts { initialTrustScore = 1.0 })
        assertEquals(1.0, reg.getTrustScore("nobody"), 1e-9)
    }

    @Test
    fun `getOrCreate seeds at initial trust score`() {
        val reg = NodeTrustRegistry(opts { initialTrustScore = 0.8 })
        val entry = reg.getOrCreate("p1")
        assertEquals(0.8, entry.trustScore, 1e-9)
        assertTrue(reg.allNodeIds.contains("p1"))
    }

    @Test
    fun `applyDegradation lowers and clamps to zero`() {
        val reg = NodeTrustRegistry(opts { initialTrustScore = 1.0 })
        val (prev, cur) = reg.applyDegradation(event("p1"), 0.3)
        assertEquals(1.0, prev, 1e-9)
        assertEquals(0.7, cur, 1e-9)

        // Huge degradation clamps to 0.
        val (_, cur2) = reg.applyDegradation(event("p1"), 5.0)
        assertEquals(0.0, cur2, 1e-9)
        assertEquals(0.0, reg.getTrustScore("p1"), 1e-9)
    }

    @Test
    fun `event history is bounded by maxEventsPerNode`() {
        val reg = NodeTrustRegistry(opts { maxEventsPerNode = 3 })
        repeat(5) { reg.applyDegradation(event("p1", desc = "e$it"), 0.01) }
        val entry = reg.getOrCreate("p1")
        assertEquals(3, entry.recentEvents.size)
        // Oldest dropped first -> newest three remain.
        assertEquals(listOf("e2", "e3", "e4"), entry.recentEvents.map { it.description })
    }

    @Test
    fun `getRecentEvents filters by event window`() {
        val reg = NodeTrustRegistry(opts { eventWindow = Duration.ofMinutes(5) })
        val old = Instant.now().minus(Duration.ofMinutes(10))
        reg.applyDegradation(event("p1", at = old, desc = "stale"), 0.01)
        reg.applyDegradation(event("p1", desc = "fresh"), 0.01)

        val recent = reg.getRecentEvents("p1")
        assertEquals(listOf("fresh"), recent.map { it.description })
    }

    @Test
    fun `getRecentEvents is empty for unknown peer`() {
        val reg = NodeTrustRegistry(opts())
        assertTrue(reg.getRecentEvents("ghost").isEmpty())
    }

    @Test
    fun `applyRecovery heals below-full peers and skips full ones`() {
        val reg = NodeTrustRegistry(opts { recoveryRatePerSecond = 0.1; initialTrustScore = 1.0 })
        reg.applyDegradation(event("hurt"), 0.5) // -> 0.5
        reg.getOrCreate("full") // stays at 1.0

        reg.applyRecovery(Duration.ofSeconds(2)) // +0.2
        assertEquals(0.7, reg.getTrustScore("hurt"), 1e-9)
        assertEquals(1.0, reg.getTrustScore("full"), 1e-9)
    }

    @Test
    fun `applyRecovery never exceeds 1_0`() {
        val reg = NodeTrustRegistry(opts { recoveryRatePerSecond = 1.0 })
        reg.applyDegradation(event("p1"), 0.1) // -> 0.9
        reg.applyRecovery(Duration.ofSeconds(5)) // +5 clamps to 1.0
        assertEquals(1.0, reg.getTrustScore("p1"), 1e-9)
    }

    @Test
    fun `trust score updates stream buffers a pre-subscription write`() = runTest {
        val reg = NodeTrustRegistry(opts())
        // Publish BEFORE any collector attaches — unbounded channel must retain it.
        reg.applyDegradation(event("p1", desc = "boot"), 0.2)

        val first = reg.trustScoreUpdates.first()
        assertEquals("p1", first.nodeId)
        assertEquals("boot", first.reason)
        assertEquals(1.0, first.previousScore, 1e-9)
        assertEquals(0.8, first.newScore, 1e-9)
    }

    @Test
    fun `recovery publishes passive-recovery updates`() = runTest {
        val reg = NodeTrustRegistry(opts { recoveryRatePerSecond = 0.1 })
        reg.applyDegradation(event("p1", desc = "hit"), 0.5)
        reg.applyRecovery(Duration.ofSeconds(1))

        val updates = reg.trustScoreUpdates.take(2).toList()
        assertEquals("hit", updates[0].reason)
        assertEquals("passive-recovery", updates[1].reason)
    }

    @Test
    fun `no update published when score does not move`() = runTest {
        val reg = NodeTrustRegistry(opts())
        // Degradation of 0 (e.g. threat None already filtered upstream) -> no move.
        reg.applyDegradation(event("p1", desc = "noop"), 0.0)
        // Then a real move so the stream has exactly one item to read.
        reg.applyDegradation(event("p1", desc = "real"), 0.1)

        val first = reg.trustScoreUpdates.first()
        assertEquals("real", first.reason, "the zero-delta change must not have been published")
    }
}
