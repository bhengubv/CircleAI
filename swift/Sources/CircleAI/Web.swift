// Web.swift
//
// Port of the PORTABLE surface of src/CircleAI.Web/:
//   • WebPrimitives.cs → RouteDescriptor, PageMetadata, CachedResponse,
//                        IWebBoard, InMemoryWebBoard
//
// NOT ported (non-portable .NET UI / DI glue, deliberately skipped):
//   • WebCompanionService.cs — a scoped Blazor service + `ServiceCollectionExtensions`
//     (`Microsoft.Extensions.DependencyInjection`, SignalR circuit lifecycle). This
//     is host wiring, not portable domain behaviour; the Companion session surface
//     it manages is already ported (CompanionSession.swift). Hosts create sessions
//     directly via `ICompanionSessionFactory`.
//
// Porting notes:
//   • C# `byte[]` → `[UInt8]`.
//   • `ConcurrentDictionary` → `final class … @unchecked Sendable` + `NSLock`.
//   • Route keys are `"METHOD path"` with the method upper-cased (as in C#);
//     metadata is keyed case-insensitively (C# `OrdinalIgnoreCase`) so lookups
//     lower-case the path.
//   • `Cache` skips already-expired entries; `Lookup` evicts on expiry — both
//     reproduced. Blank-argument guards raise via `precondition`.

import Foundation

// MARK: - RouteDescriptor

/// One registered HTTP route. Port of C# record `RouteDescriptor`.
public struct RouteDescriptor: Sendable, Equatable {
    public let path: String
    public let method: String
    public let handlerName: String
    public let tags: [String]

    public init(path: String, method: String, handlerName: String, tags: [String]) {
        self.path = path
        self.method = method
        self.handlerName = handlerName
        self.tags = tags
    }
}

// MARK: - PageMetadata

/// SEO / descriptive metadata for a page. Port of C# record `PageMetadata`.
public struct PageMetadata: Sendable, Equatable {
    public let path: String
    public let title: String
    public let description: String?
    public let keywords: [String]

    public init(path: String, title: String, description: String?, keywords: [String]) {
        self.path = path
        self.title = title
        self.description = description
        self.keywords = keywords
    }
}

// MARK: - CachedResponse

/// A cached HTTP response body with an expiry. Port of C# record `CachedResponse`.
public struct CachedResponse: Sendable, Equatable {
    public let key: String
    public let body: [UInt8]
    public let mime: String
    public let expiresUtc: Date

    public init(key: String, body: [UInt8], mime: String, expiresUtc: Date) {
        self.key = key
        self.body = body
        self.mime = mime
        self.expiresUtc = expiresUtc
    }
}

// MARK: - IWebBoard

/// Registry of routes + page metadata + a simple response cache. Port of C#
/// interface `IWebBoard`.
public protocol IWebBoard: AnyObject, Sendable {
    func register(_ route: RouteDescriptor)
    func routesByMethod(_ method: String) -> [RouteDescriptor]
    func setMetadata(_ metadata: PageMetadata)
    func metadata(path: String) -> PageMetadata?
    func cache(_ response: CachedResponse)
    func lookup(_ key: String) -> CachedResponse?
}

// MARK: - InMemoryWebBoard

/// Thread-safe in-memory `IWebBoard`. Port of C# `InMemoryWebBoard`
/// (three ConcurrentDictionaries → NSLock-guarded dictionaries).
public final class InMemoryWebBoard: IWebBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var routes: [String: RouteDescriptor] = [:]   // key: "METHOD path"
    private var meta: [String: PageMetadata] = [:]        // key: lowercased path
    private var cacheStore: [String: CachedResponse] = [:]

    public init() {}

    public func register(_ route: RouteDescriptor) {
        let key = "\(route.method.uppercased()) \(route.path)"
        lock.lock(); defer { lock.unlock() }
        routes[key] = route
    }

    public func routesByMethod(_ method: String) -> [RouteDescriptor] {
        precondition(!method.trimmingCharacters(in: .whitespaces).isEmpty, "method required")
        lock.lock(); defer { lock.unlock() }
        return routes.values
            .filter { $0.method.caseInsensitiveCompare(method) == .orderedSame }
            .sorted { $0.path < $1.path }
    }

    public func setMetadata(_ metadata: PageMetadata) {
        lock.lock(); defer { lock.unlock() }
        meta[metadata.path.lowercased()] = metadata
    }

    public func metadata(path: String) -> PageMetadata? {
        lock.lock(); defer { lock.unlock() }
        return meta[path.lowercased()]
    }

    public func cache(_ response: CachedResponse) {
        if response.expiresUtc <= Date() { return }  // already expired; skip
        lock.lock(); defer { lock.unlock() }
        cacheStore[response.key] = response
    }

    public func lookup(_ key: String) -> CachedResponse? {
        precondition(!key.trimmingCharacters(in: .whitespaces).isEmpty, "key required")
        lock.lock(); defer { lock.unlock() }
        guard let c = cacheStore[key] else { return nil }
        if c.expiresUtc <= Date() {
            cacheStore.removeValue(forKey: key)
            return nil
        }
        return c
    }
}
