// PluginRegistry.kt
//
// Kotlin port of CircleAI.Plugins/PluginRegistry.cs — the C# reference is the
// EXACT spec. Installed-plugin registry + marketplace catalog: JSON-backed,
// atomic save (tmp + rename), thread-safe, opt-in permissions per plugin. The
// plugin CONTRACT surface (IPlugin, IPluginContext, events) already lives in
// Plugins.kt; this file adds the registry + marketplace CRUD.
//
// The C# assembly-loading / hot-reload machinery (PluginLoader = AssemblyLoad-
// Context, PluginLifecycleService = IHostedService) is a CLR-specific seam and is
// intentionally out of scope — only the JSON-persisted registry + catalog are
// hermetic and portable.
//
// C# -> Kotlin conventions:
//   System.Text.Json                    -> kotlinx.serialization.json
//   lock (_gate) { }                    -> synchronized(gate) { }
//   ILogger / NullLogger.Instance       -> PluginLogger / NullPluginLogger (Plugins.kt)
//   DateTimeOffset.UtcNow               -> java.time.Instant.now() (ISO-8601 string on the wire)
//   File.WriteAllText + Move            -> atomic tmp-write then Files.move(ATOMIC_MOVE)
//   string.Equals(OrdinalIgnoreCase)    -> equals(ignoreCase = true)
//   JsonSerializerDefaults.Web (catalog) -> Json { ignoreUnknownKeys = true }

package com.bhengubv.circleai.plugins

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.nio.file.StandardCopyOption
import java.time.Instant
import kotlin.io.path.exists
import kotlin.io.path.readText

// ===========================================================================
// RegisteredPlugin  (one installed plugin entry)
// ===========================================================================

/** (3.2.0) One installed plugin entry. Mirrors C# `RegisteredPlugin`. */
@Serializable
data class RegisteredPlugin(
    @SerialName("Id") var id: String = "",
    @SerialName("DisplayName") var displayName: String = "",
    @SerialName("Version") var version: String = "0.0.0",
    @SerialName("Permissions") var permissions: MutableList<String> = mutableListOf(),
    @SerialName("Enabled") var enabled: Boolean = false,
    // DateTimeOffset serialises as an ISO-8601 string; store the same shape.
    @SerialName("InstalledAt") var installedAt: String = Instant.EPOCH.toString(),
)

// ===========================================================================
// PluginRegistry
// ===========================================================================

/**
 * (3.2.0) Tracks installed plugins. Permissions are declarative — users audit
 * before trusting. The registry is JSON-backed (`registry.json` under the
 * plugins root), saved atomically (tmp + rename) under a lock. Mirrors C#
 * `PluginRegistry`.
 */
class PluginRegistry(
    pluginsRoot: String,
    private val logger: PluginLogger = NullPluginLogger,
) {
    private val pluginsRootPath: Path = Paths.get(pluginsRoot)
    private val manifestPath: Path
    private val gate = Any()
    private val installedList = ArrayList<RegisteredPlugin>()

    init {
        Files.createDirectories(pluginsRootPath)
        manifestPath = pluginsRootPath.resolve("registry.json")
        load()
    }

    /** Snapshot of the installed plugins. */
    val installed: List<RegisteredPlugin>
        get() = synchronized(gate) { installedList.toList() }

    /** Look up a plugin by id (case-insensitive), or null. */
    fun get(id: String): RegisteredPlugin? = synchronized(gate) {
        installedList.firstOrNull { it.id.equals(id, ignoreCase = true) }
    }

    /** Register (or replace) a plugin. Newly registered plugins are disabled. */
    fun register(id: String, displayName: String, version: String, permissions: Iterable<String>): RegisteredPlugin {
        val entry = RegisteredPlugin(
            id = id,
            displayName = displayName,
            version = version,
            permissions = permissions.toMutableList(),
            enabled = false,
            installedAt = Instant.now().toString(),
        )
        synchronized(gate) {
            installedList.removeAll { it.id.equals(id, ignoreCase = true) }
            installedList.add(entry)
            save()
        }
        return entry
    }

    /** Enable / disable a plugin. Returns false if the id is unknown. */
    fun setEnabled(id: String, enabled: Boolean): Boolean = synchronized(gate) {
        val p = installedList.firstOrNull { it.id.equals(id, ignoreCase = true) } ?: return false
        p.enabled = enabled
        save()
        true
    }

    /** Grant a permission to a plugin. Returns false if the id is unknown. */
    fun grantPermission(id: String, permission: String): Boolean = synchronized(gate) {
        val p = installedList.firstOrNull { it.id.equals(id, ignoreCase = true) } ?: return false
        if (p.permissions.none { it.equals(permission, ignoreCase = true) }) {
            p.permissions.add(permission)
            save()
        }
        true
    }

    /** Revoke a permission. Returns true only if something was removed. */
    fun revokePermission(id: String, permission: String): Boolean = synchronized(gate) {
        val p = installedList.firstOrNull { it.id.equals(id, ignoreCase = true) } ?: return false
        val removed = p.permissions.removeAll { it.equals(permission, ignoreCase = true) }
        if (removed) save()
        removed
    }

    /** Uninstall a plugin and best-effort delete its folder. Returns true if removed. */
    fun uninstall(id: String): Boolean = synchronized(gate) {
        val removed = installedList.removeAll { it.id.equals(id, ignoreCase = true) }
        if (removed) {
            save()
            // Best-effort: delete the plugin folder too.
            val dir = pluginsRootPath.resolve(id)
            if (dir.exists()) {
                try {
                    deleteRecursively(dir)
                } catch (ex: Exception) {
                    logger.log(PluginLogLevel.Warning, "Failed to delete plugin folder $dir", ex)
                }
            }
        }
        removed
    }

    private fun load() {
        if (!manifestPath.exists()) return
        try {
            val json = manifestPath.readText()
            val loaded = REGISTRY_JSON.decodeFromString<List<RegisteredPlugin>>(json)
            installedList.clear()
            installedList.addAll(loaded)
        } catch (_: Exception) {
            // corrupt — start fresh
        }
    }

    private fun save() {
        try {
            val json = REGISTRY_JSON.encodeToString<List<RegisteredPlugin>>(installedList)
            val tmp = Paths.get("$manifestPath.tmp")
            Files.write(tmp, json.toByteArray(Charsets.UTF_8))
            try {
                Files.move(tmp, manifestPath, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
            } catch (_: Exception) {
                // Filesystem may not support atomic move — fall back to a plain replace.
                Files.move(tmp, manifestPath, StandardCopyOption.REPLACE_EXISTING)
            }
        } catch (ex: Exception) {
            logger.log(PluginLogLevel.Warning, "Failed to save plugin registry.", ex)
        }
    }

    private fun deleteRecursively(path: Path) {
        if (Files.isDirectory(path)) {
            Files.list(path).use { stream -> stream.forEach { deleteRecursively(it) } }
        }
        Files.deleteIfExists(path)
    }

    private companion object {
        val REGISTRY_JSON = Json {
            prettyPrint = true
            ignoreUnknownKeys = true
        }
    }
}

// ===========================================================================
// PluginMarketplace + MarketplaceEntry
// ===========================================================================

/** (3.2.0) One marketplace catalog entry. Mirrors C# `MarketplaceEntry`. */
@Serializable
data class MarketplaceEntry(
    val id: String = "",
    val displayName: String = "",
    val version: String = "0.0.0",
    val description: String = "",
    val author: String = "",
    val downloadUrl: String = "",
    val permissions: List<String> = emptyList(),
)

/**
 * (3.2.0) Marketplace catalog. Backed by a JSON file the operator publishes
 * (typically `plugins/marketplace.json`). Catalog is metadata only — install
 * downloads the plugin into `plugins/{id}/`. Mirrors C# `PluginMarketplace`.
 */
class PluginMarketplace(catalogPath: String) {
    private val catalog: Path = Paths.get(catalogPath)

    /** Read the catalog. Returns an empty list when the file is absent or corrupt. */
    fun list(): List<MarketplaceEntry> {
        if (!catalog.exists()) return emptyList()
        return try {
            CATALOG_JSON.decodeFromString<List<MarketplaceEntry>>(catalog.readText())
        } catch (_: Exception) {
            emptyList()
        }
    }

    private companion object {
        // C# used JsonSerializerDefaults.Web (camelCase, case-insensitive).
        val CATALOG_JSON = Json {
            ignoreUnknownKeys = true
            isLenient = true
        }
    }
}
