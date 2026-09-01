package com.bhengubv.circleai.mesh

import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Peer selection and the registry. */
class MeshSelectionTest {

    private fun ad(
        peer: String,
        tier: Int = 1,
        kv: Int = 1000,
        latency: Int? = null,
        model: String = "m",
        ageSeconds: Long = 0,
    ) = MeshCapabilityAdvertisement(
        peer, model, kv, tier, 4096, Instant.now().minusSeconds(ageSeconds), latency)

    private fun turn(prompt: String = "hello", out: Int = 100) =
        OffloadTurn.create("m", prompt, out, correlationId = "corr-1")!!

    @Test fun `the strongest tier wins`() {
        val best = MeshOffloadOptions.defaultSelectPeer(
            listOf(ad("weak", tier = 1), ad("strong", tier = 3), ad("mid", tier = 2)))
        assertEquals("strong", best!!.peerId)
    }

    @Test fun `within a tier the fastest wins`() {
        val best = MeshOffloadOptions.defaultSelectPeer(
            listOf(ad("slow", tier = 2, latency = 200), ad("fast", tier = 2, latency = 20)))
        assertEquals("fast", best!!.peerId)
    }

    // Unknown is NOT fast. A peer that reports no hint must not beat one that
    // measured itself.
    @Test fun `a peer with no latency hint sorts last`() {
        val best = MeshOffloadOptions.defaultSelectPeer(
            listOf(ad("unknown", tier = 2, latency = null), ad("measured", tier = 2, latency = 500)))
        assertEquals("measured", best!!.peerId)
    }

    @Test fun `spare budget breaks an otherwise exact tie`() {
        val best = MeshOffloadOptions.defaultSelectPeer(
            listOf(ad("small", tier = 2, kv = 100, latency = 50),
                   ad("roomy", tier = 2, kv = 9000, latency = 50)))
        assertEquals("roomy", best!!.peerId)
    }

    @Test fun `no candidates selects nobody`() {
        assertNull(MeshOffloadOptions.defaultSelectPeer(emptyList()))
    }

    // Four characters to the token for the prompt; the output budget is exact.
    @Test fun `the default estimate counts prompt and output`() {
        val o = MeshOffloadOptions()
        assertEquals(150, o.estimateKvTokens(turn("x".repeat(400), 50)))
        assertEquals(0, o.estimateKvTokens(turn("", 0)))
    }

    @Test fun `a stale advertisement is filtered out`() {
        val r = InMemoryMeshCapabilityRegistry()
        r.upsert(ad("fresh", ageSeconds = 1))
        r.upsert(ad("stale", ageSeconds = 600))
        assertEquals(listOf("fresh"), r.list(staleAfterSeconds = 30.0).map { it.peerId })
        assertEquals(2, r.list().size, "no staleness filter returns everything")
    }

    @Test fun `find filters by model and spare budget`() {
        val r = InMemoryMeshCapabilityRegistry()
        r.upsert(ad("a", kv = 100, model = "m"))
        r.upsert(ad("b", kv = 9000, model = "m"))
        r.upsert(ad("c", kv = 9000, model = "other"))
        assertEquals(listOf("b"), r.find("m", minFreeKvTokens = 500).map { it.peerId })
    }

    @Test fun `find returns the most capable peer first`() {
        val r = InMemoryMeshCapabilityRegistry()
        r.upsert(ad("small", kv = 100))
        r.upsert(ad("big", kv = 9000))
        assertEquals("big", r.find("m").first().peerId)
    }

    @Test fun `removing a peer is idempotent`() {
        val r = InMemoryMeshCapabilityRegistry()
        r.upsert(ad("a"))
        assertTrue(r.remove("a"))
        assertFalse(r.remove("a"))
    }

    @Test fun `a turn needs a model id`() {
        assertNull(OffloadTurn.create("  ", "x"))
        assertTrue(OffloadTurn.create("m", "x") != null)
    }
}
