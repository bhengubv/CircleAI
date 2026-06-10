// UpgradeTest.kt
//
// Parity test — 7 upgrade-detection cases matching the C# ModelUpgradeTests.

package com.bhengubv.circleai

import com.bhengubv.circleai.agents.peer.AgentMessage
import com.bhengubv.circleai.agents.peer.AgentMessageKind
import com.bhengubv.circleai.catalog.ModelEntry
import com.bhengubv.circleai.catalog.ModelRegistry
import com.bhengubv.circleai.models.BundleFile
import com.bhengubv.circleai.models.UpgradeReason
import com.bhengubv.circleai.registry.ModelRegistryService
import com.bhengubv.circleai.registry.writeInstalledManifest
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import java.io.File
import java.nio.file.Path
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

private fun makeRegistry(vararg entries: ModelEntry): ModelRegistryService {
    val svc = ModelRegistryService()
    svc.setRegistry(ModelRegistry("https://stub", Instant.now(), entries.toList()))
    return svc
}

private fun makeEntry(name: String, version: String, vararg files: BundleFile): ModelEntry =
    ModelEntry(
        name = name, version = version, quantization = "Q4",
        repo = "MNN/$name",
        totalBytes = files.sumOf { it.sizeBytes },
        bundleFiles = files.toList(),
    )

class UpgradeTest {

    @Test
    fun `Case 1 — not installed yields empty`(@TempDir tmp: Path) = runBlocking {
        val svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.0.0",
            BundleFile("config.json", "abc", 100),
            BundleFile("llm.mnn", "def", 200)))
        assertEquals(0, svc.checkForUpgradesAsync(tmp.toString()).size)
    }

    @Test
    fun `Case 2 — no manifest, files exist yields Unknown`(@TempDir tmp: Path) = runBlocking {
        val mDir = File(tmp.toFile(), "Qwen3-0.6B-MNN").apply { mkdirs() }
        File(mDir, "config.json").writeText("stub")
        val svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.0.0",
            BundleFile("config.json", "abc", 100)))
        val ups = svc.checkForUpgradesAsync(tmp.toString())
        assertEquals(1, ups.size)
        assertEquals(UpgradeReason.UNKNOWN, ups[0].reason)
        assertNull(ups[0].installedVersion)
    }

    @Test
    fun `Case 3 — all SHAs match yields empty`(@TempDir tmp: Path) = runBlocking {
        writeInstalledManifest(File(tmp.toFile(), "Qwen3-0.6B-MNN").path,
            "Qwen3-0.6B-MNN", "1.0.0", "MNN/Qwen3-0.6B-MNN",
            listOf(BundleFile("config.json", "abc", 100), BundleFile("llm.mnn", "def", 200)))
        val svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.0.0",
            BundleFile("config.json", "abc", 100),
            BundleFile("llm.mnn", "def", 200)))
        assertEquals(0, svc.checkForUpgradesAsync(tmp.toString()).size)
    }

    @Test
    fun `Case 4 — Version drift only yields VersionChanged, 0 bytes`(@TempDir tmp: Path) = runBlocking {
        writeInstalledManifest(File(tmp.toFile(), "Qwen3-0.6B-MNN").path,
            "Qwen3-0.6B-MNN", "1.0.0", "MNN/Qwen3-0.6B-MNN",
            listOf(BundleFile("config.json", "abc", 100), BundleFile("llm.mnn", "def", 200)))
        val svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.1.0",
            BundleFile("config.json", "abc", 100),
            BundleFile("llm.mnn", "def", 200)))
        val ups = svc.checkForUpgradesAsync(tmp.toString())
        assertEquals(1, ups.size)
        assertEquals(UpgradeReason.VERSION_CHANGED, ups[0].reason)
        assertEquals(0L, ups[0].estimatedDownloadBytes)
    }

    @Test
    fun `Case 5 — SHA drift only yields ShaChanged, only drifted bytes`(@TempDir tmp: Path) = runBlocking {
        writeInstalledManifest(File(tmp.toFile(), "Qwen3-0.6B-MNN").path,
            "Qwen3-0.6B-MNN", "1.0.0", "MNN/Qwen3-0.6B-MNN",
            listOf(BundleFile("config.json", "abc", 100), BundleFile("llm.mnn", "OLD", 200)))
        val svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.0.0",
            BundleFile("config.json", "abc", 100),
            BundleFile("llm.mnn", "NEW", 200)))
        val ups = svc.checkForUpgradesAsync(tmp.toString())
        assertEquals(1, ups.size)
        assertEquals(UpgradeReason.SHA_CHANGED, ups[0].reason)
        assertEquals(200L, ups[0].estimatedDownloadBytes)
    }

    @Test
    fun `Case 6 — Version + SHA yields Both, total bytes`(@TempDir tmp: Path) = runBlocking {
        writeInstalledManifest(File(tmp.toFile(), "Qwen3-0.6B-MNN").path,
            "Qwen3-0.6B-MNN", "1.0.0", "MNN/Qwen3-0.6B-MNN",
            listOf(BundleFile("config.json", "abc", 100), BundleFile("llm.mnn", "OLD", 200)))
        val svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "2.0.0",
            BundleFile("config.json", "abc2", 100),
            BundleFile("llm.mnn", "NEW", 200)))
        val ups = svc.checkForUpgradesAsync(tmp.toString())
        assertEquals(1, ups.size)
        assertEquals(UpgradeReason.BOTH, ups[0].reason)
        assertEquals(300L, ups[0].estimatedDownloadBytes)
    }

    @Test
    fun `Case 7 — writeInstalledManifest round-trip yields empty`(@TempDir tmp: Path) = runBlocking {
        writeInstalledManifest(File(tmp.toFile(), "Qwen3-0.6B-MNN").path,
            "Qwen3-0.6B-MNN", "1.0.0", "MNN/Qwen3-0.6B-MNN",
            listOf(BundleFile("config.json", "abc", 100), BundleFile("llm.mnn", "def", 200)))
        val svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.0.0",
            BundleFile("config.json", "abc", 100),
            BundleFile("llm.mnn", "def", 200)))
        assertEquals(0, svc.checkForUpgradesAsync(tmp.toString()).size)
    }

    @Test
    fun `AgentMessage correlation ID autosynth`() {
        val m1 = AgentMessage.create(AgentMessageKind.GREET, "a", "b", "text/plain",
            byteArrayOf(1, 2, 3), byteArrayOf(4, 5, 6))
        assertEquals(32, m1.correlationId.length)
        val m2 = AgentMessage.create(AgentMessageKind.GREET, "a", "b", "text/plain",
            byteArrayOf(1, 2, 3), byteArrayOf(4, 5, 6), correlationId = "trace-abc")
        assertEquals("trace-abc", m2.correlationId)
        val m3 = AgentMessage.create(AgentMessageKind.GREET, "a", "b", "text/plain",
            byteArrayOf(1, 2, 3), byteArrayOf(4, 5, 6))
        assertNotEquals(m1.correlationId, m3.correlationId)
    }
}
