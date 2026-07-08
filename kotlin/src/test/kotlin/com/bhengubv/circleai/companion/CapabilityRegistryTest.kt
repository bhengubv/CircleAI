// CapabilityRegistryTest.kt
//
// Verifies ExternalCapabilityRegistry mirrors the C# registry: the full set of
// entries is present, lookup by id is case-insensitive, filtering by package
// works, and every entry is well-formed (non-blank id/license/strategy/package,
// at least one value bullet).

package com.bhengubv.circleai.companion

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class CapabilityRegistryTest {

    @Test
    fun `registry has the full set of 30 capabilities`() {
        assertEquals(30, ExternalCapabilityRegistry.all.size)
    }

    @Test
    fun `find is case-insensitive and returns null for unknown ids`() {
        assertNotNull(ExternalCapabilityRegistry.find("HippoRAG"))
        assertNotNull(ExternalCapabilityRegistry.find("hipporag"))
        assertEquals("HippoRAG", ExternalCapabilityRegistry.find("hipporag")!!.id)
        assertNull(ExternalCapabilityRegistry.find("does-not-exist"))
    }

    @Test
    fun `byPackage filters case-insensitively`() {
        val speech = ExternalCapabilityRegistry.byPackage("CircleAI.Speech")
        assertEquals(setOf("Amphion", "yapsnap"), speech.map { it.id }.toSet())
        // Two entries land in CircleAI.Inference (airllm, shard).
        assertEquals(setOf("airllm", "shard"), ExternalCapabilityRegistry.byPackage("circleai.inference").map { it.id }.toSet())
    }

    @Test
    fun `every entry is well-formed`() {
        for (e in ExternalCapabilityRegistry.all) {
            assertTrue(e.id.isNotBlank(), "id blank")
            assertTrue(e.license.isNotBlank(), "license blank for ${e.id}")
            assertTrue(e.strategy in setOf("vendor", "pattern-port", "wrap"), "bad strategy for ${e.id}: ${e.strategy}")
            assertTrue(e.targetPackage.startsWith("CircleAI."), "bad package for ${e.id}: ${e.targetPackage}")
            assertTrue(e.valueBullets.isNotEmpty(), "no bullets for ${e.id}")
        }
    }

    @Test
    fun `known entry details match the spec`() {
        val claudeMem = ExternalCapabilityRegistry.find("claude-mem")!!
        assertEquals("thedotmack/claude-mem", claudeMem.repo)
        assertEquals("MIT", claudeMem.license)
        assertEquals("pattern-port", claudeMem.strategy)
        assertEquals("CircleAI.Memory", claudeMem.targetPackage)
        assertEquals(10, claudeMem.valueBullets.size)
    }
}
