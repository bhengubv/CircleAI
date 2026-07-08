// HybridLogicalClockTest.kt
//
// Verifies the HLC against the C# reference: composition/decomposition round
// trips, monotonic ticks within and across milliseconds, logical overflow
// bumping physical, node-id packing, and observe() advancing past a peer.

package com.bhengubv.circleai.memory.sync

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class HybridLogicalClockTest {

    @Test
    fun `compose and decompose round-trip`() {
        val v = HybridLogicalClock.compose(physicalMs = 1_700_000_000_000L, logical = 42, nodeShortId = 7)
        val (p, l, n) = HybridLogicalClock.decompose(v)
        assertEquals(1_700_000_000_000L, p)
        assertEquals(42L, l)
        assertEquals(7L, n)
    }

    @Test
    fun `node id packs into low 6 bits`() {
        val clock = HybridLogicalClock(nodeShortId = 63) { 1000L }
        val v = clock.tick()
        assertEquals(63L, HybridLogicalClock.decompose(v).nodeShortId)
    }

    @Test
    fun `rejects out-of-range node id`() {
        var threw = false
        try {
            HybridLogicalClock(nodeShortId = 64)
        } catch (_: IllegalArgumentException) {
            threw = true
        }
        assertTrue(threw)
    }

    @Test
    fun `ticks are strictly increasing within the same millisecond`() {
        val clock = HybridLogicalClock(nodeShortId = 1) { 5000L } // frozen time
        val a = clock.tick()
        val b = clock.tick()
        val c = clock.tick()
        assertTrue(b > a)
        assertTrue(c > b)
        // Physical was captured at construction (5000); each tick at the same
        // frozen ms increments the logical counter, starting from 1.
        assertEquals(1L, HybridLogicalClock.decompose(a).logical)
        assertEquals(2L, HybridLogicalClock.decompose(b).logical)
        assertEquals(3L, HybridLogicalClock.decompose(c).logical)
    }

    @Test
    fun `advancing physical resets logical`() {
        var now = 1000L
        val clock = HybridLogicalClock(nodeShortId = 2) { now }
        clock.tick() // now == lastPhysical (1000) -> logical 1
        val bumped = clock.tick() // still 1000 -> logical 2
        assertEquals(2L, HybridLogicalClock.decompose(bumped).logical)
        now = 1001L
        val next = clock.tick()
        assertEquals(1001L, HybridLogicalClock.decompose(next).physicalMs)
        assertEquals(0L, HybridLogicalClock.decompose(next).logical)
    }

    @Test
    fun `logical overflow bumps physical`() {
        val clock = HybridLogicalClock(nodeShortId = 0) { 2000L } // frozen
        var last = clock.tick()
        // Drive logical up to just under overflow then over.
        repeat(1024) { last = clock.tick() }
        val decoded = HybridLogicalClock.decompose(last)
        // Physical must have advanced past the frozen 2000 due to overflow.
        assertTrue(decoded.physicalMs > 2000L)
    }

    @Test
    fun `observe advances past a peer version`() {
        val clock = HybridLogicalClock(nodeShortId = 3) { 1000L }
        // Peer authored a version far in the future.
        val peer = HybridLogicalClock.compose(physicalMs = 9_000L, logical = 5, nodeShortId = 9)
        val observed = clock.observe(peer)
        val local = HybridLogicalClock.decompose(observed)
        assertEquals(9_000L, local.physicalMs)
        // logical = peer.logical + 1 since peer physical dominates
        assertEquals(6L, local.logical)
        assertEquals(3L, local.nodeShortId) // stamped with OUR node id
        // Subsequent tick stays monotonic above the observed version.
        assertTrue(clock.tick() > observed)
    }
}
