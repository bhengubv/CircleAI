// FileSkillStore.kt
//
// Kotlin port of CircleAI.Skills/FileSkillStore.cs — the C# reference is the
// EXACT spec. An [ISkillStore] backed by *.md files in a directory: each file
// uses YAML front-matter for metadata and a Markdown body for the skill
// instructions (the same format used by Hermes OS1). The shared types
// (SkillDetail, SkillSummary, SkillDraft, SkillSource, ISkillStore) and the slug
// generator live in Skills.kt; this file adds only the disk-backed store.
//
// C# -> Kotlin conventions:
//   Task / ValueTask         -> suspend fun (blocking file I/O off the caller's thread)
//   CancellationToken        -> dropped; suspend + cooperative cancellation via the
//                               coroutine, structured the same as the C# ct checks
//   DateTimeOffset           -> java.time.Instant
//   File.GetLastWriteTimeUtc -> Files.getLastModifiedTime(...).toInstant()
//   Directory.EnumerateFiles -> Files.list(...) filtered to *.md, top-level only
//   StringComparer.OrdinalIgnoreCase dictionary -> case-insensitive key map
//
// The SKILL.md front-matter parser (ParseSkillFile / ParseTagsList) is ported
// key-for-key so a file written by one runtime round-trips through the other.

package com.bhengubv.circleai.skills

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.time.Instant
import kotlin.io.path.exists
import kotlin.io.path.extension
import kotlin.io.path.getLastModifiedTime
import kotlin.io.path.isRegularFile
import kotlin.io.path.name
import kotlin.io.path.nameWithoutExtension
import kotlin.io.path.readText
import kotlin.io.path.writeText

/**
 * [ISkillStore] backed by SKILL.md files in a directory. Each file uses YAML
 * front-matter for metadata and Markdown body for the skill instructions.
 *
 * Expected file format:
 * ```
 * ---
 * id: calendar-summariser
 * name: Calendar Summariser
 * description: Summarises upcoming calendar events into a concise digest
 * tags: [productivity, calendar, summaries]
 * ---
 *
 * ## Instructions
 * When the user asks about their schedule, call the calendar tool…
 * ```
 *
 * The `id` front-matter field is optional; when absent the file name (without
 * extension) is used as the skill ID. Mirrors C# `FileSkillStore`.
 */
class FileSkillStore(directoryPath: String) : ISkillStore {

    private val directory: Path

    init {
        require(directoryPath.isNotBlank()) { "directoryPath" }
        directory = Paths.get(directoryPath)
        Files.createDirectories(directory)
    }

    override suspend fun list(): List<SkillSummary> = withContext(Dispatchers.IO) {
        val results = ArrayList<SkillSummary>()
        for (file in skillFiles()) {
            val detail = readSkillFile(file)
            if (detail != null) results.add(toSummary(detail))
        }
        results.sortedBy { it.name.lowercase() }
    }

    override suspend fun get(id: String): SkillDetail? {
        require(id.isNotBlank()) { "id required" }
        return withContext(Dispatchers.IO) {
            for (file in skillFiles()) {
                val detail = readSkillFile(file)
                if (detail != null && detail.id.equals(id, ignoreCase = true)) return@withContext detail
            }
            null
        }
    }

    override suspend fun search(query: String): List<SkillSummary> {
        if (query.isBlank()) return emptyList()
        val q = query.trim()
        return withContext(Dispatchers.IO) {
            val results = ArrayList<SkillSummary>()
            for (file in skillFiles()) {
                val detail = readSkillFile(file)
                if (detail != null && matchesQuery(detail, q)) results.add(toSummary(detail))
            }
            results.sortedBy { it.name.lowercase() }
        }
    }

    override suspend fun upsert(id: String?, draft: SkillDraft): SkillDetail {
        val effectiveId = if (id.isNullOrBlank()) InMemorySkillStore.generateSlug(draft.name) else id.trim()
        val filePath = directory.resolve("$effectiveId.md")

        val tags = if (draft.tags.isNotEmpty()) "[${draft.tags.joinToString(", ")}]" else "[]"

        val content = buildString {
            appendLine("---")
            appendLine("id: $effectiveId")
            appendLine("name: ${draft.name}")
            appendLine("description: ${draft.description}")
            appendLine("tags: $tags")
            appendLine("---")
            appendLine()
            append(draft.instructions)
        }

        withContext(Dispatchers.IO) { filePath.writeText(content, Charsets.UTF_8) }

        return SkillDetail(
            id = effectiveId,
            name = draft.name,
            description = draft.description,
            instructions = draft.instructions,
            tags = draft.tags,
            source = SkillSource.File,
            lastModified = Instant.now(),
        )
    }

    override suspend fun delete(id: String) {
        require(id.isNotBlank()) { "id required" }
        withContext(Dispatchers.IO) {
            val filePath = directory.resolve("$id.md")
            if (filePath.exists()) Files.delete(filePath)
        }
    }

    // ------------------------------------------------------------------
    // Parsing
    // ------------------------------------------------------------------

    private fun skillFiles(): List<Path> {
        if (!directory.exists()) return emptyList()
        Files.list(directory).use { stream ->
            return stream
                .filter { it.isRegularFile() }
                .filter { it.extension.equals("md", ignoreCase = true) }
                .sorted()
                .toList()
        }
    }

    private fun readSkillFile(filePath: Path): SkillDetail? {
        val content = try {
            filePath.readText(Charsets.UTF_8)
        } catch (_: Exception) {
            return null
        }
        return parseSkillFile(content, filePath.nameWithoutExtension, filePath)
    }

    private fun toSummary(d: SkillDetail): SkillSummary =
        SkillSummary(d.id, d.name, d.description, d.tags, d.source)

    private fun matchesQuery(s: SkillDetail, query: String): Boolean =
        s.name.contains(query, ignoreCase = true) ||
            s.description.contains(query, ignoreCase = true) ||
            s.tags.any { it.contains(query, ignoreCase = true) }

    companion object {
        /**
         * Parse a SKILL.md file's text. [fileNameWithoutExt] is the fallback id
         * when the front-matter omits `id`; [filePath] (optional) sources the
         * last-modified timestamp. Mirrors C# `FileSkillStore.ParseSkillFile`.
         */
        fun parseSkillFile(content: String, fileNameWithoutExt: String, filePath: Path? = null): SkillDetail? {
            if (content.isBlank()) return null

            // Locate the YAML front-matter block between the first two "---" lines.
            val lines = content.replace("\r\n", "\n").split('\n')
            if (lines.size < 2 || lines[0].trim() != "---") return null

            var frontMatterEnd = -1
            for (i in 1 until lines.size) {
                if (lines[i].trim() == "---") {
                    frontMatterEnd = i
                    break
                }
            }
            if (frontMatterEnd < 0) return null

            // Parse front-matter key: value pairs (case-insensitive keys).
            val meta = HashMap<String, String>()
            for (i in 1 until frontMatterEnd) {
                val line = lines[i]
                val colon = line.indexOf(':')
                if (colon < 0) continue
                val key = line.substring(0, colon).trim().lowercase()
                val value = line.substring(colon + 1).trim()
                meta[key] = value
            }

            val id = meta["id"]?.takeIf { it.isNotBlank() } ?: fileNameWithoutExt
            val name = meta["name"] ?: id
            val description = meta["description"] ?: ""
            val tags = parseTagsList(meta["tags"] ?: "")

            // Everything after the closing "---" is the instructions body.
            val instructions = lines.drop(frontMatterEnd + 1).joinToString("\n").trim()

            val lastModified =
                if (filePath != null && filePath.exists()) filePath.getLastModifiedTime().toInstant()
                else Instant.now()

            return SkillDetail(id, name, description, instructions, tags, SkillSource.File, lastModified)
        }

        /**
         * Parse a YAML inline list like `[a, b, c]` or a bare scalar. Mirrors C#
         * `FileSkillStore.ParseTagsList`.
         */
        private fun parseTagsList(raw: String): List<String> {
            if (raw.isBlank()) return emptyList()
            var s = raw.trim()
            if (s.startsWith('[') && s.endsWith(']')) s = s.substring(1, s.length - 1)
            return s.split(',')
                .map { it.trim() }
                .filter { it.isNotEmpty() }
        }
    }
}
