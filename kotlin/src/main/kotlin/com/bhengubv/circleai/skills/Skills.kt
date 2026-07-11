// Skills.kt
//
// Kotlin port of CircleAI.Skills — the C# reference is the EXACT spec
// (SkillSource.cs, SkillSummary.cs, SkillDetail.cs, SkillDraft.cs,
// ISkillStore.cs, InMemorySkillStore.cs, SkillContextBuilder.cs,
// SkillPackSource.cs, SkillPackLoader.cs, SkillPackAutoImporter.cs).
//
// Named, tagged capability definitions ("skills") that can be injected into the
// system prompt to guide behaviour for specific tasks. Includes the store
// contract + a thread-safe in-memory store, the system-prompt context builder,
// the SKILL.md frontmatter parser, the pack-source catalogue, and the
// pack-download / import orchestration (network materialisation behind the
// injected [IPackDownloader]).
//
// C# -> Kotlin conventions:
//   Task                 -> suspend fun
//   DateTimeOffset       -> java.time.Instant
//   TimeSpan             -> java.time.Duration
//   IReadOnlyList<T>     -> List<T>
//   ConcurrentDictionary -> synchronized MutableMap
//   Regex                -> kotlin.text.Regex
// The file-tree walk in the C# SkillPackLoader.LoadAsync / HttpPackDownloader
// is host I/O; the pure SKILL.md parser and the import orchestration are ported
// here, with directory materialisation injected via [IPackDownloader].

package com.bhengubv.circleai.skills

import java.time.Duration
import java.time.Instant
import java.util.UUID

// ===========================================================================
// SkillSource  (SkillSource.cs)
// ===========================================================================

/** Indicates where a [SkillDetail] originated. */
enum class SkillSource { File, InMemory, Remote }

// ===========================================================================
// SkillSummary / SkillDetail / SkillDraft
// ===========================================================================

/** Lightweight projection of a [SkillDetail] used in list and search results. */
data class SkillSummary(
    val id: String,
    val name: String,
    val description: String,
    val tags: List<String>,
    val source: SkillSource,
)

/** Full skill record — the complete definition of a single skill. */
data class SkillDetail(
    val id: String,
    val name: String,
    val description: String,
    val instructions: String,
    val tags: List<String>,
    val source: SkillSource,
    val lastModified: Instant,
)

/** Input model for creating or updating a skill via [ISkillStore.upsert]. */
data class SkillDraft(
    val name: String,
    val description: String,
    val instructions: String,
    val tags: List<String>,
)

// ===========================================================================
// ISkillStore  (ISkillStore.cs)
// ===========================================================================

/** Persistent store for skills. */
interface ISkillStore {
    /** Returns all skills as lightweight summaries. */
    suspend fun list(): List<SkillSummary>

    /** Returns the full detail for a single skill by ID, or null if absent. */
    suspend fun get(id: String): SkillDetail?

    /** Returns skills whose Name, Description, or Tags contain [query] (case-insensitive substring). */
    suspend fun search(query: String): List<SkillSummary>

    /** Creates or replaces a skill. A slug ID is auto-generated when [id] is null/blank. */
    suspend fun upsert(id: String?, draft: SkillDraft): SkillDetail

    /** Removes the skill with the given ID. No-op if it does not exist. */
    suspend fun delete(id: String)
}

// ===========================================================================
// InMemorySkillStore  (InMemorySkillStore.cs)
// ===========================================================================

/** Thread-safe in-memory [ISkillStore]. */
class InMemorySkillStore : ISkillStore {
    private val skills = HashMap<String, SkillDetail>()
    private val lock = Any()

    override suspend fun list(): List<SkillSummary> = synchronized(lock) {
        skills.values.map(::toSummary).sortedBy { it.name.lowercase() }
    }

    override suspend fun get(id: String): SkillDetail? {
        require(id.isNotBlank()) { "id required" }
        return synchronized(lock) { skills[id] }
    }

    override suspend fun search(query: String): List<SkillSummary> {
        if (query.isBlank()) return emptyList()
        val q = query.trim()
        return synchronized(lock) {
            skills.values.filter { matchesQuery(it, q) }.map(::toSummary).sortedBy { it.name.lowercase() }
        }
    }

    override suspend fun upsert(id: String?, draft: SkillDraft): SkillDetail {
        val effectiveId = if (id.isNullOrBlank()) generateSlug(draft.name) else id.trim()
        val detail = SkillDetail(
            id = effectiveId,
            name = draft.name,
            description = draft.description,
            instructions = draft.instructions,
            tags = draft.tags,
            source = SkillSource.InMemory,
            lastModified = Instant.now(),
        )
        synchronized(lock) { skills[effectiveId] = detail }
        return detail
    }

    override suspend fun delete(id: String) {
        require(id.isNotBlank()) { "id required" }
        synchronized(lock) { skills.remove(id) }
    }

    private fun toSummary(d: SkillDetail): SkillSummary =
        SkillSummary(d.id, d.name, d.description, d.tags, d.source)

    private fun matchesQuery(s: SkillDetail, query: String): Boolean =
        s.name.contains(query, ignoreCase = true) ||
            s.description.contains(query, ignoreCase = true) ||
            s.tags.any { it.contains(query, ignoreCase = true) }

    companion object {
        private val WHITESPACE = Regex("""\s+""")
        private val NON_SLUG = Regex("""[^a-z0-9\-]""")
        private val MULTI_DASH = Regex("""-{2,}""")

        /** Converts a display name to a URL-safe lowercase slug. "My Skill" -> "my-skill". */
        fun generateSlug(name: String): String {
            if (name.isBlank()) return UUID.randomUUID().toString().replace("-", "")
            var slug = name.trim().lowercase()
            slug = WHITESPACE.replace(slug, "-")
            slug = NON_SLUG.replace(slug, "")
            slug = MULTI_DASH.replace(slug, "-").trim('-')
            return slug.ifEmpty { UUID.randomUUID().toString().replace("-", "") }
        }
    }
}

// ===========================================================================
// SkillContextBuilder  (SkillContextBuilder.cs)
// ===========================================================================

/**
 * Selects the most relevant skills for a user query and formats them as a
 * system-prompt context block.
 */
class SkillContextBuilder(
    private val store: ISkillStore,
    private val maxSkills: Int = 5,
) {
    init {
        require(maxSkills >= 1) { "Must be at least 1." }
    }

    /**
     * Returns a formatted system-prompt block listing the most relevant skills
     * for [userQuery]. Returns an empty string when the store is empty or no
     * skills match.
     */
    suspend fun buildContext(userQuery: String): String {
        if (userQuery.isBlank()) return ""

        val matches = store.search(userQuery)
        val candidates: List<SkillSummary> = if (matches.isNotEmpty()) {
            matches.take(maxSkills)
        } else {
            val all = store.list()
            if (all.isEmpty()) return ""
            all.take(maxSkills)
        }

        val sb = StringBuilder()
        sb.appendLine("## Available Skills")

        for (summary in candidates) {
            val detail = store.get(summary.id) ?: continue
            sb.appendLine()
            sb.appendLine("**${detail.id}** — ${detail.description}")
            if (detail.instructions.isNotBlank()) {
                for (line in detail.instructions.split('\n')) {
                    sb.appendLine("  $line")
                }
            }
        }

        return sb.toString().trimEnd()
    }
}

// ===========================================================================
// SkillPackSource + KnownSkillPacks  (SkillPackSource.cs)
// ===========================================================================

/** Source declaration for a single skill pack. */
data class SkillPackSource(
    val name: String,
    val repoUrl: String,
    val gitRef: String = "main",
    val license: String = "unknown",
    val skillSubdir: String = "",
    val estimatedSkillCount: Int = 0,
    val isDefaultEnabled: Boolean = true,
    val defaultTags: List<String>? = null,
)

/** Default catalogue of skill packs CircleAI imports on first run. */
object KnownSkillPacks {
    val AwesomeAgentSkills = SkillPackSource(
        name = "awesome-agent-skills",
        repoUrl = "https://github.com/bhengubv/awesome-agent-skills",
        license = "Apache-2.0",
        skillSubdir = "skills",
        estimatedSkillCount = 1000,
        defaultTags = listOf("community"),
    )

    val AnthropicCybersecurity = SkillPackSource(
        name = "Anthropic-Cybersecurity-Skills",
        repoUrl = "https://github.com/mukul975/Anthropic-Cybersecurity-Skills",
        license = "Apache-2.0",
        skillSubdir = "skills",
        estimatedSkillCount = 754,
        defaultTags = listOf("security", "mitre"),
    )

    val PrivacyDataProtection = SkillPackSource(
        name = "Privacy-Data-Protection-Skills",
        repoUrl = "https://github.com/mukul975/Privacy-Data-Protection-Skills",
        license = "Apache-2.0",
        skillSubdir = "skills",
        estimatedSkillCount = 282,
        defaultTags = listOf("privacy", "compliance"),
    )

    val ClaudeBugHunter = SkillPackSource(
        name = "Claude-BugHunter",
        repoUrl = "https://github.com/bhengubv/Claude-BugHunter",
        license = "Apache-2.0",
        skillSubdir = "skills",
        estimatedSkillCount = 51,
        defaultTags = listOf("security", "bug-bounty"),
    )

    val Last30Days = SkillPackSource(
        name = "last30days-skill",
        repoUrl = "https://github.com/bhengubv/last30days-skill",
        license = "MIT",
        estimatedSkillCount = 1,
        defaultTags = listOf("research"),
    )

    val EdubaBrand = SkillPackSource(
        name = "eduba-brand",
        repoUrl = "https://github.com/bhengubv/eduba-brand",
        license = "n/a (pattern-port)",
        skillSubdir = ".agents/skills/eduba-brand",
        estimatedSkillCount = 1,
        defaultTags = listOf("branding", "eduba"),
    )

    val CareerOps = SkillPackSource(
        name = "career-ops",
        repoUrl = "https://github.com/bhengubv/career-ops",
        license = "MIT",
        estimatedSkillCount = 14,
        isDefaultEnabled = false,
        defaultTags = listOf("job-search", "career", "thejobcenter"),
    )

    val BuildYourOwnX = SkillPackSource(
        name = "build-your-own-x",
        repoUrl = "https://github.com/bhengubv/build-your-own-x",
        license = "MIT",
        estimatedSkillCount = 0,
        isDefaultEnabled = false,
        defaultTags = listOf("education", "tutorial"),
    )

    /** Every known pack. */
    val All: List<SkillPackSource> = listOf(
        AwesomeAgentSkills,
        AnthropicCybersecurity,
        PrivacyDataProtection,
        ClaudeBugHunter,
        Last30Days,
        EdubaBrand,
        CareerOps,
        BuildYourOwnX,
    )
}

// ===========================================================================
// SkillPackLoader  (SkillPackLoader.cs)
// ===========================================================================

/** Description of a skill pack — name, version, provenance. */
data class SkillPackManifest(
    val name: String,
    val version: String,
    val sourceUrl: String,
    val license: String,
    val skillCount: Int,
)

/** One parsed skill straight from a SKILL.md file. */
data class ParsedSkill(
    val id: String,
    val name: String,
    val description: String,
    val instructions: String,
    val tags: List<String>,
    val sourceFilePath: String,
)

/** Parses SKILL.md files and imports the parsed skills into an [ISkillStore]. */
object SkillPackLoader {
    /** Default file name the loader searches for. */
    const val DEFAULT_SKILL_FILE = "SKILL.md"

    private val FRONTMATTER = Regex(
        """^\s*---\s*\r?\n([\s\S]*?)\r?\n---\s*\r?\n""",
    )

    /**
     * Import every parsed skill into [store] via [ISkillStore.upsert]. Returns
     * a manifest with the count of skills imported.
     */
    suspend fun import(
        store: ISkillStore,
        parsedSkills: Iterable<ParsedSkill>,
        packName: String,
        packVersion: String = "unknown",
        sourceUrl: String = "",
        license: String = "unknown",
    ): SkillPackManifest {
        require(packName.isNotBlank()) { "packName required" }
        var count = 0
        for (parsed in parsedSkills) {
            val tags = (parsed.tags + "pack:${packName.lowercase()}")
                .distinctBy { it.lowercase() }
            val draft = SkillDraft(
                name = parsed.name,
                description = parsed.description,
                instructions = parsed.instructions,
                tags = tags,
            )
            store.upsert(parsed.id, draft)
            count++
        }
        return SkillPackManifest(packName, packVersion, sourceUrl, license, count)
    }

    /**
     * Parse a single SKILL.md file's text. [sourceFilePath] is informational —
     * used as a fallback when no name/heading can be extracted.
     */
    fun parse(content: String, sourceFilePath: String): ParsedSkill {
        require(content.isNotEmpty()) { "content required" }
        val fmMatch = FRONTMATTER.find(content)
        val fmBody: String
        val mdBody: String
        if (fmMatch != null) {
            fmBody = fmMatch.groupValues[1]
            mdBody = content.substring(fmMatch.range.last + 1).trimStart('\r', '\n')
        } else {
            fmBody = ""
            mdBody = content
        }

        val name = extractField(fmBody, "name")
            ?: extractFirstHeading(mdBody)
            ?: fileNameWithoutExtension(sourceFilePath)
        val description = extractField(fmBody, "description") ?: truncate(mdBody, 280)
        val tags = extractTags(fmBody)
        val id = slugify(name)

        return ParsedSkill(
            id = id,
            name = name,
            description = description,
            instructions = mdBody.trim(),
            tags = tags,
            sourceFilePath = sourceFilePath,
        )
    }

    private fun extractField(fmBody: String, field: String): String? {
        if (fmBody.isEmpty()) return null
        val simple = Regex(
            """^\s*${Regex.escape(field)}\s*:\s*(.*)$""",
            RegexOption.MULTILINE,
        ).find(fmBody) ?: return null
        var value = simple.groupValues[1].trim()
        if (value.length >= 2 &&
            ((value[0] == '"' && value[value.length - 1] == '"') ||
                (value[0] == '\'' && value[value.length - 1] == '\''))
        ) {
            value = value.substring(1, value.length - 1)
        }
        return value.ifEmpty { null }
    }

    private fun extractTags(fmBody: String): List<String> {
        if (fmBody.isEmpty()) return emptyList()
        val inline = Regex("""^\s*tags\s*:\s*\[([^\]]*)\]""", RegexOption.MULTILINE).find(fmBody)
        if (inline != null) {
            return inline.groupValues[1]
                .split(',')
                .map { it.trim().trim('\'', '"') }
                .filter { it.isNotEmpty() }
        }
        val block = Regex(
            """^\s*tags\s*:\s*\r?\n((?:\s+-\s+\S+\s*\r?\n?)+)""",
            RegexOption.MULTILINE,
        ).find(fmBody)
        if (block != null) {
            return block.groupValues[1]
                .split('\n')
                .map { it.trim().trimStart('-').trim().trim('\'', '"') }
                .filter { it.isNotEmpty() }
        }
        return emptyList()
    }

    private fun extractFirstHeading(mdBody: String): String? {
        val m = Regex("""^#\s+(.+)$""", RegexOption.MULTILINE).find(mdBody)
        return m?.groupValues?.get(1)?.trim()
    }

    private fun truncate(s: String, max: Int): String {
        val flat = s.replace('\r', ' ').replace('\n', ' ').trim()
        if (flat.length <= max) return flat
        return flat.substring(0, max - 1) + "…"
    }

    private fun fileNameWithoutExtension(path: String): String {
        val base = path.replace('\\', '/').substringAfterLast('/')
        val dot = base.lastIndexOf('.')
        return if (dot > 0) base.substring(0, dot) else base
    }

    private fun slugify(name: String): String {
        val sb = StringBuilder()
        var prevDash = false
        for (ch in name) {
            if (ch.isLetterOrDigit()) {
                sb.append(ch.lowercaseChar())
                prevDash = false
            } else if (!prevDash && sb.isNotEmpty()) {
                sb.append('-')
                prevDash = true
            }
        }
        val slug = sb.toString().trimEnd('-')
        return slug.ifEmpty { "unnamed" }
    }
}

// ===========================================================================
// Pack download / auto-import  (SkillPackAutoImporter.cs)
// ===========================================================================

/**
 * Strategy for materialising a remote pack into a local directory (or, in the
 * deterministic in-memory core, into a set of parsed skills). Tests substitute
 * a fake that returns pre-staged content.
 */
interface IPackDownloader {
    /**
     * Materialise [source] and return the SKILL.md-parsed skills it contains.
     * Implementations honour [cacheTtl] to avoid re-fetching fresh content.
     */
    suspend fun ensure(source: SkillPackSource, cacheTtl: Duration): List<ParsedSkill>
}

/** Settings for [SkillPackAutoImporter]. */
class SkillPackSourcesOptions {
    /** All packs the host knows about. Defaults to [KnownSkillPacks.All]. */
    var sources: MutableList<SkillPackSource> = KnownSkillPacks.All.toMutableList()

    /** When true, import every source where [SkillPackSource.isDefaultEnabled] is set. */
    var importDefaultEnabledPacks: Boolean = true

    /** Pack names to opt in beyond the default-enabled set. */
    var explicitlyEnabled: MutableList<String> = ArrayList()

    /** Reuse cached extractions younger than this without re-downloading. Default 7 days. */
    var cacheTtl: Duration = Duration.ofDays(7)
}

/** Orchestrates download + import for every enabled pack. */
class SkillPackAutoImporter(
    private val store: ISkillStore,
    private val options: SkillPackSourcesOptions,
    private val downloader: IPackDownloader,
) {
    /**
     * Resolve which packs to import, then download and import each. Continues
     * on per-pack failure; returns one manifest per successfully-imported pack.
     */
    suspend fun importEnabled(onError: ((String, Exception) -> Unit)? = null): List<SkillPackManifest> {
        val results = ArrayList<SkillPackManifest>()
        for (source in enumerateEnabled()) {
            try {
                val parsed = downloader.ensure(source, options.cacheTtl)
                val manifest = SkillPackLoader.import(
                    store,
                    parsed,
                    packName = source.name,
                    packVersion = source.gitRef,
                    sourceUrl = source.repoUrl,
                    license = source.license,
                )
                results.add(manifest)
            } catch (ex: Exception) {
                onError?.invoke(source.name, ex)
            }
        }
        return results
    }

    private fun enumerateEnabled(): List<SkillPackSource> {
        val byName = options.sources.associateBy { it.name.lowercase() }
        val seen = HashSet<String>()
        val out = ArrayList<SkillPackSource>()

        if (options.importDefaultEnabledPacks) {
            for (s in options.sources) {
                if (s.isDefaultEnabled && seen.add(s.name.lowercase())) out.add(s)
            }
        }
        for (name in options.explicitlyEnabled) {
            val src = byName[name.lowercase()]
            if (src != null && seen.add(src.name.lowercase())) out.add(src)
        }
        return out
    }
}
