// GenerativeUi.kt
//
// Kotlin port of the CircleAI.Hosting.GenerativeUI surface — the C# reference is
// the EXACT spec (IGenerativeUIRenderer.cs, JsonRenderParser.cs).
//
// (2.0.2) Generative UI plug point: the AI emits JSON constrained to a typed
// catalog; the host renders. JsonRenderParser validates against a UiCatalog so
// the LLM can't smuggle untyped components past the host. Uses kotlinx
// serialization for JSON parsing (C# uses System.Text.Json.JsonDocument).

package com.bhengubv.circleai.hosting

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.double
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.long
import kotlinx.serialization.json.longOrNull

// =====================================================================
// Component + catalog types (IGenerativeUIRenderer.cs)
// =====================================================================

/**
 * One UI element produced by a generative-UI model. Mirrors C# `UiComponent`
 * record. [properties] values are `Any?` (matching how the C# `object?` bag
 * round-trips through the parser).
 */
data class UiComponent(
    val kind: String,
    val properties: Map<String, Any?>,
    val children: List<UiComponent>? = null,
)

/**
 * Catalog entry — declares the allowed kinds + their properties. The LLM is
 * constrained to emit only kinds present in the catalog. Mirrors C#
 * `UiCatalogEntry` record.
 */
data class UiCatalogEntry(
    val kind: String,
    val description: String,
    val allowedProperties: Map<String, String>,
    val allowsChildren: Boolean = false,
)

/**
 * Pre-canned component catalogs the hosting layer can ship out of the box.
 * Mirrors C# `UiCatalogs`.
 */
object UiCatalogs {
    /**
     * Minimal "chat assistant tool output" catalog. Covers card / list / button /
     * textBlock / image. Mirrors the C# `Default` set (order preserved).
     */
    val Default: List<UiCatalogEntry> = listOf(
        UiCatalogEntry(
            "card",
            "A bordered container with a title and body. May contain children.",
            linkedMapOf("title" to "string", "caption" to "string?"),
            allowsChildren = true,
        ),
        UiCatalogEntry(
            "list",
            "An ordered or unordered list. Children are the list items.",
            linkedMapOf("ordered" to "boolean"),
            allowsChildren = true,
        ),
        UiCatalogEntry(
            "button",
            "A tappable button. Emit an action identifier when clicked.",
            linkedMapOf("label" to "string", "action" to "string", "style" to "string?"),
        ),
        UiCatalogEntry(
            "textBlock",
            "Inline text content, optionally markdown.",
            linkedMapOf("text" to "string", "markdown" to "boolean?"),
        ),
        UiCatalogEntry(
            "image",
            "An image displayed from a URL or data-URI.",
            linkedMapOf("src" to "string", "alt" to "string?"),
        ),
    )
}

/**
 * (2.0.2) Renderer contract. Consumers materialise [UiComponent] records into a
 * native UI. Mirrors C# `IGenerativeUIRenderer`.
 */
interface IGenerativeUIRenderer {
    /** Render a single root component. */
    suspend fun renderAsync(root: UiComponent)
}

/**
 * Default no-op renderer for tests and headless server scenarios. Holds the last
 * rendered component for assertion. Mirrors C# `RecordingGenerativeUIRenderer`.
 */
class RecordingGenerativeUIRenderer : IGenerativeUIRenderer {
    var lastRendered: UiComponent? = null
        private set
    var renderCount: Int = 0
        private set

    override suspend fun renderAsync(root: UiComponent) {
        lastRendered = root
        renderCount++
    }
}

// =====================================================================
// JsonRenderParser (JsonRenderParser.cs)
// =====================================================================

/**
 * (2.0.2) Strict JSON -> [UiComponent] parser. Rejects any kind not in the
 * catalog and any property not declared on its kind. Mirrors C# `JsonRenderParser`.
 */
object JsonRenderParser {
    private val json = Json { ignoreUnknownKeys = true }

    /**
     * Parse one JSON document into a [UiComponent] tree.
     *
     * @param strict When true, unknown kinds throw. When false, unknown kinds
     *   become a textBlock with the raw marker for debugging.
     */
    fun parse(json: String, catalog: List<UiCatalogEntry>, strict: Boolean = true): UiComponent {
        require(json.isNotEmpty()) { "json is required" }
        val root = this.json.parseToJsonElement(json)
        val index = catalog.associateBy { it.kind.lowercase() }
        return parseElement(root, index, strict)
    }

    private fun parseElement(
        el: JsonElement,
        catalog: Map<String, UiCatalogEntry>,
        strict: Boolean,
    ): UiComponent {
        if (el !is JsonObject) {
            throw IllegalStateException("Expected JSON object, got ${el::class.simpleName}.")
        }

        val kind = (el["kind"] as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull
        if (kind.isNullOrEmpty()) {
            throw IllegalStateException("Component missing required 'kind' field.")
        }

        val entry = catalog[kind.lowercase()]
        if (entry == null) {
            if (strict) throw IllegalStateException("Unknown component kind '$kind'.")
            return UiComponent(
                kind = "textBlock",
                properties = linkedMapOf(
                    "text" to "[unknown kind '$kind']",
                    "markdown" to false,
                ),
            )
        }

        val props = LinkedHashMap<String, Any?>()
        (el["properties"] as? JsonObject)?.let { propsEl ->
            for ((name, value) in propsEl) {
                if (strict && !entry.allowedProperties.containsKey(name)) {
                    throw IllegalStateException("Component '$kind' does not allow property '$name'.")
                }
                props[name] = toManaged(value)
            }
        }

        var children: List<UiComponent>? = null
        (el["children"] as? JsonArray)?.let { childEl ->
            if (!entry.allowsChildren) {
                if (strict) throw IllegalStateException("Component '$kind' does not allow children.")
            } else {
                children = childEl.map { parseElement(it, catalog, strict) }
            }
        }

        return UiComponent(kind, props, children)
    }

    private fun toManaged(v: JsonElement): Any? = when (v) {
        is JsonNull -> null
        is JsonObject -> v.entries.associate { it.key to toManaged(it.value) }
        is JsonArray -> v.map { toManaged(it) }
        is JsonPrimitive -> when {
            v.isString -> v.content
            v.booleanOrNull != null -> v.boolean
            v.longOrNull != null -> v.long
            v.doubleOrNull != null -> v.double
            else -> v.content
        }
    }

    /**
     * Build a system-prompt snippet that describes the catalog to the model.
     * Mirrors C# `DescribeCatalogForPrompt`.
     */
    fun describeCatalogForPrompt(catalog: List<UiCatalogEntry>): String {
        val sb = StringBuilder()
        sb.appendLine("You may respond with a single JSON object describing one UI component.")
        sb.appendLine("Allowed shape: { \"kind\": string, \"properties\": { ... }, \"children\"?: [ ... ] }")
        sb.appendLine()
        sb.appendLine("Allowed kinds:")
        for (e in catalog) {
            sb.appendLine("- ${e.kind} — ${e.description}")
            for ((name, type) in e.allowedProperties) {
                sb.appendLine("    - $name: $type")
            }
            if (e.allowsChildren) sb.appendLine("    - children: array of components")
        }
        return sb.toString()
    }
}
