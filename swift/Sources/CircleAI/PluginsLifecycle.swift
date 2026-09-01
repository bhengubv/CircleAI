// PluginsLifecycle.swift
//
// Discovers, initialises and shuts down plugins, and supports hot-reload.
//
// WHAT CROSSED AND WHAT DID NOT. The C# also carries `PluginLoader`, which
// loads `.dll` files into collectible `AssemblyLoadContext`s and finds the
// `IPlugin` by reflection. Swift has no equivalent and cannot grow one, so that
// half is excluded (see PARITY-EXCLUSIONS.md) and this half runs over plugins
// the host hands it instead of assemblies it loads. The lifecycle — resolve the
// root, register, apply the permission set, initialise, shut down, reload — is
// all here, because that is where the decisions live.
//
// Ported from src/CircleAI.Plugins/PluginLifecycleService.cs.

import Foundation

/// Where the plugins directory is. A seam because a phone, a desktop and a test
/// each answer differently, and hard-coding "./plugins" makes the first two
/// silently find nothing.
public protocol IPluginsRootResolver: Sendable {
    func resolveRoot() -> String
}

/// The workspace a plugin is allowed to touch, or nil when there is none.
///
/// Read through a provider rather than captured once: a person can switch
/// workspace while the host is running, and a plugin holding the old path
/// writes into the wrong place with no error anywhere.
public protocol IWorkspacePathProvider: Sendable {
    var workspacePath: String? { get }
}

public struct StaticPluginsRootResolver: IPluginsRootResolver, Sendable {
    private let root: String
    public init(root: String) { self.root = root }
    public func resolveRoot() -> String { root }
}

public struct StaticWorkspacePathProvider: IWorkspacePathProvider, Sendable {
    public let workspacePath: String?
    public init(workspacePath: String?) { self.workspacePath = workspacePath }
}

/// Runs the plugin lifecycle for a host.
public final class PluginLifecycleService: @unchecked Sendable {

    /// How a plugin is found. In C# this is `PluginLoader.Discover`, reading
    /// DLLs off disk; here the host supplies them, which is the only way a
    /// Swift process can obtain a plugin at all.
    public typealias Discover = @Sendable (_ pluginsRoot: String) -> [any IPlugin]

    private let discover: Discover
    private let registry: PluginRegistry
    private let events: any IPluginEvents
    private let rootResolver: (any IPluginsRootResolver)?
    private let workspace: (any IWorkspacePathProvider)?
    private let defaultRoot: String
    private let log: (@Sendable (String) -> Void)?

    private let lock = NSLock()
    private var initialised: [any IPlugin] = []
    private var reloading = false
    private var resolvedRoot = ""

    public init(discover: @escaping Discover,
                registry: PluginRegistry,
                events: any IPluginEvents,
                rootResolver: (any IPluginsRootResolver)? = nil,
                workspace: (any IWorkspacePathProvider)? = nil,
                defaultRoot: String = "plugins",
                log: (@Sendable (String) -> Void)? = nil) {
        self.discover = discover
        self.registry = registry
        self.events = events
        self.rootResolver = rootResolver
        self.workspace = workspace
        self.defaultRoot = defaultRoot
        self.log = log
    }

    /// Everything currently initialised. A snapshot, not the live array — a
    /// caller iterating while a reload runs would otherwise see it mutate.
    public var active: [any IPlugin] {
        lock.lock(); defer { lock.unlock() }
        return initialised
    }

    public var pluginsPath: String {
        lock.lock(); defer { lock.unlock() }
        return resolvedRoot
    }

    public func start() async {
        let root = rootResolver?.resolveRoot() ?? defaultRoot
        lock.lock(); resolvedRoot = root; lock.unlock()
        await loadAll()
    }

    public func stop() async {
        await unloadAll()
    }

    /// Hot-reload. Serialised against itself: two overlapping reloads would
    /// shut a plugin down while the other was initialising it, and the second
    /// `active` list would hold an object that had already been told to stop.
    public func reload() async {
        lock.lock()
        if reloading { lock.unlock(); return }
        reloading = true
        lock.unlock()

        await unloadAll()
        await loadAll()

        lock.lock(); reloading = false; lock.unlock()
    }

    private func loadAll() async {
        let root = pluginsPath
        let found = discover(root)

        for plugin in found {
            // PERMISSIONS COME FROM THE REGISTRY, and a plugin nobody has
            // registered yet gets an EMPTY set — it loads, and it can touch
            // nothing until somebody grants. The alternative, defaulting to
            // what the plugin asks for, means installing a plugin grants it.
            let registered = registry.get(plugin.id)
            let permissions = registered?.permissions ?? []
            if registered == nil {
                _ = registry.register(id: plugin.id, displayName: plugin.displayName,
                                      version: plugin.version, permissions: permissions)
            }

            // The lifecycle carries a log CLOSURE and `PluginContext` wants an
            // `ICircleAILogger`, so the closure is adapted rather than dropped:
            // a plugin whose logging goes nowhere is a plugin nobody can
            // diagnose. With no sink configured it stays silent.
            let base = PluginContext(
                workspacePathAccessor: { [workspace] in workspace?.workspacePath },
                events: events,
                logger: ClosureCircleAILogger(log))
            let context = PermissionedPluginContext(inner: base,
                                                    grantedPermissions: permissions)

            do {
                try await plugin.initialize(context: context)
                lock.lock(); initialised.append(plugin); lock.unlock()
                log?("plugin '\(plugin.id)' v\(plugin.version) initialised "
                     + "(permissions: \(permissions.isEmpty ? "none" : permissions.joined(separator: ", ")))")
            } catch {
                // ONE PLUGIN'S FAILURE IS NOT THE HOST'S. A third-party plugin
                // throwing on startup must not stop the other plugins, or the
                // app.
                log?("plugin '\(plugin.id)' threw during initialisation: \(error)")
            }
        }
    }

    private func unloadAll() async {
        lock.lock()
        let snapshot = initialised
        initialised.removeAll()
        lock.unlock()

        // Cleared FIRST, then shut down. A plugin that hangs in `shutdown`
        // otherwise stays in `active` forever and a reload initialises a second
        // copy alongside it.
        for plugin in snapshot {
            do { try await plugin.shutdown() }
            catch { log?("plugin '\(plugin.id)' threw during shutdown: \(error)") }
        }
    }
}
