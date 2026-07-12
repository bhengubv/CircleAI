// ToolsCatalog.swift
//
// Port of CircleAI.Tools.Catalog — the full provider-catalog contract surface
// (composio pattern) + real in-memory primitives.
//   • Contracts.cs             — AuthKind, ProviderDescriptor, OAuth2Descriptor,
//                                CredentialBundle, QuotaPolicy, ToolNamespace,
//                                IProviderCatalog, ICredentialStore,
//                                IOAuth2FlowDriver, IQuotaGuard, IToolNamespaceStore
//   • InMemoryToolsCatalog.cs  — InMemoryProviderCatalog, AesGcmCredentialStore,
//                                OAuth2FlowDriver, SlidingWindowQuotaGuard,
//                                InMemoryToolNamespaceStore
//   • NullImplementations.cs   — Null* fail-closed backends
//
// Porting notes:
//   • `DateTimeOffset?` → `Date?`; `IReadOnlyDictionary<string,string>` →
//     `[String:String]`; `ValueTask<T>` → `async` / `async throws`.
//   • Crypto seam (task rule): the AES-GCM store depends on the
//     `ICredentialCipher` protocol, NOT on CryptoKit directly. The default
//     `AesGcmCredentialCipher` is CryptoKit-backed (macOS build host) and
//     produces the same nonce(12) || tag(16) || ciphertext layout as the C#
//     `AesGcm` store. Injecting a different cipher (or a stub in a test) keeps
//     the store portable.
//   • `OAuth2FlowDriver` takes injected `clientIdFor` + `exchange` closures,
//     exactly like the C# `Func<>` parameters. `WebUtility.UrlEncode` →
//     `addingPercentEncoding` with a query-safe allowed set. `state` is 16
//     random bytes, URL-safe base64, padding trimmed.
//   • `SlidingWindowQuotaGuard` keeps the per-minute list + daily budget +
//     max-concurrent semantics under a single `NSLock` (C# used one `_lock`).
//   • Errors: C# throws `ArgumentException` / `InvalidOperationException`;
//     the Swift port surfaces these as `ToolsCatalogError`.

import Foundation
import CryptoKit

// MARK: - Errors

/// Errors raised by the tools-catalog primitives.
public enum ToolsCatalogError: Error, Equatable, CustomStringConvertible {
    case argument(String)
    case invalidOperation(String)

    public var description: String {
        switch self {
        case .argument(let m): return m
        case .invalidOperation(let m): return m
        }
    }
}

// MARK: - Records

/// How the provider authenticates. (C# `AuthKind`.)
public enum AuthKind: Int, Sendable, Codable, CaseIterable {
    case none = 0
    case apiKey = 1
    case bearerToken = 2
    case oauth2 = 3
    case basic = 4
    case custom = 5
}

/// OAuth2 configuration when a provider's `auth` is `.oauth2`. (C# `OAuth2Descriptor`.)
public struct OAuth2Descriptor: Sendable, Equatable, Codable {
    public let authorizeUrl: String
    public let tokenUrl: String
    public let scopes: [String]
    public let userInfoUrl: String?

    public init(authorizeUrl: String, tokenUrl: String, scopes: [String], userInfoUrl: String? = nil) {
        self.authorizeUrl = authorizeUrl
        self.tokenUrl = tokenUrl
        self.scopes = scopes
        self.userInfoUrl = userInfoUrl
    }
}

/// One provider in the catalog (Gmail, Slack, Linear, …). (C# `ProviderDescriptor`.)
public struct ProviderDescriptor: Sendable, Equatable, Codable {
    public let providerId: String
    public let displayName: String
    public let description: String
    public let homepage: String?
    public let auth: AuthKind
    public let tags: [String]
    public let capabilities: [String]
    public let oauth2: OAuth2Descriptor?

    public init(providerId: String, displayName: String, description: String, homepage: String?,
                auth: AuthKind, tags: [String], capabilities: [String], oauth2: OAuth2Descriptor? = nil) {
        self.providerId = providerId
        self.displayName = displayName
        self.description = description
        self.homepage = homepage
        self.auth = auth
        self.tags = tags
        self.capabilities = capabilities
        self.oauth2 = oauth2
    }
}

/// One stored credential for one user / one provider. (C# `CredentialBundle`.)
public struct CredentialBundle: Sendable, Equatable, Codable {
    public let providerId: String
    public let userId: String
    public let fields: [String: String]
    public let expiresAtUtc: Date?

    public init(providerId: String, userId: String, fields: [String: String], expiresAtUtc: Date? = nil) {
        self.providerId = providerId
        self.userId = userId
        self.fields = fields
        self.expiresAtUtc = expiresAtUtc
    }
}

/// A quota / rate-limit policy on one (provider, user) pair. (C# `QuotaPolicy`.)
public struct QuotaPolicy: Sendable, Equatable, Codable {
    public let providerId: String
    public let userId: String
    public let dailyCallBudget: Int
    public let maxConcurrent: Int
    public let perMinuteCap: Int

    public init(providerId: String, userId: String, dailyCallBudget: Int, maxConcurrent: Int, perMinuteCap: Int) {
        self.providerId = providerId
        self.userId = userId
        self.dailyCallBudget = dailyCallBudget
        self.maxConcurrent = maxConcurrent
        self.perMinuteCap = perMinuteCap
    }
}

/// Namespace partition — keeps one user's tool list separate from the next.
/// (C# `ToolNamespace`.)
public struct ToolNamespace: Sendable, Equatable, Codable {
    public let namespaceId: String
    public let ownerUserId: String
    public let providerIds: [String]

    public init(namespaceId: String, ownerUserId: String, providerIds: [String]) {
        self.namespaceId = namespaceId
        self.ownerUserId = ownerUserId
        self.providerIds = providerIds
    }
}

// MARK: - Contracts

/// The provider directory. (C# `IProviderCatalog`.)
public protocol IProviderCatalog: Sendable {
    var backendId: String { get }
    func listProviders() async -> [ProviderDescriptor]
    func getProvider(_ providerId: String) async throws -> ProviderDescriptor?
    /// Semantic (substring + tag) search over registered providers.
    func searchProviders(_ query: String, topK: Int) async throws -> [ProviderDescriptor]
}

public extension IProviderCatalog {
    func searchProviders(_ query: String) async throws -> [ProviderDescriptor] {
        try await searchProviders(query, topK: 8)
    }
}

/// Credential storage. Implementations must encrypt at rest. (C# `ICredentialStore`.)
public protocol ICredentialStore: Sendable {
    var backendId: String { get }
    func upsert(_ bundle: CredentialBundle) async throws
    func get(providerId: String, userId: String) async throws -> CredentialBundle?
    func delete(providerId: String, userId: String) async throws
}

/// OAuth2 flow driver — initiates + completes a 3-legged flow. (C# `IOAuth2FlowDriver`.)
public protocol IOAuth2FlowDriver: Sendable {
    var backendId: String { get }
    /// Build the redirect URL for the user's browser.
    func start(providerId: String, userId: String, redirectUri: String) async throws -> String
    /// Exchange the authorisation code for a credential bundle.
    func complete(providerId: String, userId: String, authorizationCode: String,
                  redirectUri: String) async throws -> CredentialBundle
}

/// Per-(provider,user) quota enforcement. (C# `IQuotaGuard`.)
public protocol IQuotaGuard: Sendable {
    var backendId: String { get }
    func tryAcquire(providerId: String, userId: String) async -> Bool
    func setPolicy(_ policy: QuotaPolicy) async
    func getPolicy(providerId: String, userId: String) async -> QuotaPolicy?
}

/// Namespace store. (C# `IToolNamespaceStore`.)
public protocol IToolNamespaceStore: Sendable {
    var backendId: String { get }
    func upsert(_ ns: ToolNamespace) async throws
    func get(_ namespaceId: String) async throws -> ToolNamespace?
    func listForUser(_ userId: String) async throws -> [ToolNamespace]
}

// MARK: - Crypto seam

/// Symmetric AEAD cipher seam so the credential store never depends on a
/// concrete crypto library directly. `seal` returns the combined
/// nonce || tag || ciphertext blob; `open` reverses it (returns `nil` on any
/// authentication failure).
public protocol ICredentialCipher: Sendable {
    func seal(_ plaintext: Data) -> Data
    func open(_ combined: Data) -> Data?
}

/// CryptoKit AES-256-GCM cipher. Layout: nonce(12) || tag(16) || ciphertext,
/// matching the C# `AesGcm` store byte-for-byte. Requires a 32-byte key.
public struct AesGcmCredentialCipher: ICredentialCipher, @unchecked Sendable {
    private let key: SymmetricKey

    /// - Parameter key32: exactly 32 bytes (AES-256).
    public init(key32: Data) throws {
        guard key32.count == 32 else {
            throw ToolsCatalogError.argument("key must be 32 bytes (AES-256-GCM)")
        }
        self.key = SymmetricKey(data: key32)
    }

    public func seal(_ plaintext: Data) -> Data {
        // A fresh random 12-byte nonce per seal (CryptoKit default).
        guard let sealed = try? AES.GCM.seal(plaintext, using: key) else { return Data() }
        var combined = Data(sealed.nonce)          // 12 bytes
        combined.append(contentsOf: sealed.tag)    // 16 bytes
        combined.append(sealed.ciphertext)         // n bytes
        return combined
    }

    public func open(_ combined0: Data) -> Data? {
        // Re-base to a zero-indexed buffer so the byte offsets are unambiguous
        // even if `combined0` is a slice with a non-zero startIndex.
        let combined = Data(combined0)
        guard combined.count >= 28 else { return nil }
        let nonceData = combined.subdata(in: 0..<12)
        let tagData = combined.subdata(in: 12..<28)
        let ct = combined.subdata(in: 28..<combined.count)
        guard let nonce = try? AES.GCM.Nonce(data: nonceData),
              let box = try? AES.GCM.SealedBox(nonce: nonce, ciphertext: ct, tag: tagData),
              let pt = try? AES.GCM.open(box, using: key) else {
            return nil
        }
        return pt
    }
}

// MARK: - In-memory implementations

/// In-memory provider catalog with substring + tag search. (C# `InMemoryProviderCatalog`.)
public final class InMemoryProviderCatalog: IProviderCatalog, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: ProviderDescriptor] = [:]  // keyed case-insensitively

    public init() {}

    public var backendId: String { "in-memory" }

    /// Registers (or replaces) a provider descriptor.
    public func register(_ p: ProviderDescriptor) {
        lock.lock(); items[p.providerId.lowercased()] = p; lock.unlock()
    }

    public func listProviders() async -> [ProviderDescriptor] {
        lock.lock(); let all = Array(items.values); lock.unlock()
        return all.sorted { $0.providerId < $1.providerId }
    }

    public func getProvider(_ providerId: String) async throws -> ProviderDescriptor? {
        guard !providerId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ToolsCatalogError.argument("providerId required")
        }
        lock.lock(); defer { lock.unlock() }
        return items[providerId.lowercased()]
    }

    public func searchProviders(_ query: String, topK: Int) async throws -> [ProviderDescriptor] {
        guard topK > 0 else { throw ToolsCatalogError.argument("topK must be positive") }
        lock.lock(); let all = Array(items.values); lock.unlock()
        return all
            .map { (p: $0, s: Self.score($0, query)) }
            .filter { $0.s > 0 }
            .sorted { $0.s > $1.s }
            .prefix(topK)
            .map { $0.p }
    }

    private static func score(_ p: ProviderDescriptor, _ q: String) -> Int {
        var s = 0
        if p.displayName.range(of: q, options: .caseInsensitive) != nil { s += 3 }
        if p.description.range(of: q, options: .caseInsensitive) != nil { s += 1 }
        if p.tags.contains(where: { $0.range(of: q, options: .caseInsensitive) != nil }) { s += 2 }
        if p.capabilities.contains(where: { $0.range(of: q, options: .caseInsensitive) != nil }) { s += 2 }
        return s
    }
}

/// AES-GCM-encrypted credential store. Encryption is delegated to an injected
/// `ICredentialCipher` (default AES-256-GCM). (C# `AesGcmCredentialStore`.)
public final class AesGcmCredentialStore: ICredentialStore, @unchecked Sendable {
    private let cipher: any ICredentialCipher
    private let lock = NSLock()
    private var enc: [String: Data] = [:]

    /// Injects a cipher seam. Use `AesGcmCredentialStore(key32:)` for the
    /// default CryptoKit backing.
    public init(cipher: any ICredentialCipher) {
        self.cipher = cipher
    }

    /// Convenience: build with a 32-byte key and the default AES-256-GCM cipher.
    public convenience init(key32: Data) throws {
        self.init(cipher: try AesGcmCredentialCipher(key32: key32))
    }

    public var backendId: String { "aes-gcm" }

    public func upsert(_ bundle: CredentialBundle) async throws {
        let json = try JSONEncoder().encode(bundle)
        let combined = cipher.seal(json)
        lock.lock(); enc[Self.key(bundle.providerId, bundle.userId)] = combined; lock.unlock()
    }

    public func get(providerId: String, userId: String) async throws -> CredentialBundle? {
        guard !providerId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ToolsCatalogError.argument("providerId required")
        }
        guard !userId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ToolsCatalogError.argument("userId required")
        }
        lock.lock(); let combined = enc[Self.key(providerId, userId)]; lock.unlock()
        guard let combined = combined, let pt = cipher.open(combined) else { return nil }
        // A decode failure on authenticated plaintext is treated as "absent"
        // (mirrors the C# catch → null path).
        return try? JSONDecoder().decode(CredentialBundle.self, from: pt)
    }

    public func delete(providerId: String, userId: String) async throws {
        guard !providerId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ToolsCatalogError.argument("providerId required")
        }
        guard !userId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ToolsCatalogError.argument("userId required")
        }
        lock.lock(); enc.removeValue(forKey: Self.key(providerId, userId)); lock.unlock()
    }

    private static func key(_ p: String, _ u: String) -> String { "\(p)/\(u)" }
}

/// OAuth2 flow driver — builds the authorise URL; token exchange delegated to a
/// host closure. (C# `OAuth2FlowDriver`.)
public final class OAuth2FlowDriver: IOAuth2FlowDriver, @unchecked Sendable {
    public typealias Exchange = @Sendable (_ providerId: String, _ userId: String,
                                           _ code: String, _ redirectUri: String) async throws -> CredentialBundle

    private let catalog: any IProviderCatalog
    private let clientIdFor: @Sendable (String) -> String
    private let exchange: Exchange

    public init(catalog: any IProviderCatalog,
                clientIdFor: @escaping @Sendable (String) -> String,
                exchange: @escaping Exchange) {
        self.catalog = catalog
        self.clientIdFor = clientIdFor
        self.exchange = exchange
    }

    public var backendId: String { "oauth2" }

    public func start(providerId: String, userId: String, redirectUri: String) async throws -> String {
        guard !providerId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ToolsCatalogError.argument("providerId required")
        }
        guard !userId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ToolsCatalogError.argument("userId required")
        }
        guard !redirectUri.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ToolsCatalogError.argument("redirectUri required")
        }

        guard let provider = try await catalog.getProvider(providerId) else {
            throw ToolsCatalogError.invalidOperation("Unknown provider '\(providerId)'.")
        }
        guard let oauth = provider.oauth2 else {
            throw ToolsCatalogError.invalidOperation("Provider '\(providerId)' is not OAuth2.")
        }

        let state = Self.urlSafeBase64(Self.randomBytes(16))
        let scopes = oauth.scopes.joined(separator: " ")
        let clientId = clientIdFor(providerId)
        let url = "\(oauth.authorizeUrl)?response_type=code"
            + "&client_id=\(Self.urlEncode(clientId))"
            + "&redirect_uri=\(Self.urlEncode(redirectUri))"
            + "&scope=\(Self.urlEncode(scopes))"
            + "&state=\(Self.urlEncode(state))"
        return url
    }

    public func complete(providerId: String, userId: String, authorizationCode: String,
                         redirectUri: String) async throws -> CredentialBundle {
        guard !providerId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ToolsCatalogError.argument("providerId required")
        }
        guard !userId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ToolsCatalogError.argument("userId required")
        }
        guard !authorizationCode.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ToolsCatalogError.argument("authorizationCode required")
        }
        guard !redirectUri.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ToolsCatalogError.argument("redirectUri required")
        }
        return try await exchange(providerId, userId, authorizationCode, redirectUri)
    }

    private static func randomBytes(_ n: Int) -> Data {
        var bytes = [UInt8](repeating: 0, count: n)
        for i in 0..<n { bytes[i] = UInt8.random(in: 0...255) }
        return Data(bytes)
    }

    private static func urlSafeBase64(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "=", with: "")
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
    }

    private static func urlEncode(_ s: String) -> String {
        // Query-component encoding (roughly WebUtility.UrlEncode): keep only
        // unreserved characters, percent-encode everything else.
        let allowed = CharacterSet(charactersIn:
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~")
        return s.addingPercentEncoding(withAllowedCharacters: allowed) ?? s
    }
}

/// Sliding-window per-minute quota + daily budget + max-concurrent semaphore.
/// (C# `SlidingWindowQuotaGuard`.)
public final class SlidingWindowQuotaGuard: IQuotaGuard, @unchecked Sendable {
    private let lock = NSLock()
    private var policies: [String: QuotaPolicy] = [:]
    private var calls: [String: [Date]] = [:]
    private var inflight: [String: Int] = [:]

    public init() {}

    public var backendId: String { "sliding-window" }

    public func tryAcquire(providerId: String, userId: String) async -> Bool {
        let key = Self.key(providerId, userId)
        lock.lock(); defer { lock.unlock() }
        guard let policy = policies[key] else { return true }  // no policy = unlimited

        let now = Date()
        var list = calls[key] ?? []

        // Per-minute cap: drop entries older than 60s.
        let minuteAgo = now.addingTimeInterval(-60)
        list.removeAll { $0 < minuteAgo }
        if list.count >= policy.perMinuteCap { calls[key] = list; return false }

        // Daily budget: count entries within the last 24h.
        let dayAgo = now.addingTimeInterval(-86_400)
        if list.filter({ $0 >= dayAgo }).count >= policy.dailyCallBudget { calls[key] = list; return false }

        // Concurrency.
        let current = inflight[key] ?? 0
        if current >= policy.maxConcurrent { calls[key] = list; return false }

        list.append(now)
        calls[key] = list
        inflight[key] = current + 1
        return true
    }

    /// Releases one in-flight slot. (C# `Release`.)
    public func release(providerId: String, userId: String) {
        let key = Self.key(providerId, userId)
        lock.lock()
        if let n = inflight[key], n > 0 { inflight[key] = n - 1 }
        lock.unlock()
    }

    public func setPolicy(_ policy: QuotaPolicy) async {
        lock.lock(); policies[Self.key(policy.providerId, policy.userId)] = policy; lock.unlock()
    }

    public func getPolicy(providerId: String, userId: String) async -> QuotaPolicy? {
        lock.lock(); defer { lock.unlock() }
        return policies[Self.key(providerId, userId)]
    }

    private static func key(_ p: String, _ u: String) -> String { "\(p)/\(u)" }
}

/// In-memory namespace store. (C# `InMemoryToolNamespaceStore`.)
public final class InMemoryToolNamespaceStore: IToolNamespaceStore, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: ToolNamespace] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    public func upsert(_ ns: ToolNamespace) async throws {
        guard !ns.namespaceId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ToolsCatalogError.argument("NamespaceId required")
        }
        lock.lock(); items[ns.namespaceId] = ns; lock.unlock()
    }

    public func get(_ namespaceId: String) async throws -> ToolNamespace? {
        guard !namespaceId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ToolsCatalogError.argument("namespaceId required")
        }
        lock.lock(); defer { lock.unlock() }
        return items[namespaceId]
    }

    public func listForUser(_ userId: String) async throws -> [ToolNamespace] {
        guard !userId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ToolsCatalogError.argument("userId required")
        }
        lock.lock(); let all = Array(items.values); lock.unlock()
        return all.filter { $0.ownerUserId == userId }
    }
}

// MARK: - Null implementations

/// Fail-closed provider catalog. (C# `NullProviderCatalog`.)
public final class NullProviderCatalog: IProviderCatalog, @unchecked Sendable {
    public static let instance = NullProviderCatalog()
    public init() {}
    public var backendId: String { "null" }
    public func listProviders() async -> [ProviderDescriptor] { [] }
    public func getProvider(_ providerId: String) async throws -> ProviderDescriptor? { nil }
    public func searchProviders(_ query: String, topK: Int) async throws -> [ProviderDescriptor] { [] }
}

/// Fail-closed credential store. (C# `NullCredentialStore`.)
public final class NullCredentialStore: ICredentialStore, @unchecked Sendable {
    public static let instance = NullCredentialStore()
    public init() {}
    public var backendId: String { "null" }
    public func upsert(_ bundle: CredentialBundle) async throws {}
    public func get(providerId: String, userId: String) async throws -> CredentialBundle? { nil }
    public func delete(providerId: String, userId: String) async throws {}
}

/// Fail-closed OAuth2 driver — `start` returns about:blank; `complete` throws.
/// (C# `NullOAuth2FlowDriver`.)
public final class NullOAuth2FlowDriver: IOAuth2FlowDriver, @unchecked Sendable {
    public static let instance = NullOAuth2FlowDriver()
    public init() {}
    public var backendId: String { "null" }
    public func start(providerId: String, userId: String, redirectUri: String) async throws -> String {
        "about:blank"
    }
    public func complete(providerId: String, userId: String, authorizationCode: String,
                         redirectUri: String) async throws -> CredentialBundle {
        throw ToolsCatalogError.invalidOperation("NullOAuth2FlowDriver: no real provider wired.")
    }
}

/// Fail-closed quota guard — every acquire is denied. (C# `NullQuotaGuard`.)
public final class NullQuotaGuard: IQuotaGuard, @unchecked Sendable {
    public static let instance = NullQuotaGuard()
    public init() {}
    public var backendId: String { "null" }
    public func tryAcquire(providerId: String, userId: String) async -> Bool { false }
    public func setPolicy(_ policy: QuotaPolicy) async {}
    public func getPolicy(providerId: String, userId: String) async -> QuotaPolicy? { nil }
}

/// Fail-closed namespace store. (C# `NullToolNamespaceStore`.)
public final class NullToolNamespaceStore: IToolNamespaceStore, @unchecked Sendable {
    public static let instance = NullToolNamespaceStore()
    public init() {}
    public var backendId: String { "null" }
    public func upsert(_ ns: ToolNamespace) async throws {}
    public func get(_ namespaceId: String) async throws -> ToolNamespace? { nil }
    public func listForUser(_ userId: String) async throws -> [ToolNamespace] { [] }
}
