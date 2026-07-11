// Travel.swift
//
// Port of the Travel vertical from src/CircleAI.Travel/TravelPrimitives.cs and
// the static domain-context constants from TravelDomainContext.cs:
//   • Flight, HotelStay, TravelTrip — domain records
//   • ITravelBoard                  — flights, stays, trips, trip cost
//   • InMemoryTravelBoard           — deterministic in-memory impl
//   • TravelDomainContext           — system-prompt snippet + flags
//
// The Companion-facing wrapper (TravelCompanionAdapter) is an ICompanionSession
// decorator that prefixes the travel domain prompt.
//
// Porting notes:
//   • `decimal Price/NightlyRate` → `Decimal`; `DateTimeOffset`/`DateTime` → `Date`.
//   • Two `Add` overloads → `add(_:Flight)` / `add(_:HotelStay)`.
//   • `TripCost` on an unknown trip throws `.unknownTrip`; it sums flight prices
//     plus each stay's NightlyRate × max(1, whole nights between CheckIn/CheckOut).
//   • `UpcomingTrips(now)` returns trips with StartDate >= now, ordered ascending.
//   • All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// A flight leg.
public struct Flight: Sendable, Equatable, Codable {
    public let flightId: String
    public let from: String
    public let to: String
    public let departUtc: Date
    public let arriveUtc: Date
    public let carrier: String
    public let cabin: String
    public let price: Decimal
    public let currency: String

    public init(flightId: String, from: String, to: String, departUtc: Date, arriveUtc: Date, carrier: String, cabin: String, price: Decimal, currency: String) {
        self.flightId = flightId
        self.from = from
        self.to = to
        self.departUtc = departUtc
        self.arriveUtc = arriveUtc
        self.carrier = carrier
        self.cabin = cabin
        self.price = price
        self.currency = currency
    }
}

/// A hotel stay.
public struct HotelStay: Sendable, Equatable, Codable {
    public let stayId: String
    public let hotel: String
    public let city: String
    public let checkIn: Date
    public let checkOut: Date
    public let nightlyRate: Decimal
    public let currency: String

    public init(stayId: String, hotel: String, city: String, checkIn: Date, checkOut: Date, nightlyRate: Decimal, currency: String) {
        self.stayId = stayId
        self.hotel = hotel
        self.city = city
        self.checkIn = checkIn
        self.checkOut = checkOut
        self.nightlyRate = nightlyRate
        self.currency = currency
    }
}

/// A planned trip aggregating flights and stays.
public struct TravelTrip: Sendable, Equatable, Codable {
    public let tripId: String
    public let name: String
    public let startDate: Date
    public let endDate: Date
    public let flightIds: [String]
    public let stayIds: [String]

    public init(tripId: String, name: String, startDate: Date, endDate: Date, flightIds: [String], stayIds: [String]) {
        self.tripId = tripId
        self.name = name
        self.startDate = startDate
        self.endDate = endDate
        self.flightIds = flightIds
        self.stayIds = stayIds
    }
}

// MARK: - Errors

public enum TravelError: Error, Equatable, CustomStringConvertible {
    case unknownTrip(String)

    public var description: String {
        switch self {
        case .unknownTrip(let id): return "Unknown trip \(id)"
        }
    }
}

// MARK: - Contract

/// Flights, hotel stays, trips, and trip costing for the travel vertical.
public protocol ITravelBoard: AnyObject, Sendable {
    func add(_ f: Flight)
    func add(_ s: HotelStay)
    func plan(_ t: TravelTrip)
    func getTrip(_ id: String) -> TravelTrip?
    func getFlight(_ id: String) -> Flight?
    func getStay(_ id: String) -> HotelStay?
    func tripCost(tripId: String) throws -> Decimal
    func upcomingTrips(now: Date) -> [TravelTrip]
}

// MARK: - InMemoryTravelBoard

/// Deterministic in-memory `ITravelBoard`. All state guarded by a single `NSLock`.
public final class InMemoryTravelBoard: ITravelBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var flights: [String: Flight] = [:]
    private var stays: [String: HotelStay] = [:]
    private var trips: [String: TravelTrip] = [:]

    public init() {}

    public func add(_ f: Flight) {
        lock.lock(); defer { lock.unlock() }
        flights[f.flightId] = f
    }

    public func add(_ s: HotelStay) {
        lock.lock(); defer { lock.unlock() }
        stays[s.stayId] = s
    }

    public func plan(_ t: TravelTrip) {
        lock.lock(); defer { lock.unlock() }
        trips[t.tripId] = t
    }

    public func getTrip(_ id: String) -> TravelTrip? {
        lock.lock(); defer { lock.unlock() }
        return trips[id]
    }

    public func getFlight(_ id: String) -> Flight? {
        lock.lock(); defer { lock.unlock() }
        return flights[id]
    }

    public func getStay(_ id: String) -> HotelStay? {
        lock.lock(); defer { lock.unlock() }
        return stays[id]
    }

    public func tripCost(tripId: String) throws -> Decimal {
        lock.lock(); defer { lock.unlock() }
        guard let t = trips[tripId] else { throw TravelError.unknownTrip(tripId) }
        var total = Decimal(0)
        for fid in t.flightIds {
            if let f = flights[fid] { total += f.price }
        }
        for sid in t.stayIds {
            if let s = stays[sid] {
                let nights = max(1, Self.wholeDays(from: s.checkIn, to: s.checkOut))
                total += s.nightlyRate * Decimal(nights)
            }
        }
        return total
    }

    public func upcomingTrips(now: Date) -> [TravelTrip] {
        lock.lock(); defer { lock.unlock() }
        return trips.values.filter { $0.startDate >= now }.sorted { $0.startDate < $1.startDate }
    }

    /// Whole days between two dates, matching C# `(CheckOut - CheckIn).Days`
    /// (truncated toward zero).
    private static func wholeDays(from start: Date, to end: Date) -> Int {
        let seconds = end.timeIntervalSince(start)
        return Int(seconds / 86_400.0)
    }
}

// MARK: - TravelDomainContext

/// Static domain-context constants for the travel vertical.
public enum TravelDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Travel] Expert travel planning companion. Help with trip itinerary building, visa and entry requirements, budget travel strategies, packing lists, travel insurance guidance, and safety advisories. Personalise to the traveller profile. Compliance: POPIA, Consumer Protection Act (travel packages)."
    public static let complianceFlags: [String] = ["POPIA", "Consumer_Protection_Act", "IATA_aware"]
    public static let suggestedTools: [String] = ["flight_search", "mapping", "currency_converter", "web_search"]
}

// MARK: - TravelCompanionAdapter

/// An `ICompanionSession` decorator that prepends the travel domain system
/// prompt to every conversational call and adds travel helper methods.
/// Port of `CircleAI.Travel.TravelCompanionAdapter`. Identity/context/feedback
/// are forwarded to the inner session; proactive events forward through the
/// inner session's `proactiveEvents` stream (the Swift protocol has no disposal).
public final class TravelCompanionAdapter: ICompanionSession, @unchecked Sendable {
    private let inner: ICompanionSession

    public init(_ inner: ICompanionSession) {
        self.inner = inner
    }

    public var sessionId: String { inner.sessionId }
    public var identityId: String { inner.identityId }
    public var interface: InterfaceKind { inner.interface }
    public var history: [CompanionTurn] { inner.history }

    public func getContext() -> CompanionContext { inner.getContext() }
    public func refreshContext() async throws { try await inner.refreshContext() }
    public func signalFeedback(positive: Bool, note: String?) async throws {
        try await inner.signalFeedback(positive: positive, note: note)
    }
    public var proactiveEvents: AsyncStream<CompanionProactiveEvent> { inner.proactiveEvents }

    public func send(_ message: String) async throws -> String { try await inner.send(enrich(message)) }
    public func stream(_ message: String) -> AsyncStream<String> { inner.stream(enrich(message)) }
    public func agent(_ instruction: String) async throws -> String { try await inner.agent(enrich(instruction)) }

    private func enrich(_ m: String) -> String { "\(TravelDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Travel helpers ────────────────────────────────────────────────────────

    /// Plan a trip (C# `PlanTripAsync`).
    public func planTrip(destination: String, nights: Int, travellers: String, budget: String) async throws -> String {
        try await inner.agent(
            "Plan a \(nights)-night trip to \(destination) for \(travellers). Budget: \(budget). Include flights, accommodation tiers, daily activities, transport, and estimated total cost.")
    }

    /// Create a packing list (C# `CreatePackingListAsync`).
    public func createPackingList(destination: String, duration: String, activities: String) async throws -> String {
        try await inner.agent(
            "Create a packing list for \(duration) in \(destination). Activities: \(activities). Organise by category (clothing, toiletries, documents, tech, emergency) and note carry-on vs checked restrictions.")
    }

    /// Optimise a multi-stop trip (C# `OptimiseTripAsync`).
    public func optimiseTrip(origin: String, destinations: String, constraints: String) async throws -> String {
        try await inner.agent(
            "Optimise trip from \(origin) through \(destinations). Constraints: \(constraints). Route, mode mix, lodging, pace.")
    }

    /// Draft an expense claim (C# `DraftExpenseClaimAsync`).
    public func draftExpenseClaim(tripSummary: String, expenses: String) async throws -> String {
        try await inner.agent(
            "Draft expense claim for trip: \(tripSummary). Items: \(expenses). Categorise per company policy, flag missing receipts.")
    }

    /// Generate a packing list by day count (C# `PackingListAsync`).
    public func packingList(destination: String, days: Int, activities: String) async throws -> String {
        try await inner.agent(
            "Generate packing list for \(days) days in \(destination), activities: \(activities). By category + weight optimisation.")
    }

    /// Outline visa requirements (C# `HandleVisaQueryAsync`).
    public func handleVisaQuery(fromCountry: String, toCountry: String, travelPurpose: String) async throws -> String {
        try await inner.agent(
            "Outline visa requirements: \(fromCountry) → \(toCountry) for \(travelPurpose). Process, documents, timeline, common pitfalls.")
    }
}
