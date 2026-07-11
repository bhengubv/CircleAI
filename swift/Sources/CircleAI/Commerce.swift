// Commerce.swift
//
// Port of the Commerce vertical from src/CircleAI.Commerce/CommercePrimitives.cs
// and the static domain-context constants from CommerceDomainContext.cs:
//   • CommerceCustomer, CommerceOrder, CommerceLineItem — domain records
//   • ICommerceBoard                                    — customers / orders /
//                                                          line items / LTV
//   • InMemoryCommerceBoard                             — deterministic in-memory
//   • CommerceDomainContext                             — system-prompt + flags
//
// The Companion-facing wrapper (CommerceCompanionAdapter) is intentionally NOT
// ported (see Healthcare.swift for the rationale).
//
// Porting notes:
//   • `decimal` → `Decimal`; `DateTimeOffset` → `Date`; `string? Email` → `String?`.
//   • The C# board keeps customers/orders in ConcurrentDictionaries and line
//     items in a `List` under a `lock`; here a single `NSLock` guards all state.
//   • `UpdateStatus` on an unknown order throws → `CommerceError.unknownOrder`.
//   • `OrdersFor` orders descending by `atUtc`; `LinesFor` preserves insertion
//     order (LINQ `Where` over a `List`). `LifetimeValue` sums order totals.

import Foundation

// MARK: - Records

/// A commerce customer.
public struct CommerceCustomer: Sendable, Equatable, Codable {
    /// Stable identifier for the customer.
    public let customerId: String
    /// Customer's name.
    public let name: String
    /// Customer's email, or `nil`.
    public let email: String?
    /// When the customer record was created (UTC).
    public let createdUtc: Date

    public init(customerId: String, name: String, email: String?, createdUtc: Date) {
        self.customerId = customerId
        self.name = name
        self.email = email
        self.createdUtc = createdUtc
    }
}

/// An order placed by a customer.
public struct CommerceOrder: Sendable, Equatable, Codable {
    /// Stable identifier for the order.
    public let orderId: String
    /// Identifier of the placing customer.
    public let customerId: String
    /// Order total.
    public let total: Decimal
    /// ISO currency code.
    public let currency: String
    /// Free-form status (e.g. "pending", "shipped").
    public let status: String
    /// When the order was placed (UTC).
    public let atUtc: Date

    public init(orderId: String, customerId: String, total: Decimal, currency: String,
                status: String, atUtc: Date) {
        self.orderId = orderId
        self.customerId = customerId
        self.total = total
        self.currency = currency
        self.status = status
        self.atUtc = atUtc
    }
}

/// A single line item on an order.
public struct CommerceLineItem: Sendable, Equatable, Codable {
    /// Stable identifier for the line.
    public let lineId: String
    /// Identifier of the owning order.
    public let orderId: String
    /// Stock-keeping unit.
    public let sku: String
    /// Quantity ordered.
    public let quantity: Int
    /// Unit price.
    public let unitPrice: Decimal

    public init(lineId: String, orderId: String, sku: String, quantity: Int, unitPrice: Decimal) {
        self.lineId = lineId
        self.orderId = orderId
        self.sku = sku
        self.quantity = quantity
        self.unitPrice = unitPrice
    }
}

// MARK: - Errors

/// Errors thrown by the commerce board.
public enum CommerceError: Error, Equatable, CustomStringConvertible {
    /// `updateStatus` referenced an order id that is not known.
    case unknownOrder(String)

    public var description: String {
        switch self {
        case .unknownOrder(let id): return "Unknown order \(id)"
        }
    }
}

// MARK: - ICommerceBoard

/// Customers, orders, line items, and lifetime value for the commerce vertical.
/// A synchronous contract — implementations are expected to be thread-safe.
public protocol ICommerceBoard: AnyObject, Sendable {
    /// Adds (or replaces, by `customerId`) a customer.
    func addCustomer(_ c: CommerceCustomer)
    /// Returns the customer with `id`, or `nil`.
    func getCustomer(_ id: String) -> CommerceCustomer?
    /// Places (or replaces, by `orderId`) an order.
    func place(_ o: CommerceOrder)
    /// Appends a line item.
    func addLine(_ l: CommerceLineItem)
    /// Updates an order's status. Throws when the order is unknown.
    func updateStatus(orderId: String, status: String) throws
    /// Orders for `customerId`, most-recent first.
    func ordersFor(customerId: String) -> [CommerceOrder]
    /// Line items for `orderId`, in insertion order.
    func linesFor(orderId: String) -> [CommerceLineItem]
    /// Sum of order totals for `customerId`.
    func lifetimeValue(customerId: String) -> Decimal
}

// MARK: - InMemoryCommerceBoard

/// Deterministic in-memory `ICommerceBoard`. All state is guarded by a single
/// `NSLock`; every accessor returns an immutable snapshot.
public final class InMemoryCommerceBoard: ICommerceBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var customers: [String: CommerceCustomer] = [:]
    private var orders: [String: CommerceOrder] = [:]
    private var lines: [CommerceLineItem] = []

    public init() {}

    public func addCustomer(_ c: CommerceCustomer) {
        lock.lock(); defer { lock.unlock() }
        customers[c.customerId] = c
    }

    public func getCustomer(_ id: String) -> CommerceCustomer? {
        lock.lock(); defer { lock.unlock() }
        return customers[id]
    }

    public func place(_ o: CommerceOrder) {
        lock.lock(); defer { lock.unlock() }
        orders[o.orderId] = o
    }

    public func addLine(_ l: CommerceLineItem) {
        lock.lock(); defer { lock.unlock() }
        lines.append(l)
    }

    public func updateStatus(orderId: String, status: String) throws {
        lock.lock(); defer { lock.unlock() }
        guard let o = orders[orderId] else { throw CommerceError.unknownOrder(orderId) }
        orders[orderId] = CommerceOrder(orderId: o.orderId, customerId: o.customerId, total: o.total,
                                        currency: o.currency, status: status, atUtc: o.atUtc)
    }

    public func ordersFor(customerId: String) -> [CommerceOrder] {
        lock.lock(); defer { lock.unlock() }
        return orders.values.filter { $0.customerId == customerId }.sorted { $0.atUtc > $1.atUtc }
    }

    public func linesFor(orderId: String) -> [CommerceLineItem] {
        lock.lock(); defer { lock.unlock() }
        return lines.filter { $0.orderId == orderId }
    }

    public func lifetimeValue(customerId: String) -> Decimal {
        ordersFor(customerId: customerId).reduce(Decimal(0)) { $0 + $1.total }
    }
}

// MARK: - CommerceDomainContext

/// Static domain-context constants for the commerce vertical. Mirrors
/// `CommerceDomainContext` in CommerceDomainContext.cs.
public enum CommerceDomainContext {
    /// System-prompt snippet injected ahead of user turns in this domain.
    public static let systemPromptSnippet = "[DOMAIN: Commerce] You are an e-commerce and trading expert. Help with product listings, pricing strategy, order management, supplier negotiations, marketplace analytics, and sales optimisation. Apply margin-aware thinking to every recommendation. Compliance: Consumer Protection Act, POPIA."
    /// Regulatory / compliance flags relevant to this domain.
    public static let complianceFlags: [String] = ["POPIA", "Consumer_Protection_Act", "GDPR_aware"]
    /// Tools the Companion may suggest in this domain.
    public static let suggestedTools: [String] = ["inventory", "pricing_engine", "order_management", "analytics"]
}
