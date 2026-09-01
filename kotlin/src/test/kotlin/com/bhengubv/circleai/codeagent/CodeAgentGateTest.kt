package com.bhengubv.circleai.codeagent

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** The device gate and the model catalogue. */
class CodeAgentGateTest {

    private fun probe(ramGb: Double, storageGb: Double, tier: Int = 3) = CodingDeviceProbe(
        ramAvailableBytes = (ramGb * 1024 * 1024 * 1024).toLong(),
        storageFreeBytes = (storageGb * 1024 * 1024 * 1024).toLong(),
        tier = tier,
    )

    private fun model(
        id: String,
        b: Int,
        ram: Double = 6.0,
        storage: Double = 4.0,
        caps: Set<String> = setOf("tools", "reasoning", "longContext"),
    ) = CodingModelDescriptor(id, b, ram, storage, 4_000_000_000L, "abc123", caps)

    @Test fun `a phone is refused by design`() {
        val plan = CodingCapabilityPlanner().planForCoding(probe(1.5, 40.0, tier = 1))
        assertFalse(plan.isAvailable)
        assertEquals(CodingSelectionQuality.UNAVAILABLE, plan.quality)
        assertTrue(plan.reason.contains("Unavailable by design"))
    }

    @Test fun `a capable device with no catalogue says so instead of pretending`() {
        val plan = CodingCapabilityPlanner().planForCoding(probe(16.0, 100.0))
        assertFalse(plan.isAvailable)
        assertTrue(plan.reason.contains("no on-device coding model is installed"))
    }

    @Test fun `thin storage is refused even with plenty of ram`() {
        val cat = InMemoryCodingModelCatalog(listOf(model("m", 7)))
        val plan = CodingCapabilityPlanner(cat).planForCoding(probe(16.0, 2.0))
        assertFalse(plan.isAvailable)
        assertTrue(plan.reason.contains("free storage"))
    }

    @Test fun `a capable device with a fitting model passes`() {
        val cat = InMemoryCodingModelCatalog(listOf(model("qwen-coder-7b", 7)))
        val plan = CodingCapabilityPlanner(cat).planForCoding(probe(16.0, 100.0))
        assertTrue(plan.isAvailable)
        assertEquals(CodingSelectionQuality.GOOD, plan.quality)
        assertEquals("qwen-coder-7b", plan.modelId)
    }

    @Test fun `the biggest fitting model wins`() {
        val cat = InMemoryCodingModelCatalog(listOf(model("small-3b", 3), model("big-7b", 7)))
        assertEquals("big-7b", CodingCapabilityPlanner(cat).planForCoding(probe(16.0, 100.0)).modelId)
    }

    @Test fun `a model missing a required capability does not fit`() {
        val cat = InMemoryCodingModelCatalog(
            listOf(model("no-tools-7b", 7, caps = setOf("reasoning", "longContext")))
        )
        assertEquals(
            CodingSelectionQuality.NOTHING_FITS,
            CodingCapabilityPlanner(cat).planForCoding(probe(16.0, 100.0)).quality,
        )
    }

    @Test fun `a too small model does not fit`() {
        val cat = InMemoryCodingModelCatalog(listOf(model("tiny-1b", 1)))
        assertEquals(
            CodingSelectionQuality.NOTHING_FITS,
            CodingCapabilityPlanner(cat).planForCoding(probe(16.0, 100.0)).quality,
        )
    }

    // The headroom is the point: 85% of free RAM is what a model may claim.
    // 10 GiB free is 10.7 GB decimal, which is 9.1 GB usable after headroom.
    @Test fun `ram fit uses the headroom not the raw free figure`() {
        val cat = InMemoryCodingModelCatalog(listOf(model("needs-10", 7, ram = 10.0)))
        assertEquals(
            CodingSelectionQuality.NOTHING_FITS,
            CodingCapabilityPlanner(cat).planForCoding(probe(10.0, 100.0)).quality,
        )
    }

    @Test fun `a model without a hash is refused`() {
        val bad = CodingModelDescriptor("unverified", 7, 6.0, 4.0, 1L, "  ", setOf("tools"))
        assertFailsWith<UnverifiableModelException> { InMemoryCodingModelCatalog().add(bad) }
    }

    @Test fun `adding the same model twice is idempotent`() {
        val cat = InMemoryCodingModelCatalog()
        cat.add(model("m", 7))
        cat.add(model("M", 7))
        assertEquals(1, cat.available.size)
    }

    @Test fun `an empty catalogue is empty`() {
        assertTrue(EmptyCodingModelCatalog.available.isEmpty())
        assertEquals("empty", EmptyCodingModelCatalog.backendId)
    }
}
