// Visualization.kt
//
// Kotlin port of CircleAI.Visualization (Contracts.cs + InMemoryVisualization.cs
// + NullImplementations.cs) — the C# reference is the EXACT spec. A dashboard
// definition store, an OpenAPI-normalising doc builder, and a static-site
// builder that renders a JSON page-spec into in-memory files.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `ReadOnlyMemory<byte>` -> `ByteArray`.
//   * C# `ValueTask` async members -> `suspend fun`.
//   * C# `System.Text.Json` -> `kotlinx.serialization.json` (already a build dep).
//   * The doc builder extracts info.title (default "API"), lowercases + hyphenates
//     for the id, and re-serialises the root element to canonical compact JSON.
//   * The site builder requires a `pages[]` array of `{path, html}` objects and
//     UTF-8-encodes each html into a file entry.

package com.bhengubv.circleai.visualization

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.util.UUID

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** A stored dashboard definition. Mirrors C# `DashboardDefinition`. */
data class DashboardDefinition(val dashboardId: String, val title: String, val jsonSpec: String)

/** A generated API documentation artefact. Mirrors C# `ApiDoc`. */
data class ApiDoc(val docId: String, val title: String, val openApiJson: String)

/** A rendered static site as a map of path -> bytes. Mirrors C# `GeneratedSite`. */
data class GeneratedSite(val siteId: String, val files: Map<String, ByteArray>)

/** Dashboard definition store. Mirrors C# `IDashboardDefinitionStore`. */
interface IDashboardDefinitionStore {
    val backendId: String
    suspend fun upsertAsync(d: DashboardDefinition)
    suspend fun getAsync(id: String): DashboardDefinition?
    suspend fun listAsync(): List<DashboardDefinition>
}

/** OpenAPI doc builder. Mirrors C# `IApiDocBuilder`. */
interface IApiDocBuilder {
    val backendId: String
    suspend fun buildAsync(openApiSpec: String): ApiDoc
}

/** Static-site builder. Mirrors C# `ISiteBuilder`. */
interface ISiteBuilder {
    val backendId: String
    suspend fun buildAsync(siteSpec: String): GeneratedSite
}

// =====================================================================
// In-memory implementations (InMemoryVisualization.cs)
// =====================================================================

private val VizJson = Json { ignoreUnknownKeys = true }

/** Thread-safe in-memory dashboard store. Mirrors C# `InMemoryDashboardStore`. */
class InMemoryDashboardStore : IDashboardDefinitionStore {
    private val items = java.util.concurrent.ConcurrentHashMap<String, DashboardDefinition>()

    override val backendId: String get() = "in-memory"

    override suspend fun upsertAsync(d: DashboardDefinition) {
        require(d.dashboardId.isNotBlank()) { "DashboardId required" }
        items[d.dashboardId] = d
    }

    override suspend fun getAsync(id: String): DashboardDefinition? {
        require(id.isNotBlank()) { "id required" }
        return items[id]
    }

    override suspend fun listAsync(): List<DashboardDefinition> = items.values.toList()
}

/**
 * Normalising API-doc builder. Parses the OpenAPI JSON, extracts info.title, and
 * re-serialises the root element to canonical compact JSON. Mirrors C#
 * `JsonApiDocBuilder`.
 */
class JsonApiDocBuilder : IApiDocBuilder {
    override val backendId: String get() = "json-normaliser"

    override suspend fun buildAsync(openApiSpec: String): ApiDoc {
        require(openApiSpec.isNotBlank()) { "openApiSpec required" }
        val root = VizJson.parseToJsonElement(openApiSpec)
        val title = (root as? JsonObject)
            ?.get("info")?.let { it as? JsonObject }
            ?.get("title")?.jsonPrimitive?.contentOrNull
            ?: "API"
        val docId = title.replace(' ', '-').lowercase()
        val canonical = VizJson.encodeToString(kotlinx.serialization.json.JsonElement.serializer(), root)
        return ApiDoc(docId, title, canonical)
    }
}

/**
 * Builds a static site from a JSON spec `{"pages":[{"path":"index.html","html":"..."}]}`.
 * Outputs the rendered files in-memory. Mirrors C# `StaticSiteBuilder`.
 */
class StaticSiteBuilder : ISiteBuilder {
    override val backendId: String get() = "static"

    override suspend fun buildAsync(siteSpec: String): GeneratedSite {
        require(siteSpec.isNotBlank()) { "siteSpec required" }
        val root = VizJson.parseToJsonElement(siteSpec) as? JsonObject
            ?: throw IllegalArgumentException("siteSpec must contain a pages[] array.")
        val pages = root["pages"] as? JsonArray
            ?: throw IllegalArgumentException("siteSpec must contain a pages[] array.")

        val files = LinkedHashMap<String, ByteArray>()
        for (page in pages) {
            val obj = page as? JsonObject ?: continue
            val path = obj["path"]?.jsonPrimitive?.contentOrNull
            val html = obj["html"]?.jsonPrimitive?.contentOrNull
            if (path.isNullOrBlank() || html == null) continue
            files[path] = html.toByteArray(Charsets.UTF_8)
        }

        val siteId = "site-" + UUID.randomUUID().toString().replace("-", "")
        return GeneratedSite(siteId, files)
    }
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

/** No-op [IDashboardDefinitionStore]. Mirrors C# `NullDashboardDefinitionStore`. */
class NullDashboardDefinitionStore private constructor() : IDashboardDefinitionStore {
    override val backendId: String get() = "null"
    override suspend fun upsertAsync(d: DashboardDefinition) {}
    override suspend fun getAsync(id: String): DashboardDefinition? = null
    override suspend fun listAsync(): List<DashboardDefinition> = emptyList()

    companion object {
        val Instance = NullDashboardDefinitionStore()
    }
}

/** No-op [IApiDocBuilder] returning an empty doc. Mirrors C# `NullApiDocBuilder`. */
class NullApiDocBuilder private constructor() : IApiDocBuilder {
    override val backendId: String get() = "null"
    override suspend fun buildAsync(openApiSpec: String): ApiDoc =
        ApiDoc(EMPTY_GUID, "", "{}")

    companion object {
        private const val EMPTY_GUID = "00000000-0000-0000-0000-000000000000"
        val Instance = NullApiDocBuilder()
    }
}

/** No-op [ISiteBuilder] returning an empty site. Mirrors C# `NullSiteBuilder`. */
class NullSiteBuilder private constructor() : ISiteBuilder {
    override val backendId: String get() = "null"
    override suspend fun buildAsync(siteSpec: String): GeneratedSite =
        GeneratedSite(EMPTY_GUID, emptyMap())

    companion object {
        private const val EMPTY_GUID = "00000000-0000-0000-0000-000000000000"
        val Instance = NullSiteBuilder()
    }
}
