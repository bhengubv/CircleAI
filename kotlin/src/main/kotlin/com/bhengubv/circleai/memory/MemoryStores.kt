// MemoryStores.kt
//
// The small stores: what an editor sent, goals in memory, affect and persona on
// disk, and episodes and goals in SQL.
//
// Port of HookPayload.cs, InMemoryGoalStore.cs, JsonAffectStore.cs,
// JsonPersonaStore.cs, SqliteEpisodicStore.cs, SqliteGoalStore.cs and the VAD
// extension from AffectVad.cs.

package com.bhengubv.circleai.memory

import java.io.File
import java.sql.Connection
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonPrimitive

/**
 * Getting the words out of whatever an editor sent.
 *
 * THIS LIVED IN THE COMMAND, WHERE NO TEST COULD REACH IT. It runs on every
 * prompt somebody types, it decides what gets remembered, and its behaviour was
 * only ever checked by hand. A claim nothing can test is a claim, not a fact.
 *
 * FORGIVING BY DESIGN, because the shape belongs to somebody else. The payload
 * is JSON with a "prompt" field today and that can change. Anything that is not
 * that JSON is treated as the WORDS themselves; JSON WITHOUT a prompt is
 * treated as NOTHING, because reading the envelope as if it were the message
 * would file field names as things somebody said.
 */
object HookPayload {

    fun promptFrom(raw: String?): String {
        if (raw.isNullOrBlank()) return ""
        val trimmed = raw.trimStart()

        // Not an envelope. Take it at face value - a person piping their own
        // notes in is the other half of what this reads.
        if (!trimmed.startsWith("{")) return raw

        return try {
            val element = Json.parseToJsonElement(trimmed)
            if (element !is JsonObject) return raw
            val prompt = element["prompt"] ?: return "" // an envelope with no message in it
            val primitive = prompt as? JsonPrimitive ?: return ""
            if (primitive.isString) primitive.content else ""
        } catch (e: Exception) {
            // Something that starts with a brace and is not JSON is far more
            // likely to be prose than a broken payload.
            raw
        }
    }
}

/** Thread-safe goals in memory, for tests and single-session use. */
class InMemoryGoalStore : IGoalStore {

    private val goals = ConcurrentHashMap<String, Goal>()

    override suspend fun listAsync(userId: String): List<Goal> {
        require(userId.isNotBlank()) { "userId is required." }
        return goals.values.filter { it.userId == userId }
    }

    override suspend fun getAsync(id: String): Goal? {
        require(id.isNotBlank()) { "id is required." }
        return goals[id]
    }

    override suspend fun upsertAsync(goal: Goal): Goal {
        goals[goal.id] = goal
        return goal
    }

    override suspend fun deleteAsync(id: String) {
        require(id.isNotBlank()) { "id is required." }
        goals.remove(id)
    }

    override suspend fun getActiveAsync(userId: String): List<Goal> {
        require(userId.isNotBlank()) { "userId is required." }
        return goals.values.filter { it.userId == userId && it.status == GoalStatus.Active }
    }
}

/**
 * Write to a temporary name in the same directory, then RENAME into place.
 *
 * A rename within one filesystem is atomic, so a reader never sees a partial
 * file and a kill mid-write costs the update rather than the record. The
 * temporary name is unique per save, so two saves for the same user cannot
 * contend on one path.
 */
internal fun writeAtomically(target: File, text: String) {
    target.parentFile?.mkdirs()
    val tmp = File(target.parentFile, target.name + "." + UUID.randomUUID().toString().take(8) + ".tmp")
    try {
        tmp.writeText(text)
        if (!tmp.renameTo(target)) {
            // Some filesystems refuse a rename onto an existing file.
            target.delete()
            if (!tmp.renameTo(target)) tmp.copyTo(target, overwrite = true)
        }
    } finally {
        if (tmp.exists()) tmp.delete()
    }
}

/** The JSON these stores read and write: forgiving in, tidy out. */
internal val StoreJson = Json {
    prettyPrint = true
    ignoreUnknownKeys = true
    encodeDefaults = true
    explicitNulls = false
}

/**
 * Affect on the local filesystem.
 *
 * A CORRUPT FILE READS AS A FRESH STATE rather than throwing. Affect is a
 * running estimate of how a conversation is going; refusing to start because
 * one is unreadable would trade a lost estimate for a dead app, and the next
 * save overwrites it anyway.
 */
class JsonAffectStore(directory: String) : IAffectStore {

    private val dir: File

    init {
        require(directory.isNotBlank()) { "Directory is required." }
        dir = File(directory)
        dir.mkdirs()
    }

    override suspend fun loadAsync(userId: String): AffectState {
        require(userId.isNotBlank()) { "userId is required." }
        val file = pathFor(userId)
        if (!file.exists()) return AffectState(userId = userId)
        return try {
            val row = StoreJson.decodeFromString(AffectRow.serializer(), file.readText())
            row.toState()
        } catch (e: Exception) {
            AffectState(userId = userId)
        }
    }

    override suspend fun saveAsync(state: AffectState) {
        state.lastUpdatedUtc = Instant.now()
        writeAtomically(
            pathFor(state.userId),
            StoreJson.encodeToString(AffectRow.serializer(), AffectRow.of(state)),
        )
    }

    internal fun pathFor(userId: String): File = File(dir, "affect-" + safe(userId) + ".json")

    @kotlinx.serialization.Serializable
    internal data class AffectRow(
        val userId: String = "default",
        val lastUpdatedUtc: String = "",
        val curiosity: Float = 0.5f,
        val engagement: Float = 0.5f,
        val uncertainty: Float = 0.2f,
        val rapport: Float = 0.0f,
        val energy: Float = 0.5f,
    ) {
        fun toState(): AffectState = AffectState(
            userId = userId,
            lastUpdatedUtc = runCatching { Instant.parse(lastUpdatedUtc) }.getOrDefault(Instant.EPOCH),
            curiosity = curiosity,
            engagement = engagement,
            uncertainty = uncertainty,
            rapport = rapport,
            energy = energy,
        )

        companion object {
            fun of(s: AffectState) = AffectRow(
                userId = s.userId,
                lastUpdatedUtc = s.lastUpdatedUtc.toString(),
                curiosity = s.curiosity,
                engagement = s.engagement,
                uncertainty = s.uncertainty,
                rapport = s.rapport,
                energy = s.energy,
            )
        }
    }
}

/** Persona on the local filesystem, on the same write-then-rename pattern. */
class JsonPersonaStore(directory: String) : IPersonaStore {

    private val dir: File

    init {
        require(directory.isNotBlank()) { "Directory is required." }
        dir = File(directory)
        dir.mkdirs()
    }

    override suspend fun loadAsync(userId: String): PersonaState {
        require(userId.isNotBlank()) { "userId is required." }
        val file = pathFor(userId)
        if (!file.exists()) return PersonaState(userId = userId)
        return try {
            StoreJson.decodeFromString(PersonaRow.serializer(), file.readText()).toState()
        } catch (e: Exception) {
            PersonaState(userId = userId)
        }
    }

    override suspend fun saveAsync(persona: PersonaState) {
        persona.lastUpdatedAt = Instant.now()
        writeAtomically(
            pathFor(persona.userId),
            StoreJson.encodeToString(PersonaRow.serializer(), PersonaRow.of(persona)),
        )
    }

    internal fun pathFor(userId: String): File = File(dir, "persona-" + safe(userId) + ".json")

    @kotlinx.serialization.Serializable
    internal data class PersonaRow(
        val userId: String = "default",
        val lastUpdatedAt: String = "",
        val verbosity: String = "balanced",
        val formality: String = "neutral",
        val preferredLocale: String? = null,
        val topicWeights: Map<String, Float> = emptyMap(),
        val disfavouredTopics: List<String> = emptyList(),
        val totalInteractions: Int = 0,
        val positiveSignals: Int = 0,
        val negativeSignals: Int = 0,
        val traitSummary: String = "",
    ) {
        fun toState(): PersonaState {
            val p = PersonaState(userId = userId)
            p.lastUpdatedAt = runCatching { Instant.parse(lastUpdatedAt) }.getOrDefault(Instant.EPOCH)
            p.verbosity = verbosity
            p.formality = formality
            p.preferredLocale = preferredLocale
            for ((k, v) in topicWeights) p.topicWeights[k] = v
            p.disfavouredTopics.addAll(disfavouredTopics)
            p.totalInteractions = totalInteractions
            p.positiveSignals = positiveSignals
            p.negativeSignals = negativeSignals
            p.traitSummary = traitSummary
            return p
        }

        companion object {
            fun of(p: PersonaState) = PersonaRow(
                userId = p.userId,
                lastUpdatedAt = p.lastUpdatedAt.toString(),
                verbosity = p.verbosity,
                formality = p.formality,
                preferredLocale = p.preferredLocale,
                topicWeights = p.topicWeights.toMap(),
                disfavouredTopics = p.disfavouredTopics.toList(),
                totalInteractions = p.totalInteractions,
                positiveSignals = p.positiveSignals,
                negativeSignals = p.negativeSignals,
                traitSummary = p.traitSummary,
            )
        }
    }
}

/**
 * A user id becomes part of a file name, so anything that is not a letter, a
 * digit, a hyphen or an underscore becomes a hyphen. An id containing a slash
 * would otherwise write outside the folder it was given.
 */
internal fun safe(userId: String): String {
    val cleaned = userId.map { if (it.isLetterOrDigit() || it == '-' || it == '_') it else '-' }
        .joinToString("")
        .trim('-')
    return cleaned.ifEmpty { "default" }
}

/** Episodes in SQL, over a JDBC connection the caller opens. */
class SqliteEpisodicStore(private val conn: Connection) : IEpisodicMemoryStore {

    init {
        conn.createStatement().use {
            it.execute(
                "CREATE TABLE IF NOT EXISTS episodes (" +
                    "id TEXT NOT NULL PRIMARY KEY, user_id TEXT NOT NULL, content TEXT NOT NULL, " +
                    "user_text TEXT NOT NULL, assistant_text TEXT NOT NULL, app_context TEXT, " +
                    "recorded_at_utc TEXT NOT NULL, created_utc TEXT NOT NULL, " +
                    "tags TEXT NOT NULL, importance REAL NOT NULL, embedding BLOB)",
            )
        }
        conn.createStatement().use {
            it.execute("CREATE INDEX IF NOT EXISTS ix_episodes_user ON episodes (user_id, created_utc)")
        }
    }

    override suspend fun save(entry: EpisodicMemoryEntry) {
        conn.prepareStatement(
            "INSERT OR REPLACE INTO episodes (id, user_id, content, user_text, assistant_text, " +
                "app_context, recorded_at_utc, created_utc, tags, importance, embedding) " +
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        ).use { st ->
            st.setString(1, entry.id)
            st.setString(2, entry.userId)
            st.setString(3, entry.content)
            st.setString(4, entry.userText)
            st.setString(5, entry.assistantText)
            st.setString(6, entry.appContext)
            st.setString(7, entry.recordedAtUtc.toString())
            st.setString(8, entry.createdUtc.toString())
            st.setString(9, entry.tags.joinToString(TAG_SEPARATOR.toString()))
            st.setFloat(10, entry.importance)
            st.setBytes(11, floatsToBytes(entry.embedding))
            st.executeUpdate()
        }
    }

    /** Newest first, which is what a screen showing recent turns wants. */
    override suspend fun getRecent(userId: String, limit: Int): List<EpisodicMemoryEntry> {
        val out = mutableListOf<EpisodicMemoryEntry>()
        conn.prepareStatement(
            "SELECT id, user_id, content, user_text, assistant_text, app_context, " +
                "recorded_at_utc, created_utc, tags, importance, embedding FROM episodes " +
                "WHERE user_id = ? ORDER BY created_utc DESC LIMIT ?",
        ).use { st ->
            st.setString(1, userId)
            st.setInt(2, limit)
            st.executeQuery().use { rs ->
                while (rs.next()) {
                    out.add(
                        EpisodicMemoryEntry(
                            id = rs.getString(1),
                            userId = rs.getString(2),
                            content = rs.getString(3) ?: "",
                            embedding = bytesToFloats(rs.getBytes(11)),
                            createdUtc = parseOr(rs.getString(8)),
                            tags = (rs.getString(9) ?: "").split(TAG_SEPARATOR).filter { it.isNotEmpty() },
                            importance = rs.getFloat(10),
                            userText = rs.getString(4) ?: "",
                            assistantText = rs.getString(5) ?: "",
                            appContext = rs.getString(6),
                            recordedAtUtc = parseOr(rs.getString(7)),
                        ),
                    )
                }
            }
        }
        return out
    }

    override suspend fun delete(id: String) {
        conn.prepareStatement("DELETE FROM episodes WHERE id = ?").use {
            it.setString(1, id)
            it.executeUpdate()
        }
    }

    companion object {
        /**
         * UNIT SEPARATOR, not a comma. A tag is free text somebody typed, and
         * splitting on a comma would turn one tag into two the first time
         * anybody used one.
         */
        internal const val TAG_SEPARATOR: Char = ''

        private fun parseOr(raw: String?): Instant =
            runCatching { Instant.parse(raw) }.getOrDefault(Instant.EPOCH)

        /**
         * Little-endian float bits. Written out rather than left to a
         * serialiser, because an embedding that comes back with its bytes the
         * other way round produces plausible nonsense rather than an error.
         */
        internal fun floatsToBytes(v: FloatArray): ByteArray {
            val out = ByteArray(v.size * 4)
            for (i in v.indices) {
                val bits = v[i].toRawBits()
                out[i * 4] = (bits and 0xFF).toByte()
                out[i * 4 + 1] = ((bits ushr 8) and 0xFF).toByte()
                out[i * 4 + 2] = ((bits ushr 16) and 0xFF).toByte()
                out[i * 4 + 3] = ((bits ushr 24) and 0xFF).toByte()
            }
            return out
        }

        internal fun bytesToFloats(b: ByteArray?): FloatArray {
            if (b == null || b.size < 4) return FloatArray(0)
            val out = FloatArray(b.size / 4)
            for (i in out.indices) {
                val bits = (b[i * 4].toInt() and 0xFF) or
                    ((b[i * 4 + 1].toInt() and 0xFF) shl 8) or
                    ((b[i * 4 + 2].toInt() and 0xFF) shl 16) or
                    ((b[i * 4 + 3].toInt() and 0xFF) shl 24)
                out[i] = Float.fromBits(bits)
            }
            return out
        }
    }
}

/** Goals in SQL, over a JDBC connection the caller opens. */
class SqliteGoalStore(private val conn: Connection) : IGoalStore {

    init {
        conn.createStatement().use {
            it.execute(
                "CREATE TABLE IF NOT EXISTS goals (" +
                    "id TEXT NOT NULL PRIMARY KEY, user_id TEXT NOT NULL, title TEXT NOT NULL, " +
                    "description TEXT NOT NULL, status TEXT NOT NULL, priority TEXT NOT NULL, " +
                    "created_utc TEXT NOT NULL, due_utc TEXT, completed_utc TEXT, " +
                    "notes TEXT, progress REAL NOT NULL DEFAULT 0)",
            )
        }
        conn.createStatement().use {
            it.execute("CREATE INDEX IF NOT EXISTS ix_goals_user ON goals (user_id, status)")
        }
    }

    override suspend fun listAsync(userId: String): List<Goal> = query(
        "SELECT * FROM goals WHERE user_id = ?",
        listOf(userId),
    )

    override suspend fun getAsync(id: String): Goal? =
        query("SELECT * FROM goals WHERE id = ?", listOf(id)).firstOrNull()

    override suspend fun upsertAsync(goal: Goal): Goal {
        conn.prepareStatement(
            "INSERT OR REPLACE INTO goals (id, user_id, title, description, status, priority, " +
                "created_utc, due_utc, completed_utc, notes, progress) " +
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        ).use { st ->
            st.setString(1, goal.id)
            st.setString(2, goal.userId)
            st.setString(3, goal.title)
            st.setString(4, goal.description)
            st.setString(5, goal.status.name)
            st.setString(6, goal.priority.name)
            st.setString(7, goal.createdUtc.toString())
            st.setString(8, goal.dueUtc?.toString())
            st.setString(9, goal.completedUtc?.toString())
            st.setString(10, goal.notes)
            st.setFloat(11, goal.progress)
            st.executeUpdate()
        }
        return goal
    }

    override suspend fun deleteAsync(id: String) {
        conn.prepareStatement("DELETE FROM goals WHERE id = ?").use {
            it.setString(1, id)
            it.executeUpdate()
        }
    }

    override suspend fun getActiveAsync(userId: String): List<Goal> = query(
        "SELECT * FROM goals WHERE user_id = ? AND status = ?",
        listOf(userId, GoalStatus.Active.name),
    )

    private fun query(sql: String, args: List<String>): List<Goal> {
        val out = mutableListOf<Goal>()
        conn.prepareStatement(sql).use { st ->
            for ((i, a) in args.withIndex()) st.setString(i + 1, a)
            st.executeQuery().use { rs ->
                while (rs.next()) {
                    out.add(
                        Goal(
                            id = rs.getString("id"),
                            userId = rs.getString("user_id"),
                            title = rs.getString("title"),
                            description = rs.getString("description") ?: "",
                            status = GoalStatus.entries.firstOrNull { it.name == rs.getString("status") }
                                ?: GoalStatus.Active,
                            priority = GoalPriority.entries.firstOrNull { it.name == rs.getString("priority") }
                                ?: GoalPriority.Normal,
                            createdUtc = runCatching { Instant.parse(rs.getString("created_utc")) }
                                .getOrDefault(Instant.EPOCH),
                            dueUtc = rs.getString("due_utc")
                                ?.let { runCatching { Instant.parse(it) }.getOrNull() },
                            completedUtc = rs.getString("completed_utc")
                                ?.let { runCatching { Instant.parse(it) }.getOrNull() },
                            notes = rs.getString("notes"),
                            progress = rs.getFloat("progress"),
                        ),
                    )
                }
            }
        }
        return out
    }
}
