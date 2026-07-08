// ToolCatalog.kt
//
// Kotlin port of the CircleAI.Hosting.Tools catalog surface — the C# reference
// is the EXACT spec (IToolDescriptor.cs, IToolCatalog.cs, InMemoryToolCatalog.cs).
//
// The searchable registry of every tool the host knows about, plus the
// provider/executor contracts. Keyword-substring search scoring (name 5 / tags 3
// / description 2) is byte-identical to the C# reference.

package com.bhengubv.circleai.hosting

import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// IToolDescriptor (IToolDescriptor.cs)
// =====================================================================

/**
 * Describes one tool callable by an LLM. Data-only — execution lives in
 * [IToolExecutor]. Mirrors C# `ToolDescriptor` record.
 */
data class ToolDescriptor(
    val name: String,
    val description: String,
    val provider: String,
    val jsonSchema: String = "",
    val authScheme: String = "none",
    val tags: List<String>? = null,
    val examples: List<String>? = null,
)

/**
 * Result of one tool execution. Mirrors C# `ToolExecutionResult` record.
 */
data class ToolExecutionResult(
    val success: Boolean,
    val result: Any? = null,
    val error: String? = null,
    val durationMs: Long = 0,
)

// =====================================================================
// IToolCatalog + IToolProvider + IToolExecutor (IToolCatalog.cs)
// =====================================================================

/**
 * The CircleAI tool catalog. Searchable by name, tag, and natural-language query.
 * Mirrors C# `IToolCatalog`.
 */
interface IToolCatalog {
    /** How many tools are currently registered. */
    val count: Int

    /** Register or replace one tool. Idempotent for same name. */
    suspend fun upsertAsync(descriptor: ToolDescriptor)

    /** Remove a tool by name. Idempotent. */
    suspend fun removeAsync(name: String): Boolean

    /** Get exactly one descriptor by name, or null when unknown. */
    suspend fun getAsync(name: String): ToolDescriptor?

    /** Enumerate every registered descriptor. Order is stable within one process lifetime. */
    fun list(): List<ToolDescriptor>

    /** Free-form keyword-substring search over name + description + tags. */
    fun search(query: String, topK: Int = 10): List<ToolDescriptor>

    /** Filter by provider id (exact match, case-insensitive). */
    fun listByProvider(provider: String): List<ToolDescriptor>
}

/**
 * A source of tools. The provider registers its tool descriptors against an
 * [IToolCatalog] at startup and routes executions through [IToolExecutor].
 * Mirrors C# `IToolProvider`.
 */
interface IToolProvider {
    /** Stable provider id, e.g. `"local"` / `"composio"` / `"mcp"`. */
    val providerId: String

    /** Discover every tool this provider exposes. */
    suspend fun discoverAsync(): List<ToolDescriptor>

    /** Cheap availability probe. */
    suspend fun isAvailableAsync(): Boolean
}

/**
 * Sandboxed execution surface. Implementations route the call to the owning
 * provider, enforce arg-schema validation, and wrap in an isolation policy.
 * Mirrors C# `IToolExecutor`.
 */
interface IToolExecutor {
    /**
     * Execute one tool call. [argumentsJson] is the model-emitted JSON object;
     * the executor validates against [ToolDescriptor.jsonSchema] before dispatch.
     */
    suspend fun executeAsync(tool: ToolDescriptor, argumentsJson: String): ToolExecutionResult
}

// =====================================================================
// InMemoryToolCatalog (InMemoryToolCatalog.cs)
// =====================================================================

/**
 * Default [IToolCatalog] — in-memory + keyword-substring search. Thread-safe via
 * ConcurrentHashMap (case-insensitive keys). Mirrors C# `InMemoryToolCatalog`.
 */
class InMemoryToolCatalog : IToolCatalog {
    // Keyed by lower-cased name for case-insensitive lookup; the descriptor keeps
    // the original name.
    private val byName = ConcurrentHashMap<String, ToolDescriptor>()

    override val count: Int get() = byName.size

    override suspend fun upsertAsync(descriptor: ToolDescriptor) {
        require(descriptor.name.isNotBlank()) { "descriptor.name is required" }
        byName[descriptor.name.lowercase()] = descriptor
    }

    override suspend fun removeAsync(name: String): Boolean {
        require(name.isNotBlank()) { "name is required" }
        return byName.remove(name.lowercase()) != null
    }

    override suspend fun getAsync(name: String): ToolDescriptor? {
        if (name.isBlank()) return null
        return byName[name.lowercase()]
    }

    override fun list(): List<ToolDescriptor> =
        byName.values.sortedWith(compareBy(String.CASE_INSENSITIVE_ORDER) { it.name })

    override fun search(query: String, topK: Int): List<ToolDescriptor> {
        if (query.isBlank() || topK <= 0) return emptyList()
        val terms = query.split(' ').map { it.trim() }.filter { it.isNotEmpty() }

        return byName.values
            .map { it to scoreMatch(it, terms) }
            .filter { it.second > 0 }
            .sortedWith(
                compareByDescending<Pair<ToolDescriptor, Int>> { it.second }
                    .thenBy(String.CASE_INSENSITIVE_ORDER) { it.first.name },
            )
            .take(topK)
            .map { it.first }
    }

    override fun listByProvider(provider: String): List<ToolDescriptor> {
        require(provider.isNotBlank()) { "provider is required" }
        return byName.values
            .filter { it.provider.equals(provider, ignoreCase = true) }
            .sortedWith(compareBy(String.CASE_INSENSITIVE_ORDER) { it.name })
    }

    private companion object {
        fun scoreMatch(d: ToolDescriptor, terms: List<String>): Int {
            val name = d.name
            val desc = d.description
            val tagBlob = d.tags?.joinToString(" ") ?: ""
            var score = 0
            for (t in terms) {
                if (name.contains(t, ignoreCase = true)) score += 5
                if (desc.contains(t, ignoreCase = true)) score += 2
                if (tagBlob.contains(t, ignoreCase = true)) score += 3
            }
            return score
        }
    }
}

/**
 * Discover and import every tool from [provider] into this catalog. Returns how
 * many were imported. Mirrors C# `ToolCatalogExtensions.ImportFromAsync`.
 */
suspend fun IToolCatalog.importFromAsync(provider: IToolProvider): Int {
    val tools = provider.discoverAsync()
    var count = 0
    for (tool in tools) {
        upsertAsync(tool)
        count++
    }
    return count
}
