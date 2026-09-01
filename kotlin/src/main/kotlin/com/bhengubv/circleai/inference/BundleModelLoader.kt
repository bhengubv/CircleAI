// BundleModelLoader.kt
//
// The loader that understands the BUNDLE registry shape — which is the shape
// every entry in the catalogue actually uses.
//
// WHY THIS EXISTS, as two concrete defects in the single-file loader:
//
//   1. It THROWS on any entry with bundle files, telling the caller to use the
//      download service directly. Since every registry entry is bundle-shaped,
//      that loader cannot fetch a single current model — so the host's startup
//      path could never download one at all.
//
//   2. It returns the WEIGHT file as the load path. The runtime's create call
//      wants config.json; handed the weight blob it fails deep inside a native
//      library, nowhere near the registry entry that caused it.
//
// The weight file stays the INTEGRITY anchor — it is the largest file, so a hash
// mismatch there is the most diagnostic thing that can fail. It is just no
// longer the load path.
//
// Ported from src/CircleAI.Inference/BundleModelLoader.cs.

package com.bhengubv.circleai.inference

import com.bhengubv.circleai.core.ModelModality
import com.bhengubv.circleai.core.ModelPaths
import java.io.File

class BundleModelLoader(
    modelDirectory: String? = null,
    private val entryFor: (String) -> BundleEntry?,
    private val downloadBundle: suspend (
        modelId: String, repo: String, files: List<BundleEntry.File>,
        progress: ((Double) -> Unit)?
    ) -> String,
    private val gate: IModelDownloadGate? = null
) {
    data class BundleEntry(
        val name: String,
        val version: String,
        val repo: String?,
        val totalBytes: Long,
        val files: List<File>,
        val modality: ModelModality?
    ) {
        data class File(val name: String, val sha256: String, val sizeBytes: Long)

        val isBundle: Boolean get() = files.isNotEmpty()
    }

    // Through ModelPaths, not the application-data folder: that resolves to a
    // SUBDIRECTORY of the folder the app actually uses on Android, so a caller
    // that passed nothing downloaded a second copy of every model.
    private val storageRoot: String = ModelPaths.resolve(modelDirectory)

    suspend fun downloadModel(modelName: String, progress: ((Double) -> Unit)? = null): String {
        require(modelName.isNotBlank()) { "A model name is required." }
        val entry = entryFor(modelName)
            ?: throw IllegalArgumentException("Model '$modelName' is not in the registry.")

        // THE METERED GATE IS CHECKED BEFORE ANY BYTES MOVE, and skipped when
        // the bundle is already cached — re-verifying a model on disk must never
        // be refused for being "on mobile data".
        if (gate != null && !modelExists(modelName)) {
            gate.blockReason(entry.totalBytes)?.let { throw ModelDownloadBlockedException(it) }
        }

        require(entry.isBundle) { "Registry entry '$modelName' has no bundle files." }
        val repo = entry.repo
        require(!repo.isNullOrBlank()) {
            "Registry entry '$modelName' has bundle files but no repo — URLs cannot be built."
        }

        val modelDir = downloadBundle(modelName, repo, entry.files, progress)
        return resolveLoadPath(entry, modelDir)
    }

    fun getModelPath(modelName: String): String {
        val entry = entryFor(modelName)
            ?: throw IllegalArgumentException("Model '$modelName' is not in the registry.")
        if (!entry.isBundle) return File(storageRoot, "$modelName.gguf").path

        val modelDir = File(storageRoot, modelName)
        if (modelDir.isDirectory) {
            runCatching { return resolveLoadPath(entry, modelDir.path) }
        }
        // Not fully downloaded yet: hand back the CONVENTIONAL anchor so a
        // caller can existence-test it and trigger a download, rather than an
        // error it has to special-case.
        return File(modelDir, CONFIG_FILE_NAME).path
    }

    /**
     * WHICH file the runtime loads is modality-specific. Chat means MNN and
     * therefore config.json; a speech bundle loads its own graph, so the largest
     * catalogued file is the honest answer.
     */
    internal fun resolveLoadPath(entry: BundleEntry, modelDir: String): String {
        if (entry.modality == ModelModality.Chat) {
            val config = File(modelDir, CONFIG_FILE_NAME)
            check(config.isFile) { "'\${entry.name}' is missing $CONFIG_FILE_NAME." }
            return config.path
        }
        val anchor = anchorOf(entry) ?: error("'\${entry.name}' has no files to load.")
        return File(modelDir, anchor.name).path
    }

    /** Present on disk, by SIZE. Cheap enough to call on a UI path. */
    fun modelPresent(modelName: String): Boolean {
        val entry = entryFor(modelName) ?: return false
        if (!entry.isBundle) return File(storageRoot, "$modelName.gguf").isFile

        val modelDir = File(storageRoot, modelName)
        if (!modelDir.isDirectory) return false
        if (entry.modality == ModelModality.Chat && !File(modelDir, CONFIG_FILE_NAME).isFile) {
            return false
        }
        val anchor = anchorOf(entry) ?: return false
        return File(modelDir, anchor.name).length() >= anchor.sizeBytes
    }

    /**
     * Present AND verified. Distinct from [modelPresent] on purpose: hashing a
     * 500 MB weight file is not something to do on every screen paint, so a
     * caller picks which question it is asking.
     */
    fun modelExists(modelName: String): Boolean {
        val entry = entryFor(modelName) ?: return false
        if (!entry.isBundle) return File(storageRoot, "$modelName.gguf").isFile

        val modelDir = File(storageRoot, modelName)
        if (!modelDir.isDirectory) return false
        if (entry.modality == ModelModality.Chat && !File(modelDir, CONFIG_FILE_NAME).isFile) {
            return false
        }
        val anchor = anchorOf(entry) ?: return false
        val file = File(modelDir, anchor.name)
        if (!file.isFile) return false
        if (anchor.sha256.isBlank()) return true
        return SideloadedBundleImporter.sha256Hex(file).equals(anchor.sha256, ignoreCase = true)
    }

    companion object {
        const val CONFIG_FILE_NAME = "config.json"
        /** The canonical MNN weight blob, and the preferred integrity anchor. */
        const val ANCHOR_FILE_NAME = "llm.mnn.weight"

        /** The MNN weight blob when there is one, else the LARGEST catalogued
         *  file — biggest file means a hash mismatch there is the most
         *  diagnostic failure available. */
        internal fun anchorOf(entry: BundleEntry): BundleEntry.File? =
            entry.files.firstOrNull { it.name.equals(ANCHOR_FILE_NAME, ignoreCase = true) }
                ?: entry.files.maxByOrNull { it.sizeBytes }
    }
}
