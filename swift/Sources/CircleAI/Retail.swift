// Retail.swift
//
// Port of the Retail vertical from src/CircleAI.Retail/RetailPrimitives.cs and
// the static domain-context constants from RetailDomainContext.cs:
//   • Product, StockLevel, Sale — domain records
//   • TopSeller                 — value type for the C# tuple
//                                 `(string Sku, int Sold)` (Swift cannot Codable
//                                 a raw tuple)
//   • IRetailBoard              — products, stock, sales, revenue, top sellers
//   • InMemoryRetailBoard       — deterministic in-memory impl
//   • RetailDomainContext       — system-prompt snippet + flags
//
// The Companion-facing wrapper (RetailCompanionAdapter) is intentionally NOT
// ported.
//
// Porting notes:
//   • `decimal` → `Decimal`; `DateTimeOffset` → `Date`.
//   • `RecordSale` for an unknown SKU throws → `RetailError.unknownSku`; it also
//     decrements stock (`Stock(sku) - Quantity`).
//   • `RevenueToday` sums `UnitPrice * Quantity` over sales whose calendar day
//     (UTC) matches `now`'s day. The comparison uses a UTC calendar to mirror
//     `DateTimeOffset.Date` (which drops the time-of-day at the value's offset;
//     the C# tests use UTC timestamps, so a UTC calendar reproduces them).
//   • `TopSellersSince` groups sales at/after `since` by SKU, sums quantity,
//     orders descending by quantity, `Take(topK)`; non-positive topK throws.
//   • All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// A product for sale.
public struct Product: Sendable, Equatable, Codable {
    public let sku: String
    public let name: String
    public let price: Decimal
    public let currency: String
    public let category: String?

    public init(sku: String, name: String, price: Decimal, currency: String, category: String?) {
        self.sku = sku
        self.name = name
        self.price = price
        self.currency = currency
        self.category = category
    }
}

/// A stock level for a SKU.
public struct StockLevel: Sendable, Equatable, Codable {
    public let sku: String
    public let quantity: Int

    public init(sku: String, quantity: Int) {
        self.sku = sku
        self.quantity = quantity
    }
}

/// A recorded sale.
public struct Sale: Sendable, Equatable, Codable {
    public let saleId: String
    public let sku: String
    public let quantity: Int
    public let unitPrice: Decimal
    public let atUtc: Date

    public init(saleId: String, sku: String, quantity: Int, unitPrice: Decimal, atUtc: Date) {
        self.saleId = saleId
        self.sku = sku
        self.quantity = quantity
        self.unitPrice = unitPrice
        self.atUtc = atUtc
    }
}

/// A top-selling SKU with its units sold. Value type for the C# tuple
/// `(string Sku, int Sold)`.
public struct TopSeller: Sendable, Equatable, Codable {
    public let sku: String
    public let sold: Int

    public init(sku: String, sold: Int) {
        self.sku = sku
        self.sold = sold
    }
}

// MARK: - Errors

public enum RetailError: Error, Equatable, CustomStringConvertible {
    case unknownSku(String)
    case topKOutOfRange

    public var description: String {
        switch self {
        case .unknownSku(let sku): return "Unknown SKU \(sku)"
        case .topKOutOfRange: return "topK out of range"
        }
    }
}

// MARK: - Contract

/// Products, stock, sales, revenue, and top-seller analytics for the retail
/// vertical.
public protocol IRetailBoard: AnyObject, Sendable {
    func addProduct(_ p: Product)
    func getProduct(_ sku: String) -> Product?
    func setStock(_ l: StockLevel)
    func stock(_ sku: String) -> Int
    func recordSale(_ s: Sale) throws
    func revenueToday(_ now: Date) -> Decimal
    func topSellersSince(_ since: Date, topK: Int) throws -> [TopSeller]
}

public extension IRetailBoard {
    /// Overload matching the C# default `topK = 5`.
    func topSellersSince(_ since: Date) throws -> [TopSeller] {
        try topSellersSince(since, topK: 5)
    }
}

// MARK: - InMemoryRetailBoard

/// Deterministic in-memory `IRetailBoard`. All state guarded by a single
/// `NSLock`.
public final class InMemoryRetailBoard: IRetailBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var products: [String: Product] = [:]
    private var stockMap: [String: Int] = [:]
    private var sales: [Sale] = []

    private static let utcCalendar: Calendar = {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "UTC")!
        return c
    }()

    public init() {}

    public func addProduct(_ p: Product) {
        lock.lock(); defer { lock.unlock() }
        products[p.sku] = p
    }

    public func getProduct(_ sku: String) -> Product? {
        lock.lock(); defer { lock.unlock() }
        return products[sku]
    }

    public func setStock(_ l: StockLevel) {
        lock.lock(); defer { lock.unlock() }
        stockMap[l.sku] = l.quantity
    }

    public func stock(_ sku: String) -> Int {
        lock.lock(); defer { lock.unlock() }
        return stockMap[sku] ?? 0
    }

    public func recordSale(_ s: Sale) throws {
        lock.lock(); defer { lock.unlock() }
        guard products[s.sku] != nil else { throw RetailError.unknownSku(s.sku) }
        sales.append(s)
        stockMap[s.sku] = (stockMap[s.sku] ?? 0) - s.quantity
    }

    public func revenueToday(_ now: Date) -> Decimal {
        lock.lock(); defer { lock.unlock() }
        let day = Self.utcCalendar.startOfDay(for: now)
        return sales
            .filter { Self.utcCalendar.startOfDay(for: $0.atUtc) == day }
            .reduce(Decimal.zero) { $0 + $1.unitPrice * Decimal($1.quantity) }
    }

    public func topSellersSince(_ since: Date, topK: Int) throws -> [TopSeller] {
        if topK <= 0 { throw RetailError.topKOutOfRange }
        lock.lock(); defer { lock.unlock() }
        var totals: [String: Int] = [:]
        for s in sales where s.atUtc >= since {
            totals[s.sku, default: 0] += s.quantity
        }
        let ordered = totals
            .map { TopSeller(sku: $0.key, sold: $0.value) }
            .sorted { $0.sold > $1.sold }
        return Array(ordered.prefix(topK))
    }
}

// MARK: - RetailDomainContext

/// Static domain-context constants for the retail vertical.
public enum RetailDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Retail] Expert retail operations assistant. Help with stock replenishment, planogram optimisation, shrinkage reduction, seasonal promotions, customer loyalty, and sales floor management. Ground advice in margin and sell-through rates. Compliance: Consumer Protection Act, POPIA."
    public static let complianceFlags: [String] = ["Consumer_Protection_Act", "POPIA", "Labour_Relations_Act"]
    public static let suggestedTools: [String] = ["pos_system", "inventory", "analytics", "promotions_engine"]
}
