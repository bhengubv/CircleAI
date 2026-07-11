// Personality.kt
//
// Kotlin port of CircleAI.Personality (Persona.cs + IPersonaProvider.cs +
// IPersonaConflictResolver.cs + JsonPersonaProvider.cs + PersonaPromptBuilder.cs)
// — the C# reference is the EXACT spec. The user-DECLARED persona artefact
// (distinct from the AI's LEARNED memory.PersonaState), its storage contract, a
// JSON file provider, the declared-vs-learned conflict resolvers, and the
// prompt-hint builder.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; C# `enum` -> Kotlin `enum class`.
//   * C# `Guid` -> `java.util.UUID`; C# `DateTimeOffset` -> `java.time.Instant`.
//   * C# `Task`/`IAsyncEnumerable` -> `suspend fun` / `Flow`.
//   * The conflict resolver reads the LEARNED formality from
//     com.bhengubv.circleai.memory.PersonaState.formality (already ported).
//   * PersonaPromptBuilder JSON-encodes every user string as a quoted literal —
//     the prompt-injection defence — via kotlinx JsonPrimitive.
//   * JsonPersonaProvider mirrors the C# atomic write-then-rename + per-userId
//     lock. Persona JSON round-trips via kotlinx.serialization; enums serialise
//     as their names (matching JsonStringEnumConverter). The C# diagnostics base
//     class (CircleAIComponentBase) carries no wire semantics and is not ported,
//     matching the established Kotlin convention (see Federation).

package com.bhengubv.circleai.personality

import com.bhengubv.circleai.memory.PersonaState
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.serialization.KSerializer
import kotlinx.serialization.Serializable
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.PrimitiveSerialDescriptor
import kotlinx.serialization.descriptors.SerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonPrimitive
import java.io.File
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Persona.cs
// =====================================================================

/**
 * Declared privacy posture controlling retention + surfacing aggressiveness.
 * Mirrors C# `PrivacyLevel`.
 */
enum class PrivacyLevel {
    /** Minimum retention, no proactive surfacing, no third-party calls without prompt. */
    Strict,

    /** Default. Reasonable retention, helpful proactive prompts. */
    Balanced,

    /** Maximum retention, willing to share personal context across surfaces. */
    Open,
}

/** Declared bounds on conversational formality. Mirrors C# `FormalityRange`. */
@Serializable
data class FormalityRange(val floor: String, val ceiling: String)

/** Serialises [Instant] as an ISO-8601 string (matches C# DateTimeOffset "O"). */
internal object InstantIso8601Serializer : KSerializer<Instant> {
    override val descriptor: SerialDescriptor =
        PrimitiveSerialDescriptor("java.time.Instant", PrimitiveKind.STRING)

    override fun serialize(encoder: Encoder, value: Instant) = encoder.encodeString(value.toString())
    override fun deserialize(decoder: Decoder): Instant = Instant.parse(decoder.decodeString())
}

/** Serialises [UUID] as its canonical string. */
internal object UuidSerializer : KSerializer<UUID> {
    override val descriptor: SerialDescriptor =
        PrimitiveSerialDescriptor("java.util.UUID", PrimitiveKind.STRING)

    override fun serialize(encoder: Encoder, value: UUID) = encoder.encodeString(value.toString())
    override fun deserialize(decoder: Decoder): UUID = UUID.fromString(decoder.decodeString())
}

/**
 * User-declared persona artefact — the structured identity the user chose to
 * share, distinct from the AI's learned [PersonaState]. Mirrors C# `Persona`.
 */
@Serializable
data class Persona(
    @Serializable(with = UuidSerializer::class) val id: UUID,
    val displayName: String,
    val pronouns: String?,
    val identityTags: List<String>,
    val values: List<String>,
    val taboos: List<String>,
    val preferredLocale: String,
    val voicePreference: String?,
    val formality: FormalityRange,
    val privacy: PrivacyLevel,
    @Serializable(with = InstantIso8601Serializer::class) val createdAt: Instant,
    @Serializable(with = InstantIso8601Serializer::class) val updatedAt: Instant,
) {
    companion object {
        /**
         * Creates a persona with balanced privacy, empty tags/values/taboos, an
         * unconstrained "casual".."formal" formality range, and now timestamps.
         * Mirrors C# `Persona.Create`.
         */
        fun create(displayName: String, locale: String): Persona {
            require(displayName.isNotBlank()) { "displayName required" }
            require(locale.isNotBlank()) { "locale required" }
            val now = Instant.now()
            return Persona(
                id = UUID.randomUUID(),
                displayName = displayName,
                pronouns = null,
                identityTags = emptyList(),
                values = emptyList(),
                taboos = emptyList(),
                preferredLocale = locale,
                voicePreference = null,
                formality = FormalityRange("casual", "formal"),
                privacy = PrivacyLevel.Balanced,
                createdAt = now,
                updatedAt = now,
            )
        }
    }
}

// =====================================================================
// IPersonaProvider.cs
// =====================================================================

/**
 * Persists and retrieves user-declared [Persona] documents. Distinct from the
 * memory-layer IPersonaStore (which stores the AI's learned state). Mirrors C#
 * `IPersonaProvider`.
 */
interface IPersonaProvider {
    suspend fun getAsync(userId: String): Persona?
    suspend fun saveAsync(userId: String, persona: Persona): Persona
    suspend fun existsAsync(userId: String): Boolean
    fun exportAllAsync(): Flow<Persona>
}

// =====================================================================
// IPersonaConflictResolver.cs
// =====================================================================

/**
 * Reconciles a user-declared [Persona] with the AI's learned [PersonaState].
 * Implementations must be deterministic and must NEVER mutate either input.
 * Mirrors C# `IPersonaConflictResolver`.
 */
interface IPersonaConflictResolver {
    fun resolve(declared: Persona, learned: PersonaState): Persona
}

/**
 * Default resolver: the declared persona's bounds are hard limits; the learned
 * formality is clamped into the declared [FormalityRange]. The privacy-respecting
 * default — the user's stated preference wins. Mirrors C# `DeclaredWinsResolver`.
 */
class DeclaredWinsResolver : IPersonaConflictResolver {
    override fun resolve(declared: Persona, learned: PersonaState): Persona {
        val clamped = clampFormality(learned.formality, declared.formality)
        if (clamped == learned.formality) {
            // Learned was within bounds — no adjustment to surface.
            return declared
        }
        val range = when (clamped) {
            "casual" -> FormalityRange("casual", declared.formality.ceiling)
            "formal" -> FormalityRange(declared.formality.floor, "formal")
            else -> declared.formality
        }
        return declared.copy(formality = range)
    }

    private companion object {
        fun clampFormality(learned: String, range: FormalityRange): String {
            val learnedRank = rank(learned)
            val floorRank = rank(range.floor)
            val ceilingRank = rank(range.ceiling)
            if (floorRank > ceilingRank) return range.floor
            if (learnedRank < floorRank) return range.floor
            if (learnedRank > ceilingRank) return range.ceiling
            return learned
        }

        fun rank(formality: String): Int = when (formality) {
            "casual" -> 0
            "neutral" -> 1
            "formal" -> 2
            else -> 1 // unknown values rank as neutral
        }
    }
}

/**
 * Alternative resolver: the learned state overrides the declared persona
 * ("privacy mode off"). Still returns the declared persona so identity/taboos/
 * values stay intact. Mirrors C# `LearnedWinsResolver`.
 */
class LearnedWinsResolver : IPersonaConflictResolver {
    override fun resolve(declared: Persona, learned: PersonaState): Persona = declared
}

// =====================================================================
// JsonPersonaProvider.cs
// =====================================================================

/**
 * File-system [IPersonaProvider] storing each persona as
 * `{rootDir}/{userId}.persona.json`. Atomic write-then-rename, per-userId lock.
 * Mirrors C# `JsonPersonaProvider` (minus the diagnostics base class, which
 * carries no wire semantics per the established convention).
 */
class JsonPersonaProvider(rootDirectory: String) : IPersonaProvider {
    private val rootDirectory: File
    private val locks = ConcurrentHashMap<String, Any>()

    init {
        require(rootDirectory.isNotBlank()) { "rootDirectory required" }
        this.rootDirectory = File(rootDirectory)
        this.rootDirectory.mkdirs()
    }

    override suspend fun getAsync(userId: String): Persona? {
        require(userId.isNotBlank()) { "userId required" }
        val path = personaPath(userId)
        if (!path.exists()) return null
        synchronized(lockFor(userId)) {
            return JSON.decodeFromString(Persona.serializer(), path.readText())
        }
    }

    override suspend fun saveAsync(userId: String, persona: Persona): Persona {
        require(userId.isNotBlank()) { "userId required" }
        val refreshed = persona.copy(updatedAt = Instant.now())
        val target = personaPath(userId)
        val tmp = File(target.path + "." + UUID.randomUUID().toString().replace("-", "") + ".tmp")
        synchronized(lockFor(userId)) {
            try {
                tmp.writeText(JSON.encodeToString(Persona.serializer(), refreshed))
                java.nio.file.Files.move(
                    tmp.toPath(),
                    target.toPath(),
                    java.nio.file.StandardCopyOption.REPLACE_EXISTING,
                )
                return refreshed
            } catch (ex: Exception) {
                runCatching { tmp.delete() }
                throw ex
            }
        }
    }

    override suspend fun existsAsync(userId: String): Boolean {
        require(userId.isNotBlank()) { "userId required" }
        return personaPath(userId).exists()
    }

    override fun exportAllAsync(): Flow<Persona> = flow {
        if (!rootDirectory.isDirectory) return@flow
        val files = rootDirectory.listFiles { f -> f.isFile && f.name.endsWith(".persona.json") } ?: return@flow
        for (file in files) {
            val persona = runCatching { JSON.decodeFromString(Persona.serializer(), file.readText()) }.getOrNull()
            if (persona != null) emit(persona)
        }
    }

    private fun lockFor(userId: String): Any = locks.getOrPut(userId) { Any() }

    private fun personaPath(userId: String): File {
        val invalid = charArrayOf(' ', '/', '\\', ':', '*', '?', '"', '<', '>', '|')
        var safe = userId.map { if (it in invalid) '_' else it }.joinToString("")
        if (safe.isBlank()) safe = "default"
        return File(rootDirectory, "$safe.persona.json")
    }

    private companion object {
        val JSON = Json {
            prettyPrint = true
            encodeDefaults = true
            explicitNulls = false
        }
    }
}

// =====================================================================
// PersonaPromptBuilder.cs
// =====================================================================

/**
 * Builds the natural-language system-prompt block describing a [Persona].
 * Returns an empty string when the persona is effectively default. Every
 * user-controlled string is JSON-encoded so embedded quotes / directives are
 * inert text. Mirrors C# `PersonaPromptBuilder`.
 */
object PersonaPromptBuilder {
    fun buildSystemHint(persona: Persona): String {
        if (isEffectivelyDefault(persona)) return ""

        val sb = StringBuilder()
        sb.append("[Persona]")

        sb.append("\nYou are speaking with ")
        sb.append(quote(persona.displayName))
        sb.append('.')

        if (!persona.pronouns.isNullOrBlank()) {
            sb.append(" They identify as ")
            sb.append(quote(persona.pronouns))
            sb.append('.')
        }

        sb.append("\nThey prefer responses in ")
        sb.append(quote(persona.preferredLocale))
        sb.append(", tone between ")
        sb.append(quote(persona.formality.floor))
        sb.append(" and ")
        sb.append(quote(persona.formality.ceiling))
        sb.append('.')

        if (persona.identityTags.isNotEmpty()) {
            sb.append("\nIdentity tags: ")
            sb.append(quoteList(persona.identityTags))
            sb.append('.')
        }

        if (persona.values.isNotEmpty()) {
            sb.append("\nTheir declared values: ")
            sb.append(quoteList(persona.values))
            sb.append('.')
        }

        if (persona.taboos.isNotEmpty()) {
            sb.append("\nAvoid: ")
            sb.append(quoteList(persona.taboos))
            sb.append('.')
        }

        if (!persona.voicePreference.isNullOrBlank()) {
            sb.append("\nPreferred voice tag: ")
            sb.append(quote(persona.voicePreference))
            sb.append('.')
        }

        when (persona.privacy) {
            PrivacyLevel.Strict -> sb.append(
                "\nPrivacy: strict — minimize stored signals, do not surface personal context proactively, " +
                    "and never share personal context across surfaces without explicit prompt.",
            )
            PrivacyLevel.Open -> sb.append(
                "\nPrivacy: open — the user has authorised broader retention and proactive surfacing.",
            )
            PrivacyLevel.Balanced -> { /* no line */ }
        }

        return sb.toString()
    }

    private fun isEffectivelyDefault(p: Persona): Boolean =
        p.pronouns.isNullOrBlank() &&
            p.identityTags.isEmpty() &&
            p.values.isEmpty() &&
            p.taboos.isEmpty() &&
            p.voicePreference.isNullOrBlank() &&
            p.privacy == PrivacyLevel.Balanced &&
            p.formality.floor == "casual" &&
            p.formality.ceiling == "formal"

    /** JSON-encodes [value] into a quoted literal — the prompt-injection defence. */
    private fun quote(value: String): String =
        Json.encodeToString(JsonPrimitive.serializer(), JsonPrimitive(value))

    private fun quoteList(items: List<String>): String =
        items.joinToString(", ") { quote(it) }
}
