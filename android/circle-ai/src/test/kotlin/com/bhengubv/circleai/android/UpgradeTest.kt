// UpgradeTest.kt
//
// Parity test — 7 upgrade-detection cases + correlation ID autosynth.
// Matches C# ModelUpgradeTests byte-for-byte. JUnit 4 to mesh with the
// existing Android library test config.

package com.bhengubv.circleai.android

import com.bhengubv.circleai.android.agents.peer.AgentMessage
import com.bhengubv.circleai.android.agents.peer.AgentMessageKind
import com.bhengubv.circleai.android.catalog.ModelEntry
import com.bhengubv.circleai.android.catalog.ModelRegistry
import com.bhengubv.circleai.android.models.BundleFile
import com.bhengubv.circleai.android.models.UpgradeReason
import com.bhengubv.circleai.android.registry.ModelRegistryService
import com.bhengubv.circleai.android.registry.writeInstalledManifest
import kotlinx.coroutines.runBlocking
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals
import kotlin.test.assertNull

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
    @get:Rule val tmpFolder = TemporaryFolder()

    @Test
    fun case1_notInstalled_yieldsEmpty() = runBlocking {
        val tmp = tmpFolder.newFolder().path
        val svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.0.0",
            BundleFile("config.json", "abc", 100),
            BundleFile("llm.mnn", "def", 200)))
        assertEquals(0, svc.checkForUpgradesAsync(tmp).size)
    }

    @Test
    fun case2_noManifestFilesExist_yieldsUnknown() = runBlocking {
        val tmp = tmpFolder.newFolder().path
        val mDir = File(tmp, "Qwen3-0.6B-MNN").apply { mkdirs() }
        File(mDir, "config.json").writeText("stub")
        val svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.0.0",
            BundleFile("config.json", "abc", 100)))
        val ups = svc.checkForUpgradesAsync(tmp)
        assertEquals(1, ups.size)
        assertEquals(UpgradeReason.UNKNOWN, ups[0].reason)
        assertNull(ups[0].installedVersion)
    }

    @Test
    fun case3_allShasMatch_yieldsEmpty() = runBlocking {
        val tmp = tmpFolder.newFolder().path
        writeInstalledManifest(File(tmp, "Qwen3-0.6B-MNN").path,
            "Qwen3-0.6B-MNN", "1.0.0", "MNN/Qwen3-0.6B-MNN",
            listOf(BundleFile("config.json", "abc", 100), BundleFile("llm.mnn", "def", 200)))
        val svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.0.0",
            BundleFile("config.json", "abc", 100),
            BundleFile("llm.mnn", "def", 200)))
        assertEquals(0, svc.checkForUpgradesAsync(tmp).size)
    }

    @Test
    fun case4_versionDriftOnly_yieldsVersionChangedZeroBytes() = runBlocking {
        val tmp = tmpFolder.newFolder().path
        writeInstalledManifest(File(tmp, "Qwen3-0.6B-MNN").path,
            "Qwen3-0.6B-MNN", "1.0.0", "MNN/Qwen3-0.6B-MNN",
            listOf(BundleFile("config.json", "abc", 100), BundleFile("llm.mnn", "def", 200)))
        val svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.1.0",
            BundleFile("config.json", "abc", 100),
            BundleFile("llm.mnn", "def", 200)))
        val ups = svc.checkForUpgradesAsync(tmp)
        assertEquals(1, ups.size)
        assertEquals(UpgradeReason.VERSION_CHANGED, ups[0].reason)
        assertEquals(0L, ups[0].estimatedDownloadBytes)
    }

    @Test
    fun case5_shaDriftOnly_yieldsShaChangedOnlyDriftedBytes() = runBlocking {
        val tmp = tmpFolder.newFolder().path
        writeInstalledManifest(File(tmp, "Qwen3-0.6B-MNN").path,
            "Qwen3-0.6B-MNN", "1.0.0", "MNN/Qwen3-0.6B-MNN",
            listOf(BundleFile("config.json", "abc", 100), BundleFile("llm.mnn", "OLD", 200)))
        val svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.0.0",
            BundleFile("config.json", "abc", 100),
            BundleFile("llm.mnn", "NEW", 200)))
        val ups = svc.checkForUpgradesAsync(tmp)
        assertEquals(1, ups.size)
        assertEquals(UpgradeReason.SHA_CHANGED, ups[0].reason)
        assertEquals(200L, ups[0].estimatedDownloadBytes)
    }

    @Test
    fun case6_versionAndShaDrift_yieldsBothTotalBytes() = runBlocking {
        val tmp = tmpFolder.newFolder().path
        writeInstalledManifest(File(tmp, "Qwen3-0.6B-MNN").path,
            "Qwen3-0.6B-MNN", "1.0.0", "MNN/Qwen3-0.6B-MNN",
            listOf(BundleFile("config.json", "abc", 100), BundleFile("llm.mnn", "OLD", 200)))
        val svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "2.0.0",
            BundleFile("config.json", "abc2", 100),
            BundleFile("llm.mnn", "NEW", 200)))
        val ups = svc.checkForUpgradesAsync(tmp)
        assertEquals(1, ups.size)
        assertEquals(UpgradeReason.BOTH, ups[0].reason)
        assertEquals(300L, ups[0].estimatedDownloadBytes)
    }

    @Test
    fun case7_writeInstalledManifestRoundTrip_yieldsEmpty() = runBlocking {
        val tmp = tmpFolder.newFolder().path
        writeInstalledManifest(File(tmp, "Qwen3-0.6B-MNN").path,
            "Qwen3-0.6B-MNN", "1.0.0", "MNN/Qwen3-0.6B-MNN",
            listOf(BundleFile("config.json", "abc", 100), BundleFile("llm.mnn", "def", 200)))
        val svc = makeRegistry(makeEntry("Qwen3-0.6B-MNN", "1.0.0",
            BundleFile("config.json", "abc", 100),
            BundleFile("llm.mnn", "def", 200)))
        assertEquals(0, svc.checkForUpgradesAsync(tmp).size)
    }

    @Test
    fun agentMessageCorrelationIdAutosynth() {
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
