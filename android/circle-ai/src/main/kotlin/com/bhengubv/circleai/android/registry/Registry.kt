// Registry.kt
//
// ModelRegistryService + checkForUpgradesAsync + writeInstalledManifest.

package com.bhengubv.circleai.android.registry

import com.bhengubv.circleai.android.catalog.ModelEntry
import com.bhengubv.circleai.android.catalog.ModelRegistry
import com.bhengubv.circleai.android.catalog.ModelScopeCatalogClient
import com.bhengubv.circleai.android.models.BundleFile
import com.bhengubv.circleai.android.models.InstalledManifest
import com.bhengubv.circleai.android.models.UpgradeInfo
import com.bhengubv.circleai.android.models.UpgradeReason
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.longOrNull
import java.io.File
import java.nio.charset.StandardCharsets
import java.time.Instant

class ModelRegistryService(
    private val catalogClient: ModelScopeCatalogClient? = null,
) {
    private var registry: ModelRegistry? = null

    init {
        if (catalogClient != null) {
            try {
                registry = catalogClient.loadFromDisk()
            } catch (_: Throwable) {
                registry = null
            }
        }
    }

    fun setRegistry(reg: ModelRegistry) {
        registry = reg
    }

    val allModels: List<ModelEntry> get() = registry?.models ?: emptyList()

    fun getLatestModel(name: String): ModelEntry? =
        registry?.models?.firstOrNull { it.name.equals(name, ignoreCase = true) }

    suspend fun primeFromCatalogAsync() {
        val client = catalogClient ?: return
        try {
            val reg = client.getCachedCatalogAsync(acceptStaleOnError = true)
            if (reg != null) registry = reg
        } catch (_: Throwable) {
            // best-effort
        }
    }

    suspend fun checkForUpgradesAsync(storageDirectory: String): List<UpgradeInfo> = withContext(Dispatchers.IO) {
        require(storageDirectory.isNotBlank()) { "storageDirectory is required" }
        val now = Instant.now()
        val upgrades = mutableListOf<UpgradeInfo>()

        for (entry in allModels) {
            val modelDir = File(storageDirectory, entry.name)
            if (!modelDir.isDirectory) continue

            val manifestFile = File(modelDir, "installed.json")
            val manifest = readManifest(manifestFile)
            if (manifest == null) {
                upgrades.add(
                    UpgradeInfo(
                        modelId = entry.name,
                        installedVersion = null,
                        availableVersion = entry.version,
                        reason = UpgradeReason.UNKNOWN,
                        estimatedDownloadBytes = entry.totalBytes,
                        detectedAt = now,
                    )
                )
                continue
            }

            val versionChanged = manifest.version != entry.version
            val (shaChanged, driftBytes) = compareBundleSha(manifest.files, entry.bundleFiles)
            if (!versionChanged && !shaChanged) continue

            val reason = when {
                versionChanged && shaChanged -> UpgradeReason.BOTH
                versionChanged -> UpgradeReason.VERSION_CHANGED
                else -> UpgradeReason.SHA_CHANGED
            }
            upgrades.add(
                UpgradeInfo(
                    modelId = entry.name,
                    installedVersion = manifest.version,
                    availableVersion = entry.version,
                    reason = reason,
                    estimatedDownloadBytes = driftBytes,
                    detectedAt = now,
                )
            )
        }
        upgrades
    }
}

fun writeInstalledManifest(
    modelDir: String,
    modelId: String,
    version: String,
    repo: String?,
    bundleFiles: List<BundleFile>,
) {
    try {
        val dir = File(modelDir)
        dir.mkdirs()
        val totalBytes = bundleFiles.sumOf { if (it.sizeBytes > 0) it.sizeBytes else 0L }
        val sb = StringBuilder("{")
        sb.append("\"model_id\":").append(quote(modelId)).append(',')
        sb.append("\"version\":").append(quote(version)).append(',')
        sb.append("\"repo\":").append(if (repo != null) quote(repo) else "null").append(',')
        sb.append("\"total_bytes\":").append(totalBytes).append(',')
        sb.append("\"installed_at_utc\":").append(quote(Instant.now().toString())).append(',')
        sb.append("\"files\":[")
        for ((i, f) in bundleFiles.withIndex()) {
            if (i > 0) sb.append(',')
            sb.append('{')
            sb.append("\"name\":").append(quote(f.name)).append(',')
            sb.append("\"sha256\":").append(quote(f.sha256)).append(',')
            sb.append("\"size_bytes\":").append(f.sizeBytes)
            sb.append('}')
        }
        sb.append("]}")
        File(dir, "installed.json").writeText(sb.toString(), StandardCharsets.UTF_8)
    } catch (_: Throwable) {
        // best-effort
    }
}

// ── helpers ──────────────────────────────────────────────────────────────

private val parserJson = Json { ignoreUnknownKeys = true; isLenient = true }

private fun readManifest(file: File): InstalledManifest? {
    if (!file.exists()) return null
    return try {
        val obj = parserJson.parseToJsonElement(file.readText(StandardCharsets.UTF_8)).jsonObject
        val files: List<BundleFile> = obj["files"]?.jsonArray?.map { el ->
            val fo = el.jsonObject
            BundleFile(
                name = fo["name"]?.jsonPrimitive?.contentOrNull ?: "",
                sha256 = fo["sha256"]?.jsonPrimitive?.contentOrNull ?: "",
                sizeBytes = fo["size_bytes"]?.jsonPrimitive?.longOrNull ?: 0L,
            )
        } ?: emptyList()
        val installedRaw = obj["installed_at_utc"]?.jsonPrimitive?.contentOrNull
        val installedAt: Instant = if (installedRaw != null) Instant.parse(installedRaw) else Instant.now()
        InstalledManifest(
            modelId = obj["model_id"]?.jsonPrimitive?.contentOrNull ?: "",
            version = obj["version"]?.jsonPrimitive?.contentOrNull ?: "",
            repo = obj["repo"]?.jsonPrimitive?.contentOrNull,
            totalBytes = obj["total_bytes"]?.jsonPrimitive?.longOrNull ?: 0L,
            files = files,
            installedAtUtc = installedAt,
        )
    } catch (_: Throwable) {
        null
    }
}

private fun compareBundleSha(installed: List<BundleFile>?, available: List<BundleFile>): Pair<Boolean, Long> {
    if (available.isEmpty()) return false to 0L
    val byName = (installed ?: emptyList()).associateBy { it.name }
    var drift = false
    var bytes = 0L
    for (av in available) {
        val inst = byName[av.name]
        if (inst == null || !inst.sha256.equals(av.sha256, ignoreCase = true)) {
            drift = true
            bytes += av.sizeBytes
        }
    }
    return drift to bytes
}

private fun quote(s: String): String {
    val sb = StringBuilder(s.length + 2)
    sb.append('"')
    for (c in s) {
        when (c) {
            '"' -> sb.append("\\\"")
            '\\' -> sb.append("\\\\")
            '\n' -> sb.append("\\n")
            '\r' -> sb.append("\\r")
            '\t' -> sb.append("\\t")
            else -> if (c.code < 0x20) {
                sb.append(String.format("\\u%04x", c.code))
            } else sb.append(c)
        }
    }
    sb.append('"')
    return sb.toString()
}
