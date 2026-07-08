// ModelDownload.kt
//
// Kotlin port of CircleAI.Inference.IModelDownloadService + ModelDownloadService
// (+ BundleFileSpec). C# is the EXACT spec. Downloads and manages model files
// on disk. Supports both the legacy single-file shape (one URL → one cached
// weight) and the bundle shape (a per-model directory with every file MNN-LLM
// needs).
//
// The C# service uses HttpClient; per the port rules, external I/O is injected
// behind IByteFetcher so the cache-check / temp-file / SHA-256-verify / atomic
// move / progress logic is exercised without standing up real sockets. The
// hash normalisation, URL building, and disk bookkeeping port faithfully.

package com.bhengubv.circleai.inference

import com.bhengubv.circleai.models.BundleFile
import com.bhengubv.circleai.models.InstalledManifest
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.io.File
import java.net.URLEncoder
import java.nio.file.Files
import java.security.MessageDigest
import java.time.Instant
import kotlin.math.max
import kotlin.math.min

/**
 * One file in a model bundle (compatible shape with CircleAI.Core.Models.BundleFile).
 *
 * @param name Filename relative to the model directory (e.g. `config.json`).
 * @param sha256 SHA-256 in `sha256:<hex>` or bare-hex form.
 * @param sizeBytes Expected file size for diagnostics.
 */
data class BundleFileSpec(
    val name: String,
    val sha256: String,
    val sizeBytes: Long,
)

/**
 * The byte-fetch seam. Production hosts back this with an HTTP client; tests
 * inject an in-memory map. Implementations stream the resource at [uri] to
 * [dest], reporting 0..1 progress. Throw on any non-success (missing URL, etc.)
 * so the caller's primary→fallback logic can react.
 */
interface IByteFetcher {
    /** Download [uri] to [dest], reporting fractional progress. Throws on failure. */
    suspend fun fetchToFileAsync(uri: String, dest: File, progress: ((Double) -> Unit)?)
}

/**
 * Downloads and manages model files on disk. Ports IModelDownloadService.
 */
interface IModelDownloadService {
    /**
     * Ensures a single model file is present on disk and matches
     * [expectedSha256]. Returns the absolute path to the cached file.
     */
    suspend fun ensureModelAsync(
        modelId: String,
        downloadUri: String,
        expectedSha256: String?,
        progress: ((Double) -> Unit)?,
    ): String

    /**
     * Ensures every file in [bundleFiles] is present under a per-model
     * directory and matches its pinned SHA-256. Returns the absolute path to
     * the model directory.
     */
    suspend fun ensureBundleAsync(
        modelId: String,
        repo: String,
        bundleFiles: List<BundleFileSpec>,
        progress: ((Double) -> Unit)?,
    ): String

    /** `true` if the model file (single-file shape) exists on disk. */
    suspend fun isModelCachedAsync(modelId: String): Boolean

    /** Deletes the model file or directory if it exists. No-op when absent. */
    suspend fun deleteModelAsync(modelId: String)

    /** Free bytes available on the drive that hosts the storage directory. */
    suspend fun getAvailableDiskSpaceBytesAsync(): Long
}

/**
 * Default implementation of [IModelDownloadService].
 *
 * Single-file entries land at `{storageDirectory}/{modelId}.gguf`; bundle
 * entries land at `{storageDirectory}/{modelId}/` with every bundle file under
 * that directory.
 *
 * @param storageDirectory Root directory for cached models.
 * @param fetcher Byte-fetch seam (injected; no real sockets in tests).
 */
class ModelDownloadService(
    storageDirectory: String,
    private val fetcher: IByteFetcher,
) : IModelDownloadService {

    private val storageDirectory: String

    init {
        require(storageDirectory.isNotBlank()) { "Storage directory must not be empty." }
        this.storageDirectory = storageDirectory
        File(storageDirectory).mkdirs()
    }

    // ── Single-file (legacy) ─────────────────────────────────────────────

    override suspend fun ensureModelAsync(
        modelId: String,
        downloadUri: String,
        expectedSha256: String?,
        progress: ((Double) -> Unit)?,
    ): String {
        validateModelId(modelId)

        val filePath = singleFilePath(modelId)
        val file = File(filePath)

        if (file.exists() && expectedSha256 != null) {
            if (verifySha256(file, expectedSha256)) {
                progress?.invoke(1.0)
                return filePath
            }
            file.delete()
        } else if (file.exists() && expectedSha256 == null) {
            progress?.invoke(1.0)
            return filePath
        }

        val tempPath = "$filePath.tmp"
        val temp = File(tempPath)
        try {
            fetcher.fetchToFileAsync(downloadUri, temp, progress)

            if (expectedSha256 != null && !verifySha256(temp, expectedSha256)) {
                temp.delete()
                throw IllegalStateException(
                    "SHA-256 mismatch for model '$modelId'. The downloaded file has been deleted.",
                )
            }

            if (file.exists()) file.delete()
            moveFile(temp, file)
        } catch (e: Exception) {
            if (temp.exists()) temp.delete()
            throw e
        }
        return filePath
    }

    // ── Bundle ────────────────────────────────────────────────────────────

    override suspend fun ensureBundleAsync(
        modelId: String,
        repo: String,
        bundleFiles: List<BundleFileSpec>,
        progress: ((Double) -> Unit)?,
    ): String {
        validateModelId(modelId)
        require(repo.isNotBlank()) { "Repo path is required for bundle entries." }
        require(bundleFiles.isNotEmpty()) { "Bundle file list must not be empty." }

        val modelDir = File(storageDirectory, modelId)
        modelDir.mkdirs()

        var totalBytes = 0L
        for (f in bundleFiles) totalBytes += max(0L, f.sizeBytes)
        var doneBytes = 0L

        for (bf in bundleFiles) {
            require(bf.name.isNotBlank()) { "Bundle for '$modelId' contains a file with no Name." }

            val destPath = File(modelDir, bf.name)
            destPath.parentFile?.mkdirs()

            // Skip when cached + valid.
            if (destPath.exists() && verifySha256(destPath, bf.sha256)) {
                doneBytes += bf.sizeBytes
                reportOverall(progress, doneBytes, totalBytes)
                continue
            }
            if (destPath.exists()) destPath.delete()

            val temp = File("${destPath.path}.tmp")
            try {
                val captured = doneBytes
                val perFile: ((Double) -> Unit)? = if (progress == null) null else { p ->
                    reportOverall(progress, captured + (bf.sizeBytes * p).toLong(), totalBytes)
                }

                // PrimaryUrl (API form) → FallbackUrl (CDN form).
                val primary = buildPrimaryUrl(repo, bf.name)
                val fallback = buildFallbackUrl(repo, bf.name)
                try {
                    fetcher.fetchToFileAsync(primary, temp, perFile)
                } catch (_: Exception) {
                    if (temp.exists()) temp.delete()
                    fetcher.fetchToFileAsync(fallback, temp, perFile)
                }

                if (!verifySha256(temp, bf.sha256)) {
                    temp.delete()
                    throw IllegalStateException(
                        "SHA-256 mismatch for bundle file '${bf.name}' of model '$modelId'. " +
                            "The downloaded file has been deleted.",
                    )
                }
                if (destPath.exists()) destPath.delete()
                moveFile(temp, destPath)
                doneBytes += bf.sizeBytes
                reportOverall(progress, doneBytes, totalBytes)
            } catch (e: Exception) {
                if (temp.exists()) {
                    try { temp.delete() } catch (_: Exception) {}
                }
                throw e
            }
        }

        progress?.invoke(1.0)
        return modelDir.path
    }

    /**
     * Stamps an `installed.json` file in [modelDir] describing what's on disk.
     * Best-effort — failures are swallowed so a manifest hiccup never breaks a
     * working install. Ports WriteInstalledManifestAsync.
     */
    suspend fun writeInstalledManifestAsync(
        modelDir: String,
        modelId: String,
        version: String,
        repo: String?,
        bundleFiles: List<BundleFileSpec>,
    ) {
        try {
            require(modelDir.isNotBlank())
            require(modelId.isNotBlank())

            var totalBytes = 0L
            val files = ArrayList<BundleFile>(bundleFiles.size)
            for (f in bundleFiles) {
                files.add(BundleFile(f.name, f.sha256, f.sizeBytes))
                totalBytes += max(0L, f.sizeBytes)
            }

            val manifest = InstalledManifestDto(
                modelId = modelId,
                version = version,
                repo = repo,
                totalBytes = totalBytes,
                files = files.map { BundleFileDto(it.name, it.sha256, it.sizeBytes) },
                installedAtUtc = Instant.now().toString(),
            )

            val path = File(modelDir, "installed.json")
            path.writeText(MANIFEST_JSON.encodeToString(InstalledManifestDto.serializer(), manifest))
        } catch (_: Exception) {
            // Best-effort. A missing manifest just downgrades upgrade detection.
        }
    }

    /**
     * Reads the `installed.json` stamped by [writeInstalledManifestAsync] back
     * into a strongly-typed [InstalledManifest]. Returns `null` when absent or
     * malformed.
     */
    fun readInstalledManifest(modelDir: String): InstalledManifest? {
        return try {
            val path = File(modelDir, "installed.json")
            if (!path.exists()) return null
            val dto = MANIFEST_JSON.decodeFromString(InstalledManifestDto.serializer(), path.readText())
            InstalledManifest(
                modelId = dto.modelId,
                version = dto.version,
                repo = dto.repo,
                totalBytes = dto.totalBytes,
                files = dto.files.map { BundleFile(it.name, it.sha256, it.sizeBytes) },
                installedAtUtc = Instant.parse(dto.installedAtUtc),
            )
        } catch (_: Exception) {
            null
        }
    }

    // ── Common ───────────────────────────────────────────────────────────

    override suspend fun isModelCachedAsync(modelId: String): Boolean {
        validateModelId(modelId)
        if (File(singleFilePath(modelId)).exists()) return true
        return File(storageDirectory, modelId).isDirectory
    }

    override suspend fun deleteModelAsync(modelId: String) {
        validateModelId(modelId)
        val single = File(singleFilePath(modelId))
        if (single.exists()) single.delete()
        val dir = File(storageDirectory, modelId)
        if (dir.isDirectory) dir.deleteRecursively()
    }

    override suspend fun getAvailableDiskSpaceBytesAsync(): Long {
        val absolute = File(storageDirectory).absoluteFile
        return absolute.usableSpace
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private fun singleFilePath(modelId: String): String =
        File(storageDirectory, "$modelId.gguf").path

    private fun moveFile(src: File, dest: File) {
        // Prefer atomic rename; fall back to copy+delete across volumes.
        if (!src.renameTo(dest)) {
            Files.copy(src.toPath(), dest.toPath())
            src.delete()
        }
    }

    private fun reportOverall(p: ((Double) -> Unit)?, done: Long, total: Long) {
        if (p == null) return
        if (total <= 0) p.invoke(0.0) else p.invoke(min(0.999, done.toDouble() / total))
    }

    private fun verifySha256(file: File, expectedHex: String): Boolean {
        val md = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { stream ->
            val buffer = ByteArray(81_920)
            while (true) {
                val read = stream.read(buffer)
                if (read <= 0) break
                md.update(buffer, 0, read)
            }
        }
        val actualHex = md.digest().joinToString("") { "%02x".format(it) }
        val expectedNormalised = stripShaAlgorithmPrefix(expectedHex)
        return actualHex.equals(expectedNormalised, ignoreCase = true)
    }

    companion object {
        private const val PROGRESS_CHUNK_BYTES = 1 * 1024 * 1024 // 1 MB (parity constant)

        private val MANIFEST_JSON = Json { prettyPrint = true; encodeDefaults = true }

        internal fun validateModelId(modelId: String) {
            require(modelId.isNotBlank()) { "Model ID must not be empty." }
        }

        internal fun buildPrimaryUrl(repo: String, fileName: String): String =
            "https://modelscope.cn/api/v1/models/$repo/repo?Revision=master&FilePath=${escape(fileName)}"

        internal fun buildFallbackUrl(repo: String, fileName: String): String =
            "https://modelscope.cn/models/$repo/resolve/master/${escape(fileName)}"

        private fun escape(s: String): String =
            URLEncoder.encode(s, "UTF-8").replace("+", "%20")

        /**
         * Returns the hex portion of a SHA-256 checksum, stripping an optional
         * leading algorithm token of the form `sha256:`, `SHA-256:`, etc.
         * Ports StripShaAlgorithmPrefix exactly.
         */
        fun stripShaAlgorithmPrefix(raw: String): String {
            if (raw.isEmpty()) return ""
            val trimmed = raw.trim()
            val colon = trimmed.indexOf(':')
            if (colon < 0) return trimmed
            val prefix = trimmed.substring(0, colon)
            if (prefix.isNotEmpty() && prefix.length <= 16) {
                var isAlgName = true
                for (c in prefix) {
                    if (!(c.isLetterOrDigit() || c == '-' || c == '_')) {
                        isAlgName = false
                        break
                    }
                }
                if (isAlgName) return trimmed.substring(colon + 1).trim()
            }
            return trimmed
        }
    }
}

// Serialization DTOs for installed.json (Instant serialised as ISO string).
@Serializable
private data class BundleFileDto(val name: String, val sha256: String, val sizeBytes: Long)

@Serializable
private data class InstalledManifestDto(
    val modelId: String,
    val version: String,
    val repo: String?,
    val totalBytes: Long,
    val files: List<BundleFileDto>,
    val installedAtUtc: String,
)
