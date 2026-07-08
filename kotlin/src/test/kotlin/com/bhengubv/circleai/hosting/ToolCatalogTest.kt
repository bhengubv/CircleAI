// ToolCatalogTest.kt
//
// Verifies InMemoryToolCatalog against the C# reference: upsert idempotence,
// case-insensitive get/remove, provider filtering, the keyword-substring search
// scoring (name 5 / tags 3 / description 2) + ordering + topK, and importFrom.

package com.bhengubv.circleai.hosting

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ToolCatalogTest {

    private fun tool(name: String, desc: String = "", provider: String = "local", tags: List<String>? = null) =
        ToolDescriptor(name, desc, provider, tags = tags)

    @Test
    fun `upsert is idempotent by name and get is case-insensitive`() = runTest {
        val cat = InMemoryToolCatalog()
        cat.upsertAsync(tool("Gmail.Send", "send email"))
        cat.upsertAsync(tool("Gmail.Send", "send email v2"))
        assertEquals(1, cat.count)
        assertEquals("send email v2", cat.getAsync("gmail.send")?.description)
    }

    @Test
    fun `remove is case-insensitive and idempotent`() = runTest {
        val cat = InMemoryToolCatalog()
        cat.upsertAsync(tool("A"))
        assertTrue(cat.removeAsync("a"))
        assertFalse(cat.removeAsync("a"))
        assertNull(cat.getAsync("A"))
    }

    @Test
    fun `list by provider filters case-insensitively`() = runTest {
        val cat = InMemoryToolCatalog()
        cat.upsertAsync(tool("t1", provider = "gmail"))
        cat.upsertAsync(tool("t2", provider = "github"))
        cat.upsertAsync(tool("t3", provider = "Gmail"))
        assertEquals(setOf("t1", "t3"), cat.listByProvider("GMAIL").map { it.name }.toSet())
    }

    @Test
    fun `search scores name over tags over description and orders by score`() = runTest {
        val cat = InMemoryToolCatalog()
        cat.upsertAsync(tool("weather", "gets forecast", tags = null))          // name hit: 5
        cat.upsertAsync(tool("umbrella", "weather advice", tags = null))         // desc hit: 2
        cat.upsertAsync(tool("raincoat", "outdoor gear", tags = listOf("weather"))) // tag hit: 3

        val results = cat.search("weather", topK = 10)
        assertEquals(listOf("weather", "raincoat", "umbrella"), results.map { it.name })
    }

    @Test
    fun `search honours topK and empty query returns empty`() = runTest {
        val cat = InMemoryToolCatalog()
        cat.upsertAsync(tool("alpha_tool", "alpha"))
        cat.upsertAsync(tool("beta_tool", "alpha"))
        cat.upsertAsync(tool("gamma_tool", "alpha"))
        assertEquals(2, cat.search("alpha", topK = 2).size)
        assertTrue(cat.search("   ", topK = 10).isEmpty())
        assertTrue(cat.search("alpha", topK = 0).isEmpty())
    }

    private class FakeProvider(private val tools: List<ToolDescriptor>) : IToolProvider {
        override val providerId = "fake"
        override suspend fun discoverAsync() = tools
        override suspend fun isAvailableAsync() = true
    }

    @Test
    fun `importFrom drains provider into catalog`() = runTest {
        val cat = InMemoryToolCatalog()
        val n = cat.importFromAsync(FakeProvider(listOf(tool("x"), tool("y"))))
        assertEquals(2, n)
        assertEquals(2, cat.count)
    }
}
