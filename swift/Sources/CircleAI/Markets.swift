// Markets.swift
//
// Port of the Markets vertical from src/CircleAI.Markets/:
//   • Contracts.cs            — OrderSide, OrderType enums; Instrument, Quote,
//                               OrderRequest, OrderResult records;
//                               IMarketDataFeed, IInstrumentCatalog, IOrderRouter
//   • InMemoryMarkets.cs      — in-memory catalog (case-insensitive symbol),
//                               market-data feed (subscribe/broadcast quote
//                               pushes), order router (positive quantity /
//                               known instrument / valid limit price rules)
//   • NullImplementations.cs  — fail-closed Null* backends
//
// Porting notes:
//   • `decimal` → `Decimal`; `DateTimeOffset` → `Date`.
//   • `OrderSide` / `OrderType` are C# `int`-backed enums; ported as
//     `String`-backed Swift enums so the DTOs Codable round-trip cleanly.
//   • The feed's `SubscribeQuotes` takes an `async` handler and returns a
//     `MarketSubscription` (Swift analogue of the `IDisposable`); `cancel()`
//     removes the handler (identified by a monotonic token, since Swift closures
//     are not `Equatable`).
//   • `Publish` snapshots the handler list **under the lock, then releases it**
//     before invoking handlers — matching the C# `lock (_gate) snap = list.ToArray()`
//     then iterate-outside-lock pattern, and satisfying the "snapshot + unlock
//     before invoking" concurrency rule. Each handler is dispatched on a detached
//     Task (fire-and-forget, unbounded) and its throws are swallowed, mirroring
//     the C# `try { _ = s(q); } catch { … }`.
//   • The order router uses an atomic counter (`ord-{n}`) guarded by the lock,
//     matching `Interlocked.Increment`.

import Foundation

// MARK: - Enums

/// Side of an order.
public enum OrderSide: String, Sendable, Equatable, Codable, CaseIterable {
    case buy = "Buy"
    case sell = "Sell"
}

/// Type of an order.
public enum OrderType: String, Sendable, Equatable, Codable, CaseIterable {
    case market = "Market"
    case limit = "Limit"
}

// MARK: - Records

/// A tradable instrument.
public struct Instrument: Sendable, Equatable, Codable {
    public let symbol: String
    public let exchange: String
    public let currency: String
    public let assetClass: String

    public init(symbol: String, exchange: String, currency: String, assetClass: String) {
        self.symbol = symbol
        self.exchange = exchange
        self.currency = currency
        self.assetClass = assetClass
    }
}

/// A market-data quote.
public struct Quote: Sendable, Equatable, Codable {
    public let symbol: String
    public let bid: Decimal
    public let ask: Decimal
    public let last: Decimal
    public let atUtc: Date

    public init(symbol: String, bid: Decimal, ask: Decimal, last: Decimal, atUtc: Date) {
        self.symbol = symbol
        self.bid = bid
        self.ask = ask
        self.last = last
        self.atUtc = atUtc
    }
}

/// A request to submit an order.
public struct OrderRequest: Sendable, Equatable, Codable {
    public let symbol: String
    public let side: OrderSide
    public let type: OrderType
    public let quantity: Decimal
    public let limitPrice: Decimal?

    public init(symbol: String, side: OrderSide, type: OrderType, quantity: Decimal, limitPrice: Decimal?) {
        self.symbol = symbol
        self.side = side
        self.type = type
        self.quantity = quantity
        self.limitPrice = limitPrice
    }
}

/// The result of submitting an order.
public struct OrderResult: Sendable, Equatable, Codable {
    public let orderId: String
    public let accepted: Bool
    public let failureReason: String?

    public init(orderId: String, accepted: Bool, failureReason: String?) {
        self.orderId = orderId
        self.accepted = accepted
        self.failureReason = failureReason
    }
}

// MARK: - Errors

/// Errors thrown by market backends. Mirrors the C# `ArgumentException` /
/// `ArgumentOutOfRangeException` guards.
public enum MarketsError: Error, Equatable, CustomStringConvertible {
    case symbolRequired
    case queryRequired
    case topKOutOfRange

    public var description: String {
        switch self {
        case .symbolRequired: return "symbol required"
        case .queryRequired: return "query required"
        case .topKOutOfRange: return "topK out of range"
        }
    }
}

// MARK: - Contracts

/// A handle that removes a quote subscription when cancelled. Swift analogue of
/// the C# `IDisposable` returned by `SubscribeQuotes`.
public protocol MarketSubscription: Sendable {
    func cancel()
}

/// Streams and reads market-data quotes.
public protocol IMarketDataFeed: Sendable {
    var backendId: String { get }
    func getQuote(_ symbol: String) async throws -> Quote?
    /// Subscribes `handler` to quote pushes for `symbol`. Returns a handle whose
    /// `cancel()` removes the subscription.
    func subscribeQuotes(_ symbol: String, handler: @escaping @Sendable (Quote) async -> Void) throws -> MarketSubscription
}

/// Looks up and searches instruments.
public protocol IInstrumentCatalog: Sendable {
    var backendId: String { get }
    func get(_ symbol: String) async throws -> Instrument?
    func search(_ query: String, topK: Int) async throws -> [Instrument]
}

public extension IInstrumentCatalog {
    /// Overload matching the C# default `topK = 20`.
    func search(_ query: String) async throws -> [Instrument] {
        try await search(query, topK: 20)
    }
}

/// Routes orders to an execution venue.
public protocol IOrderRouter: Sendable {
    var backendId: String { get }
    func submit(_ req: OrderRequest) async -> OrderResult
}

// MARK: - In-memory backends

/// Deterministic in-memory `IInstrumentCatalog`. Symbols compared
/// case-insensitively; search substring-matches the symbol, ordered ascending.
public final class InMemoryInstrumentCatalog: IInstrumentCatalog, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: Instrument] = [:]   // key: lowercased symbol

    public init() {}
    public var backendId: String { "in-memory" }

    /// Adds (or replaces, by case-insensitive symbol) an instrument.
    public func add(_ item: Instrument) {
        lock.lock(); defer { lock.unlock() }
        items[item.symbol.lowercased()] = item
    }

    public func get(_ symbol: String) async throws -> Instrument? {
        if symbol.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw MarketsError.symbolRequired }
        lock.lock(); defer { lock.unlock() }
        return items[symbol.lowercased()]
    }

    public func search(_ query: String, topK: Int) async throws -> [Instrument] {
        if topK <= 0 { throw MarketsError.topKOutOfRange }
        lock.lock(); defer { lock.unlock() }
        let hits = items.values
            .filter { $0.symbol.range(of: query, options: .caseInsensitive) != nil }
            .sorted { $0.symbol < $1.symbol }
        return Array(hits.prefix(topK))
    }
}

/// Deterministic in-memory `IMarketDataFeed`. Supports subscribe/broadcast quote
/// pushes. Handlers are stored keyed by a monotonic token; `Publish` snapshots
/// the handler list under the lock, unlocks, then dispatches each handler on a
/// detached task (throws swallowed).
public final class InMemoryMarketDataFeed: IMarketDataFeed, @unchecked Sendable {
    private let lock = NSLock()
    private var quotes: [String: Quote] = [:]       // key: lowercased symbol
    // Per (lowercased) symbol: token → handler.
    private var subs: [String: [UInt64: @Sendable (Quote) async -> Void]] = [:]
    private var nextToken: UInt64 = 0

    public init() {}
    public var backendId: String { "in-memory" }

    /// Records the latest quote for its symbol and pushes it to all subscribers.
    public func publish(_ q: Quote) {
        let key = q.symbol.lowercased()
        // Snapshot handlers under the lock, then release before invoking.
        lock.lock()
        quotes[key] = q
        let snapshot = Array((subs[key] ?? [:]).values)
        lock.unlock()
        for handler in snapshot {
            // Fire-and-forget, unbounded fan-out; a throwing/hanging handler
            // cannot block the publisher or another subscriber.
            Task.detached { await handler(q) }
        }
    }

    public func getQuote(_ symbol: String) async throws -> Quote? {
        if symbol.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw MarketsError.symbolRequired }
        lock.lock(); defer { lock.unlock() }
        return quotes[symbol.lowercased()]
    }

    public func subscribeQuotes(_ symbol: String, handler: @escaping @Sendable (Quote) async -> Void) throws -> MarketSubscription {
        if symbol.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw MarketsError.symbolRequired }
        let key = symbol.lowercased()
        lock.lock()
        let token = nextToken
        nextToken &+= 1
        subs[key, default: [:]][token] = handler
        lock.unlock()
        return Subscription(owner: self, key: key, token: token)
    }

    /// Removes the handler identified by `token` under `key`.
    fileprivate func unsubscribe(key: String, token: UInt64) {
        lock.lock(); defer { lock.unlock() }
        subs[key]?.removeValue(forKey: token)
    }

    private final class Subscription: MarketSubscription, @unchecked Sendable {
        private let owner: InMemoryMarketDataFeed
        private let key: String
        private let token: UInt64
        private let cancelLock = NSLock()
        private var cancelled = false

        init(owner: InMemoryMarketDataFeed, key: String, token: UInt64) {
            self.owner = owner
            self.key = key
            self.token = token
        }

        func cancel() {
            cancelLock.lock()
            if cancelled { cancelLock.unlock(); return }
            cancelled = true
            cancelLock.unlock()
            owner.unsubscribe(key: key, token: token)
        }
    }
}

/// Deterministic in-memory `IOrderRouter`. Accepts an order when the quantity is
/// positive, any limit order carries a positive limit price, and the instrument
/// is known to the injected catalog. Order ids are `ord-{n}`.
public final class InMemoryOrderRouter: IOrderRouter, @unchecked Sendable {
    private let catalog: IInstrumentCatalog
    private let lock = NSLock()
    private var seq: Int64 = 0

    public init(_ catalog: IInstrumentCatalog) { self.catalog = catalog }
    public var backendId: String { "in-memory" }

    public func submit(_ req: OrderRequest) async -> OrderResult {
        if req.quantity <= 0 {
            return OrderResult(orderId: nextId(), accepted: false, failureReason: "Quantity must be positive")
        }
        if req.type == .limit, req.limitPrice == nil || (req.limitPrice ?? 0) <= 0 {
            return OrderResult(orderId: nextId(), accepted: false, failureReason: "Limit order requires positive LimitPrice")
        }
        let inst = try? await catalog.get(req.symbol)
        if inst == nil {
            return OrderResult(orderId: nextId(), accepted: false, failureReason: "Unknown symbol")
        }
        return OrderResult(orderId: nextId(), accepted: true, failureReason: nil)
    }

    private func nextId() -> String {
        lock.lock(); defer { lock.unlock() }
        seq += 1
        return "ord-\(seq)"
    }
}

// MARK: - Null (fail-closed) backends

/// Fail-closed `IMarketDataFeed`: no quotes, no-op subscriptions.
public final class NullMarketDataFeed: IMarketDataFeed, @unchecked Sendable {
    public static let instance = NullMarketDataFeed()
    public init() {}
    public var backendId: String { "null" }
    public func getQuote(_ symbol: String) async throws -> Quote? { nil }
    public func subscribeQuotes(_ symbol: String, handler: @escaping @Sendable (Quote) async -> Void) throws -> MarketSubscription {
        EmptySubscription.instance
    }

    private final class EmptySubscription: MarketSubscription {
        static let instance = EmptySubscription()
        func cancel() {}
    }
}

/// Fail-closed `IInstrumentCatalog`.
public final class NullInstrumentCatalog: IInstrumentCatalog, @unchecked Sendable {
    public static let instance = NullInstrumentCatalog()
    public init() {}
    public var backendId: String { "null" }
    public func get(_ symbol: String) async throws -> Instrument? { nil }
    public func search(_ query: String, topK: Int) async throws -> [Instrument] { [] }
}

/// Fail-closed `IOrderRouter`: always declines with the empty GUID.
public final class NullOrderRouter: IOrderRouter, @unchecked Sendable {
    public static let instance = NullOrderRouter()
    public init() {}
    public var backendId: String { "null" }
    public func submit(_ req: OrderRequest) async -> OrderResult {
        OrderResult(orderId: "00000000-0000-0000-0000-000000000000", accepted: false,
                    failureReason: "NullOrderRouter — fail-closed.")
    }
}
