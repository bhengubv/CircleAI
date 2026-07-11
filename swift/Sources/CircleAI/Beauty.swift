// Beauty.swift
//
// Port of the Beauty vertical from src/CircleAI.Beauty/BeautyPrimitives.cs and
// the static domain-context constants from BeautyDomainContext.cs:
//   • Treatment, Appointment, SkinProfile — domain records
//   • IBeautyBoard                        — treatments, bookings, profiles
//   • InMemoryBeautyBoard                 — deterministic in-memory impl
//   • BeautyDomainContext                 — system-prompt snippet + flags
//
// The Companion-facing wrapper (BeautyCompanionAdapter) is an ICompanionSession
// decorator that prefixes the beauty domain prompt.
//
// Porting notes:
//   • `decimal Price` → `Decimal`; `DateTimeOffset` → `Date`; `string? Notes` → `String?`.
//   • `AppointmentsBetween` filters AtUtc in [start, end], ordered ascending.
//   • `RecommendFor` returns [] when no profile; else treatments whose Name
//     contains any of the profile's Concerns (case-insensitive substring).
//   • All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// A bookable treatment.
public struct Treatment: Sendable, Equatable, Codable {
    public let treatmentId: String
    public let name: String
    public let durationMinutes: Int
    public let price: Decimal
    public let currency: String

    public init(treatmentId: String, name: String, durationMinutes: Int, price: Decimal, currency: String) {
        self.treatmentId = treatmentId
        self.name = name
        self.durationMinutes = durationMinutes
        self.price = price
        self.currency = currency
    }
}

/// A client appointment.
public struct Appointment: Sendable, Equatable, Codable {
    public let apptId: String
    public let clientName: String
    public let treatmentId: String
    public let atUtc: Date
    public let notes: String?

    public init(apptId: String, clientName: String, treatmentId: String, atUtc: Date, notes: String?) {
        self.apptId = apptId
        self.clientName = clientName
        self.treatmentId = treatmentId
        self.atUtc = atUtc
        self.notes = notes
    }
}

/// A client skin profile.
public struct SkinProfile: Sendable, Equatable, Codable {
    public let clientName: String
    public let skinType: String
    public let concerns: [String]

    public init(clientName: String, skinType: String, concerns: [String]) {
        self.clientName = clientName
        self.skinType = skinType
        self.concerns = concerns
    }
}

// MARK: - Contract

/// Treatments, appointments, and skin profiles for the beauty vertical.
public protocol IBeautyBoard: AnyObject, Sendable {
    func addTreatment(_ t: Treatment)
    func getTreatment(_ id: String) -> Treatment?
    func book(_ a: Appointment)
    func appointmentsBetween(start: Date, end: Date) -> [Appointment]
    func saveProfile(_ p: SkinProfile)
    func getProfile(clientName: String) -> SkinProfile?
    func recommendFor(clientName: String) -> [Treatment]
}

// MARK: - InMemoryBeautyBoard

/// Deterministic in-memory `IBeautyBoard`. All state guarded by a single `NSLock`.
public final class InMemoryBeautyBoard: IBeautyBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var treatments: [String: Treatment] = [:]
    private var appts: [Appointment] = []
    private var profiles: [String: SkinProfile] = [:]

    public init() {}

    public func addTreatment(_ t: Treatment) {
        lock.lock(); defer { lock.unlock() }
        treatments[t.treatmentId] = t
    }

    public func getTreatment(_ id: String) -> Treatment? {
        lock.lock(); defer { lock.unlock() }
        return treatments[id]
    }

    public func book(_ a: Appointment) {
        lock.lock(); defer { lock.unlock() }
        appts.append(a)
    }

    public func appointmentsBetween(start: Date, end: Date) -> [Appointment] {
        lock.lock(); defer { lock.unlock() }
        return appts.filter { $0.atUtc >= start && $0.atUtc <= end }.sorted { $0.atUtc < $1.atUtc }
    }

    public func saveProfile(_ p: SkinProfile) {
        lock.lock(); defer { lock.unlock() }
        profiles[p.clientName] = p
    }

    public func getProfile(clientName: String) -> SkinProfile? {
        lock.lock(); defer { lock.unlock() }
        return profiles[clientName]
    }

    public func recommendFor(clientName: String) -> [Treatment] {
        lock.lock(); defer { lock.unlock() }
        guard let p = profiles[clientName] else { return [] }
        return treatments.values.filter { t in
            p.concerns.contains { t.name.range(of: $0, options: .caseInsensitive) != nil }
        }
    }
}

// MARK: - BeautyDomainContext

/// Static domain-context constants for the beauty vertical.
public enum BeautyDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Beauty] Expert beauty and personal care companion. Help with skincare routine building, ingredient education, product recommendations (without brand bias), hair care, makeup guidance, and wellness rituals. Celebrate all skin tones, types, and expressions. Compliance: POPIA, Medicines and Related Substances Act (cosmetic claims)."
    public static let complianceFlags: [String] = ["POPIA", "Medicines_Act_cosmetic_claims"]
    public static let suggestedTools: [String] = ["product_db", "ingredient_checker", "web_search"]
}

// MARK: - BeautyCompanionAdapter

/// An `ICompanionSession` decorator that prepends the beauty domain system
/// prompt to every conversational call and adds beauty helper methods.
/// Port of `CircleAI.Beauty.BeautyCompanionAdapter`. Identity/context/feedback
/// are forwarded to the inner session; proactive events forward through the
/// inner session's `proactiveEvents` stream (the Swift protocol has no disposal).
public final class BeautyCompanionAdapter: ICompanionSession, @unchecked Sendable {
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

    private func enrich(_ m: String) -> String { "\(BeautyDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Beauty helpers ────────────────────────────────────────────────────────

    /// Build a skincare routine (C# `BuildSkincareRoutineAsync`).
    public func buildSkincareRoutine(skinType: String, concerns: String) async throws -> String {
        try await inner.agent(
            "Build a skincare routine for \(skinType) skin. Concerns: \(concerns). Include morning and evening steps, key ingredients, and ingredients to avoid.")
    }

    /// Analyse a skincare ingredient (C# `AnalyseIngredientAsync`).
    public func analyseIngredient(ingredient: String) async throws -> String {
        try await inner.agent(
            "Analyse the skincare ingredient: \(ingredient). Explain function, benefits, potential irritants, and who it suits best.")
    }

    /// Recommend an AM/PM routine on a budget (C# `RecommendRoutineAsync`).
    public func recommendRoutine(skinType: String, concerns: String, budget: String) async throws -> String {
        try await inner.agent(
            "Recommend an AM/PM skincare routine for \(skinType) skin with \(concerns), budget \(budget). Include ingredient targets and product categories (not brands).")
    }

    /// Assess ingredient layering compatibility (C# `AssessIngredientCompatibilityAsync`).
    public func assessIngredientCompatibility(ingredientList: String) async throws -> String {
        try await inner.agent(
            "Assess this ingredient list for layering safety + irritation risk: \(ingredientList). Flag known clashes (retinol+AHA, vit C+niacinamide, etc.).")
    }

    /// Design a multi-session treatment plan (C# `DesignTreatmentPlanAsync`).
    public func designTreatmentPlan(clientGoals: String, sessionCount: Int) async throws -> String {
        try await inner.agent(
            "Design a \(sessionCount)-session treatment plan to achieve: \(clientGoals). Specify modality, interval, expected progress, and at-home care.")
    }

    /// Draft a booking confirmation (C# `DraftBookingConfirmationAsync`).
    public func draftBookingConfirmation(clientName: String, treatment: String, dateTime: String) async throws -> String {
        try await inner.agent(
            "Draft a warm booking confirmation message: \(clientName), \(treatment), \(dateTime). Include prep instructions, cancellation policy, location.")
    }
}
