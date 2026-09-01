package com.bhengubv.circleai.mesh

import kotlinx.coroutines.test.runTest
import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/** The routing decision. */
class MeshRoutingTest {

    private fun ad(peer: String, tier: Int = 1, kv: Int = 1000) =
        MeshCapabilityAdvertisement(peer, "m", kv, tier, 4096, Instant.now(), null)

    private fun turn() = OffloadTurn.create("m", "hello", 100, correlationId = "corr-1")!!

    private class StubClient(private val answers: Map<String, OffloadResult>) : MeshOffloadClient {
        var attempts = 0
        override val isReady = true
        override suspend fun request(peerId: String, turn: OffloadTurn, timeoutSeconds: Double): OffloadResult {
            attempts++
            return answers[peerId] ?: OffloadResult.fail("no answer", OffloadServedBy.NONE)
        }
    }

    /** Deliberately reports NONE so the router has to label it. */
    private class StubFallback(private val text: String?) : LocalInferenceFallback {
        override suspend fun complete(turn: OffloadTurn): OffloadResult {
            if (text == null) throw IllegalStateException("boom")
            return OffloadResult(true, text, OffloadServedBy.NONE, null, 1, 1.0, null)
        }
    }

    private fun ok(peer: String, text: String) =
        OffloadResult(true, text, OffloadServedBy.REMOTE_PEER, peer, 1, 1.0, null)

    private val generous = MeshOffloadOptions(staleAfterSeconds = 3600.0)

    @Test fun `a working peer serves the turn`() = runTest {
        val registry = InMemoryMeshCapabilityRegistry().apply { upsert(ad("peer-1")) }
        val r = MeshOffloadRouter(
            registry, StubClient(mapOf("peer-1" to ok("peer-1", "from peer"))),
            StubFallback("from local"), generous,
        ).route(turn())
        assertTrue(r.success)
        assertEquals("from peer", r.outputText)
        assertEquals(OffloadServedBy.REMOTE_PEER, r.servedBy)
    }

    // Nobody in range is the NORMAL case, not an error.
    @Test fun `with no peers it answers locally`() = runTest {
        val r = MeshOffloadRouter(
            InMemoryMeshCapabilityRegistry(), StubClient(emptyMap()),
            StubFallback("from local"), generous,
        ).route(turn())
        assertTrue(r.success)
        assertEquals("from local", r.outputText)
        assertEquals(OffloadServedBy.LOCAL_FALLBACK, r.servedBy,
            "the router labels an unlabelled fallback")
    }

    @Test fun `a failing peer is retried on the next one`() = runTest {
        val registry = InMemoryMeshCapabilityRegistry().apply {
            upsert(ad("bad", tier = 3))
            upsert(ad("good", tier = 2))
        }
        val r = MeshOffloadRouter(
            registry,
            StubClient(mapOf("bad" to OffloadResult.fail("busy"), "good" to ok("good", "second try"))),
            StubFallback("from local"),
            generous.copy(maxPeerAttempts = 2),
        ).route(turn())
        assertEquals("second try", r.outputText)
    }

    // The attempt budget is a BUDGET: it must not try a third peer.
    @Test fun `it stops after the attempt budget and falls back`() = runTest {
        val registry = InMemoryMeshCapabilityRegistry()
        listOf(4, 3, 2).forEachIndexed { i, t -> registry.upsert(ad("p" + i, tier = t)) }
        val client = StubClient(emptyMap())

        val r = MeshOffloadRouter(
            registry, client, StubFallback("from local"), generous.copy(maxPeerAttempts = 2),
        ).route(turn())
        assertEquals("from local", r.outputText)
        assertEquals(2, client.attempts)
    }

    // A mesh that throws when nobody answers takes the app down with it.
    @Test fun `a throwing local fallback still returns a result`() = runTest {
        val r = MeshOffloadRouter(
            InMemoryMeshCapabilityRegistry(), StubClient(emptyMap()),
            StubFallback(null), generous,
        ).route(turn())
        assertFalse(r.success)
        assertNotNull(r.failureReason)
        assertTrue(r.failureReason!!.contains("Local fallback also failed"))
    }

    @Test fun `the null fallback admits it cannot serve`() = runTest {
        val r = NullLocalInferenceFallback.complete(turn())
        assertFalse(r.success)
        assertEquals(OffloadServedBy.NONE, r.servedBy)
        assertTrue(r.failureReason!!.contains("cannot serve locally"))
    }
}
