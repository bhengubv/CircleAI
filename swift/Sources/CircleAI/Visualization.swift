// Visualization.swift
//
// Port of src/CircleAI.Visualization/:
//   • Contracts.cs                 — DashboardDefinition, ApiDoc, GeneratedSite;
//                                     IDashboardDefinitionStore, IApiDocBuilder,
//                                     ISiteBuilder
//   • InMemoryVisualization.cs     — InMemoryDashboardStore, JsonApiDocBuilder
//                                     (OpenAPI title extraction + canonical
//                                     re-serialise), StaticSiteBuilder (renders
//                                     a {"pages":[{path,html}]} spec to files)
//   • NullImplementations.cs       — Null* backends
//
// Porting notes:
//   • `record` → `struct: Sendable`. `ReadOnlyMemory<byte>` → `[UInt8]`.
//     `GeneratedSite` holds a `[String: [UInt8]]`, so it is Sendable +
//     Equatable but not Codable (raw bytes need no JSON round-trip here).
//   • `System.Text.Json.JsonDocument` → `JSONSerialization`; the canonical
//     re-serialise uses `JSONSerialization.data(..., options: [])` (compact),
//     mirroring `WriteIndented = false`.
//   • `Guid.NewGuid():n` → `UUID().uuidString` lowercased with dashes removed.
//   • Guards → `VisualizationError`.

import Foundation

// MARK: - Records

/// A stored dashboard definition — an opaque JSON spec keyed by id + title.
public struct DashboardDefinition: Sendable, Equatable, Codable {
    public let dashboardId: String
    public let title: String
    public let jsonSpec: String

    public init(dashboardId: String, title: String, jsonSpec: String) {
        self.dashboardId = dashboardId
        self.title = title
        self.jsonSpec = jsonSpec
    }
}

/// A normalised API document (OpenAPI JSON) keyed by id + title.
public struct ApiDoc: Sendable, Equatable, Codable {
    public let docId: String
    public let title: String
    public let openApiJson: String

    public init(docId: String, title: String, openApiJson: String) {
        self.docId = docId
        self.title = title
        self.openApiJson = openApiJson
    }
}

/// A rendered static site — a map of relative path → file bytes.
public struct GeneratedSite: Sendable, Equatable {
    public let siteId: String
    public let files: [String: [UInt8]]

    public init(siteId: String, files: [String: [UInt8]]) {
        self.siteId = siteId
        self.files = files
    }
}

// MARK: - Errors

public enum VisualizationError: Error, Equatable, CustomStringConvertible {
    case dashboardIdRequired
    case idRequired
    case openApiSpecRequired
    case siteSpecRequired
    case siteSpecMissingPages

    public var description: String {
        switch self {
        case .dashboardIdRequired: return "DashboardId required"
        case .idRequired: return "id required"
        case .openApiSpecRequired: return "openApiSpec required"
        case .siteSpecRequired: return "siteSpec required"
        case .siteSpecMissingPages: return "siteSpec must contain a pages[] array."
        }
    }
}

// MARK: - Contracts

public protocol IDashboardDefinitionStore: Sendable {
    var backendId: String { get }
    func upsert(_ d: DashboardDefinition) async throws
    func get(id: String) async throws -> DashboardDefinition?
    func list() async throws -> [DashboardDefinition]
}

public protocol IApiDocBuilder: Sendable {
    var backendId: String { get }
    func build(openApiSpec: String) async throws -> ApiDoc
}

public protocol ISiteBuilder: Sendable {
    var backendId: String { get }
    func build(siteSpec: String) async throws -> GeneratedSite
}

// MARK: - In-memory dashboard store

/// Thread-safe in-memory dashboard-definition store.
public final class InMemoryDashboardStore: IDashboardDefinitionStore, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: DashboardDefinition] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    public func upsert(_ d: DashboardDefinition) async throws {
        if d.dashboardId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw VisualizationError.dashboardIdRequired
        }
        lock.lock(); defer { lock.unlock() }
        items[d.dashboardId] = d
    }

    public func get(id: String) async throws -> DashboardDefinition? {
        if id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw VisualizationError.idRequired
        }
        lock.lock(); defer { lock.unlock() }
        return items[id]
    }

    public func list() async throws -> [DashboardDefinition] {
        lock.lock(); defer { lock.unlock() }
        return Array(items.values)
    }
}

// MARK: - JSON API-doc builder

/// Normalising API-doc builder. Parses the OpenAPI JSON, extracts the title,
/// derives a doc id, and re-serialises compactly for deterministic output.
public struct JsonApiDocBuilder: IApiDocBuilder {
    public init() {}
    public var backendId: String { "json-normaliser" }

    public func build(openApiSpec: String) async throws -> ApiDoc {
        if openApiSpec.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw VisualizationError.openApiSpecRequired
        }
        guard let data = openApiSpec.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data) else {
            throw VisualizationError.openApiSpecRequired
        }

        var title = "API"
        if let obj = root as? [String: Any],
           let info = obj["info"] as? [String: Any],
           let t = info["title"] as? String {
            title = t
        }
        let docId = title.replacingOccurrences(of: " ", with: "-").lowercased()
        let canonicalData = (try? JSONSerialization.data(withJSONObject: root, options: [])) ?? Data()
        let canonical = String(decoding: canonicalData, as: UTF8.self)
        return ApiDoc(docId: docId, title: title, openApiJson: canonical)
    }
}

// MARK: - Static site builder

/// Builds a static site from a JSON spec `{"pages":[{"path":..,"html":..}]}`.
public struct StaticSiteBuilder: ISiteBuilder {
    public init() {}
    public var backendId: String { "static" }

    public func build(siteSpec: String) async throws -> GeneratedSite {
        if siteSpec.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw VisualizationError.siteSpecRequired
        }
        guard let data = siteSpec.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let pages = root["pages"] as? [Any] else {
            throw VisualizationError.siteSpecMissingPages
        }

        var files: [String: [UInt8]] = [:]
        for page in pages {
            guard let p = page as? [String: Any] else { continue }
            let path = p["path"] as? String
            let html = p["html"] as? String
            guard let path, !path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                  let html else { continue }
            files[path] = Array(html.utf8)
        }

        let siteId = "site-" + UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        return GeneratedSite(siteId: siteId, files: files)
    }
}

// MARK: - Null backends

public final class NullDashboardDefinitionStore: IDashboardDefinitionStore, @unchecked Sendable {
    public static let instance = NullDashboardDefinitionStore()
    public init() {}
    public var backendId: String { "null" }
    public func upsert(_ d: DashboardDefinition) async throws {}
    public func get(id: String) async throws -> DashboardDefinition? { nil }
    public func list() async throws -> [DashboardDefinition] { [] }
}

public struct NullApiDocBuilder: IApiDocBuilder {
    public static let instance = NullApiDocBuilder()
    public init() {}
    public var backendId: String { "null" }
    public func build(openApiSpec: String) async throws -> ApiDoc {
        ApiDoc(docId: "00000000-0000-0000-0000-000000000000", title: "", openApiJson: "{}")
    }
}

public struct NullSiteBuilder: ISiteBuilder {
    public static let instance = NullSiteBuilder()
    public init() {}
    public var backendId: String { "null" }
    public func build(siteSpec: String) async throws -> GeneratedSite {
        GeneratedSite(siteId: "00000000-0000-0000-0000-000000000000", files: [:])
    }
}
