// Agriculture.swift
//
// Port of the Agriculture vertical from
// src/CircleAI.Agriculture/AgriculturePrimitives.cs and the static
// domain-context constants from AgricultureDomainContext.cs:
//   • Field, Crop, YieldRecord — domain records
//   • IFarmBoard               — fields, crops, yields
//   • InMemoryFarmBoard        — deterministic in-memory impl
//   • AgricultureDomainContext — system-prompt snippet + flags
//
// The Companion-facing wrapper (AgricultureCompanionAdapter) is an
// ICompanionSession decorator that prefixes the agriculture domain prompt.
//
// Porting notes:
//   • `DateTime` → `Date`; `DateTime? ExpectedHarvest`/`?` → `Date?`.
//   • `CropsForField` filters by FieldId, ordered ascending by PlantedOn.
//   • `AvgYieldOfVariety` averages TonsPerHa across yields whose crop's Variety
//     matches (case-insensitive); 0.0 when none. All state guarded by a
//     single `NSLock`.

import Foundation

// MARK: - Records

/// A farm field.
public struct Field: Sendable, Equatable, Codable {
    public let fieldId: String
    public let areaHa: Double
    public let soilType: String
    public let irrigationKind: String

    public init(fieldId: String, areaHa: Double, soilType: String, irrigationKind: String) {
        self.fieldId = fieldId
        self.areaHa = areaHa
        self.soilType = soilType
        self.irrigationKind = irrigationKind
    }
}

/// A planted crop.
public struct Crop: Sendable, Equatable, Codable {
    public let cropId: String
    public let fieldId: String
    public let variety: String
    public let plantedOn: Date
    public let expectedHarvest: Date?

    public init(cropId: String, fieldId: String, variety: String, plantedOn: Date, expectedHarvest: Date?) {
        self.cropId = cropId
        self.fieldId = fieldId
        self.variety = variety
        self.plantedOn = plantedOn
        self.expectedHarvest = expectedHarvest
    }
}

/// A recorded harvest yield.
public struct YieldRecord: Sendable, Equatable, Codable {
    public let cropId: String
    public let tonsPerHa: Double
    public let harvestedOn: Date

    public init(cropId: String, tonsPerHa: Double, harvestedOn: Date) {
        self.cropId = cropId
        self.tonsPerHa = tonsPerHa
        self.harvestedOn = harvestedOn
    }
}

// MARK: - Contract

/// Fields, crops, and yields for the agriculture vertical.
public protocol IFarmBoard: AnyObject, Sendable {
    func addField(_ f: Field)
    func plant(_ c: Crop)
    func recordYield(_ y: YieldRecord)
    func getField(_ id: String) -> Field?
    func cropsForField(fieldId: String) -> [Crop]
    func avgYieldOfVariety(_ variety: String) -> Double
}

// MARK: - InMemoryFarmBoard

/// Deterministic in-memory `IFarmBoard`. All state guarded by a single `NSLock`.
public final class InMemoryFarmBoard: IFarmBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var fields: [String: Field] = [:]
    private var crops: [String: Crop] = [:]
    private var yields: [YieldRecord] = []

    public init() {}

    public func addField(_ f: Field) {
        lock.lock(); defer { lock.unlock() }
        fields[f.fieldId] = f
    }

    public func plant(_ c: Crop) {
        lock.lock(); defer { lock.unlock() }
        crops[c.cropId] = c
    }

    public func recordYield(_ y: YieldRecord) {
        lock.lock(); defer { lock.unlock() }
        yields.append(y)
    }

    public func getField(_ id: String) -> Field? {
        lock.lock(); defer { lock.unlock() }
        return fields[id]
    }

    public func cropsForField(fieldId: String) -> [Crop] {
        lock.lock(); defer { lock.unlock() }
        return crops.values.filter { $0.fieldId == fieldId }.sorted { $0.plantedOn < $1.plantedOn }
    }

    public func avgYieldOfVariety(_ variety: String) -> Double {
        lock.lock(); defer { lock.unlock() }
        let rows = yields.filter { y in
            if let c = crops[y.cropId] { return c.variety.caseInsensitiveCompare(variety) == .orderedSame }
            return false
        }
        if rows.isEmpty { return 0.0 }
        return rows.reduce(0.0) { $0 + $1.tonsPerHa } / Double(rows.count)
    }

    /// Number of registered fields (matches C#'s `FieldCount`).
    public var fieldCount: Int {
        lock.lock(); defer { lock.unlock() }
        return fields.count
    }

    /// Remove a field by id. Returns true if it was present (matches C#'s
    /// `RemoveField` → `TryRemove`).
    @discardableResult
    public func removeField(_ fieldId: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return fields.removeValue(forKey: fieldId) != nil
    }

    /// Total area (ha) across all fields (matches C#'s `TotalAreaHa` →
    /// `Sum(AreaHa)`).
    public func totalAreaHa() -> Double {
        lock.lock(); defer { lock.unlock() }
        return fields.values.reduce(0.0) { $0 + $1.areaHa }
    }

    /// Fields with a given soil type (case-insensitive), largest area first.
    /// Matches C#'s `FieldsBySoil` → `OrderByDescending(AreaHa)`.
    public func fieldsBySoil(_ soilType: String) -> [Field] {
        lock.lock(); defer { lock.unlock() }
        return fields.values
            .filter { $0.soilType.caseInsensitiveCompare(soilType) == .orderedSame }
            .sorted { $0.areaHa > $1.areaHa }
    }

    /// Crops whose expected harvest is on/before `asOf`, earliest-harvest first.
    /// Matches C#'s `DueForHarvest` (crops with no expected harvest are excluded).
    public func dueForHarvest(asOf: Date) -> [Crop] {
        lock.lock(); defer { lock.unlock() }
        return crops.values
            .filter { if let h = $0.expectedHarvest { return h <= asOf } else { return false } }
            .sorted { $0.expectedHarvest! < $1.expectedHarvest! }
    }

    /// The variety with the highest average yield across recorded yields whose
    /// crop is known, or nil when there are none. Matches C#'s
    /// `BestYieldingVariety` (groups by variety case-insensitively; ties keep
    /// first-appearance order).
    public func bestYieldingVariety() -> String? {
        lock.lock(); defer { lock.unlock() }
        // Group yields (whose crop exists) by variety, case-insensitively,
        // preserving the first-seen display casing + appearance order.
        var order: [String] = []            // lowercased keys in first-seen order
        var display: [String: String] = [:] // lowercased key → first-seen variety casing
        var sums: [String: Double] = [:]
        var counts: [String: Int] = [:]
        for y in yields {
            guard let c = crops[y.cropId] else { continue }
            let key = c.variety.lowercased()
            if display[key] == nil { display[key] = c.variety; order.append(key) }
            sums[key, default: 0] += y.tonsPerHa
            counts[key, default: 0] += 1
        }
        guard !order.isEmpty else { return nil }
        // Descending by average; ties preserve first-appearance order (mirrors
        // C#'s stable OrderByDescending(...).First()).
        let best = order.enumerated()
            .sorted { a, b in
                let avgA = sums[a.element]! / Double(counts[a.element]!)
                let avgB = sums[b.element]! / Double(counts[b.element]!)
                if avgA != avgB { return avgA > avgB }
                return a.offset < b.offset
            }
            .first!
            .element
        return display[best]
    }
}

// MARK: - AgricultureDomainContext

/// Static domain-context constants for the agriculture vertical.
public enum AgricultureDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Agriculture] Expert agricultural advisor. Help with crop planning, soil management, pest and disease identification, livestock health, market price analysis, irrigation scheduling, and agri-finance applications. Adapt advice to the specific region, climate zone, and crop type. Compliance: DAFF regulations, Conservation of Agricultural Resources Act, POPIA."
    public static let complianceFlags: [String] = ["DAFF_regs", "CARA", "Fertilizer_Act", "POPIA"]
    public static let suggestedTools: [String] = ["weather_api", "market_prices", "soil_data", "document_editor"]
}

// MARK: - AgricultureCompanionAdapter

/// An `ICompanionSession` decorator that prepends the agriculture domain system
/// prompt to every conversational call and adds agronomy helper methods.
/// Port of `CircleAI.Agriculture.AgricultureCompanionAdapter`. Identity/context/
/// feedback are forwarded to the inner session; proactive events forward through
/// the inner session's `proactiveEvents` stream (the Swift protocol has no disposal).
public final class AgricultureCompanionAdapter: ICompanionSession, @unchecked Sendable {
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

    private func enrich(_ m: String) -> String { "\(AgricultureDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Agriculture helpers ───────────────────────────────────────────────────

    /// Diagnose a pest problem (C# `DiagnosePestAsync`).
    public func diagnosePest(cropType: String, symptoms: String) async throws -> String {
        try await inner.agent(
            "Diagnose this crop problem and recommend treatment. Crop: \(cropType). Symptoms: \(symptoms). Include integrated pest management (IPM) options and registered chemical controls.")
    }

    /// Plan a crop rotation (C# `PlanCropRotationAsync`).
    public func planCropRotation(farmContext: String, seasons: Int) async throws -> String {
        try await inner.agent(
            "Design a \(seasons)-season crop rotation plan for: \(farmContext). Optimise soil health, disease break cycles, and profitability.")
    }

    /// Diagnose a crop issue by region (C# `DiagnoseCropIssueAsync`).
    public func diagnoseCropIssue(crop: String, symptoms: String, region: String) async throws -> String {
        try await inner.agent(
            "Diagnose this \(crop) issue in \(region): \(symptoms). Cover likely pests/disease/deficiency, confidence, and an integrated-pest-management plan.")
    }

    /// Optimise a planting schedule (C# `OptimisePlantingScheduleAsync`).
    public func optimisePlantingSchedule(crop: String, climate: String, areaHa: Double) async throws -> String {
        try await inner.agent(
            "Plan planting for \(areaHa)ha of \(crop) in \(climate). Include sowing dates, density, irrigation, fertiliser, and harvest window.")
    }

    /// Estimate yield (C# `EstimateYieldAsync`).
    public func estimateYield(crop: String, areaHa: Double, conditions: String) async throws -> String {
        try await inner.agent(
            "Estimate yield (t/ha and total tons) for \(areaHa)ha of \(crop) under: \(conditions). Show baseline, best, worst case.")
    }

    /// Draft a sustainability report (C# `DraftSustainabilityReportAsync`).
    public func draftSustainabilityReport(operationSummary: String) async throws -> String {
        try await inner.agent(
            "Draft a sustainability report for: \(operationSummary). Cover soil health, water use, biodiversity, GHG, and SDG alignment.")
    }
}
