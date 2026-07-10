// MeshCapabilityRegistryTest.kt
//
// Verifies the RT-12 v1 mesh-capability registry + broadcaster:
//   - upsert replaces per-peer, blank peerId rejected
//   - remove is idempotent and reports whether anything was removed
//   - list honours the staleAfter cutoff (with an injected clock)
//   - find filters by model (case-insensitive) + minFreeKvTokens + staleness,
//     sorted by spare budget descending
//   - NullMeshCapabilityBroadcaster is a no-op; LocalRegistryBroadcaster mirrors
//     into the registry

package com.bhengubv.circleai.aethernet

import com.bhengubv.circleai.device.DeviceTier
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class MeshCapabilityRegistryTest {

    private val t0: Instant = Instant.parse("2026-07-10T00:00:00Z")

    private fun ad(
        peer: String,
        model: String = "Qwen3-1.7B-MNN",
        freeKv: Int = 2048,
        at: Instant = t0,
        tier: DeviceTier = DeviceTier.PHONE,
    ) = MeshCapabilityAdvertisement(
        peerId = peer,
        modelId = model,
        freeKvTokens = freeKv,
        tier = tier,
        contextWindowTokens = 4096,
        advertisedAtUtc = at,
    )

    @Test
    fun `upsert stores and replaces per peer`() = runTest {
        val reg = InMemoryMeshCapabilityRegistry(nowUtc = { t0 })
        reg.upsert(ad("p1", freeKv = 1000))
        reg.upsert(ad("p1", freeKv = 3000)) // replaces
        val all = reg.list()
        assertEquals(1, all.size)
        assertEquals(3000, all.single().freeKvTokens)
    }

    @Test
    fun `upsert rejects blank peer id`() = runTest {
        val reg = InMemoryMeshCapabilityRegistry()
        assertFailsWith<IllegalArgumentException> { reg.upsert(ad("   ")) }
    }

    @Test
    fun `remove is idempotent`() = runTest {
        val reg = InMemoryMeshCapabilityRegistry()
        reg.upsert(ad("p1"))
        assertTrue(reg.remove("p1"))
        assertFalse(reg.remove("p1")) // already gone
        assertTrue(reg.list().isEmpty())
    }

    @Test
    fun `list filters stale entries against injected clock`() = runTest {
        val now = t0.plus(Duration.ofSeconds(120))
        val reg = InMemoryMeshCapabilityRegistry(nowUtc = { now })
        reg.upsert(ad("fresh", at = now.minus(Duration.ofSeconds(30))))
        reg.upsert(ad("stale", at = now.minus(Duration.ofSeconds(90))))

        val within60 = reg.list(staleAfter = Duration.ofSeconds(60))
        assertEquals(listOf("fresh"), within60.map { it.peerId })

        // No filter → both.
        assertEquals(2, reg.list().size)
    }

    @Test
    fun `find matches model case-insensitively and sorts by free budget desc`() = runTest {
        val reg = InMemoryMeshCapabilityRegistry(nowUtc = { t0 })
        reg.upsert(ad("a", model = "Qwen3-1.7B-MNN", freeKv = 500))
        reg.upsert(ad("b", model = "qwen3-1.7b-mnn", freeKv = 4000)) // different case
        reg.upsert(ad("c", model = "Llama-3B", freeKv = 9000)) // different model

        val found = reg.find("QWEN3-1.7B-MNN", minFreeKvTokens = 1000)
        // Only b qualifies (a is below min, c is a different model).
        assertEquals(listOf("b"), found.map { it.peerId })

        val all = reg.find("Qwen3-1.7B-MNN")
        // Both a and b, sorted by budget desc.
        assertEquals(listOf("b", "a"), all.map { it.peerId })
    }

    @Test
    fun `find honours staleness cutoff`() = runTest {
        val now = t0.plus(Duration.ofSeconds(120))
        val reg = InMemoryMeshCapabilityRegistry(nowUtc = { now })
        reg.upsert(ad("fresh", at = now.minus(Duration.ofSeconds(10))))
        reg.upsert(ad("old", at = now.minus(Duration.ofSeconds(100))))

        val found = reg.find("Qwen3-1.7B-MNN", staleAfter = Duration.ofSeconds(60))
        assertEquals(listOf("fresh"), found.map { it.peerId })
    }

    @Test
    fun `find rejects blank model id`() = runTest {
        val reg = InMemoryMeshCapabilityRegistry()
        assertFailsWith<IllegalArgumentException> { reg.find(" ") }
    }

    @Test
    fun `null broadcaster is a no-op`() = runTest {
        // Must not throw and must not touch any registry.
        NullMeshCapabilityBroadcaster.broadcast(ad("p1"))
    }

    @Test
    fun `local registry broadcaster mirrors into the registry`() = runTest {
        val reg = InMemoryMeshCapabilityRegistry(nowUtc = { t0 })
        val broadcaster = LocalRegistryBroadcaster(reg)
        broadcaster.broadcast(ad("self", freeKv = 1234))
        val found = reg.find("Qwen3-1.7B-MNN")
        assertEquals(1, found.size)
        assertEquals("self", found.single().peerId)
        assertEquals(1234, found.single().freeKvTokens)
    }
}
