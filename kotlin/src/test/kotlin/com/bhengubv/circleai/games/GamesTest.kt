// GamesTest.kt — verifies the CircleAI.Games port against the C# reference.

package com.bhengubv.circleai.games

import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class GamesTest {

    @Test
    fun `timer loop ticks subscribers and stops`() = runBlocking {
        val loop = TimerGameLoop()
        assertEquals("timer", loop.backendId)
        val ticks = AtomicInteger(0)
        val token = loop.subscribe { ticks.incrementAndGet() }

        assertFailsWith<IllegalArgumentException> { loop.startAsync(0.0) }
        loop.startAsync(200.0) // ~5ms period
        assertFailsWith<IllegalStateException> { loop.startAsync() } // already started
        delay(120)
        loop.stopAsync()
        val after = ticks.get()
        assertTrue(after > 0, "expected at least one tick, got $after")

        token.close()
        delay(60)
        // No hard assertion on exact counts (timing), but disposal must not throw.
        loop.disposeAsync()
    }

    @Test
    fun `input map fans events to subscribers`() = runBlocking {
        val map = InMemoryInputMap()
        assertEquals("in-memory", map.backendId)
        val seen = mutableListOf<String>()
        val token = map.subscribe { ev -> synchronized(seen) { seen.add(ev.action) } }
        map.raise(InputEvent("jump", mapOf("power" to "2")))
        map.raise(InputEvent("dash"))
        delay(50)
        assertEquals(setOf("jump", "dash"), synchronized(seen) { seen.toSet() })
        token.close()
        map.raise(InputEvent("ignored"))
        delay(30)
        assertTrue("ignored" !in synchronized(seen) { seen.toList() })
    }

    @Test
    fun `scene graph add remove snapshot`() = runTest {
        val sg = InMemorySceneGraph()
        assertEquals("in-memory", sg.backendId)
        sg.addAsync(SceneNode("n1", "sprite", 1.0, 2.0, 0.0))
        sg.addAsync(SceneNode("n2", "light", 0.0, 0.0, 5.0))
        assertEquals(setOf("n1", "n2"), sg.snapshotAsync().map { it.nodeId }.toSet())
        sg.removeAsync("n1")
        assertEquals(setOf("n2"), sg.snapshotAsync().map { it.nodeId }.toSet())
        assertFailsWith<IllegalArgumentException> { sg.addAsync(SceneNode("  ", "x", 0.0, 0.0, 0.0)) }
        assertFailsWith<IllegalArgumentException> { sg.removeAsync(" ") }
    }

    @Test
    fun `null implementations are inert`() = runTest {
        val loop = NullGameLoop()
        assertEquals("null", loop.backendId)
        loop.startAsync()
        loop.subscribe { }.close()
        loop.stopAsync()
        loop.disposeAsync()

        assertEquals("null", NullInputMap.Instance.backendId)
        NullInputMap.Instance.subscribe { }.close()

        val sg = NullSceneGraph.Instance
        assertEquals("null", sg.backendId)
        sg.addAsync(SceneNode("n", "k", 0.0, 0.0, 0.0))
        sg.removeAsync("n")
        assertTrue(sg.snapshotAsync().isEmpty())
    }
}
