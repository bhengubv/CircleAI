// Business.swift
//
// Port of the Business vertical from src/CircleAI.Business/BusinessPrimitives.cs
// and the static domain-context constants from BusinessDomainContext.cs:
//   • BusinessUnit, KpiSample, QuarterTarget — domain records
//   • IBusinessBoard          — unit hierarchy, KPI tracking, quarter targets
//   • InMemoryBusinessBoard   — deterministic in-memory impl
//   • BusinessDomainContext   — system-prompt snippet + flags
//
// The Companion-facing wrapper (BusinessCompanionAdapter) is intentionally NOT
// ported.
//
// Porting notes:
//   • `DateTimeOffset` → `Date`.
//   • `LatestKpi` returns `Double.nan` when there is no matching sample
//     (mirrors C# `double.NaN`), otherwise the most-recent (by AtUtc) value.
//   • `TargetAchievement` returns `Double.nan` when the target is missing or
//     its Target is 0; otherwise `LatestKpi / target.Target`. The target key is
//     `"{UnitId}/{Metric}/{Year}Q{Quarter}"`.
//   • `ChildrenOf` filters by ParentUnitId (unordered, dictionary-values order).
//   • All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// A business unit in an org hierarchy.
public struct BusinessUnit: Sendable, Equatable, Codable {
    public let unitId: String
    public let name: String
    public let parentUnitId: String
    public let kpiTags: [String]

    public init(unitId: String, name: String, parentUnitId: String, kpiTags: [String]) {
        self.unitId = unitId
        self.name = name
        self.parentUnitId = parentUnitId
        self.kpiTags = kpiTags
    }
}

/// A recorded KPI sample.
public struct KpiSample: Sendable, Equatable, Codable {
    public let unitId: String
    public let metric: String
    public let value: Double
    public let atUtc: Date

    public init(unitId: String, metric: String, value: Double, atUtc: Date) {
        self.unitId = unitId
        self.metric = metric
        self.value = value
        self.atUtc = atUtc
    }
}

/// A per-quarter target for a metric.
public struct QuarterTarget: Sendable, Equatable, Codable {
    public let unitId: String
    public let metric: String
    public let year: Int
    public let quarter: Int
    public let target: Double

    public init(unitId: String, metric: String, year: Int, quarter: Int, target: Double) {
        self.unitId = unitId
        self.metric = metric
        self.year = year
        self.quarter = quarter
        self.target = target
    }
}

// MARK: - Contract

/// Unit hierarchy, KPI tracking, and quarter targets for the business vertical.
public protocol IBusinessBoard: AnyObject, Sendable {
    func add(_ u: BusinessUnit)
    func getUnit(_ id: String) -> BusinessUnit?
    func childrenOf(_ parentUnitId: String) -> [BusinessUnit]
    func record(_ s: KpiSample)
    func latestKpi(unitId: String, metric: String) -> Double
    func setTarget(_ t: QuarterTarget)
    func targetAchievement(unitId: String, metric: String, year: Int, quarter: Int) -> Double
}

// MARK: - InMemoryBusinessBoard

/// Deterministic in-memory `IBusinessBoard`. All state guarded by a single
/// `NSLock`.
public final class InMemoryBusinessBoard: IBusinessBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var units: [String: BusinessUnit] = [:]
    private var kpis: [KpiSample] = []
    private var targets: [String: QuarterTarget] = [:]

    public init() {}

    public func add(_ u: BusinessUnit) {
        lock.lock(); defer { lock.unlock() }
        units[u.unitId] = u
    }

    public func getUnit(_ id: String) -> BusinessUnit? {
        lock.lock(); defer { lock.unlock() }
        return units[id]
    }

    public func childrenOf(_ parentUnitId: String) -> [BusinessUnit] {
        lock.lock(); defer { lock.unlock() }
        return units.values.filter { $0.parentUnitId == parentUnitId }
    }

    public func record(_ s: KpiSample) {
        lock.lock(); defer { lock.unlock() }
        kpis.append(s)
    }

    public func latestKpi(unitId: String, metric: String) -> Double {
        lock.lock(); defer { lock.unlock() }
        return latestKpiLocked(unitId: unitId, metric: metric)
    }

    /// Non-reentrant KPI lookup; caller must already hold `lock`.
    private func latestKpiLocked(unitId: String, metric: String) -> Double {
        let hit = kpis.filter { $0.unitId == unitId && $0.metric == metric }
            .max { $0.atUtc < $1.atUtc }
        return hit?.value ?? Double.nan
    }

    public func setTarget(_ t: QuarterTarget) {
        lock.lock(); defer { lock.unlock() }
        targets["\(t.unitId)/\(t.metric)/\(t.year)Q\(t.quarter)"] = t
    }

    public func targetAchievement(unitId: String, metric: String, year: Int, quarter: Int) -> Double {
        lock.lock(); defer { lock.unlock() }
        let key = "\(unitId)/\(metric)/\(year)Q\(quarter)"
        guard let target = targets[key], target.target != 0 else { return Double.nan }
        return latestKpiLocked(unitId: unitId, metric: metric) / target.target
    }
}

// MARK: - BusinessDomainContext

/// Static domain-context constants for the business vertical.
public enum BusinessDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Business] You are a business strategy and operations expert. Help with OKRs, strategic planning, meeting facilitation, competitive analysis, and executive decision support. Structure advice with clear options and trade-offs. Compliance: POPIA data handling, general commercial law."
    public static let complianceFlags: [String] = ["POPIA", "Commercial_Law", "GDPR_aware"]
    public static let suggestedTools: [String] = ["calendar", "web_search", "document_editor", "task_manager"]
}
