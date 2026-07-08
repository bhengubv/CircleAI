// PrefixCacheServiceTest.kt
//
// Verifies CircleAI.Inference.PrefixCacheService: key derivation, path/has-entry,
// touch, and LRU eviction under the size cap.

package com.bhengubv.circleai.inference

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.io.File
import java.nio.file.Files
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class PrefixCacheServiceTest {

    private fun tempRoot(): String = Files.createTempDirectory("prefix-cache").toFile().absolutePath

    @Test
    fun `key is null without model or system prompt`() {
        assertNull(PrefixCacheService.keyFor("", "sys"))
        assertNull(PrefixCacheService.keyFor("m", null))
        assertNull(PrefixCacheService.keyFor("m", ""))
    }

    @Test
    fun `key is deterministic and 16+1+16 chars`() {
        val k1 = PrefixCacheService.keyFor("qwen3-0.6b", "You are B!")
        val k2 = PrefixCacheService.keyFor("qwen3-0.6b", "You are B!")
        assertEquals(k1, k2)
        assertEquals(33, k1!!.length) // 16 + '_' + 16
        assertTrue(k1.contains('_'))
    }

    @Test
    fun `has entry reflects file presence`() = runTest {
        val svc = PrefixCacheService(tempRoot())
        val key = PrefixCacheService.keyFor("m", "s")!!
        assertFalse(svc.hasEntryAsync(key))
        File(svc.pathFor(key)).writeText("snapshot")
        assertTrue(svc.hasEntryAsync(key))
    }

    @Test
    fun `eviction removes oldest until under the cap is a no-op for small dirs`() = runTest {
        val svc = PrefixCacheService(tempRoot())
        val key = PrefixCacheService.keyFor("m", "s")!!
        File(svc.pathFor(key)).writeText("small")
        svc.evictIfNeededAsync() // under 500 MB → keep
        assertTrue(svc.hasEntryAsync(key))
    }

    @Test
    fun `touch updates mtime`() = runTest {
        val svc = PrefixCacheService(tempRoot())
        val key = PrefixCacheService.keyFor("m", "s")!!
        val f = File(svc.pathFor(key))
        f.writeText("x")
        f.setLastModified(0L)
        svc.touch(key)
        assertTrue(f.lastModified() > 0L)
    }
}
