// HomeBoardTests.swift
//
// Exercises the Home records' Codable round-trips and the deterministic
// behaviour of InMemoryHomeBoard — rooms (name-ordered), devices (add, toggle
// incl. unknown throw, devices-in-room, active-devices), and maintenance tasks
// (schedule, complete incl. unknown throw, upcoming by due date). Also checks
// the HomeDomainContext constants. Mirrors CircleAI.Home/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class HomeBoardTests: XCTestCase {

    func testHomeDeviceCodableRoundTrip() throws {
        let d = HomeDevice(deviceId: "d1", name: "Lamp", kind: "light", roomId: "r1", isOn: false)
        XCTAssertEqual(try JSONDecoder().decode(HomeDevice.self, from: try JSONEncoder().encode(d)), d)
    }

    func testMaintenanceTaskCodableRoundTrip() throws {
        let t = MaintenanceTask(taskId: "t1", description: "Gutters", dueOn: Date(timeIntervalSince1970: 9), completed: false)
        XCTAssertEqual(try JSONDecoder().decode(MaintenanceTask.self, from: try JSONEncoder().encode(t)), t)
    }

    func testRoomsNameOrdered() {
        let b = InMemoryHomeBoard()
        b.addRoom(Room(roomId: "r2", name: "Lounge", areaM2: 30))
        b.addRoom(Room(roomId: "r1", name: "Bedroom", areaM2: 20))
        XCTAssertEqual(b.getRoom("r1")?.name, "Bedroom")
        XCTAssertEqual(b.rooms.map { $0.name }, ["Bedroom", "Lounge"])
    }

    func testToggleUpdatesStateAndUnknownThrows() throws {
        let b = InMemoryHomeBoard()
        b.addDevice(HomeDevice(deviceId: "d1", name: "Lamp", kind: "light", roomId: "r1", isOn: false))
        try b.toggle(deviceId: "d1", on: true)
        XCTAssertEqual(b.activeDevices.map { $0.deviceId }, ["d1"])
        try b.toggle(deviceId: "d1", on: false)
        XCTAssertTrue(b.activeDevices.isEmpty)
        XCTAssertThrowsError(try b.toggle(deviceId: "ghost", on: true)) { XCTAssertEqual($0 as? HomeError, .unknownDevice("ghost")) }
    }

    func testDevicesIn() {
        let b = InMemoryHomeBoard()
        b.addDevice(HomeDevice(deviceId: "d1", name: "Lamp", kind: "light", roomId: "r1", isOn: false))
        b.addDevice(HomeDevice(deviceId: "d2", name: "TV", kind: "media", roomId: "r2", isOn: true))
        b.addDevice(HomeDevice(deviceId: "d3", name: "Fan", kind: "climate", roomId: "r1", isOn: false))
        XCTAssertEqual(Set(b.devicesIn(roomId: "r1").map { $0.deviceId }), ["d1", "d3"])
    }

    func testUpcomingTasksFilteredAndDueOrdered() throws {
        let b = InMemoryHomeBoard()
        let base = Date(timeIntervalSince1970: 1000)
        b.scheduleTask(MaintenanceTask(taskId: "t1", description: "A", dueOn: base.addingTimeInterval(30), completed: false))
        b.scheduleTask(MaintenanceTask(taskId: "t2", description: "B", dueOn: base.addingTimeInterval(10), completed: false))
        b.scheduleTask(MaintenanceTask(taskId: "t3", description: "C", dueOn: base.addingTimeInterval(20), completed: true))   // done
        b.scheduleTask(MaintenanceTask(taskId: "t4", description: "D", dueOn: base.addingTimeInterval(999), completed: false)) // after cutoff
        let by = base.addingTimeInterval(50)
        XCTAssertEqual(b.upcomingTasks(by: by).map { $0.taskId }, ["t2", "t1"])
        // Completing t1 removes it from upcoming.
        try b.completeTask(taskId: "t1")
        XCTAssertEqual(b.upcomingTasks(by: by).map { $0.taskId }, ["t2"])
    }

    func testCompleteUnknownTaskThrows() {
        let b = InMemoryHomeBoard()
        XCTAssertThrowsError(try b.completeTask(taskId: "ghost")) { XCTAssertEqual($0 as? HomeError, .unknownTask("ghost")) }
    }

    func testDomainContext() {
        XCTAssertTrue(HomeDomainContext.systemPromptSnippet.contains("[DOMAIN: Home]"))
        XCTAssertEqual(HomeDomainContext.complianceFlags, ["NHBRC", "National_Building_Regs", "POPIA"])
        XCTAssertEqual(HomeDomainContext.suggestedTools, ["home_inventory", "task_manager", "web_search", "calculator"])
    }
}
