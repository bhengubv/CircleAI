// SyncableEntryStoreTest.kt
//
// Verifies InMemorySyncableEntryStore apply rules (higher version wins, tie ->
// higher content hash, tombstone beats non-tombstone at equal version),
// getSince ordering, and state-vector high-watermarks — matching the C#
// reference apply semantics exactly.

package com.bhengubv.circleai.memory.sync

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class SyncableEntryStoreTest {

    private fun entry(
        type: String = "PersonaState",
        id: String = "u1",
        version: Long,
        tombstone: Boolean = false,
        hash: String = "aa",
        payload: String = "p",
    ) = SyncableEntry(type, id, version, tombstone, hash, payload, "node", Instant.EPOCH)

    @Test
    fun `first apply of a key succeeds`() = runTest {
        val store = InMemorySyncableEntryStore()
        assertTrue(store.apply(entry(version = 10)))
        assertNotNull(store.get("PersonaState", "u1"))
    }

    @Test
    fun `higher version wins, lower is rejected`() = runTest {
        val store = InMemorySyncableEntryStore()
        assertTrue(store.apply(entry(version = 10)))
        assertTrue(store.apply(entry(version = 20)))
        assertFalse(store.apply(entry(version = 15)))
        assertEquals(20L, store.get("PersonaState", "u1")!!.version)
    }

    @Test
    fun `equal version resolves by higher content hash`() = runTest {
        val store = InMemorySyncableEntryStore()
        assertTrue(store.apply(entry(version = 10, hash = "bb")))
        // Lower hash at equal version loses.
        assertFalse(store.apply(entry(version = 10, hash = "aa")))
        // Higher hash at equal version wins.
        assertTrue(store.apply(entry(version = 10, hash = "cc")))
        assertEquals("cc", store.get("PersonaState", "u1")!!.contentHash)
    }

    @Test
    fun `tombstone beats non-tombstone at equal version`() = runTest {
        val store = InMemorySyncableEntryStore()
        assertTrue(store.apply(entry(version = 10, tombstone = false, hash = "zz")))
        // Tombstone wins even though its hash is lower.
        assertTrue(store.apply(entry(version = 10, tombstone = true, hash = "aa")))
        assertTrue(store.get("PersonaState", "u1")!!.isTombstone)
        // A non-tombstone at the same version cannot overwrite the tombstone.
        assertFalse(store.apply(entry(version = 10, tombstone = false, hash = "zz")))
    }

    @Test
    fun `getSince returns only newer entries ascending`() = runTest {
        val store = InMemorySyncableEntryStore()
        store.apply(entry(id = "a", version = 10))
        store.apply(entry(id = "b", version = 30))
        store.apply(entry(id = "c", version = 20))
        val since = store.getSince("PersonaState", 10)
        assertEquals(listOf(20L, 30L), since.map { it.version })
    }

    @Test
    fun `state vector reports per-type high watermark sorted by type`() = runTest {
        val store = InMemorySyncableEntryStore()
        store.apply(entry(type = "PersonaState", id = "a", version = 10))
        store.apply(entry(type = "PersonaState", id = "b", version = 40))
        store.apply(entry(type = "CoreMemory", id = "c", version = 5))
        val vector = store.getStateVector()
        assertEquals(listOf("CoreMemory", "PersonaState"), vector.map { it.entityType })
        assertEquals(40L, vector.first { it.entityType == "PersonaState" }.maxKnownVersion)
        assertEquals(5L, vector.first { it.entityType == "CoreMemory" }.maxKnownVersion)
    }

    @Test
    fun `get returns null for unknown key`() = runTest {
        val store = InMemorySyncableEntryStore()
        assertNull(store.get("PersonaState", "missing"))
    }
}
