// CompanionStateSyncEngineTest.kt
//
// Verifies the Announce/Request/Push convergence protocol end-to-end over the
// in-process loopback hub, plus WriteLocal semantics (fresh HLC version, local
// persist, live Push broadcast) and content-hash stability — matching the C#
// CompanionStateSyncEngine reference.

package com.bhengubv.circleai.memory.sync

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class CompanionStateSyncEngineTest {

    private fun engineOn(
        hub: InProcessSyncHub,
        nodeId: String,
        nodeShortId: Long,
        store: ISyncableEntryStore = InMemorySyncableEntryStore(),
        physicalMs: Long = 1000L,
    ): Pair<CompanionStateSyncEngine, ISyncableEntryStore> {
        val channel = InProcessCompanionStateChannel(hub, nodeId)
        val clock = HybridLogicalClock(nodeShortId) { physicalMs }
        val engine = CompanionStateSyncEngine(channel, store, clock) { Instant.EPOCH }
        return engine to store
    }

    @Test
    fun `write local stamps a version and persists`() = runTest {
        val hub = InProcessSyncHub()
        val (engine, store) = engineOn(hub, "A", 1)
        val entry = engine.writeLocal("PersonaState", "u1", "{\"v\":1}")
        assertTrue(entry.version > 0)
        assertEquals("PersonaState", entry.entityType)
        assertEquals("u1", entry.entityId)
        val stored = store.get("PersonaState", "u1")
        assertNotNull(stored)
        assertEquals(entry.version, stored!!.version)
        engine.close()
    }

    @Test
    fun `content hash is deterministic sha-256 hex of payload`() = runTest {
        val hub = InProcessSyncHub()
        val (engine, _) = engineOn(hub, "A", 1)
        val entry = engine.writeLocal("T", "id", "hello")
        // SHA-256("hello") lowercase hex.
        assertEquals(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            entry.contentHash,
        )
        engine.close()
    }

    @Test
    fun `two started peers converge after a local write on one`() = runTest {
        val hub = InProcessSyncHub()
        val (a, storeA) = engineOn(hub, "A", 1, physicalMs = 1000L)
        val (b, storeB) = engineOn(hub, "B", 2, physicalMs = 1000L)
        a.start()
        b.start()

        // A writes; the live Push reaches B synchronously.
        a.writeLocal("PersonaState", "u1", "{\"name\":\"ada\"}")

        val onB = storeB.get("PersonaState", "u1")
        assertNotNull(onB)
        assertEquals("{\"name\":\"ada\"}", onB!!.payload)
        // A retains its own copy identically.
        assertEquals(storeA.get("PersonaState", "u1")!!.version, onB.version)

        a.close()
        b.close()
    }

    @Test
    fun `announce-request-push back-fills a peer that joined late`() = runTest {
        val hub = InProcessSyncHub()
        // A already has data before B exists.
        val (a, _) = engineOn(hub, "A", 1, physicalMs = 1000L)
        a.start()
        a.writeLocal("PersonaState", "u1", "v1") // no peers yet -> only A has it

        val (b, storeB) = engineOn(hub, "B", 2, physicalMs = 1000L)
        b.start()
        assertNull(storeB.get("PersonaState", "u1"))

        // B announces its (empty) vector; A does nothing on that. So A drives
        // convergence by announcing its vector -> B requests -> A pushes.
        a.syncNow()

        val onB = storeB.get("PersonaState", "u1")
        assertNotNull(onB)
        assertEquals("v1", onB!!.payload)

        a.close()
        b.close()
    }

    @Test
    fun `tombstone propagates to peers`() = runTest {
        val hub = InProcessSyncHub()
        val (a, _) = engineOn(hub, "A", 1, physicalMs = 1000L)
        val (b, storeB) = engineOn(hub, "B", 2, physicalMs = 1000L)
        a.start()
        b.start()

        a.writeLocal("ConversationState", "s1", "partial")
        assertNotNull(storeB.get("ConversationState", "s1"))

        a.writeLocal("ConversationState", "s1", "", isTombstone = true)
        val onB = storeB.get("ConversationState", "s1")
        assertNotNull(onB)
        assertTrue(onB!!.isTombstone)

        a.close()
        b.close()
    }

    @Test
    fun `disposed engine rejects further writes`() = runTest {
        val hub = InProcessSyncHub()
        val (a, _) = engineOn(hub, "A", 1)
        a.close()
        var threw = false
        try {
            a.writeLocal("T", "id", "x")
        } catch (_: IllegalStateException) {
            threw = true
        }
        assertTrue(threw)
    }
}
