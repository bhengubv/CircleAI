// PacaPlugins.kt
//
// Kotlin port of CircleAI.Workflows/PacaPlugins.cs.
//
// (3.3.0) Plugin runtime + manifest + lifecycle ported from paca: plugin
// manifest validation, semver upgrade detection, reverse-DNS naming, marketplace
// install/upgrade/uninstall, frontend module surface, extension points, artifact
// + migration management, per-plugin resource limits + WASI snapshot preview-1
// support.
//
// The wazero / WASM execution layer is host-supplied via IPluginRuntimeHost;
// this package owns the lifecycle.

package com.bhengubv.circleai.workflows

import java.net.URI
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

/** (3.3.0) Plugin extension points supported by the marketplace. */
enum class PluginExtensionPoint {
    Sidebar,
    TaskDetail,
    Settings,
    CustomView,
    Route,
    Event,
    McpTool,
}

/**
 * (3.3.0) Per-plugin resource limits.
 *
 * @property callTimeoutMs Max wall-clock time for one host call. Default 5000ms.
 * @property memoryCeilingBytes Max memory the WASM instance may allocate.
 *   Default 64MB.
 */
data class PluginResourceLimits(
    val callTimeoutMs: Int = 5000,
    val memoryCeilingBytes: Long = 64L * 1024 * 1024,
)

/** (3.3.0) Plugin manifest from `plugin.json`. */
data class PluginManifest(
    val name: String,                  // reverse-DNS, e.g. "com.paca.bdd"
    val displayName: String,
    val version: String,               // SemVer
    val description: String,
    val artifactWasmUrl: URI?,
    val frontendModuleUrl: URI?,
    val extensionPoints: List<PluginExtensionPoint>,
    val mcpTools: List<String>,
    val sqlMigrationFiles: List<String>,
    val limits: PluginResourceLimits,
)

/** (3.3.0) Installed instance. */
data class InstalledPlugin(
    val id: String,                   // matches manifest.name
    val manifest: PluginManifest,
    val installedFromCatalog: String,
    val installedAtUtc: Instant,
    val enabled: Boolean,
)

/** (3.3.0) Plugin runtime host (wazero-style). Provided by the deploy. */
interface IPluginRuntimeHost {
    /** Install + initialise. Run SQL migrations + cache the WASM artifact. */
    suspend fun install(plugin: InstalledPlugin)

    /** Uninstall — drop WASM + clean artifacts; do NOT roll back data unless asked. */
    suspend fun uninstall(pluginId: String, dropArtifacts: Boolean)

    /** Hot-swap to a new version (semver upgrade). */
    suspend fun upgrade(from: InstalledPlugin, to: InstalledPlugin)
}

/**
 * (3.3.0) Plugin lifecycle manager. Installs / upgrades / uninstalls / enables
 * / disables.
 */
class PacaPluginRegistry(
    private val runtime: IPluginRuntimeHost,
    private val clock: () -> Instant = { Instant.now() },
) {
    private val installed = ConcurrentHashMap<String, InstalledPlugin>()

    fun listInstalled(): List<InstalledPlugin> = installed.values.toList()

    fun get(id: String): InstalledPlugin? = installed[id]

    /** (3.3.0) Install plugin from the supplied manifest. */
    suspend fun install(manifest: PluginManifest, catalog: String): InstalledPlugin {
        validateManifest(manifest)
        if (installed.containsKey(manifest.name)) {
            throw IllegalStateException("Plugin '${manifest.name}' is already installed; use upgrade.")
        }
        val plugin = InstalledPlugin(manifest.name, manifest, catalog, clock(), enabled = true)
        runtime.install(plugin)
        installed[manifest.name] = plugin
        return plugin
    }

    /** (3.3.0) Upgrade if [newManifest]'s SemVer is strictly newer. */
    suspend fun upgrade(newManifest: PluginManifest, catalog: String): InstalledPlugin {
        validateManifest(newManifest)
        val current = installed[newManifest.name]
            ?: throw IllegalStateException("Plugin '${newManifest.name}' is not installed.")
        if (compareSemver(newManifest.version, current.manifest.version) <= 0) {
            throw IllegalStateException("Version ${newManifest.version} is not newer than ${current.manifest.version}.")
        }
        val next = InstalledPlugin(newManifest.name, newManifest, catalog, clock(), current.enabled)
        runtime.upgrade(current, next)
        installed[newManifest.name] = next
        return next
    }

    suspend fun uninstall(id: String, dropArtifacts: Boolean = true) {
        if (installed.remove(id) == null) return
        runtime.uninstall(id, dropArtifacts)
    }

    fun setEnabled(id: String, enabled: Boolean) {
        val current = installed[id] ?: return
        installed[id] = current.copy(enabled = enabled)
    }

    companion object {
        private val REVERSE_DNS_PATTERN = Regex("^[a-z][a-z0-9]*(\\.[a-z][a-z0-9_-]*)+$")

        /** (3.3.0) Validate a manifest before install / upgrade. */
        fun validateManifest(manifest: PluginManifest) {
            require(REVERSE_DNS_PATTERN.matches(manifest.name)) {
                "Plugin name '${manifest.name}' must be reverse-DNS (e.g. com.paca.bdd)."
            }
            require(tryParseVersion(stripPrerelease(manifest.version)) != null) {
                "Plugin version '${manifest.version}' is not parseable SemVer."
            }
            require(manifest.limits.callTimeoutMs > 0) { "callTimeoutMs must be positive." }
            require(manifest.limits.memoryCeilingBytes > 0) { "memoryCeilingBytes must be positive." }
        }

        /** (3.3.0) Compare SemVer-ish strings: returns <0 / 0 / >0. */
        fun compareSemver(a: String, b: String): Int {
            val va = requireNotNull(tryParseVersion(stripPrerelease(a))) { "Unparseable version '$a'." }
            val vb = requireNotNull(tryParseVersion(stripPrerelease(b))) { "Unparseable version '$b'." }
            val len = maxOf(va.size, vb.size)
            for (i in 0 until len) {
                val ai = va.getOrElse(i) { 0 }
                val bi = vb.getOrElse(i) { 0 }
                if (ai != bi) return ai.compareTo(bi)
            }
            return 0
        }

        private fun stripPrerelease(v: String): String = v.split('-', '+')[0]

        /**
         * Parses a dotted numeric version (e.g. "1.2.3", "1.2.3.4") into its
         * components. Returns `null` if any component is non-numeric or the
         * string is empty — mirrors .NET `Version.TryParse` semantics closely
         * enough for the manifest validation + comparison used here.
         */
        private fun tryParseVersion(v: String): List<Int>? {
            if (v.isBlank()) return null
            val parts = v.split('.')
            if (parts.isEmpty()) return null
            val nums = ArrayList<Int>(parts.size)
            for (p in parts) {
                val n = p.toIntOrNull() ?: return null
                if (n < 0) return null
                nums.add(n)
            }
            return nums
        }
    }
}
