// PluginsLifecycle.kt
//
// Discovers, initialises and shuts down plugins, and supports hot-reload.
//
// WHAT CROSSED AND WHAT DID NOT. The C# also carries `PluginLoader`, which loads
// .NET assemblies into collectible contexts and finds the plugin by reflection.
// The JVM cannot load a .NET assembly, so that half is excluded (see
// PARITY-EXCLUSIONS.md) and this half runs over plugins the host hands it. The
// lifecycle — resolve the root, register, apply the permission set, initialise,
// shut down, reload — is all here, because that is where the decisions live.
//
// Ported from src/CircleAI.Plugins/PluginLifecycleService.cs.

package com.bhengubv.circleai.plugins

import java.util.concurrent.atomic.AtomicBoolean

/**
 * Where the plugins directory is. A seam because a phone, a desktop and a test
 * each answer differently, and hard-coding "./plugins" makes the first two
 * silently find nothing.
 */
interface IPluginsRootResolver {
    fun resolveRoot(): String
}

/**
 * The workspace a plugin is allowed to touch, or null when there is none.
 *
 * Read through a provider rather than captured once: a person can switch
 * workspace while the host is running, and a plugin holding the old path writes
 * into the wrong place with no error anywhere.
 */
interface IWorkspacePathProvider {
    val workspacePath: String?
}

/** Runs the plugin lifecycle for a host. */
class PluginLifecycleService(
    /**
     * How a plugin is found. In C# this is the assembly loader reading DLLs off
     * disk; here the host supplies them, which is the only way a JVM process can
     * obtain a .NET-shaped plugin at all.
     */
    private val discover: (pluginsRoot: String) -> List<IPlugin>,
    private val registry: PluginRegistry,
    private val events: IPluginEvents,
    private val rootResolver: IPluginsRootResolver? = null,
    private val workspace: IWorkspacePathProvider? = null,
    private val defaultRoot: String = "plugins",
    private val log: ((String) -> Unit)? = null
) {
    private val initialised = ArrayList<IPlugin>()
    private val reloading = AtomicBoolean(false)

    @Volatile
    var pluginsPath: String = ""
        private set

    /** A snapshot, not the live list — a caller iterating while a reload runs
     *  would otherwise see it mutate. */
    val active: List<IPlugin> get() = synchronized(initialised) { initialised.toList() }

    suspend fun start() {
        pluginsPath = rootResolver?.resolveRoot() ?: defaultRoot
        loadAll()
    }

    suspend fun stop() = unloadAll()

    /**
     * Hot-reload. Serialised against itself: two overlapping reloads would shut
     * a plugin down while the other was initialising it, and the second `active`
     * list would hold an object that had already been told to stop.
     */
    suspend fun reload() {
        if (!reloading.compareAndSet(false, true)) return
        try {
            unloadAll()
            loadAll()
        } finally {
            reloading.set(false)
        }
    }

    private suspend fun loadAll() {
        for (plugin in discover(pluginsPath)) {
            // PERMISSIONS COME FROM THE REGISTRY, and a plugin nobody has
            // registered yet gets an EMPTY set — it loads, and it can touch
            // nothing until somebody grants. The alternative, defaulting to what
            // the plugin asks for, means installing a plugin grants it.
            val registered = registry.get(plugin.id)
            val permissions = registered?.permissions ?: emptyList()
            if (registered == null) {
                registry.register(plugin.id, plugin.displayName, plugin.version, permissions)
            }

            // The lifecycle carries a log CLOSURE and PluginContext wants a
            // PluginLogger, so the closure is adapted rather than dropped: a
            // plugin whose logging goes nowhere is one nobody can diagnose.
            val pluginLogger = log?.let { sink ->
                PluginLogger { level, message, error ->
                    // The level and any error are folded into the line: the
                    // sink is a plain string closure, and dropping them
                    // would make a failure indistinguishable from a trace.
                    sink("[$level] $message" + (error?.let { " — $it" } ?: ""))
                }
            } ?: NullPluginLogger
            val base = PluginContext({ workspace?.workspacePath }, events, logger = pluginLogger)
            val context = PermissionedPluginContext(base, permissions)

            try {
                plugin.initialize(context)
                synchronized(initialised) { initialised.add(plugin) }
                log?.invoke(
                    "plugin '${plugin.id}' v${plugin.version} initialised " +
                        "(permissions: ${permissions.ifEmpty { listOf("none") }.joinToString(", ")})"
                )
            } catch (t: Throwable) {
                // ONE PLUGIN'S FAILURE IS NOT THE HOST'S. A third-party plugin
                // throwing on startup must not stop the other plugins, or the app.
                log?.invoke("plugin '${plugin.id}' threw during initialisation: $t")
            }
        }
    }

    private suspend fun unloadAll() {
        val snapshot = synchronized(initialised) {
            val copy = initialised.toList()
            initialised.clear()
            copy
        }
        // Cleared FIRST, then shut down. A plugin that hangs in shutdown
        // otherwise stays in `active` forever, and a reload initialises a second
        // copy alongside it.
        for (plugin in snapshot) {
            try {
                plugin.shutdown()
            } catch (t: Throwable) {
                log?.invoke("plugin '${plugin.id}' threw during shutdown: $t")
            }
        }
    }
}
