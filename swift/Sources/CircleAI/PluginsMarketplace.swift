// PluginsMarketplace.swift
//
// Port of the CircleAI.Plugins/PluginRegistry.cs types that Plugins.swift left
// out: the JSON-persisted installed-plugin registry, the marketplace catalog,
// and its entry record.
//   • PluginRegistry.cs → PluginRegistry  → FilePluginRegistry (JSON CRUD)
//   • PluginRegistry.cs → PluginMarketplace (JSON catalog reader)
//   • PluginRegistry.cs → MarketplaceEntry (catalog record)
//
// Porting notes:
//   • Plugins.swift already ports the C# `PluginRegistry` as an *in-memory*
//     `PluginRegistry` (its JSON persistence was dropped there) and owns the
//     `RegisteredPlugin` struct. To avoid a redefinition while honouring the
//     "PluginRegistry (JSON CRUD)" work-unit, the disk-backed variant is named
//     `FilePluginRegistry` (mirroring `FileSkillStore` vs `InMemorySkillStore`)
//     and REUSES the existing `Codable RegisteredPlugin`. Its register / enable
//     / grant / revoke / uninstall semantics are byte-for-byte the C#'s,
//     including the atomic save (write tmp → delete → rename) and the
//     best-effort plugin-folder delete on uninstall.
//   • Direct `FileManager` I/O is used (a *file* registry's identity), matching
//     the precedent set by the rest of the port; the C# `ILogger` warnings map
//     to the tree's optional `ICircleAILogger`.
//   • `PluginMarketplace.list()` reads a JSON array of `MarketplaceEntry`,
//     fail-soft: missing file or corrupt JSON → empty list (C# behaviour).
//   • `DateTimeOffset` → `Date`; the registry JSON round-trips via
//     `JSONEncoder`/`JSONDecoder`. Dates use ISO-8601 so a hand-authored or
//     cross-tool manifest stays legible and stable.

import Foundation

// MARK: - MarketplaceEntry

/// One marketplace catalog entry. (C# `MarketplaceEntry`.)
public struct MarketplaceEntry: Sendable, Equatable, Codable {
    public var id: String
    public var displayName: String
    public var version: String
    public var description: String
    public var author: String
    public var downloadUrl: String
    public var permissions: [String]

    public init(id: String = "", displayName: String = "", version: String = "0.0.0",
                description: String = "", author: String = "", downloadUrl: String = "",
                permissions: [String] = []) {
        self.id = id
        self.displayName = displayName
        self.version = version
        self.description = description
        self.author = author
        self.downloadUrl = downloadUrl
        self.permissions = permissions
    }
}

// MARK: - PluginMarketplace

/// Marketplace catalog. Backed by a JSON file the operator publishes (typically
/// `plugins/marketplace.json`). Catalog is metadata only. (C#
/// `PluginMarketplace`.)
public final class PluginMarketplace: @unchecked Sendable {
    private let catalogPath: String

    public init(catalogPath: String) {
        precondition(!catalogPath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "catalogPath required")
        self.catalogPath = catalogPath
    }

    /// All catalog entries. Missing file or corrupt JSON → empty list. (C#
    /// `List`.)
    public func list() -> [MarketplaceEntry] {
        guard FileManager.default.fileExists(atPath: catalogPath) else { return [] }
        guard
            let data = try? Data(contentsOf: URL(fileURLWithPath: catalogPath)),
            let entries = try? Self.decoder.decode([MarketplaceEntry].self, from: data)
        else {
            return []
        }
        return entries
    }

    // The C# used JsonSerializerDefaults.Web (camelCase). The Swift property
    // names are already camelCase, so the default keys line up.
    private static let decoder = JSONDecoder()
}

// MARK: - FilePluginRegistry

/// Tracks installed plugins, persisted to a `registry.json` under a plugins
/// root. Declarative, opt-in permissions per plugin. Thread-safe; atomic save.
/// (C# `PluginRegistry`; named `FilePluginRegistry` here to coexist with the
/// in-memory `PluginRegistry` already in Plugins.swift.)
public final class FilePluginRegistry: @unchecked Sendable {
    private let pluginsRoot: String
    private let manifestPath: String
    private let logger: (any ICircleAILogger)?
    private let lock = NSLock()
    private var installed: [RegisteredPlugin] = []

    /// Creates the registry, creating `pluginsRoot` if absent and loading any
    /// existing `registry.json`.
    public init(pluginsRoot: String, logger: (any ICircleAILogger)? = nil) {
        precondition(!pluginsRoot.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "pluginsRoot required")
        self.pluginsRoot = pluginsRoot
        self.logger = logger
        try? FileManager.default.createDirectory(atPath: pluginsRoot, withIntermediateDirectories: true)
        self.manifestPath = (pluginsRoot as NSString).appendingPathComponent("registry.json")
        load()
    }

    /// All installed entries (snapshot). (C# `Installed`.)
    public var allInstalled: [RegisteredPlugin] {
        lock.lock(); defer { lock.unlock() }
        return installed
    }

    /// The entry with `id` (case-insensitive), or nil. (C# `Get`.)
    public func get(_ id: String) -> RegisteredPlugin? {
        lock.lock(); defer { lock.unlock() }
        return installed.first { $0.id.caseInsensitiveCompare(id) == .orderedSame }
    }

    /// Registers (or replaces by id) a plugin; new entries start disabled and
    /// the manifest is saved. (C# `Register`.)
    @discardableResult
    public func register(id: String, displayName: String, version: String, permissions: [String]) -> RegisteredPlugin {
        let entry = RegisteredPlugin(
            id: id, displayName: displayName, version: version,
            permissions: permissions, enabled: false, installedAt: Date())
        lock.lock()
        installed.removeAll { $0.id.caseInsensitiveCompare(id) == .orderedSame }
        installed.append(entry)
        save()
        lock.unlock()
        return entry
    }

    /// Enables/disables a plugin. Returns false when unknown. (C# `SetEnabled`.)
    @discardableResult
    public func setEnabled(_ id: String, _ enabled: Bool) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let idx = installed.firstIndex(where: { $0.id.caseInsensitiveCompare(id) == .orderedSame }) else { return false }
        installed[idx].enabled = enabled
        save()
        return true
    }

    /// Grants a permission (idempotent). Returns false when unknown. (C#
    /// `GrantPermission`.)
    @discardableResult
    public func grantPermission(_ id: String, _ permission: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let idx = installed.firstIndex(where: { $0.id.caseInsensitiveCompare(id) == .orderedSame }) else { return false }
        if !installed[idx].permissions.contains(where: { $0.caseInsensitiveCompare(permission) == .orderedSame }) {
            installed[idx].permissions.append(permission)
            save()
        }
        return true
    }

    /// Revokes a permission. Returns true when something was removed (and saved).
    /// (C# `RevokePermission`.)
    @discardableResult
    public func revokePermission(_ id: String, _ permission: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let idx = installed.firstIndex(where: { $0.id.caseInsensitiveCompare(id) == .orderedSame }) else { return false }
        let before = installed[idx].permissions.count
        installed[idx].permissions.removeAll { $0.caseInsensitiveCompare(permission) == .orderedSame }
        let removed = installed[idx].permissions.count != before
        if removed { save() }
        return removed
    }

    /// Uninstalls a plugin (and best-effort deletes its `plugins/{id}/` folder).
    /// Returns true when an entry was removed. (C# `Uninstall`.)
    @discardableResult
    public func uninstall(_ id: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        let before = installed.count
        installed.removeAll { $0.id.caseInsensitiveCompare(id) == .orderedSame }
        let removed = installed.count != before
        if removed {
            save()
            let dir = (pluginsRoot as NSString).appendingPathComponent(id)
            var isDir: ObjCBool = false
            if FileManager.default.fileExists(atPath: dir, isDirectory: &isDir), isDir.boolValue {
                do {
                    try FileManager.default.removeItem(atPath: dir)
                } catch {
                    logger?.logInformation("Failed to delete plugin folder \(dir): \(error.localizedDescription)")
                }
            }
        }
        return removed
    }

    // MARK: persistence

    /// Load `registry.json`; corrupt content starts fresh. (C# `Load`.)
    private func load() {
        guard FileManager.default.fileExists(atPath: manifestPath) else { return }
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: manifestPath)) else { return }
        if let loaded = try? Self.decoder.decode([RegisteredPlugin].self, from: data) {
            installed = loaded
        }
        // else: corrupt — start fresh (matches the C# catch-and-ignore).
    }

    /// Atomic save: write tmp → delete existing → rename. (C# `Save`.)
    /// Caller holds `lock`.
    private func save() {
        do {
            let data = try Self.encoder.encode(installed)
            let tmp = manifestPath + ".tmp"
            try data.write(to: URL(fileURLWithPath: tmp))
            if FileManager.default.fileExists(atPath: manifestPath) {
                try FileManager.default.removeItem(atPath: manifestPath)
            }
            try FileManager.default.moveItem(atPath: tmp, toPath: manifestPath)
        } catch {
            logger?.logInformation("Failed to save plugin registry: \(error.localizedDescription)")
        }
    }

    private static let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        e.dateEncodingStrategy = .iso8601
        return e
    }()

    private static let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()
}
