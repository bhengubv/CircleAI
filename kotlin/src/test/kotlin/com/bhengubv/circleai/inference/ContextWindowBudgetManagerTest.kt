// ContextWindowBudgetManagerTest.kt
//
// Verifies CircleAI.Inference.ContextWindowBudgetManager: fill-ratio tracking,
// eviction threshold signalling, eviction-count maths, and validation.

package com.bhengubv.circleai.inference

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ContextWindowBudgetManagerTest {

    @Test
    fun `records exchanges and tracks remaining and fill ratio`() {
        val m = ContextWindowBudgetManager(contextSize = 1000)
        m.recordExchange(200, 100)
        assertEquals(300, m.usedTokens)
        assertEquals(700, m.remainingTokens)
        assertEquals(0.3, m.fillRatio, 1e-9)
    }

    @Test
    fun `should evict crosses at the threshold`() {
        val m = ContextWindowBudgetManager(contextSize = 100, evictionThreshold = 0.85)
        m.recordExchange(80, 0)
        assertFalse(m.shouldEvict)
        m.recordExchange(5, 0) // now 85%
        assertTrue(m.shouldEvict)
    }

    @Test
    fun `eviction count drops back to target fill`() {
        val m = ContextWindowBudgetManager(contextSize = 1000)
        m.recordExchange(900, 0)
        // target 0.50 → keep 500 → evict 400
        assertEquals(400, m.calculateEvictionCount(0.50))
        // already below target → 0
        m.reset()
        m.recordExchange(100, 0)
        assertEquals(0, m.calculateEvictionCount(0.50))
    }

    @Test
    fun `reset zeroes the counter`() {
        val m = ContextWindowBudgetManager(contextSize = 100)
        m.recordExchange(50, 10)
        m.reset()
        assertEquals(0, m.usedTokens)
    }

    @Test
    fun `constructor and record validate their inputs`() {
        assertFailsWith<IllegalArgumentException> { ContextWindowBudgetManager(0) }
        assertFailsWith<IllegalArgumentException> { ContextWindowBudgetManager(10, 1.5) }
        val m = ContextWindowBudgetManager(10)
        assertFailsWith<IllegalArgumentException> { m.recordExchange(-1, 0) }
        assertFailsWith<IllegalArgumentException> { m.calculateEvictionCount(2.0) }
    }
}
