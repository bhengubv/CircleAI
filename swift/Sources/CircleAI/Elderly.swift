// Elderly.swift
//
// Port of the Elderly vertical from src/CircleAI.Elderly/ElderlyPrimitives.cs
// and the static domain-context constants from ElderlyDomainContext.cs:
//   • CarePlan, MedReminder, ElderlyCheckIn — domain records
//   • IElderlyCareBoard       — care plans, medication reminders, check-ins
//   • InMemoryElderlyCareBoard — deterministic in-memory impl
//   • ElderlyDomainContext    — system-prompt snippet + flags
//
// The Companion-facing wrapper (ElderlyCompanionAdapter) is intentionally NOT
// ported.
//
// Porting notes:
//   • `DateTimeOffset` → `Date`; `TimeSpan DailyAt` → `TimeInterval` (seconds
//     since midnight), Codable as a Double.
//   • Care plans / reminders are keyed by ResidentName / ReminderId.
//   • `DeactivateReminder` on an unknown reminder throws
//     `ElderlyError.unknownReminder`.
//   • `ActiveRemindersFor` returns the resident's active reminders (unordered).
//   • The C# record `CheckIn` is renamed `ElderlyCheckIn` here so it does not
//     collide with `CheckIn` (Safety.Child) in the flat Swift module. All other
//     names are preserved.
//   • `LatestCheckIn` returns the resident's most-recent check-in (by AtUtc).
//   • `MissedCheckIn(resident, since)` is true when there is no check-in or the
//     latest is strictly before `since`.
//   • All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// A care plan for a resident.
public struct CarePlan: Sendable, Equatable, Codable {
    public let planId: String
    public let residentName: String
    public let medicalConditions: [String]
    public let allergies: [String]
    public let carerNotes: String

    public init(planId: String, residentName: String, medicalConditions: [String], allergies: [String], carerNotes: String) {
        self.planId = planId
        self.residentName = residentName
        self.medicalConditions = medicalConditions
        self.allergies = allergies
        self.carerNotes = carerNotes
    }
}

/// A recurring medication reminder.
public struct MedReminder: Sendable, Equatable, Codable {
    public let reminderId: String
    public let residentName: String
    public let medication: String
    /// Time-of-day for the reminder, as seconds since midnight (C# `TimeSpan`).
    public let dailyAt: TimeInterval
    public let active: Bool

    public init(reminderId: String, residentName: String, medication: String, dailyAt: TimeInterval, active: Bool) {
        self.reminderId = reminderId
        self.residentName = residentName
        self.medication = medication
        self.dailyAt = dailyAt
        self.active = active
    }
}

/// A wellbeing check-in for a resident. (C# `CheckIn` in CircleAI.Elderly;
/// renamed to avoid colliding with `CheckIn` from Safety.Child.)
public struct ElderlyCheckIn: Sendable, Equatable, Codable {
    public let checkInId: String
    public let residentName: String
    public let atUtc: Date
    public let status: String
    public let note: String?

    public init(checkInId: String, residentName: String, atUtc: Date, status: String, note: String?) {
        self.checkInId = checkInId
        self.residentName = residentName
        self.atUtc = atUtc
        self.status = status
        self.note = note
    }
}

// MARK: - Errors

public enum ElderlyError: Error, Equatable, CustomStringConvertible {
    case unknownReminder(String)

    public var description: String {
        switch self {
        case .unknownReminder(let id): return "Unknown reminder \(id)"
        }
    }
}

// MARK: - Contract

/// Care plans, medication reminders, and wellbeing check-ins for the
/// elderly-care vertical.
public protocol IElderlyCareBoard: AnyObject, Sendable {
    func setPlan(_ p: CarePlan)
    func getPlan(_ resident: String) -> CarePlan?
    func addReminder(_ r: MedReminder)
    func deactivateReminder(reminderId: String) throws
    func activeRemindersFor(_ resident: String) -> [MedReminder]
    func recordCheckIn(_ c: ElderlyCheckIn)
    func latestCheckIn(_ resident: String) -> ElderlyCheckIn?
    func missedCheckIn(resident: String, since: Date) -> Bool
}

// MARK: - InMemoryElderlyCareBoard

/// Deterministic in-memory `IElderlyCareBoard`. All state guarded by a single
/// `NSLock`.
public final class InMemoryElderlyCareBoard: IElderlyCareBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var plans: [String: CarePlan] = [:]
    private var reminders: [String: MedReminder] = [:]
    private var checkIns: [ElderlyCheckIn] = []

    public init() {}

    public func setPlan(_ p: CarePlan) {
        lock.lock(); defer { lock.unlock() }
        plans[p.residentName] = p
    }

    public func getPlan(_ resident: String) -> CarePlan? {
        lock.lock(); defer { lock.unlock() }
        return plans[resident]
    }

    public func addReminder(_ r: MedReminder) {
        lock.lock(); defer { lock.unlock() }
        reminders[r.reminderId] = r
    }

    public func deactivateReminder(reminderId: String) throws {
        lock.lock(); defer { lock.unlock() }
        guard let r = reminders[reminderId] else { throw ElderlyError.unknownReminder(reminderId) }
        reminders[reminderId] = MedReminder(reminderId: r.reminderId, residentName: r.residentName,
                                            medication: r.medication, dailyAt: r.dailyAt, active: false)
    }

    public func activeRemindersFor(_ resident: String) -> [MedReminder] {
        lock.lock(); defer { lock.unlock() }
        return reminders.values.filter { $0.residentName == resident && $0.active }
    }

    public func recordCheckIn(_ c: ElderlyCheckIn) {
        lock.lock(); defer { lock.unlock() }
        checkIns.append(c)
    }

    public func latestCheckIn(_ resident: String) -> ElderlyCheckIn? {
        lock.lock(); defer { lock.unlock() }
        return latestCheckInLocked(resident)
    }

    /// Non-reentrant latest-check-in lookup; caller must already hold `lock`.
    private func latestCheckInLocked(_ resident: String) -> ElderlyCheckIn? {
        return checkIns.filter { $0.residentName == resident }.max { $0.atUtc < $1.atUtc }
    }

    public func missedCheckIn(resident: String, since: Date) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let latest = latestCheckInLocked(resident) else { return true }
        return latest.atUtc < since
    }
}

// MARK: - ElderlyDomainContext

/// Static domain-context constants for the elderly-care vertical.
public enum ElderlyDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Elderly] Compassionate care assistant for elderly persons and their caregivers. Help with medication reminders, appointment management, benefit and pension queries, carer communication, and social activity suggestions. Use clear, patient language. Compliance: Older Persons Act 13/2006, POPIA, Social Assistance Act."
    public static let complianceFlags: [String] = ["Older_Persons_Act_13_2006", "Social_Assistance_Act", "POPIA"]
    public static let suggestedTools: [String] = ["medication_reminder", "calendar", "web_search", "document_editor"]
}
