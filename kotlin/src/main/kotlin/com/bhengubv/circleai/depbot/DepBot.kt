// DepBot.kt
//
// Kotlin port of CircleAI.DepBot (Contracts.cs + InMemoryDepBot.cs +
// NullImplementations.cs) — the C# reference is the EXACT spec. A filesystem
// dependency analyzer (npm / pypi / cargo / nuget manifests) and a text-rewrite
// updater that edits manifests in place.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `ValueTask` async members -> `suspend fun`.
//   * `System.Text.Json` (package.json parse) -> `kotlinx.serialization.json`.
//   * The C# updater's ProposeUpdatesAsync intentionally returns empty (hosts
//     with registry access fill LatestVersion); ApplyUpdateAsync rewrites the
//     matching manifest entry per ecosystem.

package com.bhengubv.circleai.depbot

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonPrimitive
import java.io.File

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** One declared dependency. Mirrors C# `Dependency`. */
data class Dependency(val ecosystem: String, val name: String, val currentVersion: String, val latestVersion: String?)

/** A proposed dependency version bump. Mirrors C# `DependencyUpdate`. */
data class DependencyUpdate(
    val ecosystem: String,
    val name: String,
    val fromVersion: String,
    val toVersion: String,
    val isBreaking: Boolean,
)

/** Dependency analyzer. Mirrors C# `IDependencyAnalyzer`. */
interface IDependencyAnalyzer {
    val backendId: String
    suspend fun scanAsync(repoPath: String): List<Dependency>
}

/** Dependency updater. Mirrors C# `IDependencyUpdater`. */
interface IDependencyUpdater {
    val backendId: String
    suspend fun proposeUpdatesAsync(repoPath: String): List<DependencyUpdate>
    suspend fun applyUpdateAsync(repoPath: String, update: DependencyUpdate)
}

// =====================================================================
// In-memory implementations (InMemoryDepBot.cs)
// =====================================================================

private val DepJson = Json { ignoreUnknownKeys = true }

/** Scans a repository for declared dependencies. Mirrors C# `FilesystemDependencyAnalyzer`. */
class FilesystemDependencyAnalyzer : IDependencyAnalyzer {
    override val backendId: String get() = "filesystem"

    override suspend fun scanAsync(repoPath: String): List<Dependency> {
        require(repoPath.isNotBlank()) { "repoPath required" }
        val root = File(repoPath)
        if (!root.isDirectory) throw java.io.FileNotFoundException(repoPath)

        val results = ArrayList<Dependency>()

        // npm / yarn — package.json
        for (pkg in root.walkTopDown().filter { it.isFile && it.name == "package.json" }) {
            if (pkg.path.contains("node_modules")) continue
            try {
                val doc = DepJson.parseToJsonElement(pkg.readText()) as? JsonObject ?: continue
                for (key in listOf("dependencies", "devDependencies")) {
                    val section = doc[key] as? JsonObject ?: continue
                    for ((name, ver) in section) {
                        results.add(Dependency("npm", name, ver.jsonPrimitive.contentOrNull ?: "", null))
                    }
                }
            } catch (ex: Exception) {
                // skipping malformed file
            }
        }

        // Python — requirements.txt
        for (req in root.walkTopDown().filter { it.isFile && it.name == "requirements.txt" }) {
            for (rawLine in req.readLines()) {
                val line = rawLine.trim()
                if (line.isEmpty() || line.startsWith("#")) continue
                val match = Regex("""^([A-Za-z0-9_.\-]+)\s*([=<>!~]=?)?\s*([0-9.A-Za-z_\-]+)?""").find(line) ?: continue
                results.add(Dependency("pypi", match.groupValues[1], match.groupValues[3], null))
            }
        }

        // Rust — Cargo.toml [dependencies]
        for (toml in root.walkTopDown().filter { it.isFile && it.name == "Cargo.toml" }) {
            if (toml.path.contains("target")) continue
            var inDepsSection = false
            for (rawLine in toml.readLines()) {
                val line = rawLine.trim()
                if (line.startsWith("[")) {
                    inDepsSection = line.equals("[dependencies]", ignoreCase = true)
                    continue
                }
                if (!inDepsSection || line.isEmpty() || line.startsWith("#")) continue
                val match = Regex("""^([A-Za-z0-9_\-]+)\s*=\s*"([^"]+)"""").find(line) ?: continue
                results.add(Dependency("cargo", match.groupValues[1], match.groupValues[2], null))
            }
        }

        // .NET — *.csproj <PackageReference Include="X" Version="Y" />
        for (csproj in root.walkTopDown().filter { it.isFile && it.extension.equals("csproj", ignoreCase = true) }) {
            val rx = Regex("""<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"""")
            for (m in rx.findAll(csproj.readText())) {
                results.add(Dependency("nuget", m.groupValues[1], m.groupValues[2], null))
            }
        }

        return results
    }
}

/** Text-rewrite updater. Mirrors C# `TextRewriteDependencyUpdater`. */
class TextRewriteDependencyUpdater : IDependencyUpdater {
    override val backendId: String get() = "text-rewrite"

    override suspend fun proposeUpdatesAsync(repoPath: String): List<DependencyUpdate> {
        // Surfaces nothing without a registry; hosts fill LatestVersion and feed
        // a richer update list through this same interface. Matches C#.
        require(repoPath.isNotBlank()) { "repoPath required" }
        return emptyList()
    }

    override suspend fun applyUpdateAsync(repoPath: String, update: DependencyUpdate) {
        require(repoPath.isNotBlank()) { "repoPath required" }
        val root = File(repoPath)
        if (!root.isDirectory) throw java.io.FileNotFoundException(repoPath)

        when (update.ecosystem.lowercase()) {
            "nuget" -> {
                for (csproj in root.walkTopDown().filter { it.isFile && it.extension.equals("csproj", ignoreCase = true) }) {
                    val text = csproj.readText()
                    val pattern = Regex("""<PackageReference\s+Include="${Regex.escape(update.name)}"\s+Version="[^"]+"""")
                    val replacement = """<PackageReference Include="${update.name}" Version="${update.toVersion}""""
                    val updated = pattern.replace(text, Regex.escapeReplacement(replacement))
                    if (updated != text) csproj.writeText(updated)
                }
            }
            "npm" -> {
                for (pkg in root.walkTopDown().filter { it.isFile && it.name == "package.json" }) {
                    if (pkg.path.contains("node_modules")) continue
                    val json = pkg.readText()
                    val pattern = Regex(""""${Regex.escape(update.name)}"\s*:\s*"[^"]+"""")
                    val replacement = """"${update.name}": "${update.toVersion}""""
                    pkg.writeText(pattern.replace(json, Regex.escapeReplacement(replacement)))
                }
            }
            "pypi" -> {
                for (req in root.walkTopDown().filter { it.isFile && it.name == "requirements.txt" }) {
                    val lines = req.readLines().toMutableList()
                    for (i in lines.indices) {
                        val line = lines[i].trim()
                        if (line.startsWith("#") || line.isEmpty()) continue
                        val m = Regex("""^${Regex.escape(update.name)}\s*[=<>!~]=?\s*[0-9.A-Za-z_\-]+""").find(line)
                        if (m != null) lines[i] = "${update.name}==${update.toVersion}"
                    }
                    req.writeText(lines.joinToString(System.lineSeparator()))
                }
            }
        }
    }
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

/** No-op [IDependencyAnalyzer]. Mirrors C# `NullDependencyAnalyzer`. */
class NullDependencyAnalyzer private constructor() : IDependencyAnalyzer {
    override val backendId: String get() = "null"
    override suspend fun scanAsync(repoPath: String): List<Dependency> = emptyList()

    companion object {
        val Instance = NullDependencyAnalyzer()
    }
}

/** No-op [IDependencyUpdater]. Mirrors C# `NullDependencyUpdater`. */
class NullDependencyUpdater private constructor() : IDependencyUpdater {
    override val backendId: String get() = "null"
    override suspend fun proposeUpdatesAsync(repoPath: String): List<DependencyUpdate> = emptyList()
    override suspend fun applyUpdateAsync(repoPath: String, update: DependencyUpdate) {}

    companion object {
        val Instance = NullDependencyUpdater()
    }
}
