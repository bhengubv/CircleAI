// Catalog.kt
//
// ModelEntry + ModelRegistry + ModelScopeCatalogClient + signature verifier.
// Port of CircleAI.Core.Models.ModelScopeCatalogClient.

package com.bhengubv.circleai.android.catalog

import com.bhengubv.circleai.android.models.BundleFile
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.BufferedReader
import java.io.File
import java.io.InputStreamReader
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.nio.charset.StandardCharsets
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset

enum class CatalogSignatureResult { VALID, INVALID, MISSING, NOT_CONFIGURED }

interface ICatalogSignatureVerifier {
    fun verify(payload: ByteArray, signatureBase64: String?): CatalogSignatureResult
}

object NullCatalogSignatureVerifier : ICatalogSignatureVerifier {
    override fun verify(payload: ByteArray, signatureBase64: String?): CatalogSignatureResult =
        CatalogSignatureResult.NOT_CONFIGURED
}

enum class CatalogRefreshCadence { ON_STARTUP, DAILY, MANUAL, NEVER }

data class ModelScopeCatalogOptions(
    val baseUri: String = "https://www.modelscope.cn",
    val cacheDirectory: String = defaultCacheDir(),
    val cadence: CatalogRefreshCadence = CatalogRefreshCadence.ON_STARTUP,
    val filter: String = "MNN",
    val pageSize: Int = 100,
    val userAgent: String = "Mozilla/5.0 (Circle AI SDK) CircleAI-Kotlin/1.5",
) {
    companion object {
        fun defaultCacheDir(): String =
            File(System.getProperty("user.home"), ".circleai/catalog").path
    }
}

data class ModelEntry(
    val name: String,
    val version: String,
    val quantization: String = "",
    val url: String? = null,
    val checksum: String? = null,
    val repo: String? = null,
    val totalBytes: Long = 0,
    val bundleFiles: List<BundleFile> = emptyList(),
    val minRamGb: Double = 0.0,
    val minStorageGb: Double = 0.0,
    val capabilities: List<String>? = null,
    val qualityRank: Int = 0,
) {
    val isBundle: Boolean get() = bundleFiles.isNotEmpty()
}

data class ModelRegistry(
    val registryUrl: String,
    val lastUpdated: Instant,
    val models: List<ModelEntry>,
)

class ModelScopeCatalogClient(
    private val options: ModelScopeCatalogOptions = ModelScopeCatalogOptions(),
    private val verifier: ICatalogSignatureVerifier = NullCatalogSignatureVerifier,
    private val networkTypeProvider: (() -> String?)? = null,
) {
    private var refreshedThisRun: Boolean = false

    val cacheFilePath: String get() = File(options.cacheDirectory, "catalog.json").path
    val signatureFilePath: String get() = File(options.cacheDirectory, "catalog.sig").path

    init {
        File(options.cacheDirectory).mkdirs()
    }

    suspend fun isRefreshDueAsync(): Boolean = withContext(Dispatchers.IO) {
        if (options.cadence == CatalogRefreshCadence.NEVER) return@withContext false
        if (options.cadence == CatalogRefreshCadence.MANUAL) return@withContext false
        networkTypeProvider?.let {
            val net = it()?.lowercase()
            if (net == "none") return@withContext false
        }
        val cacheFile = File(cacheFilePath)
        if (!cacheFile.exists()) return@withContext true
        if (options.cadence == CatalogRefreshCadence.ON_STARTUP) {
            return@withContext !refreshedThisRun
        }
        val mtimeDate = Instant.ofEpochMilli(cacheFile.lastModified())
            .atOffset(ZoneOffset.UTC).toLocalDate()
        mtimeDate < LocalDate.now(ZoneOffset.UTC)
    }

    fun loadFromDisk(): ModelRegistry? {
        return try {
            val file = File(cacheFilePath)
            if (!file.exists()) return null
            parseRegistryJson(file.readText(StandardCharsets.UTF_8))
        } catch (_: Throwable) {
            null
        }
    }

    suspend fun getCachedCatalogAsync(acceptStaleOnError: Boolean = true): ModelRegistry? {
        return try {
            if (isRefreshDueAsync()) refreshAsync() else loadFromDisk()
        } catch (e: Throwable) {
            if (acceptStaleOnError) loadFromDisk() else throw e
        }
    }

    suspend fun refreshAsync(): ModelRegistry = withContext(Dispatchers.IO) {
        val reg = fetchLive()
        val json = registryToJson(reg)
        val bytes = json.toByteArray(StandardCharsets.UTF_8)

        val sigFile = File(signatureFilePath)
        val existingSig = if (sigFile.exists()) sigFile.readText().trim().ifBlank { null } else null

        val sigResult = verifier.verify(bytes, existingSig)
        if (sigResult == CatalogSignatureResult.INVALID) {
            throw RuntimeException("Catalog signature did not verify against the configured public key.")
        }

        File(options.cacheDirectory).mkdirs()
        File(cacheFilePath).writeBytes(bytes)
        refreshedThisRun = true
        reg
    }

    // ── Network ─────────────────────────────────────────────────────────

    private fun fetchLive(): ModelRegistry {
        val listingUrl = "${options.baseUri}/api/v1/models?Name=" +
            URLEncoder.encode(options.filter, StandardCharsets.UTF_8) +
            "&PageSize=${options.pageSize}"
        val listing = httpGetJson(listingUrl)
        val items = extractListingItems(listing)
        val entries = mutableListOf<ModelEntry>()
        for (item in items) {
            val name = item["Name"] ?: continue
            val repoPath = item["Path"] ?: continue
            val filesUrl = "${options.baseUri}/api/v1/models/$repoPath/repo/files?Revision=master"
            val fileBody = try { httpGetJson(filesUrl) } catch (_: Throwable) { continue }
            val files = extractFileItems(fileBody)
            var total = 0L
            for (f in files) total += f.sizeBytes
            entries.add(
                ModelEntry(
                    name = name,
                    version = item["Revision"] ?: "master",
                    quantization = item["Quantization"] ?: "",
                    repo = repoPath,
                    totalBytes = total,
                    bundleFiles = files,
                )
            )
        }
        return ModelRegistry(options.baseUri, Instant.now(), entries)
    }

    private fun httpGetJson(url: String): String {
        val conn = URL(url).openConnection() as HttpURLConnection
        try {
            conn.requestMethod = "GET"
            conn.setRequestProperty("User-Agent", options.userAgent)
            conn.connectTimeout = 10_000
            conn.readTimeout = 30_000
            val code = conn.responseCode
            if (code != 200) throw RuntimeException("HTTP $code fetching $url")
            BufferedReader(InputStreamReader(conn.inputStream, StandardCharsets.UTF_8)).use { r ->
                return r.readText()
            }
        } finally {
            conn.disconnect()
        }
    }
}
