// Plugins.kt
//
// Kotlin port of CircleAI.Plugins — the C# reference is the EXACT spec for the
// plugin contract surface (IPlugin.cs, PluginContext.cs). Ports the stable
// surface a plugin is allowed to use: the plugin contract, the plugin context,
// the string-keyed event bus, and the permission-gated context wrapper.
//
// The C# assembly-loading / hot-reload machinery (PluginLoader,
// PluginRegistry, PluginLifecycleService) is reflection + IHostedService +
// AssemblyLoadContext specific and is intentionally out of scope for the
// deterministic in-memory Kotlin core.
//
// C# -> Kotlin conventions:
//   Task                 -> suspend fun
//   IDisposable          -> AutoCloseable
//   Action<object?>      -> (Any?) -> Unit
//   ILogger              -> PluginLogger (minimal local abstraction)
//   ConcurrentDictionary -> synchronized MutableMap

package com.bhengubv.circleai.plugins

// ===========================================================================
// Logging abstraction
// ===========================================================================

/**
 * Minimal logger a plugin receives through its context. Stands in for the C#
 * `Microsoft.Extensions.Logging.ILogger`; hosts provide a real implementation.
 */
fun interface PluginLogger {
    fun log(level: PluginLogLevel, message: String, error: Throwable?)
}

enum class PluginLogLevel { Trace, Debug, Information, Warning, Error, Critical }

/** A logger that drops everything on the floor. Default when the host wires none. */
object NullPluginLogger : PluginLogger {
    override fun log(level: PluginLogLevel, message: String, error: Throwable?) {}

    /** Convenience overload for message-only logging. */
    fun log(level: PluginLogLevel, message: String) = log(level, message, null)
}

// ===========================================================================
// IPlugin  (IPlugin.cs)
// ===========================================================================

/**
 * Contract every CircleAI plugin implements. Plugins subscribe to host events
 * through [IPluginContext.events] and read the configured workspace path
 * through [IPluginContext.workspacePath].
 */
interface IPlugin {
    /** Unique identifier (matches the assembly name by default). */
    val id: String

    /** Human-readable label. */
    val displayName: String

    /** SemVer string. */
    val version: String

    /** Called once at host startup. */
    suspend fun initialize(context: IPluginContext)

    /** Called when the host is shutting down or the plugin is being unloaded. */
    suspend fun shutdown()
}

/**
 * Stable surface plugins are allowed to use. Deliberately does not expose a
 * service collection — plugins should not swap out host services. They get an
 * event bus, a logger, and the configured workspace path.
 */
interface IPluginContext {
    /** Host-configured workspace directory (or null when not set). */
    val workspacePath: String?

    /** Event bus the host raises events into. */
    val events: IPluginEvents

    /** Logger scoped to this plugin. */
    val logger: PluginLogger
}

/**
 * String-keyed event bus. The host raises events via [raise]; plugins
 * subscribe with [subscribe]. Payload is opaque; senders + listeners agree on
 * the concrete type per event name.
 */
interface IPluginEvents {
    /** Subscribe to events. Returns an unsubscribe handle. */
    fun subscribe(eventName: String, handler: (Any?) -> Unit): AutoCloseable

    /** Raise an event. Host-only API. */
    fun raise(eventName: String, payload: Any?)
}

/** Thread-safe default [IPluginEvents]. */
class PluginEvents : IPluginEvents {
    private val handlers = HashMap<String, MutableList<(Any?) -> Unit>>()
    private val lock = Any()

    override fun subscribe(eventName: String, handler: (Any?) -> Unit): AutoCloseable {
        require(eventName.isNotEmpty()) { "eventName required" }
        synchronized(lock) { handlers.getOrPut(eventName) { ArrayList() }.add(handler) }
        return Subscription(eventName, handler)
    }

    override fun raise(eventName: String, payload: Any?) {
        val snapshot = synchronized(lock) { handlers[eventName]?.toList() } ?: return
        for (h in snapshot) {
            try {
                h(payload)
            } catch (ex: Throwable) {
                // an unhealthy plugin must not corrupt the host
            }
        }
    }

    private fun unsubscribe(eventName: String, handler: (Any?) -> Unit) {
        synchronized(lock) { handlers[eventName]?.remove(handler) }
    }

    private inner class Subscription(
        private val name: String,
        private val handler: (Any?) -> Unit,
    ) : AutoCloseable {
        private var disposed = false
        override fun close() {
            if (disposed) return
            disposed = true
            unsubscribe(name, handler)
        }
    }
}

/** Well-known event names hosts raise and plugins subscribe to. */
object PluginEventNames {
    const val WORKSPACE_LOADED = "workspace.loaded"
    const val CHAT_MESSAGE = "chat.message"
    const val MODEL_LOADED = "model.loaded"
    const val MODEL_UNLOADED = "model.unloaded"
}

// ===========================================================================
// PluginContext / PermissionedPluginContext  (PluginContext.cs)
// ===========================================================================

/** Default [IPluginContext]. */
class PluginContext(
    private val workspacePathAccessor: () -> String?,
    override val events: IPluginEvents,
    override val logger: PluginLogger,
) : IPluginContext {
    override val workspacePath: String?
        get() = workspacePathAccessor()
}

/**
 * Wraps an inner context and gates capabilities by a granted-permission set.
 * Permission-denied plugins see a null workspace path and a silent event bus.
 */
class PermissionedPluginContext(
    private val inner: IPluginContext,
    grantedPermissions: Iterable<String>,
) : IPluginContext {

    object Permissions {
        const val WORKSPACE_READ = "workspace.read"
        const val WORKSPACE_WRITE = "workspace.write"
        const val EVENTS_SUBSCRIBE = "events.subscribe"
    }

    private val granted: Set<String> = grantedPermissions.map { it.lowercase() }.toHashSet()
    private val gatedEvents: IPluginEvents =
        if (granted.contains(Permissions.EVENTS_SUBSCRIBE)) inner.events else SilentEvents

    override val workspacePath: String?
        get() = if (granted.contains(Permissions.WORKSPACE_READ) || granted.contains(Permissions.WORKSPACE_WRITE)) {
            inner.workspacePath
        } else {
            null
        }

    override val events: IPluginEvents get() = gatedEvents
    override val logger: PluginLogger get() = inner.logger

    /** Drop-on-the-floor event bus for permission-denied plugins. */
    private object SilentEvents : IPluginEvents {
        override fun subscribe(eventName: String, handler: (Any?) -> Unit): AutoCloseable = AutoCloseable { }
        override fun raise(eventName: String, payload: Any?) {}
    }
}
