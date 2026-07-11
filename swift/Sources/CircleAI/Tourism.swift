// Tourism.swift
//
// Port of the Tourism vertical from src/CircleAI.Tourism/TourismPrimitives.cs
// and the static domain-context constants from TourismDomainContext.cs:
//   • Attraction, ItineraryItem, Itinerary, TourismBooking — domain records
//   • ITourismBoard                                        — attractions, itineraries, bookings
//   • InMemoryTourismBoard                                 — deterministic in-memory impl
//   • TourismDomainContext                                 — system-prompt snippet + flags
//
// The Companion-facing wrapper (TourismCompanionAdapter) is an ICompanionSession
// decorator that prefixes the tourism domain prompt.
//
// Porting notes:
//   • `TimeSpan` → `TimeInterval` (seconds); `decimal` → `Decimal`; `DateTime` → `Date`.
//   • `AttractionsInCity`/`ByTag` require a non-blank argument (else
//     `.cityRequired`/`.tagRequired`), match case-insensitively, and order
//     ascending by Name.
//   • `Bookings` returns a snapshot in insertion order. All state guarded by a
//     single `NSLock`.

import Foundation

// MARK: - Records

/// A tourist attraction.
public struct Attraction: Sendable, Equatable, Codable {
    public let attractionId: String
    public let name: String
    public let city: String
    public let country: String
    public let lat: Double
    public let lon: Double
    public let tags: [String]

    public init(attractionId: String, name: String, city: String, country: String, lat: Double, lon: Double, tags: [String]) {
        self.attractionId = attractionId
        self.name = name
        self.city = city
        self.country = country
        self.lat = lat
        self.lon = lon
        self.tags = tags
    }
}

/// A single slot in an itinerary.
public struct ItineraryItem: Sendable, Equatable, Codable {
    public let dayIndex: Int
    public let startLocal: TimeInterval
    public let endLocal: TimeInterval
    public let attractionId: String
    public let note: String?

    public init(dayIndex: Int, startLocal: TimeInterval, endLocal: TimeInterval, attractionId: String, note: String?) {
        self.dayIndex = dayIndex
        self.startLocal = startLocal
        self.endLocal = endLocal
        self.attractionId = attractionId
        self.note = note
    }
}

/// A planned itinerary.
public struct Itinerary: Sendable, Equatable, Codable {
    public let itineraryId: String
    public let title: String
    public let items: [ItineraryItem]

    public init(itineraryId: String, title: String, items: [ItineraryItem]) {
        self.itineraryId = itineraryId
        self.title = title
        self.items = items
    }
}

/// A tourism booking against an itinerary.
public struct TourismBooking: Sendable, Equatable, Codable {
    public let bookingId: String
    public let itineraryId: String
    public let startDate: Date
    public let travelers: Int
    public let totalPrice: Decimal
    public let currency: String

    public init(bookingId: String, itineraryId: String, startDate: Date, travelers: Int, totalPrice: Decimal, currency: String) {
        self.bookingId = bookingId
        self.itineraryId = itineraryId
        self.startDate = startDate
        self.travelers = travelers
        self.totalPrice = totalPrice
        self.currency = currency
    }
}

// MARK: - Errors

public enum TourismError: Error, Equatable, CustomStringConvertible {
    case cityRequired
    case tagRequired

    public var description: String {
        switch self {
        case .cityRequired: return "city required"
        case .tagRequired: return "tag required"
        }
    }
}

// MARK: - Contract

/// Attractions, itineraries, and bookings for the tourism vertical.
public protocol ITourismBoard: AnyObject, Sendable {
    func add(_ a: Attraction)
    func attractionsInCity(_ city: String) throws -> [Attraction]
    func byTag(_ tag: String) throws -> [Attraction]
    func plan(_ i: Itinerary)
    func getItinerary(_ id: String) -> Itinerary?
    func book(_ b: TourismBooking)
    var bookings: [TourismBooking] { get }
}

// MARK: - InMemoryTourismBoard

/// Deterministic in-memory `ITourismBoard`. All state guarded by a single `NSLock`.
public final class InMemoryTourismBoard: ITourismBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var attractions: [String: Attraction] = [:]
    private var itineraries: [String: Itinerary] = [:]
    private var bookingsList: [TourismBooking] = []

    public init() {}

    public func add(_ a: Attraction) {
        lock.lock(); defer { lock.unlock() }
        attractions[a.attractionId] = a
    }

    public func attractionsInCity(_ city: String) throws -> [Attraction] {
        if city.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw TourismError.cityRequired }
        lock.lock(); defer { lock.unlock() }
        return attractions.values.filter { $0.city.caseInsensitiveCompare(city) == .orderedSame }.sorted { $0.name < $1.name }
    }

    public func byTag(_ tag: String) throws -> [Attraction] {
        if tag.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw TourismError.tagRequired }
        lock.lock(); defer { lock.unlock() }
        return attractions.values
            .filter { a in a.tags.contains { $0.caseInsensitiveCompare(tag) == .orderedSame } }
            .sorted { $0.name < $1.name }
    }

    public func plan(_ i: Itinerary) {
        lock.lock(); defer { lock.unlock() }
        itineraries[i.itineraryId] = i
    }

    public func getItinerary(_ id: String) -> Itinerary? {
        lock.lock(); defer { lock.unlock() }
        return itineraries[id]
    }

    public func book(_ b: TourismBooking) {
        lock.lock(); defer { lock.unlock() }
        bookingsList.append(b)
    }

    public var bookings: [TourismBooking] {
        lock.lock(); defer { lock.unlock() }
        return bookingsList
    }
}

// MARK: - TourismDomainContext

/// Static domain-context constants for the tourism vertical.
public enum TourismDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Tourism] Expert tourism and travel operations assistant. Help with itinerary design, tour package costing, guide briefing notes, destination marketing, and safety management plans. Apply experiential travel principles. Compliance: Tourism Act 3/2014, SABS tour operator standards, SATSA, POPIA."
    public static let complianceFlags: [String] = ["Tourism_Act_3_2014", "SABS_Tour_Ops", "SATSA", "POPIA"]
    public static let suggestedTools: [String] = ["mapping", "booking_system", "document_editor", "weather_api"]
}

// MARK: - TourismCompanionAdapter

/// An `ICompanionSession` decorator that prepends the tourism domain system
/// prompt to every conversational call and adds tourism helper methods.
/// Port of `CircleAI.Tourism.TourismCompanionAdapter`. Identity/context/feedback
/// are forwarded to the inner session; proactive events forward through the
/// inner session's `proactiveEvents` stream (the Swift protocol has no disposal).
public final class TourismCompanionAdapter: ICompanionSession, @unchecked Sendable {
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

    private func enrich(_ m: String) -> String { "\(TourismDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Tourism helpers ───────────────────────────────────────────────────────

    /// Design a tailored itinerary (C# `DesignItineraryAsync`).
    public func designItinerary(destination: String, nights: Int, guestProfile: String) async throws -> String {
        try await inner.agent(
            "Design a \(nights)-night itinerary for \(destination) tailored to: \(guestProfile). Include daily schedule, accommodation category, transport, meals, and activities with timing.")
    }

    /// Cost a tour package (C# `CostPackageAsync`).
    public func costPackage(itinerary: String, pax: Int) async throws -> String {
        try await inner.agent(
            "Cost this tour package for \(pax) passengers:\n\(itinerary)\nProvide cost per person, breakeven point, and suggested selling price at 25% margin.")
    }

    /// Build a day-by-day itinerary (C# `BuildItineraryAsync`).
    public func buildItinerary(destination: String, days: Int, travelerProfile: String) async throws -> String {
        try await inner.agent(
            "Build a \(days)-day \(destination) itinerary for \(travelerProfile). Day-by-day rhythm, must-sees, hidden gems, food.")
    }

    /// Estimate a trip budget (C# `EstimateBudgetAsync`).
    public func estimateBudget(destination: String, travellers: Int, days: Int, standard: String) async throws -> String {
        try await inner.agent(
            "Estimate budget for \(travellers) pax, \(days) days in \(destination), \(standard) standard. Categories + total range.")
    }

    /// Handle a travel disruption (C# `HandleTravelDisruptionAsync`).
    public func handleTravelDisruption(disruption: String, itineraryContext: String) async throws -> String {
        try await inner.agent(
            "Handle travel disruption: \(disruption). Itinerary context: \(itineraryContext). Recovery options, comms templates, rebook checklist.")
    }

    /// Recommend an experience (C# `RecommendExperienceAsync`).
    public func recommendExperience(interests: String, timeOfDay: String, location: String) async throws -> String {
        try await inner.agent(
            "Recommend an experience for \(interests) at \(timeOfDay) in \(location). Why-it-fits + booking practicalities.")
    }
}
