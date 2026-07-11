// Hospitality.swift
//
// Port of the Hospitality vertical from
// src/CircleAI.Hospitality/HospitalityPrimitives.cs and the static
// domain-context constants from HospitalityDomainContext.cs:
//   • HotelRoom, GuestReservation, FrontDeskNote — domain records
//   • IHospitalityBoard                          — rooms, reservations, notes
//   • InMemoryHospitalityBoard                   — deterministic in-memory impl
//   • HospitalityDomainContext                   — system-prompt snippet + flags
//
// The Companion-facing wrapper (HospitalityCompanionAdapter) is an
// ICompanionSession decorator that prefixes the hospitality domain prompt.
//
// Porting notes:
//   • `decimal NightlyRate` → `Decimal`; `DateTime`/`DateTimeOffset` → `Date`.
//   • `AvailableOn(date)` excludes rooms booked over that date (CheckIn <= date
//     && CheckOut > date) and keeps only clean rooms.
//   • `CheckOut(res, roomNeedsCleaning)` on an unknown reservation throws
//     `.unknownReservation`; when cleaning is needed the room is flagged unclean.
//   • `NotesFor` orders descending by AtUtc. All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// A hotel room.
public struct HotelRoom: Sendable, Equatable, Codable {
    public let roomId: String
    public let type: String
    public let nightlyRate: Decimal
    public let currency: String
    public let isClean: Bool

    public init(roomId: String, type: String, nightlyRate: Decimal, currency: String, isClean: Bool) {
        self.roomId = roomId
        self.type = type
        self.nightlyRate = nightlyRate
        self.currency = currency
        self.isClean = isClean
    }
}

/// A guest reservation.
public struct GuestReservation: Sendable, Equatable, Codable {
    public let reservationId: String
    public let guestName: String
    public let roomId: String
    public let checkIn: Date
    public let checkOut: Date

    public init(reservationId: String, guestName: String, roomId: String, checkIn: Date, checkOut: Date) {
        self.reservationId = reservationId
        self.guestName = guestName
        self.roomId = roomId
        self.checkIn = checkIn
        self.checkOut = checkOut
    }
}

/// A front-desk note against a reservation.
public struct FrontDeskNote: Sendable, Equatable, Codable {
    public let noteId: String
    public let reservationId: String
    public let body: String
    public let atUtc: Date

    public init(noteId: String, reservationId: String, body: String, atUtc: Date) {
        self.noteId = noteId
        self.reservationId = reservationId
        self.body = body
        self.atUtc = atUtc
    }
}

// MARK: - Errors

public enum HospitalityError: Error, Equatable, CustomStringConvertible {
    case unknownReservation(String)

    public var description: String {
        switch self {
        case .unknownReservation(let id): return "Unknown reservation \(id)"
        }
    }
}

// MARK: - Contract

/// Rooms, reservations, and front-desk notes for the hospitality vertical.
public protocol IHospitalityBoard: AnyObject, Sendable {
    func addRoom(_ r: HotelRoom)
    func getRoom(_ id: String) -> HotelRoom?
    func availableOn(date: Date) -> [HotelRoom]
    func reserve(_ r: GuestReservation)
    func checkOut(reservationId: String, roomNeedsCleaning: Bool) throws
    func getReservation(_ id: String) -> GuestReservation?
    func addNote(_ n: FrontDeskNote)
    func notesFor(reservationId: String) -> [FrontDeskNote]
}

// MARK: - InMemoryHospitalityBoard

/// Deterministic in-memory `IHospitalityBoard`. All state guarded by a single `NSLock`.
public final class InMemoryHospitalityBoard: IHospitalityBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var rooms: [String: HotelRoom] = [:]
    private var res: [String: GuestReservation] = [:]
    private var notes: [FrontDeskNote] = []

    public init() {}

    public func addRoom(_ r: HotelRoom) {
        lock.lock(); defer { lock.unlock() }
        rooms[r.roomId] = r
    }

    public func getRoom(_ id: String) -> HotelRoom? {
        lock.lock(); defer { lock.unlock() }
        return rooms[id]
    }

    public func availableOn(date: Date) -> [HotelRoom] {
        lock.lock(); defer { lock.unlock() }
        let booked = Set(res.values.filter { $0.checkIn <= date && $0.checkOut > date }.map { $0.roomId })
        return rooms.values.filter { !booked.contains($0.roomId) && $0.isClean }
    }

    public func reserve(_ r: GuestReservation) {
        lock.lock(); defer { lock.unlock() }
        res[r.reservationId] = r
    }

    public func checkOut(reservationId: String, roomNeedsCleaning: Bool) throws {
        lock.lock(); defer { lock.unlock() }
        guard let r = res[reservationId] else { throw HospitalityError.unknownReservation(reservationId) }
        if roomNeedsCleaning, let room = rooms[r.roomId] {
            rooms[r.roomId] = HotelRoom(roomId: room.roomId, type: room.type, nightlyRate: room.nightlyRate, currency: room.currency, isClean: false)
        }
    }

    public func getReservation(_ id: String) -> GuestReservation? {
        lock.lock(); defer { lock.unlock() }
        return res[id]
    }

    public func addNote(_ n: FrontDeskNote) {
        lock.lock(); defer { lock.unlock() }
        notes.append(n)
    }

    public func notesFor(reservationId: String) -> [FrontDeskNote] {
        lock.lock(); defer { lock.unlock() }
        return notes.filter { $0.reservationId == reservationId }.sorted { $0.atUtc > $1.atUtc }
    }
}

// MARK: - HospitalityDomainContext

/// Static domain-context constants for the hospitality vertical.
public enum HospitalityDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Hospitality] Expert hospitality operations assistant. Help with PMS integration, RevPAR optimisation, F&B menu costing, housekeeping scheduling, guest satisfaction recovery, and MICE event coordination. Apply yield management principles. Compliance: Tourism Act, CATHSSETA, Liquor Act, Health regulations, POPIA."
    public static let complianceFlags: [String] = ["Tourism_Act", "CATHSSETA", "Liquor_Act", "Health_Regs", "POPIA"]
    public static let suggestedTools: [String] = ["pms_system", "analytics", "document_editor", "reservation_engine"]
}

// MARK: - HospitalityCompanionAdapter

/// An `ICompanionSession` decorator that prepends the hospitality domain system
/// prompt to every conversational call and adds hospitality helper methods.
/// Port of `CircleAI.Hospitality.HospitalityCompanionAdapter`. Identity/context/
/// feedback are forwarded to the inner session; proactive events forward through
/// the inner session's `proactiveEvents` stream (the Swift protocol has no disposal).
public final class HospitalityCompanionAdapter: ICompanionSession, @unchecked Sendable {
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

    private func enrich(_ m: String) -> String { "\(HospitalityDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Hospitality helpers ───────────────────────────────────────────────────

    /// Optimise RevPAR (C# `OptimiseRevParAsync`).
    public func optimiseRevPar(occupancyData: String, rateData: String) async throws -> String {
        try await inner.agent(
            "Analyse RevPAR performance and recommend rate and distribution strategies:\nOccupancy: \(occupancyData)\nRates: \(rateData)")
    }

    /// Handle a guest complaint via LAST (C# `HandleGuestComplaintAsync`).
    public func handleGuestComplaint(complaint: String, context: String) async throws -> String {
        try await inner.agent(
            "Draft a service recovery response for this guest complaint. Complaint: \(complaint). Context: \(context). Apply LAST (Listen, Apologise, Solve, Thank) framework.")
    }

    /// Draft a guest welcome (C# `DraftGuestWelcomeAsync`).
    public func draftGuestWelcome(guestName: String, roomType: String, lengthOfStay: String) async throws -> String {
        try await inner.agent(
            "Draft a warm welcome message for \(guestName) in \(roomType), staying \(lengthOfStay). Include wifi, breakfast, local pick.")
    }

    /// Handle a complaint by sentiment (C# `HandleComplaintAsync`).
    public func handleComplaint(complaint: String, sentiment: String) async throws -> String {
        try await inner.agent(
            "Handle this guest complaint (\(sentiment)): \(complaint). Apologise, recover, prevent — concrete next step in each.")
    }

    /// Suggest a guest experience (C# `SuggestExperienceAsync`).
    public func suggestExperience(guestProfile: String, lengthOfStay: String, budget: Decimal) async throws -> String {
        try await inner.agent(
            "Suggest a \(lengthOfStay) experience for guest: \(guestProfile) on \(budget) budget. Mix dining, activity, downtime.")
    }

    /// Optimise a housekeeping route (C# `OptimiseHousekeepingRouteAsync`).
    public func optimiseHousekeepingRoute(roomList: String, staffCount: Int) async throws -> String {
        try await inner.agent(
            "Optimise housekeeping route for rooms \(roomList) with \(staffCount) staff. Sequence for minimum dead-walk + checkout-priority first.")
    }
}
