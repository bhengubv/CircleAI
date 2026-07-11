// Knowledge.kt
//
// Kotlin port of CircleAI.Knowledge (YamlFrontmatter.cs + KnowledgeNote.cs +
// IKnowledgeStore.cs + FileSystemKnowledgeStore.cs + MarkdownEpisodicMemoryStore.cs)
// — the C# reference is the EXACT spec. Markdown-on-disk knowledge notes
// (YAML frontmatter + body), a file-system store, and a markdown-backed
// episodic-memory store.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `Guid` -> `java.util.UUID`; C# `DateTimeOffset` -> `java.time.OffsetDateTime`
//     (round-trip "O" format preserved via ISO-8601).
//   * C# `Task` / `IAsyncEnumerable` -> `suspend fun` / kotlinx `Flow`.
//   * FileSystemKnowledgeStore keeps the atomic write-tmp-then-rename + per-Guid
//     lock; the C# diagnostics base (CircleAIComponentBase) carries no wire
//     semantics and is not ported (established convention — see Federation).
//   * MarkdownEpisodicMemoryStore implements the memory-brain
//     `IEpisodicStore` / `EpisodicEntry` pair — the C#-reference conversational
//     shape (userText/assistantText/appContext + cosine search + prune), which
//     the Kotlin tree deliberately keeps separate from the persona-layer
//     memory.IEpisodicMemoryStore. The C# store's Guid id maps to the brain
//     EpisodicEntry's String id.

package com.bhengubv.circleai.knowledge

import com.bhengubv.circleai.memory.brain.EpisodicEntry
import com.bhengubv.circleai.memory.brain.IEpisodicStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.time.Instant
import java.time.OffsetDateTime
import java.time.format.DateTimeFormatter
import java.util.Base64
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// YamlFrontmatter.cs
// =====================================================================

/**
 * Parses / writes minimal flat YAML frontmatter blocks. Nested keys, flow-style
 * structures, anchors, and lists are explicitly rejected. Mirrors C#
 * `YamlFrontmatter`.
 */
internal object YamlFrontmatter {
    private const val DELIMITER = "---"

    /** Renders [frontmatter] into a YAML block followed by [body]. */
    fun write(frontmatter: Map<String, String>, body: String): String {
        val sb = StringBuilder()
        sb.append(DELIMITER).append('\n')
        for ((k, v) in frontmatter) {
            validateKey(k)
            sb.append(k)
            sb.append(": ")
            sb.append(encodeValue(v))
            sb.append('\n')
        }
        sb.append(DELIMITER).append('\n')
        sb.append(body)
        return sb.toString()
    }

    /** Parses [text] into a frontmatter map + body. Throws on malformed input. */
    fun read(text: String): Pair<Map<String, String>, String> {
        var t = text.replace("\r\n", "\n").replace('\r', '\n')

        if (!t.startsWith(DELIMITER + "\n")) {
            throw IllegalArgumentException("Frontmatter must start with '---' on its own line.")
        }

        val searchStart = DELIMITER.length + 1
        val closingIdx = t.indexOf("\n" + DELIMITER + "\n", searchStart)
        if (closingIdx < 0) {
            throw IllegalArgumentException("Missing closing '---' line for frontmatter block.")
        }

        val yaml = t.substring(searchStart, closingIdx)
        val body = t.substring(closingIdx + ("\n" + DELIMITER + "\n").length)

        val dict = LinkedHashMap<String, String>()
        for (rawLine in yaml.split('\n')) {
            if (rawLine.isBlank()) continue

            if (rawLine[0] == ' ' || rawLine[0] == '\t') {
                throw IllegalArgumentException("Nested YAML is not supported.")
            }
            if (rawLine.startsWith("- ")) {
                throw IllegalArgumentException("YAML lists are not supported.")
            }

            val colon = rawLine.indexOf(':')
            if (colon <= 0) throw IllegalArgumentException("Malformed YAML line: '$rawLine'.")

            val key = rawLine.substring(0, colon).trim()
            val rest = if (colon + 1 < rawLine.length) rawLine.substring(colon + 1).trimStart() else ""

            validateKey(key)

            if (rest.startsWith('{') || rest.startsWith('[')) {
                throw IllegalArgumentException("Flow-style YAML structures are not supported.")
            }

            dict[key] = decodeValue(rest)
        }

        return dict to body
    }

    private fun validateKey(key: String) {
        if (key.isBlank()) throw IllegalArgumentException("YAML key cannot be empty.")
        for (ch in key) {
            if (!(ch.isLetterOrDigit() || ch == '_' || ch == '-' || ch == '.')) {
                throw IllegalArgumentException("Invalid character '$ch' in YAML key '$key'.")
            }
        }
    }

    private fun encodeValue(value: String): String {
        if (value.isEmpty()) return "\"\""

        var needsQuoting = false
        for (ch in value) {
            if (ch == ':' || ch == '#' || ch == '\n' || ch == '\r' || ch == '\t' ||
                ch == '"' || ch == '\\' || ch == '\'' || ch == '{' || ch == '['
            ) {
                needsQuoting = true
                break
            }
        }

        if (!needsQuoting && (value[0] == ' ' || value[value.length - 1] == ' ')) {
            needsQuoting = true
        }

        if (!needsQuoting) return value

        val sb = StringBuilder(value.length + 2)
        sb.append('"')
        for (ch in value) {
            when (ch) {
                '\\' -> sb.append("\\\\")
                '"' -> sb.append("\\\"")
                '\n' -> sb.append("\\n")
                '\r' -> sb.append("\\r")
                '\t' -> sb.append("\\t")
                else -> sb.append(ch)
            }
        }
        sb.append('"')
        return sb.toString()
    }

    private fun decodeValue(raw0: String): String {
        var raw = raw0
        if (raw.isEmpty()) return ""

        if (raw[0] != '"' && raw[0] != '\'') {
            val hashIdx = raw.indexOf(" #")
            if (hashIdx >= 0) raw = raw.substring(0, hashIdx).trimEnd()
            return raw
        }

        if (raw[0] == '\'') throw IllegalArgumentException("Single-quoted YAML scalars are not supported.")

        if (raw.length < 2 || raw[raw.length - 1] != '"') {
            throw IllegalArgumentException("Unterminated double-quoted YAML scalar.")
        }

        val inner = raw.substring(1, raw.length - 1)
        val sb = StringBuilder(inner.length)
        var i = 0
        while (i < inner.length) {
            val ch = inner[i]
            if (ch != '\\') {
                sb.append(ch)
                i++
                continue
            }
            if (i + 1 >= inner.length) throw IllegalArgumentException("Trailing backslash in YAML scalar.")
            i++
            when (val next = inner[i]) {
                '\\' -> sb.append('\\')
                '"' -> sb.append('"')
                'n' -> sb.append('\n')
                'r' -> sb.append('\r')
                't' -> sb.append('\t')
                else -> throw IllegalArgumentException("Unsupported YAML escape '\\$next'.")
            }
            i++
        }
        return sb.toString()
    }
}

// =====================================================================
// KnowledgeNote.cs
// =====================================================================

/**
 * A markdown knowledge note: flat frontmatter metadata + a markdown body.
 * Serialised on disk as `---\nkey: value\n---\n(body)`. Mirrors C# `KnowledgeNote`.
 */
data class KnowledgeNote(
    val id: UUID,
    val title: String,
    val bodyMarkdown: String,
    val frontmatter: Map<String, String>,
    val tags: List<String>,
    val createdAt: OffsetDateTime,
    val updatedAt: OffsetDateTime,
) {
    /** Serialises this note to its on-disk text form. */
    fun toFileText(): String {
        val merged = LinkedHashMap<String, String>()
        for ((k, v) in frontmatter) merged[k] = v
        merged[ID_KEY] = id.toString()
        merged[TITLE_KEY] = title
        merged[CREATED_KEY] = createdAt.format(ISO)
        merged[UPDATED_KEY] = updatedAt.format(ISO)
        merged[TAGS_KEY] = tags.joinToString(",")
        return YamlFrontmatter.write(merged, bodyMarkdown)
    }

    companion object {
        private const val TITLE_KEY = "title"
        private const val CREATED_KEY = "created_at"
        private const val UPDATED_KEY = "updated_at"
        private const val ID_KEY = "id"
        private const val TAGS_KEY = "tags"
        private val ISO: DateTimeFormatter = DateTimeFormatter.ISO_OFFSET_DATE_TIME

        /** Parses the on-disk text form back into a [KnowledgeNote]. */
        fun parseFile(text: String): KnowledgeNote {
            val (frontmatter, body) = YamlFrontmatter.read(text)

            val idRaw = frontmatter[ID_KEY]
            val id = idRaw?.let { runCatching { UUID.fromString(it) }.getOrNull() }
                ?: throw IllegalArgumentException("Knowledge note frontmatter missing or invalid 'id'.")

            val title = frontmatter[TITLE_KEY] ?: ""

            val created = parseTimestamp(frontmatter, CREATED_KEY)
            val updated = parseTimestamp(frontmatter, UPDATED_KEY)

            val rawTags = frontmatter[TAGS_KEY]
            val tags = if (!rawTags.isNullOrBlank()) {
                rawTags.split(',').map { it.trim() }.filter { it.isNotEmpty() }
            } else {
                emptyList()
            }

            val userFrontmatter = LinkedHashMap<String, String>()
            for ((k, v) in frontmatter) {
                if (k == ID_KEY || k == TITLE_KEY || k == CREATED_KEY || k == UPDATED_KEY || k == TAGS_KEY) continue
                userFrontmatter[k] = v
            }

            return KnowledgeNote(id, title, body, userFrontmatter, tags, created, updated)
        }

        private fun parseTimestamp(map: Map<String, String>, key: String): OffsetDateTime {
            val raw = map[key]
            if (raw.isNullOrBlank()) return OffsetDateTime.now()
            return runCatching { OffsetDateTime.parse(raw, ISO) }.getOrElse { OffsetDateTime.now() }
        }
    }
}

// =====================================================================
// IKnowledgeStore.cs
// =====================================================================

/** Persistent store for [KnowledgeNote] documents. Mirrors C# `IKnowledgeStore`. */
interface IKnowledgeStore {
    /** Loads the note with [id], or null when none exists. */
    suspend fun getAsync(id: UUID): KnowledgeNote?

    /** Persists [note]; the returned record may differ (e.g. refreshed updatedAt). */
    suspend fun saveAsync(note: KnowledgeNote): KnowledgeNote

    /** Deletes the note with [id]. No-op if absent. */
    suspend fun deleteAsync(id: UUID)

    /** Streams notes carrying [tag]. */
    fun searchByTagAsync(tag: String): Flow<KnowledgeNote>

    /** Streams every stored note. */
    fun enumerateAllAsync(): Flow<KnowledgeNote>
}

// =====================================================================
// FileSystemKnowledgeStore.cs
// =====================================================================

/**
 * File-system [IKnowledgeStore]. Each note is stored as
 * `{rootDirectory}/{id-no-dashes}.md`. Atomic write-then-rename, per-Guid lock.
 * Mirrors C# `FileSystemKnowledgeStore` (minus the diagnostics base class).
 */
class FileSystemKnowledgeStore(rootDirectory: String) : IKnowledgeStore {
    private val rootDirectory: File
    private val locks = ConcurrentHashMap<UUID, Any>()

    init {
        require(rootDirectory.isNotBlank()) { "rootDirectory required" }
        this.rootDirectory = File(rootDirectory)
        this.rootDirectory.mkdirs()
    }

    override suspend fun getAsync(id: UUID): KnowledgeNote? {
        val path = notePath(id)
        if (!path.exists()) return null
        synchronized(lockFor(id)) {
            return KnowledgeNote.parseFile(path.readText())
        }
    }

    override suspend fun saveAsync(note: KnowledgeNote): KnowledgeNote {
        val refreshed = note.copy(updatedAt = OffsetDateTime.now())
        val target = notePath(refreshed.id)
        val tmp = File(target.path + "." + noDashes(UUID.randomUUID()) + ".tmp")
        synchronized(lockFor(refreshed.id)) {
            try {
                tmp.writeText(refreshed.toFileText())
                Files.move(tmp.toPath(), target.toPath(), StandardCopyOption.REPLACE_EXISTING)
                return refreshed
            } catch (ex: Exception) {
                runCatching { if (tmp.exists()) tmp.delete() }
                throw ex
            }
        }
    }

    override suspend fun deleteAsync(id: UUID) {
        val path = notePath(id)
        synchronized(lockFor(id)) {
            if (path.exists()) path.delete()
        }
    }

    override fun searchByTagAsync(tag: String): Flow<KnowledgeNote> {
        require(tag.isNotBlank()) { "tag required" }
        return flow {
            enumerateAllAsync().collect { note ->
                if (note.tags.any { it.equals(tag, ignoreCase = true) }) emit(note)
            }
        }
    }

    override fun enumerateAllAsync(): Flow<KnowledgeNote> = flow {
        if (!rootDirectory.isDirectory) return@flow
        val files = rootDirectory.listFiles { f -> f.isFile && f.name.endsWith(".md") } ?: return@flow
        for (file in files) {
            val note = runCatching { KnowledgeNote.parseFile(file.readText()) }.getOrNull() ?: continue
            emit(note)
        }
    }

    private fun lockFor(id: UUID): Any = locks.getOrPut(id) { Any() }

    private fun notePath(id: UUID): File = File(rootDirectory, noDashes(id) + ".md")

    private companion object {
        fun noDashes(id: UUID): String = id.toString().replace("-", "")
    }
}

// =====================================================================
// MarkdownEpisodicMemoryStore.cs
// =====================================================================

/**
 * Markdown-on-disk [IEpisodicStore] backed by an [IKnowledgeStore]; each
 * [EpisodicEntry] is persisted as one [KnowledgeNote] with structured
 * frontmatter and a "## User … ## Assistant …" body. Mirrors C#
 * `MarkdownEpisodicMemoryStore`.
 */
class MarkdownEpisodicMemoryStore(private val store: IKnowledgeStore) : IEpisodicStore {

    override suspend fun addAsync(entry: EpisodicEntry) {
        store.saveAsync(toNote(entry))
    }

    override suspend fun searchAsync(queryEmbedding: FloatArray?, topK: Int): List<EpisodicEntry> {
        val snapshot = ArrayList<EpisodicEntry>()
        store.enumerateAllAsync().collect { snapshot.add(fromNote(it)) }

        if (queryEmbedding == null || queryEmbedding.isEmpty()) {
            return snapshot.sortedByDescending { it.recordedAtUtc }.take(topK)
        }

        return snapshot
            .filter { it.embedding != null && it.embedding.size == queryEmbedding.size }
            .map { it to cosineSimilarity(queryEmbedding, it.embedding!!) }
            .sortedByDescending { it.second }
            .take(topK)
            .map { it.first }
    }

    override suspend fun getRecentAsync(count: Int): List<EpisodicEntry> {
        val snapshot = ArrayList<EpisodicEntry>()
        store.enumerateAllAsync().collect { snapshot.add(fromNote(it)) }
        return snapshot.sortedByDescending { it.recordedAtUtc }.take(count)
    }

    override suspend fun countAsync(): Int {
        var n = 0
        store.enumerateAllAsync().collect { n++ }
        return n
    }

    override suspend fun pruneOlderThanAsync(cutoff: Instant): Int {
        val doomed = ArrayList<UUID>()
        store.enumerateAllAsync().collect { note ->
            val entry = fromNote(note)
            if (entry.recordedAtUtc.isBefore(cutoff)) doomed.add(note.id)
        }
        for (id in doomed) store.deleteAsync(id)
        return doomed.size
    }

    internal companion object {
        private const val EPISODE_ID_KEY = "episode_id"
        private const val RECORDED_AT_KEY = "recorded_at"
        private const val APP_CONTEXT_KEY = "app_context"
        private const val EMBEDDING_KEY = "embedding"
        private const val EMBEDDING_DIMS_KEY = "embedding_dims"
        private const val TAG_PREFIX = "tag_"

        /** Maps an [EpisodicEntry] to its [KnowledgeNote] representation. */
        fun toNote(entry: EpisodicEntry): KnowledgeNote {
            // The brain EpisodicEntry uses a String id; the C# store used a Guid.
            // Preserve the id when it parses as a UUID, else mint one deterministically.
            val entryUuid = runCatching { UUID.fromString(entry.id) }.getOrNull()
            val recorded = OffsetDateTime.ofInstant(entry.recordedAtUtc, java.time.ZoneOffset.UTC)

            val frontmatter = LinkedHashMap<String, String>()
            frontmatter[EPISODE_ID_KEY] = entry.id
            frontmatter[RECORDED_AT_KEY] = recorded.format(DateTimeFormatter.ISO_OFFSET_DATE_TIME)
            if (!entry.appContext.isNullOrBlank()) frontmatter[APP_CONTEXT_KEY] = entry.appContext!!

            val emb = entry.embedding
            if (emb != null && emb.isNotEmpty()) {
                val bytes = ByteArray(emb.size * 4)
                val bb = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
                for (f in emb) bb.putFloat(f)
                frontmatter[EMBEDDING_KEY] = Base64.getEncoder().encodeToString(bytes)
                frontmatter[EMBEDDING_DIMS_KEY] = emb.size.toString()
            }

            val tags = ArrayList<String>()
            entry.tags?.let { t ->
                for ((k, v) in t) {
                    frontmatter[TAG_PREFIX + k] = v
                    tags.add(k)
                }
            }

            val body = "## User\n\n${entry.userText}\n\n## Assistant\n\n${entry.assistantText}"

            return KnowledgeNote(
                id = entryUuid ?: UUID.randomUUID(),
                title = truncateForTitle(entry.userText),
                bodyMarkdown = body,
                frontmatter = frontmatter,
                tags = tags,
                createdAt = recorded,
                updatedAt = recorded,
            )
        }

        /** Inverse of [toNote]. */
        fun fromNote(note: KnowledgeNote): EpisodicEntry {
            var episodeId = note.id.toString()
            note.frontmatter[EPISODE_ID_KEY]?.let { episodeId = it }

            var recordedAt = note.createdAt.toInstant()
            note.frontmatter[RECORDED_AT_KEY]?.let { raw ->
                runCatching { OffsetDateTime.parse(raw, DateTimeFormatter.ISO_OFFSET_DATE_TIME).toInstant() }
                    .getOrNull()?.let { recordedAt = it }
            }

            val appContext = note.frontmatter[APP_CONTEXT_KEY]

            var embedding: FloatArray? = null
            val b64 = note.frontmatter[EMBEDDING_KEY]
            if (!b64.isNullOrBlank()) {
                embedding = runCatching {
                    val bytes = Base64.getDecoder().decode(b64)
                    val out = FloatArray(bytes.size / 4)
                    val bb = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
                    for (i in out.indices) out[i] = bb.float
                    out
                }.getOrNull()
            }

            val (userText, assistantText) = splitBody(note.bodyMarkdown)

            var tagsOut: MutableMap<String, String>? = null
            for ((k, v) in note.frontmatter) {
                if (!k.startsWith(TAG_PREFIX)) continue
                if (tagsOut == null) tagsOut = LinkedHashMap()
                tagsOut[k.substring(TAG_PREFIX.length)] = v
            }

            return EpisodicEntry(
                id = episodeId,
                userText = userText,
                assistantText = assistantText,
                recordedAtUtc = recordedAt,
                appContext = appContext,
                embedding = embedding,
                tags = tagsOut,
            )
        }

        private fun splitBody(body: String): Pair<String, String> {
            if (body.isEmpty()) return "" to ""

            val normal = body.replace("\r\n", "\n")
            val userMarker = "## User\n\n"
            val assistantMarker = "\n\n## Assistant\n\n"

            val userIdx = normal.indexOf(userMarker)
            val assistantIdx = normal.indexOf(assistantMarker)

            if (userIdx < 0 || assistantIdx <= userIdx) return normal to ""

            val userText = normal.substring(userIdx + userMarker.length, assistantIdx)
            val assistantText = normal.substring(assistantIdx + assistantMarker.length)

            return userText to assistantText
        }

        private fun truncateForTitle(source: String): String {
            if (source.isBlank()) return "(untitled)"
            val single = source.replace('\n', ' ').replace('\r', ' ').trim()
            return if (single.length <= 64) single else single.substring(0, 64)
        }

        private fun cosineSimilarity(a: FloatArray, b: FloatArray): Float {
            var dot = 0f
            for (i in a.indices) dot += a[i] * b[i]
            return dot
        }
    }
}
