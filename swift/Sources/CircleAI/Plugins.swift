// Plugins.swift
//
// Port of the named CircleAI.Plugins/ types — the stable plugin contract
// surface + a string-keyed event bus + a permission-gated context wrapper.
//   • IPlugin.cs         — IPlugin, IPluginContext, IPluginEvents, PluginEvents,
//                          PluginEventNames
//   • PluginContext.cs   — PluginContext (default), PermissionedPluginContext
//   • PluginRegistry.cs  — RegisteredPlugin, PluginRegistry (in-memory port)
//
// Porting notes:
//   • `Microsoft.Extensions.Logging.ILogger` → the tree's `ICircleAILogger`
//     (from Auditing.swift) — the SDK's logging seam.
//   • `IDisposable Subscribe(string, Action<object?>)` → `subscribe(_:_:)
//     -> IPluginSubscription` (idempotent dispose).
//   • The event payload (`object?`) → `(any Sendable)?` so it can cross the
//     handler boundary safely; senders + listeners agree on the concrete type
//     per event name (the C# contract).
//   • Assembly-reflection loading (`PluginLoader`), the DI hosted service
//     (`PluginLifecycleService`), and the JSON-file marketplace are host /
//     runtime infrastructure with no portable Swift analogue and are out of the
//     named work-unit scope. `PluginRegistry` is ported as a pure in-memory
//     registry (its C# JSON persistence is dropped; the register / enable /
//     grant / revoke / uninstall semantics are preserved).

import Foundation

// MARK: - Subscription handle

/// A disposable event subscription. Mirrors the C# `IDisposable` returned by
/// `IPluginEvents.Subscribe`. `dispose()` is idempotent.
public protocol IPluginSubscription: AnyObject, Sendable {
    /// Unsubscribe. Idempotent.
    func dispose()
}

// MARK: - IPluginEvents

/// String-keyed event bus. The host raises events via `raise`; plugins
/// subscribe with `subscribe`. Payload is opaque; senders + listeners agree on
/// the concrete type per event name. (C# `IPluginEvents`.)
public protocol IPluginEvents: Sendable {
    /// Subscribe to `eventName`. Returns a dispose handle.
    func subscribe(_ eventName: String, _ handler: @escaping @Sendable ((any Sendable)?) -> Void) -> IPluginSubscription
    /// Raise `eventName` with `payload`. Host-only API.
    func raise(_ eventName: String, _ payload: (any Sendable)?)
}

/// Thread-safe default `IPluginEvents`. Fan-out is snapshot-then-release so a
/// handler that (un)subscribes cannot self-deadlock, and an unhealthy handler
/// cannot corrupt the host (throws are swallowed — Swift closures can't throw
/// here, so this is inherent). (C# `PluginEvents`.)
public final class PluginEvents: IPluginEvents, @unchecked Sendable {
    private let lock = NSLock()
    private var handlers: [String: [UUID: @Sendable ((any Sendable)?) -> Void]] = [:]

    public init() {}

    public func subscribe(_ eventName: String, _ handler: @escaping @Sendable ((any Sendable)?) -> Void) -> IPluginSubscription {
        precondition(!eventName.isEmpty, "eventName required")
        let id = UUID()
        lock.lock(); handlers[eventName, default: [:]][id] = handler; lock.unlock()
        return Subscription(owner: self, name: eventName, id: id)
    }

    public func raise(_ eventName: String, _ payload: (any Sendable)?) {
        lock.lock()
        let snapshot = handlers[eventName].map { Array($0.values) } ?? []
        lock.unlock()
        for h in snapshot { h(payload) }
    }

    /// Subscriber count for `eventName`. Useful in tests.
    public func subscriberCount(_ eventName: String) -> Int {
        lock.lock(); defer { lock.unlock() }
        return handlers[eventName]?.count ?? 0
    }

    private func unsubscribe(_ eventName: String, _ id: UUID) {
        lock.lock(); handlers[eventName]?[id] = nil; lock.unlock()
    }

    private final class Subscription: IPluginSubscription, @unchecked Sendable {
        private weak var owner: PluginEvents?
        private let name: String
        private let id: UUID
        private let disposeLock = NSLock()
        private var disposed = false

        init(owner: PluginEvents, name: String, id: UUID) {
            self.owner = owner; self.name = name; self.id = id
        }

        func dispose() {
            disposeLock.lock()
            if disposed { disposeLock.unlock(); return }
            disposed = true
            disposeLock.unlock()
            owner?.unsubscribe(name, id)
        }
    }
}

/// Well-known event names. (C# `PluginEventNames`.)
public enum PluginEventNames {
    public static let workspaceLoaded = "workspace.loaded"
    public static let chatMessage = "chat.message"
    public static let modelLoaded = "model.loaded"
    public static let modelUnloaded = "model.unloaded"
}

// MARK: - IPluginContext

/// Stable surface plugins may use: an event bus, a logger, and the configured
/// workspace path. Deliberately does NOT expose service registration.
/// (C# `IPluginContext`.)
public protocol IPluginContext: Sendable {
    /// Host-configured workspace directory (or `nil`).
    var workspacePath: String? { get }
    /// Event bus the host raises events into.
    var events: any IPluginEvents { get }
    /// Logger scoped to this plugin.
    var logger: any ICircleAILogger { get }
}

/// Default `IPluginContext`. The workspace path is resolved lazily via an
/// accessor closure (C# `Func<string?>`). (C# `PluginContext`.)
public final class PluginContext: IPluginContext, @unchecked Sendable {
    private let workspacePathAccessor: @Sendable () -> String?

    public init(workspacePathAccessor: @escaping @Sendable () -> String?,
                events: any IPluginEvents, logger: any ICircleAILogger) {
        self.workspacePathAccessor = workspacePathAccessor
        self.events = events
        self.logger = logger
    }

    public var workspacePath: String? { workspacePathAccessor() }
    public let events: any IPluginEvents
    public let logger: any ICircleAILogger
}

/// Wraps an inner context and gates capabilities by a granted-permission set.
/// (C# `PermissionedPluginContext`.)
public final class PermissionedPluginContext: IPluginContext, @unchecked Sendable {
    /// Well-known permission strings. (C# nested `Permissions`.)
    public enum Permissions {
        public static let workspaceRead = "workspace.read"
        public static let workspaceWrite = "workspace.write"
        public static let eventsSubscribe = "events.subscribe"
    }

    private let inner: any IPluginContext
    private let granted: Set<String>
    private let gatedEvents: any IPluginEvents

    public init(inner: any IPluginContext, grantedPermissions: [String]) {
        self.inner = inner
        // Case-insensitive membership (matches the C# OrdinalIgnoreCase set).
        self.granted = Set(grantedPermissions.map { $0.lowercased() })
        self.gatedEvents = self.granted.contains(Permissions.eventsSubscribe.lowercased())
            ? inner.events
            : SilentEvents()
    }

    public var workspacePath: String? {
        if granted.contains(Permissions.workspaceRead.lowercased())
            || granted.contains(Permissions.workspaceWrite.lowercased()) {
            return inner.workspacePath
        }
        return nil
    }

    public var events: any IPluginEvents { gatedEvents }
    public var logger: any ICircleAILogger { inner.logger }

    /// Drop-on-the-floor event bus for permission-denied plugins.
    private final class SilentEvents: IPluginEvents, @unchecked Sendable {
        func subscribe(_ eventName: String, _ handler: @escaping @Sendable ((any Sendable)?) -> Void) -> IPluginSubscription {
            NoopSubscription.shared
        }
        func raise(_ eventName: String, _ payload: (any Sendable)?) {}
    }

    private final class NoopSubscription: IPluginSubscription, @unchecked Sendable {
        static let shared = NoopSubscription()
        func dispose() {}
    }
}

// MARK: - IPlugin

/// Contract every CircleAI plugin implements. (C# `IPlugin`.)
public protocol IPlugin: Sendable {
    /// Unique identifier.
    var id: String { get }
    /// Human-readable label.
    var displayName: String { get }
    /// SemVer string.
    var version: String { get }
    /// Called once at host startup with the plugin context.
    func initialize(context: any IPluginContext) async throws
    /// Called on host shutdown / plugin unload.
    func shutdown() async throws
}

// MARK: - PluginRegistry (in-memory port)

/// One installed-plugin entry. (C# `RegisteredPlugin`.)
public struct RegisteredPlugin: Sendable, Equatable, Codable {
    public var id: String
    public var displayName: String
    public var version: String
    public var permissions: [String]
    public var enabled: Bool
    public var installedAt: Date

    public init(id: String, displayName: String, version: String, permissions: [String],
                enabled: Bool, installedAt: Date) {
        self.id = id
        self.displayName = displayName
        self.version = version
        self.permissions = permissions
        self.enabled = enabled
        self.installedAt = installedAt
    }
}

/// Tracks installed plugins. Declarative, opt-in permissions per plugin. The C#
/// version persists to a JSON manifest; this in-memory port keeps the same
/// register / enable / grant / revoke / uninstall semantics without file I/O.
/// (C# `PluginRegistry`.)
public final class PluginRegistry: @unchecked Sendable {
    private let lock = NSLock()
    private var installed: [RegisteredPlugin] = []

    public init() {}

    /// All installed entries (snapshot).
    public var all: [RegisteredPlugin] {
        lock.lock(); defer { lock.unlock() }
        return installed
    }

    /// Returns the entry with `id` (case-insensitive), or `nil`.
    public func get(_ id: String) -> RegisteredPlugin? {
        lock.lock(); defer { lock.unlock() }
        return installed.first { $0.id.caseInsensitiveCompare(id) == .orderedSame }
    }

    /// Registers (or replaces, by id) a plugin. New entries start disabled.
    @discardableResult
    public func register(id: String, displayName: String, version: String, permissions: [String]) -> RegisteredPlugin {
        let entry = RegisteredPlugin(id: id, displayName: displayName, version: version,
                                     permissions: permissions, enabled: false, installedAt: Date())
        lock.lock()
        installed.removeAll { $0.id.caseInsensitiveCompare(id) == .orderedSame }
        installed.append(entry)
        lock.unlock()
        return entry
    }

    /// Enables/disables a plugin. Returns false when unknown.
    @discardableResult
    public func setEnabled(_ id: String, _ enabled: Bool) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let idx = installed.firstIndex(where: { $0.id.caseInsensitiveCompare(id) == .orderedSame }) else { return false }
        installed[idx].enabled = enabled
        return true
    }

    /// Grants a permission (idempotent). Returns false when the plugin is unknown.
    @discardableResult
    public func grantPermission(_ id: String, _ permission: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let idx = installed.firstIndex(where: { $0.id.caseInsensitiveCompare(id) == .orderedSame }) else { return false }
        if !installed[idx].permissions.contains(where: { $0.caseInsensitiveCompare(permission) == .orderedSame }) {
            installed[idx].permissions.append(permission)
        }
        return true
    }

    /// Revokes a permission. Returns true when something was removed.
    @discardableResult
    public func revokePermission(_ id: String, _ permission: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let idx = installed.firstIndex(where: { $0.id.caseInsensitiveCompare(id) == .orderedSame }) else { return false }
        let before = installed[idx].permissions.count
        installed[idx].permissions.removeAll { $0.caseInsensitiveCompare(permission) == .orderedSame }
        return installed[idx].permissions.count != before
    }

    /// Uninstalls a plugin. Returns true when an entry was removed.
    @discardableResult
    public func uninstall(_ id: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        let before = installed.count
        installed.removeAll { $0.id.caseInsensitiveCompare(id) == .orderedSame }
        return installed.count != before
    }
}
