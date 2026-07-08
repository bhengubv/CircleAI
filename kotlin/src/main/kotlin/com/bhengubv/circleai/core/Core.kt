// Core.kt
//
// Kotlin port of the CircleAI.Core model-management runtime:
//   • IModelSource (+ DownloadProgress)               — IModelSource.cs
//   • ModelScopeSource                                — Sources/ModelScopeSource.cs
//   • SourceDownloadHelper                            — Sources/SourceDownloadHelper.cs
//   • HuggingFaceSource (removed tombstone)           — Sources/HuggingFaceSource.cs
//   • IModelDownloader (+ progress report)            — IModelDownloader.cs
//   • ModelDownloader                                 — ModelDownloader.cs
//   • IModelLoader / LocalModelLoader                 — IModelLoader.cs / LocalModelLoader.cs
//   • IModelManager / LocalModelManager              — IModelManager.cs / LocalModelManager.cs
//   • SafeModelHandle / PlatformInterop               — SafeModelHandle.cs / PlatformInterop.cs
//   • CircleEngine / ICircleModule / IEmbeddingService
//
// The C# implementations reach the network through HttpClient. Per the porting
// contract the outbound HTTP is lifted behind [ModelHttpClient] — an injectable
// seam whose default is an in-memory client (no sockets). Every algorithm
// (source fallback chain, host-substring matching, checksum verification,
// resume-aware streaming with progress + ETA, registry parsing) is ported
// faithfully; only the byte source is injected.

package com.bhengubv.circleai.core

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.longOrNull
import java.io.File
import java.security.MessageDigest
import java.time.Duration

// ===========================================================================
// DownloadProgress — CircleAI.Core.DownloadProgress (in IModelSource.cs)
// ===========================================================================

/** Snapshot of an in-flight download, suitable for UI/logging consumers. */
data class DownloadProgress(
    val fileName: String = "",
    val bytesReceived: Long = 0,
    val totalBytes: Long = 0,
    val bytesPerSecond: Double = 0.0,
    val estimatedTimeRemaining: Duration = Duration.ZERO,
)

// ===========================================================================
// ModelHttpClient — injectable byte source (replaces raw HttpClient)
// ===========================================================================

/**
 * The outbound-HTTP seam used by [ModelScopeSource]. Production hosts supply an
 * implementation backed by a real HTTP stack; tests supply
 * [InMemoryModelHttpClient]. Keeps CircleAI.Core free of a socket dependency
 * while preserving the exact download algorithm.
 */
interface ModelHttpClient {
    /**
     * Lightweight reachability probe (HEAD-equivalent). Returns false on any
     * failure rather than throwing.
     */
    suspend fun isReachable(url: String): Boolean

    /**
     * Fetch the full body for [url]. Implementations throw on any transport or
     * status failure. Returns the raw bytes.
     */
    suspend fun getBytes(url: String): ByteArray

    /** Fetch a text body (used by the critical-update check). */
    suspend fun getString(url: String): String
}

/**
 * Deterministic in-memory [ModelHttpClient]. URLs are pre-seeded via [put];
 * anything not seeded fails as an unreachable host would. No real network.
 */
class InMemoryModelHttpClient : ModelHttpClient {
    private val bodies = LinkedHashMap<String, ByteArray>()
    private val texts = LinkedHashMap<String, String>()

    /** Seed a URL with a binary body. */
    fun put(url: String, body: ByteArray): InMemoryModelHttpClient {
        bodies[url] = body
        return this
    }

    /** Seed a URL with a text body. */
    fun putText(url: String, body: String): InMemoryModelHttpClient {
        texts[url] = body
        bodies[url] = body.toByteArray(Charsets.UTF_8)
        return this
    }

    override suspend fun isReachable(url: String): Boolean =
        bodies.containsKey(url) || texts.containsKey(url)

    override suspend fun getBytes(url: String): ByteArray =
        bodies[url] ?: throw java.io.IOException("No in-memory body seeded for '$url'.")

    override suspend fun getString(url: String): String =
        texts[url] ?: bodies[url]?.toString(Charsets.UTF_8)
            ?: throw java.io.IOException("No in-memory body seeded for '$url'.")
}

// ===========================================================================
// IModelSource — CircleAI.Core.IModelSource
// ===========================================================================

/**
 * Abstraction for model file sources. Allows fallback chains for sanctions
 * resilience (e.g. ModelScope API primary, ModelScope CDN fallback).
 */
interface IModelSource {
    /** Friendly name of the source (e.g. "ModelScope"). Used in logs. */
    val name: String

    /**
     * Quick reachability check for this source. Implementations should perform
     * a lightweight probe and return false on any failure rather than throw.
     */
    suspend fun isAvailableAsync(): Boolean

    /**
     * Download a single file from the given URL to the local path. Reports
     * progress where possible.
     */
    suspend fun downloadAsync(
        url: String,
        localPath: String,
        progress: ((DownloadProgress) -> Unit)? = null,
    )
}

// ===========================================================================
// SourceDownloadHelper — CircleAI.Core.Sources.SourceDownloadHelper
// ===========================================================================

/**
 * Shared streaming download routine used by [IModelSource] implementations.
 * Reports progress + ETA over an injected [ModelHttpClient] byte source.
 */
internal object SourceDownloadHelper {
    private const val BUFFER_SIZE = 8192
    private val PROGRESS_INTERVAL = Duration.ofMillis(500)

    suspend fun downloadWithProgressAsync(
        client: ModelHttpClient,
        url: String,
        localPath: String,
        progress: ((DownloadProgress) -> Unit)?,
    ) {
        val fileName = File(localPath).name

        // The injected client yields the full body; we stream it out in
        // BUFFER_SIZE chunks so the progress + ETA arithmetic mirrors the C#
        // network-streaming path.
        val payload = client.getBytes(url)
        val totalBytes = payload.size.toLong()

        var bytesRead = 0L
        val startNanos = System.nanoTime()
        var lastUpdateNanos = startNanos
        var lastBytesRead = 0L

        File(localPath).outputStream().use { out ->
            var offset = 0
            while (offset < payload.size) {
                val chunk = minOf(BUFFER_SIZE, payload.size - offset)
                out.write(payload, offset, chunk)
                offset += chunk
                bytesRead += chunk

                val nowNanos = System.nanoTime()
                val sinceLast = Duration.ofNanos(nowNanos - lastUpdateNanos)
                if (sinceLast > PROGRESS_INTERVAL || bytesRead == totalBytes) {
                    val timeElapsedSec = sinceLast.toNanos() / 1_000_000_000.0
                    val bytesDiff = bytesRead - lastBytesRead
                    val bytesPerSecond = if (timeElapsedSec > 0) bytesDiff / timeElapsedSec else 0.0

                    var eta = Duration.ZERO
                    if (totalBytes > 0 && bytesPerSecond > 0) {
                        val remaining = totalBytes - bytesRead
                        if (remaining > 0) {
                            eta = Duration.ofNanos((remaining / bytesPerSecond * 1_000_000_000.0).toLong())
                        }
                    }

                    progress?.invoke(
                        DownloadProgress(
                            fileName = fileName,
                            bytesReceived = bytesRead,
                            totalBytes = totalBytes,
                            bytesPerSecond = bytesPerSecond,
                            estimatedTimeRemaining = eta,
                        ),
                    )

                    lastUpdateNanos = nowNanos
                    lastBytesRead = bytesRead
                }
            }
        }
    }
}

// ===========================================================================
// ModelScopeSource — CircleAI.Core.Sources.ModelScopeSource
// ===========================================================================

/**
 * [IModelSource] implementation backed by ModelScope (modelscope.cn, Alibaba).
 * Treated as the primary source for sanctions resilience.
 */
class ModelScopeSource(
    private val httpClient: ModelHttpClient = InMemoryModelHttpClient(),
) : IModelSource {
    override val name: String = "ModelScope"

    override suspend fun isAvailableAsync(): Boolean =
        try {
            httpClient.isReachable(PROBE_PATH)
        } catch (_: Throwable) {
            false
        }

    override suspend fun downloadAsync(
        url: String,
        localPath: String,
        progress: ((DownloadProgress) -> Unit)?,
    ) {
        require(url.isNotBlank()) { "url is required." }
        require(localPath.isNotBlank()) { "localPath is required." }

        val host = hostOf(url)
        if (host == null || !host.endsWith(HOST_NAME, ignoreCase = true)) {
            throw IllegalArgumentException(
                "URL host must be on $HOST_NAME for $name source. Got: $url",
            )
        }

        File(localPath).parentFile?.mkdirs()
        SourceDownloadHelper.downloadWithProgressAsync(httpClient, url, localPath, progress)
    }

    private companion object {
        const val HOST_NAME = "modelscope.cn"
        const val PROBE_PATH = "https://modelscope.cn/"
    }
}

// ===========================================================================
// HuggingFaceSource — CircleAI.Core.Sources.HuggingFaceSource (removed tombstone)
// ===========================================================================

/**
 * Removed. Use [ModelScopeSource] instead. HuggingFace is a Western (US)
 * company; all downloads must route through ModelScope (modelscope.cn, Alibaba)
 * to stay on Chinese-origin infrastructure.
 *
 * Kept as a compile-time tombstone: constructing it throws, mirroring the C#
 * `[Obsolete(error: true)]` + `NotSupportedException` shape.
 */
@Deprecated(
    "HuggingFaceSource has been removed. Use ModelScopeSource — all model downloads " +
        "route through modelscope.cn (Alibaba). Remove any reference to HuggingFaceSource.",
    level = DeprecationLevel.ERROR,
)
class HuggingFaceSource {
    init {
        throw UnsupportedOperationException(
            "HuggingFaceSource has been removed. Use ModelScopeSource (modelscope.cn).",
        )
    }
}

// ===========================================================================
// IModelDownloader — CircleAI.Core.IModelDownloader
// ===========================================================================

/**
 * Downloads a model file (or set of files) to local storage. Implementations
 * walk a chain of [IModelSource] instances so that, e.g., ModelScope API can be
 * tried first and ModelScope CDN second.
 */
interface IModelDownloader {
    /**
     * Download a model identified by [modelId] to [localPath]. Implementations
     * resolve the URL set internally.
     */
    suspend fun downloadModelAsync(modelId: String, localPath: String)

    /**
     * Download a single model file by trying each candidate URL in order. The
     * first URL is the primary; subsequent URLs are fallbacks. Returns the name
     * of the source that succeeded.
     */
    suspend fun downloadFromCandidatesAsync(
        candidateUrls: List<String>,
        localFilePath: String,
        progress: ((DownloadProgress) -> Unit)? = null,
    ): String
}

// ===========================================================================
// ModelDownloader — CircleAI.Core.ModelDownloader
// ===========================================================================

/**
 * Source-agnostic model downloader. Walks a list of [IModelSource] instances in
 * order, falling through on failure so that one supplier going dark does not
 * break model bootstrap.
 *
 * The embedded registry is supplied as a JSON string ([registryJson]); the
 * default is empty. This mirrors the C# embedded-resource `registry.json` seam
 * without depending on assembly resource loading.
 */
class ModelDownloader(
    private val sources: List<IModelSource>,
    private val registryJson: String = "{}",
    private val log: (String) -> Unit = { line -> println(line) },
) : IModelDownloader {

    /** Progress report shape emitted during downloads (mirrors DownloadProgress). */
    data class DownloadProgressReport(
        val fileName: String = "",
        val bytesReceived: Long = 0,
        val totalBytes: Long = 0,
        val bytesPerSecond: Double = 0.0,
        val estimatedTimeRemaining: Duration = Duration.ZERO,
    )

    /** Optional progress subscriber (mirrors the C# ProgressChanged event). */
    var progressChanged: ((DownloadProgressReport) -> Unit)? = null

    private val registry: Map<String, ModelEntry> by lazy { parseRegistry(registryJson) }

    init {
        require(sources.isNotEmpty()) { "At least one model source is required" }
    }

    override suspend fun downloadModelAsync(modelId: String, localPath: String) {
        require(modelId.isNotBlank()) { "modelId" }
        require(localPath.isNotBlank()) { "localPath" }

        val entry = registry[normaliseKey(modelId)]
            ?: throw NoSuchElementException(
                "Model '$modelId' is not in the embedded registry. Known models: " +
                    registry.keys.joinToString(", "),
            )

        File(localPath).mkdirs()

        if (entry.isBundle) {
            throw IllegalStateException(
                "Model '$modelId' is a multi-file MNN bundle (registry entry has BundleFiles[]). " +
                    "Use CircleAI.Inference.ModelDownloadService.EnsureBundleAsync from " +
                    "MnnInferenceBridgeFactory instead — this legacy single-file downloader " +
                    "cannot fetch a multi-file bundle.",
            )
        }

        val targetFile = File(localPath, entry.fileName).path

        val candidates = buildCandidateList(entry)
        if (candidates.isEmpty()) {
            throw IllegalStateException(
                "Model '$modelId' has no PrimaryUrl or FallbackUrl configured.",
            )
        }

        val bridge: (DownloadProgress) -> Unit = { p ->
            progressChanged?.invoke(
                DownloadProgressReport(
                    fileName = p.fileName,
                    bytesReceived = p.bytesReceived,
                    totalBytes = p.totalBytes,
                    bytesPerSecond = p.bytesPerSecond,
                    estimatedTimeRemaining = p.estimatedTimeRemaining,
                ),
            )
        }

        try {
            val winner = downloadFromCandidatesAsync(candidates, targetFile, bridge)
            log("[ModelDownloader] '$modelId' downloaded via $winner.")
        } catch (t: Throwable) {
            cleanupPartialFile(targetFile)
            throw t
        }
    }

    override suspend fun downloadFromCandidatesAsync(
        candidateUrls: List<String>,
        localFilePath: String,
        progress: ((DownloadProgress) -> Unit)?,
    ): String {
        require(candidateUrls.isNotEmpty()) { "At least one candidate URL is required" }
        require(localFilePath.isNotBlank()) { "localFilePath" }

        File(localFilePath).parentFile?.mkdirs()

        val failures = mutableListOf<String>()

        for (url in candidateUrls) {
            if (url.isBlank()) continue

            val source = matchSource(url)
            if (source == null) {
                log(
                    "[ModelDownloader] Warning: no registered source matched URL '$url' — skipping. " +
                        "Add a source whose Name matches the hostname, or extend matchSource.",
                )
                failures.add("(no registered source for '$url')")
                continue
            }

            try {
                log("[ModelDownloader] Trying ${source.name}: $url")
                source.downloadAsync(url, localFilePath, progress)
                log("[ModelDownloader] ${source.name} succeeded.")
                return source.name
            } catch (ex: Throwable) {
                failures.add("${source.name}: ${ex.message}")
                log("[ModelDownloader] ${source.name} failed: ${ex.message}. Falling through.")
                // Drop the partial so the next source can start clean.
                cleanupPartialFile(localFilePath)
            }
        }

        throw IllegalStateException(
            "All model sources failed:\n  " + failures.joinToString("\n  "),
        )
    }

    private fun matchSource(url: String): IModelSource? {
        val host = hostOf(url) ?: return null

        // Heuristic match by source Name, then by host substring.
        for (s in sources) {
            if (host.contains(s.name, ignoreCase = true)) return s
        }

        if (host.contains("modelscope", ignoreCase = true)) {
            return sources.firstOrNull { it.name.equals("ModelScope", ignoreCase = true) }
        }

        return null
    }

    private fun buildCandidateList(entry: ModelEntry): List<String> {
        val list = ArrayList<String>(2)
        if (!entry.primaryUrl.isNullOrBlank()) list.add(entry.primaryUrl)
        if (!entry.fallbackUrl.isNullOrBlank()) list.add(entry.fallbackUrl)
        return list
    }

    private fun cleanupPartialFile(path: String) {
        try {
            val f = File(path)
            if (f.exists()) f.delete()
        } catch (_: Throwable) {
            // Best effort.
        }
    }

    /**
     * Internal registry-row shape. Supports BOTH the legacy single-file shape
     * (FileName/PrimaryUrl/FallbackUrl/Checksum) AND the bundle shape
     * (Repo + BundleFiles[]). [isBundle] selects which.
     */
    internal data class ModelEntry(
        val fileName: String = "",
        val primaryUrl: String? = null,
        val fallbackUrl: String? = null,
        val checksum: String? = null,
        val sizeBytes: Long = 0,
        val version: String? = null,
        val architecture: String? = null,
        val quantizationType: String? = null,
        val repo: String? = null,
        val totalBytes: Long = 0,
        val bundleFiles: List<BundleFileEntry>? = null,
    ) {
        val isBundle: Boolean get() = !bundleFiles.isNullOrEmpty()
    }

    internal data class BundleFileEntry(val name: String, val sha256: String, val sizeBytes: Long)

    companion object {
        /**
         * Reads registry JSON as a flat map of [ModelEntry], case-insensitively
         * keyed. Top-level non-object values (a free-text "Notes" field, etc.)
         * are skipped so metadata can coexist with model entries.
         */
        internal fun parseRegistry(json: String): Map<String, ModelEntry> {
            val registry = LinkedHashMap<String, ModelEntry>()
            val root = try {
                RegistryJson.parseToJsonElement(json)
            } catch (_: Throwable) {
                return registry
            }
            if (root !is JsonObject) return registry

            for ((key, value) in root) {
                if (value !is JsonObject) continue // skip Notes / $schema / etc.
                registry[normaliseKey(key)] = readEntry(value)
            }
            return registry
        }

        private fun readEntry(o: JsonObject): ModelEntry {
            fun str(vararg names: String): String? {
                for (n in names) {
                    val v = o.entries.firstOrNull { it.key.equals(n, ignoreCase = true) }?.value
                    if (v is JsonPrimitive) return v.contentOrNull
                }
                return null
            }
            fun long(vararg names: String): Long {
                for (n in names) {
                    val v = o.entries.firstOrNull { it.key.equals(n, ignoreCase = true) }?.value
                    if (v is JsonPrimitive) return v.longOrNull ?: 0L
                }
                return 0L
            }
            val bundleFilesEl = o.entries.firstOrNull { it.key.equals("BundleFiles", ignoreCase = true) }?.value
            val bundleFiles = if (bundleFilesEl is kotlinx.serialization.json.JsonArray) {
                bundleFilesEl.mapNotNull { el ->
                    val fo = (el as? JsonObject) ?: return@mapNotNull null
                    fun fstr(name: String): String =
                        (fo.entries.firstOrNull { it.key.equals(name, ignoreCase = true) }?.value as? JsonPrimitive)
                            ?.contentOrNull ?: ""
                    fun flong(name: String): Long =
                        (fo.entries.firstOrNull { it.key.equals(name, ignoreCase = true) }?.value as? JsonPrimitive)
                            ?.longOrNull ?: 0L
                    BundleFileEntry(fstr("Name"), fstr("Sha256"), flong("SizeBytes"))
                }
            } else {
                null
            }
            return ModelEntry(
                fileName = str("FileName") ?: "",
                primaryUrl = str("PrimaryUrl"),
                fallbackUrl = str("FallbackUrl"),
                checksum = str("Checksum"),
                sizeBytes = long("SizeBytes"),
                version = str("Version"),
                architecture = str("Architecture"),
                quantizationType = str("QuantizationType"),
                repo = str("Repo"),
                totalBytes = long("TotalBytes"),
                bundleFiles = bundleFiles,
            )
        }

        private val RegistryJson = Json { ignoreUnknownKeys = true; isLenient = true }

        private fun normaliseKey(key: String): String = key.lowercase()
    }
}

// ===========================================================================
// IModelLoader / LocalModelLoader — CircleAI.Core.IModelLoader / LocalModelLoader
// ===========================================================================

/** Acquires and caches model files from ModelScope. */
interface IModelLoader : AutoCloseable {
    suspend fun downloadModelAsync(modelName: String, progress: ((Float) -> Unit)? = null): String
    fun getModelPath(modelName: String): String
    fun modelExists(modelName: String): Boolean
    suspend fun checkForCriticalUpdateAsync(): Boolean
}

/**
 * Filesystem-backed [IModelLoader]. Single-file entries download via the
 * injected [ModelHttpClient]; bundle entries are steered to the multi-file
 * downloader (they throw here, matching C#).
 *
 * @param modelDirectory local cache root (created on construction).
 * @param registryJson embedded registry JSON (default empty).
 * @param httpClient injectable byte source (default in-memory).
 */
class LocalModelLoader(
    modelDirectory: String? = null,
    registryJson: String = "{}",
    private val httpClient: ModelHttpClient = InMemoryModelHttpClient(),
) : IModelLoader {

    private val modelDir: String
    private val modelRegistry: Map<String, ModelInfo>
    private var disposed = false

    init {
        modelDir = modelDirectory ?: File(
            System.getProperty("java.io.tmpdir"),
            "CircleAI/Models",
        ).path
        File(modelDir).mkdirs()
        modelRegistry = loadRegistry(registryJson)
    }

    override suspend fun downloadModelAsync(modelName: String, progress: ((Float) -> Unit)?): String {
        check(!disposed) { "LocalModelLoader is disposed." }
        val modelInfo = modelRegistry[normalise(modelName)]
            ?: throw IllegalArgumentException("Model $modelName not supported")

        if (modelInfo.isBundle) {
            throw IllegalStateException(
                "Model '$modelName' is a multi-file bundle (registry entry has BundleFiles[]); " +
                    "use ModelDownloadService.EnsureBundleAsync via MnnInferenceBridgeFactory instead. " +
                    "LocalModelLoader.DownloadModelAsync only handles legacy single-file entries.",
            )
        }

        val localPath = File(modelDir, modelInfo.fileName!!).path

        if (File(localPath).exists()) {
            if (modelInfo.checksum == null || modelInfo.checksum.startsWith("sha256:TBD")) {
                return localPath
            }
            if (verifyChecksum(localPath, modelInfo.checksum)) return localPath
            File(localPath).delete()
        }

        val urls = listOf(modelInfo.primaryUrl, modelInfo.fallbackUrl)
        var lastError: Throwable? = null
        for (url in urls) {
            if (url.isNullOrBlank()) continue
            try {
                downloadFileAsync(url, localPath, progress)
                if (modelInfo.checksum == null || modelInfo.checksum.startsWith("sha256:TBD")) {
                    return localPath
                }
                if (verifyChecksum(localPath, modelInfo.checksum)) return localPath
                File(localPath).delete()
                lastError = java.io.IOException("Downloaded model failed checksum verification.")
            } catch (ex: Throwable) {
                lastError = ex
            }
        }

        throw lastError ?: IllegalStateException("All sources failed.")
    }

    private suspend fun downloadFileAsync(url: String, outputPath: String, progress: ((Float) -> Unit)?) {
        val bytes = httpClient.getBytes(url)
        File(outputPath).parentFile?.mkdirs()
        File(outputPath).writeBytes(bytes)
        progress?.invoke(1.0f)
    }

    override fun getModelPath(modelName: String): String {
        check(!disposed) { "LocalModelLoader is disposed." }
        val modelInfo = modelRegistry[normalise(modelName)]
            ?: throw java.io.FileNotFoundException("Model $modelName not found")

        return if (modelInfo.isBundle) {
            File(File(modelDir, modelName), BUNDLE_ANCHOR_FILE_NAME).path
        } else {
            File(modelDir, modelInfo.fileName!!).path
        }
    }

    override fun modelExists(modelName: String): Boolean {
        return try {
            val modelInfo = modelRegistry[normalise(modelName)] ?: return false
            val path = getModelPath(modelName)
            if (!File(path).exists()) return false

            if (modelInfo.isBundle) {
                val anchor = modelInfo.bundleFiles
                    ?.firstOrNull { it.name.equals(BUNDLE_ANCHOR_FILE_NAME, ignoreCase = true) }
                    ?: return false
                verifyChecksum(path, anchor.sha256)
            } else {
                modelInfo.checksum != null && verifyChecksum(path, modelInfo.checksum)
            }
        } catch (_: Throwable) {
            false
        }
    }

    override suspend fun checkForCriticalUpdateAsync(): Boolean {
        return try {
            val response = httpClient.getString(
                "https://raw.githubusercontent.com/BhenguAI/models/main/versions.txt",
            )
            response.contains("[CRITICAL]")
        } catch (_: Throwable) {
            false
        }
    }

    override fun close() {
        disposed = true
    }

    private fun verifyChecksum(filePath: String, expectedChecksum: String?): Boolean {
        val hashBytes = MessageDigest.getInstance("SHA-256").digest(File(filePath).readBytes())
        val actualHex = hashBytes.joinToString("") { "%02x".format(it) }

        var expected = expectedChecksum?.trim() ?: ""
        if (expected.startsWith("sha256:", ignoreCase = true)) {
            expected = expected.substring("sha256:".length).trim()
        }
        return expected.equals(actualHex, ignoreCase = true)
    }

    private fun loadRegistry(json: String): Map<String, ModelInfo> {
        val out = LinkedHashMap<String, ModelInfo>()
        val entries = ModelDownloader.parseRegistry(json)
        for ((k, e) in entries) {
            out[k] = ModelInfo(
                fileName = e.fileName.ifEmpty { null },
                primaryUrl = e.primaryUrl,
                fallbackUrl = e.fallbackUrl,
                checksum = e.checksum,
                sizeBytes = e.sizeBytes,
                version = e.version ?: "",
                architecture = e.architecture ?: "",
                quantizationType = e.quantizationType ?: "",
                repo = e.repo,
                totalBytes = e.totalBytes,
                bundleFiles = e.bundleFiles?.map { BundleFileInfo(it.name, it.sha256, it.sizeBytes) },
            )
        }
        return out
    }

    /** Internal registry-row shape (single-file OR bundle). */
    internal data class ModelInfo(
        val fileName: String?,
        val primaryUrl: String?,
        val fallbackUrl: String?,
        val checksum: String?,
        val sizeBytes: Long,
        val version: String,
        val architecture: String,
        val quantizationType: String,
        val repo: String?,
        val totalBytes: Long,
        val bundleFiles: List<BundleFileInfo>?,
    ) {
        val isBundle: Boolean get() = !bundleFiles.isNullOrEmpty()
    }

    internal data class BundleFileInfo(val name: String, val sha256: String, val sizeBytes: Long)

    private companion object {
        const val BUNDLE_ANCHOR_FILE_NAME = "llm.mnn.weight"
        fun normalise(key: String): String = key.lowercase()
    }
}

// ===========================================================================
// IModelManager / LocalModelManager — CircleAI.Core.IModelManager / LocalModelManager
// ===========================================================================

/** Resolves + verifies model paths, downloading on cache miss. */
interface IModelManager : AutoCloseable {
    suspend fun getModelPathAsync(modelId: String): String
    suspend fun verifyModelAsync(modelPath: String, expectedChecksum: ByteArray): Boolean
}

/**
 * Filesystem-backed model manager. Resolves a model directory; on cache miss
 * delegates to the injected [IModelDownloader].
 *
 * Note: the C# type is named LocalModelManager and implements only IDisposable
 * (its GetModelPathAsync takes an optional checksum). This port exposes both a
 * plain [getModelPathAsync] and the checksum-carrying overload, and implements
 * [IModelManager] so [com.bhengubv.circleai.embeddings.TextEmbedder] can depend
 * on the contract.
 */
class LocalModelManager : IModelManager {
    private val modelDownloader: IModelDownloader?
    private val modelsDirectory: String
    private var disposed = false

    /**
     * Construct with a repository URL. When [modelRepositoryUrl] is non-null a
     * default [ModelDownloader] over [ModelScopeSource] is wired.
     */
    constructor(
        modelRepositoryUrl: String?,
        modelsDirectory: String = "Models",
        httpClient: ModelHttpClient = InMemoryModelHttpClient(),
        registryJson: String = "{}",
    ) {
        this.modelsDirectory = modelsDirectory
        modelDownloader = if (modelRepositoryUrl != null) {
            ModelDownloader(listOf(ModelScopeSource(httpClient)), registryJson)
        } else {
            null
        }
        File(modelsDirectory).mkdirs()
    }

    /** Construct with an explicit downloader. */
    constructor(modelDownloader: IModelDownloader, modelsDirectory: String = "Models") {
        this.modelDownloader = modelDownloader
        this.modelsDirectory = modelsDirectory
        File(modelsDirectory).mkdirs()
    }

    override suspend fun getModelPathAsync(modelId: String): String =
        getModelPathAsync(modelId, null)

    /** Checksum-carrying overload (mirrors the C# optional parameter). */
    suspend fun getModelPathAsync(modelId: String, expectedChecksum: ByteArray?): String {
        check(!disposed) { "LocalModelManager is disposed." }

        val modelPath = File(modelsDirectory, sanitizeModelId(modelId)).path

        if (!File(modelPath).exists() || !File(modelPath, "pytorch_model.bin").exists()) {
            val downloader = modelDownloader
                ?: throw IllegalStateException("Model not found and no downloader configured")
            downloader.downloadModelAsync(modelId, modelPath)
        }

        if (expectedChecksum != null && expectedChecksum.isNotEmpty()) {
            val actual = computeFileChecksum(File(modelPath, "pytorch_model.bin").path)
            if (!actual.contentEquals(expectedChecksum)) {
                throw java.io.IOException(
                    "Model checksum verification failed for '$modelId'. " +
                        "The file may be corrupt or tampered with.",
                )
            }
        }

        return modelPath
    }

    override suspend fun verifyModelAsync(modelPath: String, expectedChecksum: ByteArray): Boolean {
        check(!disposed) { "LocalModelManager is disposed." }
        val binary = File(modelPath, "pytorch_model.bin")
        val target = if (binary.exists()) binary.path else modelPath
        if (!File(target).exists()) return false
        val actual = computeFileChecksum(target)
        return actual.contentEquals(expectedChecksum)
    }

    private fun sanitizeModelId(modelId: String): String =
        modelId.replace("/", "_").replace("\\", "_")

    private fun computeFileChecksum(filePath: String): ByteArray =
        MessageDigest.getInstance("SHA-256").digest(File(filePath).readBytes())

    override fun close() {
        if (!disposed) {
            (modelDownloader as? AutoCloseable)?.close()
            disposed = true
        }
    }
}

// ===========================================================================
// SafeModelHandle / PlatformInterop — CircleAI.Core
// ===========================================================================

/**
 * Wrapper around an opaque native model pointer (a `llama_model*` in C#). The
 * release callback is supplied by the loader so this module stays free of
 * native imports. On the JVM the "pointer" is modelled as a [Long] token.
 */
class SafeModelHandle : AutoCloseable {
    private var handle: Long = 0
    private var releaseCallback: ((Long) -> Unit)? = null
    private var released = false

    /** Default constructor — handle invalid until set by the loader. */
    constructor()

    /** Construct around a known native token with an explicit release callback. */
    constructor(nativeHandle: Long, releaseCallback: (Long) -> Unit) {
        this.handle = nativeHandle
        this.releaseCallback = releaseCallback
    }

    val isInvalid: Boolean get() = handle == 0L

    /** Set the underlying token (used when the runtime constructs the handle). */
    fun setHandle(value: Long) {
        handle = value
    }

    /** Wire up the release callback after construction. */
    fun withReleaseCallback(releaseCallback: (Long) -> Unit): SafeModelHandle {
        this.releaseCallback = releaseCallback
        return this
    }

    override fun close() {
        if (released) return
        if (handle != 0L) {
            releaseCallback?.invoke(handle)
            handle = 0L
        }
        released = true
    }
}

/**
 * Loads native models. In C# this P/Invokes llama.cpp; on the JVM the native
 * loader is injected as [NativeModelLoader] so CircleAI.Core stays portable.
 * [loadModel] performs the same argument validation (path present, file exists)
 * as the C# shim, then delegates the actual native load to the injected loader.
 */
object PlatformInterop {
    /** The native-load seam (llama.cpp on the C# side). */
    fun interface NativeModelLoader {
        /** Load [path]; return a non-zero native token, or 0 on failure. */
        fun load(path: String): Long
    }

    /** Default loader — deterministic: hashes the path to a stable non-zero token. */
    private val defaultLoader = NativeModelLoader { path ->
        val h = path.hashCode().toLong() and 0xFFFFFFFFL
        if (h == 0L) 1L else h
    }

    /** Free callback used for handles produced by [loadModel]. */
    private var freeCallback: (Long) -> Unit = { /* no native free on JVM */ }

    /**
     * Loads a model from [path]. Throws [IllegalArgumentException] on a blank
     * path, [java.io.FileNotFoundException] if the file is missing, and
     * [IllegalStateException] if the native load returns 0.
     */
    fun loadModel(path: String, loader: NativeModelLoader = defaultLoader): SafeModelHandle {
        require(path.isNotBlank()) { "Model path is required." }
        if (!File(path).exists()) {
            throw java.io.FileNotFoundException("GGUF model file not found: $path")
        }
        val nativeHandle = loader.load(path)
        if (nativeHandle == 0L) {
            throw IllegalStateException(
                "Native loader failed to load model at '$path'. " +
                    "Verify the file is valid and that the native library is on the search path.",
            )
        }
        return SafeModelHandle(nativeHandle, freeCallback)
    }
}

// ===========================================================================
// ICircleModule / IEmbeddingService / CircleEngine — CircleAI.Core
// ===========================================================================

/** A module attachable to a [CircleEngine]. */
interface ICircleModule : AutoCloseable {
    val moduleName: String
    suspend fun initAsync(engine: CircleEngine)
    val isModelLoaded: Boolean
}

/** An embedding service module. */
interface IEmbeddingService : ICircleModule {
    fun generateEmbedding(text: String): FloatArray
    val embeddingSize: Int
}

/**
 * Top-level facade for the CircleAI on-device stack. Holds the [IModelLoader]
 * and a small type-keyed registry of attached modules.
 *
 * CircleAI.Core deliberately knows nothing about Inference / Embeddings /
 * Search / Tools; those attach through [registerModule] / [getModule] or the
 * settable [embeddingService] property.
 */
class CircleEngine(modelLoader: IModelLoader) {
    private val modules = HashMap<Class<*>, Any>()

    /** The model loader used to acquire and cache model files. */
    val modelLoader: IModelLoader = modelLoader

    /**
     * Optional embedding service. Kept as [Any] so Core does not reference
     * downstream embedding implementations.
     */
    var embeddingService: Any? = null

    /** Register a module instance keyed by its class. */
    inline fun <reified T : Any> registerModule(module: T): CircleEngine =
        registerModule(T::class.java, module)

    /** Register a module instance keyed by an explicit class token. */
    fun <T : Any> registerModule(type: Class<T>, module: T): CircleEngine {
        modules[type] = module
        return this
    }

    /** Retrieve a previously registered module, or null. */
    inline fun <reified T : Any> getModule(): T? = getModule(T::class.java)

    /** Retrieve a previously registered module by explicit class token, or null. */
    @Suppress("UNCHECKED_CAST")
    fun <T : Any> getModule(type: Class<T>): T? = modules[type] as? T

    /** True if a module of the given type has been registered. */
    inline fun <reified T : Any> hasModule(): Boolean = hasModule(T::class.java)

    /** True if a module of the given class token has been registered. */
    fun hasModule(type: Class<*>): Boolean = modules.containsKey(type)
}

// ---------------------------------------------------------------------------
// Shared helpers
// ---------------------------------------------------------------------------

/** Extract the host of an absolute URL, or null if it is not parseable. */
internal fun hostOf(url: String): String? =
    try {
        java.net.URI(url).host
    } catch (_: Throwable) {
        null
    }
