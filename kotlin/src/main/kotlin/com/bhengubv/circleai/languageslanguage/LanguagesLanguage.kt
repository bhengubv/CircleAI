// LanguagesLanguage.kt
//
// Kotlin port of CircleAI.Languages.Language — the C# reference is the EXACT
// spec. The language-pack framework: a per-language knowledge pack contract
// (idioms, cultural notes, prompt tuning) plus registries. The concrete packs
// (Afrikaans, isiZulu, Swahili, …) live in sibling packages and implement
// [ILanguagePack].
//
// Covers (C# file -> Kotlin type):
//   ILanguagePack.cs               -> LanguagePackMetadata, CulturalNote,
//                                     ILanguagePack
//   ILanguagePackRegistry.cs       -> ILanguagePackRegistry
//   DefaultLanguagePackRegistry.cs -> DefaultLanguagePackRegistry
//   LanguagePackHelpers.cs         -> LanguagePackRegistry, LocaleHintMerge
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `Version PackVersion` -> `packVersion: String` (this port represents
//     C# `Version` as a string, matching how `skills.Skills` carries pack
//     versions).
//   * C# `string[]` -> `List<String>`; `IReadOnlyDictionary` -> `Map`.
//   * C# `StringComparer.OrdinalIgnoreCase` dictionaries -> case-insensitive
//     lookup implemented by lower-casing keys (`Locale.ROOT`).
//   * C# `ConcurrentDictionary` (Helpers registry) -> `ConcurrentHashMap`;
//     the `Default` registry's `lock` -> `synchronized`.
//   * `LocaleHintMerge.Merge`: primary overrides secondary, case-insensitive
//     keys, primary wins on collision.

package com.bhengubv.circleai.languageslanguage

import java.util.Locale
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// ILanguagePack (ILanguagePack.cs)
// =====================================================================

/** Metadata for a language pack. */
data class LanguagePackMetadata(
    val bcpTag: String,
    val displayName: String,
    val nativeName: String,
    val primaryRegion: String,
    val spokenInRegions: List<String>,
    val packVersion: String,
)

/** Cultural/contextual note for a specific topic. */
data class CulturalNote(val context: String, val guidance: String, val examples: List<String>)

/**
 * A language-specific knowledge pack. Provides idiomatic expressions, cultural
 * context, and prompt tuning for the on-device LLM to reason correctly in this
 * language.
 */
interface ILanguagePack {
    val metadata: LanguagePackMetadata

    /** Returns the idiomatic translation of a common phrase, or null if not mapped. */
    fun getIdiomaticExpression(phrase: String): String?

    /** Adapts a base system prompt for this language and culture. */
    fun adaptSystemPrompt(basePrompt: String): String

    /** Cultural notes for a given context (e.g. "greeting", "business", "medical"). */
    fun getCulturalNotes(context: String): List<CulturalNote>

    /** Returns a locale-appropriate greeting for the given time of day. */
    fun getGreeting(timeOfDay: String): String

    /** Returns locale-specific number/date/currency formatting hints. */
    fun getLocaleHints(): Map<String, String>
}

// =====================================================================
// ILanguagePackRegistry (ILanguagePackRegistry.cs)
// =====================================================================

/** Registry of all installed language packs. */
interface ILanguagePackRegistry {
    fun register(pack: ILanguagePack)
    fun getByBcpTag(bcpTag: String): ILanguagePack?
    fun getAvailablePacks(): List<LanguagePackMetadata>
    fun hasPack(bcpTag: String): Boolean
}

// =====================================================================
// DefaultLanguagePackRegistry (DefaultLanguagePackRegistry.cs)
// =====================================================================

/** Thread-safe in-memory [ILanguagePackRegistry]. */
class DefaultLanguagePackRegistry : ILanguagePackRegistry {
    private val packs = HashMap<String, ILanguagePack>()
    private val lock = Any()

    override fun register(pack: ILanguagePack) {
        synchronized(lock) { packs[pack.metadata.bcpTag] = pack }
    }

    override fun getByBcpTag(bcpTag: String): ILanguagePack? =
        synchronized(lock) { packs[bcpTag] }

    override fun getAvailablePacks(): List<LanguagePackMetadata> =
        synchronized(lock) { packs.values.map { it.metadata } }

    override fun hasPack(bcpTag: String): Boolean =
        synchronized(lock) { packs.containsKey(bcpTag) }
}

// =====================================================================
// LanguagePackHelpers (LanguagePackHelpers.cs)
// =====================================================================

/**
 * Concurrent language-pack registry with BCP-47 prefix matching and region
 * lookup. Keys are matched case-insensitively (mirrors the C#
 * `StringComparer.OrdinalIgnoreCase`).
 */
class LanguagePackRegistry {
    // Preserve the original (cased) tag on the value; key on its lower-cased form.
    private val byTag = ConcurrentHashMap<String, ILanguagePack>()

    fun register(pack: ILanguagePack) {
        byTag[pack.metadata.bcpTag.lowercase(Locale.ROOT)] = pack
    }

    fun getByExactTag(bcpTag: String): ILanguagePack? =
        if (bcpTag.isBlank()) null else byTag[bcpTag.lowercase(Locale.ROOT)]

    fun getByLanguage(langPrefix: String): ILanguagePack? {
        if (langPrefix.isBlank()) return null
        val prefix = langPrefix.split('-')[0].lowercase(Locale.ROOT)
        return byTag.values.firstOrNull {
            it.metadata.bcpTag.lowercase(Locale.ROOT).startsWith(prefix)
        }
    }

    fun forRegion(region: String): List<ILanguagePack> {
        require(region.isNotBlank()) { "region required" }
        return byTag.values.filter { p ->
            p.metadata.spokenInRegions.any { it.equals(region, ignoreCase = true) }
        }
    }

    fun allTags(): List<String> = byTag.values.map { it.metadata.bcpTag }.sorted()
}

/** Locale-hint merge helper. */
object LocaleHintMerge {
    /**
     * Merges [primary] over [secondary] with case-insensitive keys; [primary]
     * wins on collision.
     */
    fun merge(
        primary: Map<String, String>,
        secondary: Map<String, String>,
    ): Map<String, String> {
        // Case-insensitive accumulation: last write per lower-cased key wins,
        // and we seed with secondary first so primary overrides.
        val merged = LinkedHashMap<String, String>()
        val index = HashMap<String, String>() // lower-key -> stored key
        fun put(k: String, v: String) {
            val lk = k.lowercase(Locale.ROOT)
            val existing = index[lk]
            if (existing != null) {
                merged[existing] = v
            } else {
                merged[k] = v
                index[lk] = k
            }
        }
        for ((k, v) in secondary) put(k, v)
        for ((k, v) in primary) put(k, v)
        return merged
    }
}
