// SkillsStores.kt
//
// What the assistant can actually do, read from the capability manifest; and
// fetching a skill pack.
//
// THE POINT IS HONESTY, NOT MARKETING. The skill store existed and the service
// already injected skill context from it — nothing ever populated it. Filling it
// from the manifest means the assistant answers "can you do voice?" from the
// repository rather than from optimism. Every entry carries its STATUS, and
// non-shipping entries say so in words the model cannot miss: a catalogue that
// let it claim planned features would be a machine for confident lying.
//
// Ported from src/CircleAI.Skills/{CapabilityManifestSkillStore,
// SkillPackAutoImporter}.cs.

package com.bhengubv.circleai.skills

import java.io.File
import java.time.Instant
import java.util.Locale

/** Read-only by design: if the assistant could edit its own capability list it
 *  could write itself a capability it does not have, and then cite it. */
class SkillManifestReadOnlyException :
    Exception(
        "Capabilities come from the capability manifest and are verified against the " +
            "repository. Editing them at runtime would let the assistant claim things the " +
            "code cannot back up. Change the manifest instead."
    )

class CapabilityManifestSkillStore(private val skills: List<SkillDetail>) : ISkillStore {

    override suspend fun list(): List<SkillSummary> = skills.map(::summary)

    override suspend fun get(id: String): SkillDetail? =
        skills.firstOrNull { it.id.equals(id, ignoreCase = true) }

    override suspend fun search(query: String): List<SkillSummary> {
        val q = query.trim()
        if (q.isEmpty()) return emptyList()

        // ID FIRST: it is the handle the compact listing hands out, so a lookup
        // by id has to resolve. Kept identical to the other stores.
        fun hit(s: SkillDetail) =
            s.id.contains(q, true) || s.name.contains(q, true) ||
                s.description.contains(q, true) || s.tags.any { it.contains(q, true) }

        return skills.filter(::hit).map(::summary)
    }

    override suspend fun upsert(id: String?, draft: SkillDraft): SkillDetail =
        throw SkillManifestReadOnlyException()

    override suspend fun delete(id: String) = throw SkillManifestReadOnlyException()

    private fun summary(s: SkillDetail) =
        SkillSummary(s.id, s.name, s.description, s.tags, s.source)

    companion object {
        /** Empty. A manifest that will not parse yields an empty store rather
         *  than throwing: missing self-knowledge must never stop the assistant
         *  answering ordinary questions. */
        val empty = CapabilityManifestSkillStore(emptyList())

        fun fromJson(json: String): CapabilityManifestSkillStore =
            CapabilityManifestSkillStore(parse(json))

        internal fun parse(json: String): List<SkillDetail> {
            if (json.isBlank()) return emptyList()

            // Each capability object, scanned rather than fully parsed: this
            // package has no JSON dependency and the manifest shape is fixed.
            return Regex("\\{[^{}]*\"Id\"\\s*:\\s*\"[^\"]+\"[^{}]*}")
                .findAll(json)
                .mapNotNull { m ->
                    val body = m.value
                    val id = str(body, "Id") ?: return@mapNotNull null
                    val name = str(body, "Name") ?: return@mapNotNull null
                    val status = str(body, "Status") ?: "unknown"
                    val summary = str(body, "Summary").orEmpty()

                    SkillDetail(
                        id = id,
                        name = name,
                        // The status leads the DESCRIPTION too, not just the
                        // instructions: a compact listing shows descriptions
                        // only, and that is where an unqualified claim slips
                        // through.
                        description = "[$status] $summary",
                        instructions = instructions(status, summary),
                        tags = tags(id, status),
                        source = SkillSource.InMemory,
                        lastModified = Instant.EPOCH
                    )
                }.toList()
        }

        /**
         * The instruction text the model actually reads.
         *
         * Written AT the model, in the imperative, because a status word alone is
         * not an instruction — "scaffold" means nothing to a model that has never
         * seen this repo, and it will helpfully assume the feature works.
         */
        internal fun instructions(status: String, summary: String): String = buildString {
            appendLine("Status: $status")
            when (status) {
                "shipping" ->
                    appendLine("This works and is covered by tests. You may state it plainly.")
                "partial" ->
                    appendLine("This works WITH LIMITS. State the limits when they are relevant; do not oversell it.")
                "scaffold" ->
                    appendLine("NOT USABLE YET — contracts exist but there is no working implementation. Do NOT claim you can do this.")
                "planned" ->
                    appendLine("DOES NOT EXIST YET. Do NOT claim you can do this. Say it is planned.")
                "rejected" ->
                    appendLine("DELIBERATELY NOT BUILT. Do NOT claim you can do this, and do not offer to add it.")
            }
            if (summary.isNotBlank()) { appendLine(); appendLine(summary) }
        }.trimEnd()

        /** Status first, then the id's own segments. Status as a TAG is what
         *  makes "what can you not do yet" a searchable question rather than one
         *  the model has to reason its way to. */
        internal fun tags(id: String, status: String): List<String> =
            (listOf(status) + id.split('.').filter { it.isNotEmpty() })
                .distinctBy { it.lowercase(Locale.ROOT) }

        private fun str(body: String, key: String): String? =
            Regex("\"$key\"\\s*:\\s*\"([^\"]*)\"").find(body)?.groupValues?.get(1)
                ?.takeIf { it.isNotBlank() }
    }
}

/**
 * Fetches a skill pack over HTTP and stages it.
 *
 * BOTH THE TRANSPORT AND THE EXTRACTOR ARE FUNCTIONS. A host already owns an
 * HTTP client configured with its own timeouts and pinning, and archive
 * extraction differs per platform — so neither is hidden in here where it would
 * silently bypass what the host configured.
 */
class HttpPackDownloader(
    private val fetch: suspend (url: String) -> Pair<ByteArray, Int>,
    /** Extracts an archive into a directory. Returns false when it cannot. */
    private val extract: ((archive: ByteArray, directory: String) -> Boolean)? = null,
    private val now: () -> Instant = { Instant.now() }
) : IPackDownloader {

    override suspend fun ensure(
        source: SkillPackSource, cacheRoot: String, cacheTtlMillis: Long
    ): String {
        require(cacheRoot.isNotBlank()) { "A cache directory is required." }

        val packDir = File(cacheRoot, sanitise(source.name))
        val stamp = File(packDir, ".stamp")

        // A FRESH CACHE SHORT-CIRCUITS EVERYTHING. Skill packs change rarely and
        // a host that re-fetches on every launch costs somebody data for bytes
        // they already have.
        if (stamp.isFile && now().toEpochMilli() - stamp.lastModified() <= cacheTtlMillis) {
            return packDir.path
        }

        val (body, status) = fetch(tarballUrl(source))
        require(status in 200..299) {
            "Skill pack '${source.name}' could not be fetched (HTTP $status)."
        }
        val extractor = extract
            ?: error("This host has no archive extractor wired. Supply one to HttpPackDownloader.")

        // EXTRACTED TO A STAGING DIRECTORY AND MOVED. A failure part-way leaves
        // the previous pack intact rather than a half-written one the loader
        // then reads.
        val stage = File(packDir.path + ".stage")
        stage.deleteRecursively()
        stage.mkdirs()

        if (!extractor(body, stage.path)) {
            stage.deleteRecursively()
            error("Skill pack '${source.name}' could not be extracted.")
        }

        // A GitHub tarball nests its content under <repo>-<ref>/. Flattened,
        // because the loader looks for SKILL.md at the top and would otherwise
        // find an empty pack and report nothing wrong.
        val entries = stage.listFiles().orEmpty()
        val staged = if (entries.none { it.isFile } && entries.size == 1) entries[0] else stage

        packDir.deleteRecursively()
        staged.renameTo(packDir)
        stage.deleteRecursively()
        File(packDir, ".stamp").writeText(now().toString())

        return packDir.path
    }

    companion object {
        /** `https://github.com/<owner>/<repo>/archive/<ref>.tar.gz` */
        internal fun tarballUrl(source: SkillPackSource): String =
            "${source.repoUrl.trimEnd('/')}/archive/${source.gitRef}.tar.gz"

        /** Anything that is not a letter, digit, dash or underscore becomes a
         *  dash — a pack called "SA / Public Sector" must not create nested
         *  directories or escape the cache root. */
        internal fun sanitise(name: String): String =
            name.map { if (it.isLetterOrDigit() || it == '-' || it == '_') it else '-' }
                .joinToString("").trim('-').lowercase(Locale.ROOT).ifEmpty { "pack" }
    }
}
