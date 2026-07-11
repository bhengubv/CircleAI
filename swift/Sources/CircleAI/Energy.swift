// Energy.swift
//
// Port of the Energy vertical from src/CircleAI.Energy/EnergyPrimitives.cs and
// the static domain-context constants from EnergyDomainContext.cs:
//   • MeterReading, EnergyTariff, Outage — domain records
//   • IEnergyBoard                       — readings, tariffs, cost, outages
//   • InMemoryEnergyBoard                — deterministic in-memory impl
//   • EnergyDomainContext                — system-prompt snippet + flags
//
// The Companion-facing wrapper (EnergyCompanionAdapter) is an ICompanionSession
// decorator that prefixes the energy domain prompt.
//
// Porting notes:
//   • `decimal` → `Decimal`; `DateTimeOffset` → `Date`; `DateTimeOffset? EndUtc` → `Date?`.
//   • `ReadingsFor(meterId, since)` filters + orders ascending by AtUtc.
//   • `TotalKwhSince` = last reading kWh − first reading kWh over the window
//     (0.0 when fewer than 2 readings).
//   • `EstimateCost` on an unknown tariff throws `.unknownTariff`; it multiplies
//     total kWh by the tariff's peak rate.
//   • `ActiveOutages` returns outages with no EndUtc. All state guarded by a
//     single `NSLock` (kWh totals use non-locking private helpers).

import Foundation

// MARK: - Records

/// A meter reading.
public struct MeterReading: Sendable, Equatable, Codable {
    public let meterId: String
    public let kwh: Double
    public let atUtc: Date

    public init(meterId: String, kwh: Double, atUtc: Date) {
        self.meterId = meterId
        self.kwh = kwh
        self.atUtc = atUtc
    }
}

/// An energy tariff.
public struct EnergyTariff: Sendable, Equatable, Codable {
    public let tariffId: String
    public let name: String
    public let peakKwhRate: Double
    public let offPeakKwhRate: Double
    public let currency: String

    public init(tariffId: String, name: String, peakKwhRate: Double, offPeakKwhRate: Double, currency: String) {
        self.tariffId = tariffId
        self.name = name
        self.peakKwhRate = peakKwhRate
        self.offPeakKwhRate = offPeakKwhRate
        self.currency = currency
    }
}

/// A power outage.
public struct Outage: Sendable, Equatable, Codable {
    public let outageId: String
    public let area: String
    public let startUtc: Date
    public let endUtc: Date?
    public let reason: String?

    public init(outageId: String, area: String, startUtc: Date, endUtc: Date?, reason: String?) {
        self.outageId = outageId
        self.area = area
        self.startUtc = startUtc
        self.endUtc = endUtc
        self.reason = reason
    }
}

// MARK: - Errors

public enum EnergyError: Error, Equatable, CustomStringConvertible {
    case unknownTariff(String)

    public var description: String {
        switch self {
        case .unknownTariff(let id): return "Unknown tariff \(id)"
        }
    }
}

// MARK: - Contract

/// Readings, tariffs, cost estimation, and outages for the energy vertical.
public protocol IEnergyBoard: AnyObject, Sendable {
    func record(_ r: MeterReading)
    func readingsFor(meterId: String, since: Date) -> [MeterReading]
    func totalKwhSince(meterId: String, since: Date) -> Double
    func setTariff(_ t: EnergyTariff)
    func getTariff(_ id: String) -> EnergyTariff?
    func estimateCost(meterId: String, tariffId: String, since: Date) throws -> Decimal
    func logOutage(_ o: Outage)
    func activeOutages() -> [Outage]
}

// MARK: - InMemoryEnergyBoard

/// Deterministic in-memory `IEnergyBoard`. All state guarded by a single `NSLock`.
public final class InMemoryEnergyBoard: IEnergyBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var readings: [MeterReading] = []
    private var tariffs: [String: EnergyTariff] = [:]
    private var outages: [String: Outage] = [:]

    public init() {}

    public func record(_ r: MeterReading) {
        lock.lock(); defer { lock.unlock() }
        readings.append(r)
    }

    public func readingsFor(meterId: String, since: Date) -> [MeterReading] {
        lock.lock(); defer { lock.unlock() }
        return readingsForLocked(meterId: meterId, since: since)
    }

    public func totalKwhSince(meterId: String, since: Date) -> Double {
        lock.lock(); defer { lock.unlock() }
        return totalKwhSinceLocked(meterId: meterId, since: since)
    }

    public func setTariff(_ t: EnergyTariff) {
        lock.lock(); defer { lock.unlock() }
        tariffs[t.tariffId] = t
    }

    public func getTariff(_ id: String) -> EnergyTariff? {
        lock.lock(); defer { lock.unlock() }
        return tariffs[id]
    }

    public func estimateCost(meterId: String, tariffId: String, since: Date) throws -> Decimal {
        lock.lock(); defer { lock.unlock() }
        guard let t = tariffs[tariffId] else { throw EnergyError.unknownTariff(tariffId) }
        let kwh = totalKwhSinceLocked(meterId: meterId, since: since)
        return Decimal(kwh * t.peakKwhRate)
    }

    public func logOutage(_ o: Outage) {
        lock.lock(); defer { lock.unlock() }
        outages[o.outageId] = o
    }

    public func activeOutages() -> [Outage] {
        lock.lock(); defer { lock.unlock() }
        return outages.values.filter { $0.endUtc == nil }
    }

    // ── Non-locking helpers (caller must hold `lock`) ─────────────────────────

    private func readingsForLocked(meterId: String, since: Date) -> [MeterReading] {
        readings.filter { $0.meterId == meterId && $0.atUtc >= since }.sorted { $0.atUtc < $1.atUtc }
    }

    private func totalKwhSinceLocked(meterId: String, since: Date) -> Double {
        let rows = readingsForLocked(meterId: meterId, since: since)
        if rows.count < 2 { return 0.0 }
        return rows[rows.count - 1].kwh - rows[0].kwh
    }
}

// MARK: - EnergyDomainContext

/// Static domain-context constants for the energy vertical.
public enum EnergyDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Energy] Expert energy management and renewable energy assistant. Help with solar/wind feasibility, load flow analysis, tariff optimisation, battery storage sizing, grid connection requirements, and energy efficiency audits. Apply NERSA and SABS standards. Compliance: Electricity Act, NERSA regulations, Municipal By-laws, Renewable Energy IPP."
    public static let complianceFlags: [String] = ["Electricity_Act", "NERSA", "SABS", "Municipal_Energy_By_laws", "POPIA"]
    public static let suggestedTools: [String] = ["energy_model", "analytics", "document_editor", "web_search"]
}

// MARK: - EnergyCompanionAdapter

/// An `ICompanionSession` decorator that prepends the energy domain system
/// prompt to every conversational call and adds energy helper methods.
/// Port of `CircleAI.Energy.EnergyCompanionAdapter`. Identity/context/feedback
/// are forwarded to the inner session; proactive events forward through the
/// inner session's `proactiveEvents` stream (the Swift protocol has no disposal).
public final class EnergyCompanionAdapter: ICompanionSession, @unchecked Sendable {
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

    private func enrich(_ m: String) -> String { "\(EnergyDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Energy helpers ────────────────────────────────────────────────────────

    /// Size a solar PV system by monthly usage (C# `SizeSolarSystemAsync`).
    public func sizeSolarSystem(monthlyConsumptionKwh: String, location: String, gridTied: Bool) async throws -> String {
        try await inner.agent(
            "Size a solar PV system for \(monthlyConsumptionKwh) kWh/month in \(location). Grid-tied: \(gridTied). Include panel capacity, inverter size, battery sizing (if off-grid), estimated generation, and payback period.")
    }

    /// Analyse a tariff schedule (C# `AnalyseTariffAsync`).
    public func analyseTariff(tariffSchedule: String, consumptionProfile: String) async throws -> String {
        try await inner.agent(
            "Analyse this tariff schedule for cost optimisation opportunities:\n\(tariffSchedule)\nConsumption profile:\n\(consumptionProfile)\nRecommend demand management and TOU strategies.")
    }

    /// Recommend the best tariff (C# `OptimiseTariffChoiceAsync`).
    public func optimiseTariffChoice(usagePattern: String, availableTariffs: String) async throws -> String {
        try await inner.agent(
            "Recommend the best tariff for usage \(usagePattern) from: \(availableTariffs). Show annual cost compare + breakeven assumptions.")
    }

    /// Explain a bill spike (C# `ExplainBillSpikeAsync`).
    public func explainBillSpike(priorBill: String, currentBill: String, conditions: String) async throws -> String {
        try await inner.agent(
            "Explain bill change from \(priorBill) to \(currentBill). Conditions: \(conditions). Cover usage, tariff, weather, meter issues.")
    }

    /// Size a solar PV system by daily usage (C# `PlanSolarSizingAsync`).
    public func planSolarSizing(averageDailyKwh: String, roofOrientation: String, budget: String) async throws -> String {
        try await inner.agent(
            "Size a solar PV system for \(averageDailyKwh) kWh/day, \(roofOrientation), budget \(budget). Output panels, inverter, battery, payback years.")
    }

    /// Draft a load-shedding plan (C# `DraftLoadSheddingPlanAsync`).
    public func draftLoadSheddingPlan(householdSize: String, criticalLoads: String) async throws -> String {
        try await inner.agent(
            "Draft a load-shedding plan for \(householdSize)-person home, critical: \(criticalLoads). Cover backup priority, run-time budget, safety.")
    }
}
