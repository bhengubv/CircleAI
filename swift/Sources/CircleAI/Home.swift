// Home.swift
//
// Port of the Home vertical from src/CircleAI.Home/HomePrimitives.cs and the
// static domain-context constants from HomeDomainContext.cs:
//   • Room, HomeDevice, MaintenanceTask — domain records
//   • IHomeBoard              — rooms, devices (toggle), maintenance tasks
//   • InMemoryHomeBoard       — deterministic in-memory impl
//   • HomeDomainContext       — system-prompt snippet + flags
//
// The Companion-facing wrapper (HomeCompanionAdapter) is intentionally NOT
// ported.
//
// Porting notes:
//   • `DateTime` → `Date`.
//   • `Toggle` on an unknown device throws → `HomeError.unknownDevice`;
//     `CompleteTask` on an unknown task throws → `.unknownTask`.
//   • `Rooms` is ordered ascending by Name. `DevicesIn` filters by RoomId
//     (unordered). `ActiveDevices` filters `IsOn` (unordered).
//   • `UpcomingTasks(by)` returns incomplete tasks with `DueOn <= by`, ordered
//     ascending by DueOn.
//   • All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// A room in the home.
public struct Room: Sendable, Equatable, Codable {
    public let roomId: String
    public let name: String
    public let areaM2: Double

    public init(roomId: String, name: String, areaM2: Double) {
        self.roomId = roomId
        self.name = name
        self.areaM2 = areaM2
    }
}

/// A home device (optionally in a room), on or off.
public struct HomeDevice: Sendable, Equatable, Codable {
    public let deviceId: String
    public let name: String
    public let kind: String
    public let roomId: String?
    public let isOn: Bool

    public init(deviceId: String, name: String, kind: String, roomId: String?, isOn: Bool) {
        self.deviceId = deviceId
        self.name = name
        self.kind = kind
        self.roomId = roomId
        self.isOn = isOn
    }
}

/// A scheduled maintenance task.
public struct MaintenanceTask: Sendable, Equatable, Codable {
    public let taskId: String
    public let description: String
    public let dueOn: Date
    public let completed: Bool

    public init(taskId: String, description: String, dueOn: Date, completed: Bool) {
        self.taskId = taskId
        self.description = description
        self.dueOn = dueOn
        self.completed = completed
    }
}

// MARK: - Errors

public enum HomeError: Error, Equatable, CustomStringConvertible {
    case unknownDevice(String)
    case unknownTask(String)

    public var description: String {
        switch self {
        case .unknownDevice(let id): return "Unknown device \(id)"
        case .unknownTask(let id): return "Unknown task \(id)"
        }
    }
}

// MARK: - Contract

/// Rooms, devices, and maintenance tasks for the home vertical.
public protocol IHomeBoard: AnyObject, Sendable {
    func addRoom(_ r: Room)
    func getRoom(_ id: String) -> Room?
    var rooms: [Room] { get }
    func addDevice(_ d: HomeDevice)
    func toggle(deviceId: String, on: Bool) throws
    func devicesIn(roomId: String) -> [HomeDevice]
    var activeDevices: [HomeDevice] { get }
    func scheduleTask(_ t: MaintenanceTask)
    func completeTask(taskId: String) throws
    func upcomingTasks(by: Date) -> [MaintenanceTask]
}

// MARK: - InMemoryHomeBoard

/// Deterministic in-memory `IHomeBoard`. All state guarded by a single `NSLock`.
public final class InMemoryHomeBoard: IHomeBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var roomsMap: [String: Room] = [:]
    private var devices: [String: HomeDevice] = [:]
    private var tasks: [String: MaintenanceTask] = [:]

    public init() {}

    public func addRoom(_ r: Room) {
        lock.lock(); defer { lock.unlock() }
        roomsMap[r.roomId] = r
    }

    public func getRoom(_ id: String) -> Room? {
        lock.lock(); defer { lock.unlock() }
        return roomsMap[id]
    }

    public var rooms: [Room] {
        lock.lock(); defer { lock.unlock() }
        return roomsMap.values.sorted { $0.name < $1.name }
    }

    public func addDevice(_ d: HomeDevice) {
        lock.lock(); defer { lock.unlock() }
        devices[d.deviceId] = d
    }

    public func toggle(deviceId: String, on: Bool) throws {
        lock.lock(); defer { lock.unlock() }
        guard let d = devices[deviceId] else { throw HomeError.unknownDevice(deviceId) }
        devices[deviceId] = HomeDevice(deviceId: d.deviceId, name: d.name, kind: d.kind, roomId: d.roomId, isOn: on)
    }

    public func devicesIn(roomId: String) -> [HomeDevice] {
        lock.lock(); defer { lock.unlock() }
        return devices.values.filter { $0.roomId == roomId }
    }

    public var activeDevices: [HomeDevice] {
        lock.lock(); defer { lock.unlock() }
        return devices.values.filter { $0.isOn }
    }

    public func scheduleTask(_ t: MaintenanceTask) {
        lock.lock(); defer { lock.unlock() }
        tasks[t.taskId] = t
    }

    public func completeTask(taskId: String) throws {
        lock.lock(); defer { lock.unlock() }
        guard let t = tasks[taskId] else { throw HomeError.unknownTask(taskId) }
        tasks[taskId] = MaintenanceTask(taskId: t.taskId, description: t.description, dueOn: t.dueOn, completed: true)
    }

    public func upcomingTasks(by: Date) -> [MaintenanceTask] {
        lock.lock(); defer { lock.unlock() }
        return tasks.values.filter { !$0.completed && $0.dueOn <= by }.sorted { $0.dueOn < $1.dueOn }
    }
}

// MARK: - HomeDomainContext

/// Static domain-context constants for the home vertical.
public enum HomeDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Home] Expert home management assistant. Help with maintenance schedules, renovation planning and budgeting, appliance troubleshooting, utility cost optimisation, and smart home setup. Practical, no-nonsense advice. Compliance: NHBRC, National Building Regulations, POPIA."
    public static let complianceFlags: [String] = ["NHBRC", "National_Building_Regs", "POPIA"]
    public static let suggestedTools: [String] = ["home_inventory", "task_manager", "web_search", "calculator"]
}
