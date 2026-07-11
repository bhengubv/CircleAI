// RealEstate.swift
//
// Port of the RealEstate vertical from src/CircleAI.RealEstate/RealEstatePrimitives.cs
// and the static domain-context constants from RealEstateDomainContext.cs:
//   • PropertyKind (enum)                       — apartment / house / …
//   • Property, Listing, Valuation, Viewing     — domain records
//   • IRealEstateBoard                          — listings, valuations, viewings
//   • InMemoryRealEstateBoard                   — deterministic in-memory impl
//   • RealEstateDomainContext                   — system-prompt snippet + flags
//
// The Companion-facing wrapper (RealEstateCompanionAdapter) is intentionally NOT
// ported.
//
// Porting notes:
//   • `decimal` → `Decimal`; `DateTimeOffset` → `Date`.
//   • `PropertyKind` is a C# `int`-backed enum; ported as a `String`-backed
//     Swift enum for clean Codable round-tripping.
//   • `Close` on an unknown listing throws → `RealEstateError.unknownListing`.
//   • Blank suburb throws `.suburbRequired`.
//   • `ActiveInSuburb` returns active listings whose property is in `suburb`
//     (case-insensitive), ordered descending by ListedUtc.
//   • `SuburbAverage` returns `nil` for an empty suburb, otherwise the mean
//     AskingPrice over the active listings.
//   • All state guarded by a single `NSLock`.

import Foundation

// MARK: - Enum

/// Kind of property.
public enum PropertyKind: String, Sendable, Equatable, Codable, CaseIterable {
    case apartment = "Apartment"
    case house = "House"
    case townhouse = "Townhouse"
    case commercial = "Commercial"
    case land = "Land"
}

// MARK: - Records

/// A property.
public struct Property: Sendable, Equatable, Codable {
    public let propertyId: String
    public let suburb: String
    public let kind: PropertyKind
    public let beds: Int
    public let baths: Int
    public let floorAreaM2: Double

    public init(propertyId: String, suburb: String, kind: PropertyKind, beds: Int, baths: Int, floorAreaM2: Double) {
        self.propertyId = propertyId
        self.suburb = suburb
        self.kind = kind
        self.beds = beds
        self.baths = baths
        self.floorAreaM2 = floorAreaM2
    }
}

/// A listing of a property for sale.
public struct Listing: Sendable, Equatable, Codable {
    public let listingId: String
    public let propertyId: String
    public let askingPrice: Decimal
    public let currency: String
    public let listedUtc: Date
    public let isActive: Bool

    public init(listingId: String, propertyId: String, askingPrice: Decimal, currency: String, listedUtc: Date, isActive: Bool) {
        self.listingId = listingId
        self.propertyId = propertyId
        self.askingPrice = askingPrice
        self.currency = currency
        self.listedUtc = listedUtc
        self.isActive = isActive
    }
}

/// A property valuation.
public struct Valuation: Sendable, Equatable, Codable {
    public let propertyId: String
    public let estimatedValue: Decimal
    public let source: String
    public let atUtc: Date

    public init(propertyId: String, estimatedValue: Decimal, source: String, atUtc: Date) {
        self.propertyId = propertyId
        self.estimatedValue = estimatedValue
        self.source = source
        self.atUtc = atUtc
    }
}

/// A scheduled viewing of a listing.
public struct Viewing: Sendable, Equatable, Codable {
    public let viewingId: String
    public let listingId: String
    public let attendeeName: String
    public let atUtc: Date

    public init(viewingId: String, listingId: String, attendeeName: String, atUtc: Date) {
        self.viewingId = viewingId
        self.listingId = listingId
        self.attendeeName = attendeeName
        self.atUtc = atUtc
    }
}

// MARK: - Errors

public enum RealEstateError: Error, Equatable, CustomStringConvertible {
    case unknownListing(String)
    case suburbRequired

    public var description: String {
        switch self {
        case .unknownListing(let id): return "Unknown listing \(id)"
        case .suburbRequired: return "suburb required"
        }
    }
}

// MARK: - Contract

/// Listings, valuations, viewings, and suburb comparables for the real-estate
/// vertical.
public protocol IRealEstateBoard: AnyObject, Sendable {
    func registerProperty(_ p: Property)
    func list(_ l: Listing)
    func close(listingId: String) throws
    func value(_ v: Valuation)
    func scheduleViewing(_ v: Viewing)
    func activeInSuburb(_ suburb: String) throws -> [Listing]
    func suburbAverage(_ suburb: String) throws -> Decimal?
}

// MARK: - InMemoryRealEstateBoard

/// Deterministic in-memory `IRealEstateBoard`. All state guarded by a single
/// `NSLock`.
public final class InMemoryRealEstateBoard: IRealEstateBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var props: [String: Property] = [:]
    private var listings: [String: Listing] = [:]
    private var vals: [Valuation] = []
    private var viewings: [Viewing] = []

    public init() {}

    public func registerProperty(_ p: Property) {
        lock.lock(); defer { lock.unlock() }
        props[p.propertyId] = p
    }

    public func list(_ l: Listing) {
        lock.lock(); defer { lock.unlock() }
        listings[l.listingId] = l
    }

    public func close(listingId: String) throws {
        lock.lock(); defer { lock.unlock() }
        guard let l = listings[listingId] else { throw RealEstateError.unknownListing(listingId) }
        listings[listingId] = Listing(listingId: l.listingId, propertyId: l.propertyId, askingPrice: l.askingPrice,
                                      currency: l.currency, listedUtc: l.listedUtc, isActive: false)
    }

    public func value(_ v: Valuation) {
        lock.lock(); defer { lock.unlock() }
        vals.append(v)
    }

    public func scheduleViewing(_ v: Viewing) {
        lock.lock(); defer { lock.unlock() }
        viewings.append(v)
    }

    public func activeInSuburb(_ suburb: String) throws -> [Listing] {
        if suburb.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw RealEstateError.suburbRequired }
        lock.lock(); defer { lock.unlock() }
        return activeInSuburbLocked(suburb)
    }

    /// Non-reentrant suburb query; caller must already hold `lock`.
    private func activeInSuburbLocked(_ suburb: String) -> [Listing] {
        return listings.values.filter { l in
            guard l.isActive, let p = props[l.propertyId] else { return false }
            return p.suburb.caseInsensitiveCompare(suburb) == .orderedSame
        }
        .sorted { $0.listedUtc > $1.listedUtc }
    }

    public func suburbAverage(_ suburb: String) throws -> Decimal? {
        if suburb.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw RealEstateError.suburbRequired }
        lock.lock(); defer { lock.unlock() }
        let rows = activeInSuburbLocked(suburb)
        if rows.isEmpty { return nil }
        let sum = rows.reduce(Decimal.zero) { $0 + $1.askingPrice }
        return sum / Decimal(rows.count)
    }
}

// MARK: - RealEstateDomainContext

/// Static domain-context constants for the real-estate vertical.
public enum RealEstateDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: RealEstate] Expert real estate assistant. Help with property market analysis, valuation frameworks, lease and sale agreement review, conveyancing timelines, sectional title rules, and rental management. Ground advice in current market data. Compliance: Alienation of Land Act, Rental Housing Act, PPRA, FICA, POPIA."
    public static let complianceFlags: [String] = ["Alienation_of_Land_Act", "Rental_Housing_Act", "PPRA", "FICA", "POPIA"]
    public static let suggestedTools: [String] = ["property_listings", "document_editor", "map", "analytics"]
}
