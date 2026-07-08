// SyncBridgesTest.kt
//
// Verifies the three sync bridges against the C# reference:
//   PersonaStateSyncBridge      — save persists + pushes; encode/decode round-trip
//   LoraAdapterSyncBridge       — publish reads file + pushes; tryWrite decodes + writes bytes
//   CompanionConversationSyncBridge — publish/terminate + tryDecode

package com.bhengubv.circleai.memory.sync

import com.bhengubv.circleai.memory.IPersonaStore
import com.bhengubv.circleai.memory.PersonaState
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import java.io.File
import java.nio.file.Path
import java.time.Instant
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class SyncBridgesTest {

    private class FakePersonaStore : IPersonaStore {
        val saved = ArrayList<PersonaState>()
        override suspend fun loadAsync(userId: String): PersonaState = PersonaState(userId)
        override suspend fun saveAsync(persona: PersonaState) { saved.add(persona) }
    }

    private fun freshEngine(nodeId: String = "A"): Pair<CompanionStateSyncEngine, ISyncableEntryStore> {
        val hub = InProcessSyncHub()
        val channel = InProcessCompanionStateChannel(hub, nodeId)
        val store = InMemorySyncableEntryStore()
        val clock = HybridLogicalClock(1) { 1000L }
        val engine = CompanionStateSyncEngine(channel, store, clock) { Instant.EPOCH }
        return engine to store
    }

    // -- PersonaStateSyncBridge ---------------------------------------------

    @Test
    fun `persona save persists locally and pushes an entry`() = runTest {
        val (engine, store) = freshEngine()
        engine.start()
        val fakeStore = FakePersonaStore()
        val bridge = PersonaStateSyncBridge(fakeStore, engine)

        val persona = PersonaState("user-7").apply {
            verbosity = "brief"
            formality = "formal"
            preferredLocale = "en-ZA"
            topicWeights["finance"] = 2.5f
            disfavouredTopics.add("sport")
            totalInteractions = 12
            positiveSignals = 9
            negativeSignals = 3
        }

        bridge.save(persona)
        assertEquals(1, fakeStore.saved.size)
        val entry = store.get(PersonaStateSyncBridge.EntityType, "user-7")
        assertNotNull(entry)

        val decoded = PersonaStateSyncBridge.tryDecode(entry!!)
        assertNotNull(decoded)
        assertEquals("user-7", decoded!!.userId)
        assertEquals("brief", decoded.verbosity)
        assertEquals("formal", decoded.formality)
        assertEquals("en-ZA", decoded.preferredLocale)
        assertEquals(2.5f, decoded.topicWeights["finance"])
        assertTrue(decoded.disfavouredTopics.contains("sport"))
        assertEquals(12, decoded.totalInteractions)
        assertEquals(9, decoded.positiveSignals)
        assertEquals(3, decoded.negativeSignals)

        engine.close()
    }

    @Test
    fun `persona tryDecode rejects tombstone and wrong type`() = runTest {
        val tombstone = SyncableEntry(
            PersonaStateSyncBridge.EntityType, "u", 1, true, "h", "", "n", Instant.EPOCH,
        )
        assertNull(PersonaStateSyncBridge.tryDecode(tombstone))

        val wrongType = SyncableEntry("Other", "u", 1, false, "h", "{}", "n", Instant.EPOCH)
        assertNull(PersonaStateSyncBridge.tryDecode(wrongType))
    }

    // -- LoraAdapterSyncBridge ----------------------------------------------

    @Test
    fun `lora publish reads file and tryWrite round-trips the bytes`(@TempDir dir: Path) = runTest {
        val (engine, store) = freshEngine()
        engine.start()
        val bridge = LoraAdapterSyncBridge(engine)

        val src = File(dir.toFile(), "adapter.bin")
        val bytes = byteArrayOf(1, 2, 3, 4, 5, 9, 8, 7)
        src.writeBytes(bytes)

        bridge.publish("personal-u1", src.absolutePath, stepCount = 42)
        val entry = store.get(LoraAdapterSyncBridge.EntityType, "personal-u1")
        assertNotNull(entry)

        val dest = File(dir.toFile(), "nested/out.bin")
        val snapshot = LoraAdapterSyncBridge.tryWrite(entry!!, dest.absolutePath)
        assertNotNull(snapshot)
        assertEquals("personal-u1", snapshot!!.adapterId)
        assertEquals(42L, snapshot.stepCount)
        assertTrue(dest.exists())
        assertContentEquals(bytes, dest.readBytes())

        engine.close()
    }

    @Test
    fun `lora tryWrite rejects tombstone and wrong type`() = runTest {
        val tombstone = SyncableEntry(
            LoraAdapterSyncBridge.EntityType, "a", 1, true, "h", "", "n", Instant.EPOCH,
        )
        assertNull(LoraAdapterSyncBridge.tryWrite(tombstone, "ignored"))
        val wrongType = SyncableEntry("Other", "a", 1, false, "h", "{}", "n", Instant.EPOCH)
        assertNull(LoraAdapterSyncBridge.tryWrite(wrongType, "ignored"))
    }

    @Test
    fun `lora publish rejects missing file`() = runTest {
        val (engine, _) = freshEngine()
        engine.start()
        val bridge = LoraAdapterSyncBridge(engine)
        var threw = false
        try {
            bridge.publish("id", "C:/nonexistent/definitely/missing.bin", 1)
        } catch (_: java.io.FileNotFoundException) {
            threw = true
        }
        assertTrue(threw)
        engine.close()
    }

    // -- CompanionConversationSyncBridge ------------------------------------

    @Test
    fun `conversation publish and tryDecode round-trip`() = runTest {
        val (engine, store) = freshEngine()
        engine.start()
        val bridge = CompanionConversationSyncBridge(engine)

        val delta = ConversationStateDelta(
            sessionId = "sess-1",
            userText = "what's the weather",
            assistantText = "Checking",
            isTurnComplete = false,
            startedAtUtc = Instant.EPOCH.toString(),
            updatedAtUtc = Instant.EPOCH.plusSeconds(2).toString(),
        )
        bridge.publish(delta)

        val entry = store.get(CompanionConversationSyncBridge.EntityType, "sess-1")
        assertNotNull(entry)
        val decoded = CompanionConversationSyncBridge.tryDecode(entry!!)
        assertNotNull(decoded)
        assertEquals("sess-1", decoded!!.sessionId)
        assertEquals("what's the weather", decoded.userText)
        assertEquals("Checking", decoded.assistantText)
        assertEquals(false, decoded.isTurnComplete)

        engine.close()
    }

    @Test
    fun `conversation terminate writes a tombstone`() = runTest {
        val (engine, store) = freshEngine()
        engine.start()
        val bridge = CompanionConversationSyncBridge(engine)
        bridge.publish(
            ConversationStateDelta("sess-2", "hi", "", true, Instant.EPOCH.toString(), Instant.EPOCH.toString())
        )
        bridge.terminate("sess-2")
        val entry = store.get(CompanionConversationSyncBridge.EntityType, "sess-2")
        assertNotNull(entry)
        assertTrue(entry!!.isTombstone)
        assertNull(CompanionConversationSyncBridge.tryDecode(entry))
        engine.close()
    }
}
