// GamesTests.swift
//
// Exercises the Games contracts + working implementations from
// CircleAI.Games/{Contracts,InMemoryGames,NullImplementations}.cs:
//   • record Codable round-trips
//   • InMemorySceneGraph add/remove/snapshot (+ empty-id throw)
//   • InMemoryInputMap raise -> subscriber, dispose stops delivery
//   • TimerGameLoop start (fan-out ticks) / stop / dispose, guards
//   • Null implementations are inert
//
// Async callbacks are collected in a lock-guarded box and a short sleep lets
// spawned handler tasks land, matching the existing async-test idiom.

import XCTest
import Foundation
@testable import CircleAI

private final class Collector<T>: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [T] = []
    func add(_ v: T) { lock.lock(); items.append(v); lock.unlock() }
    var all: [T] { lock.lock(); defer { lock.unlock() }; return items }
    var count: Int { lock.lock(); defer { lock.unlock() }; return items.count }
}

final class GamesTests: XCTestCase {

    func testRecordsCodableRoundTrip() throws {
        let tick = GameTick(frame: 3, elapsed: 0.05)
        XCTAssertEqual(try JSONDecoder().decode(GameTick.self, from: try JSONEncoder().encode(tick)), tick)
        let ev = InputEvent(action: "jump", payload: ["height": "2"])
        XCTAssertEqual(try JSONDecoder().decode(InputEvent.self, from: try JSONEncoder().encode(ev)), ev)
        let node = SceneNode(nodeId: "n1", kind: "sprite", x: 1, y: 2, z: 3)
        XCTAssertEqual(try JSONDecoder().decode(SceneNode.self, from: try JSONEncoder().encode(node)), node)
    }

    func testSceneGraphAddRemoveSnapshot() async throws {
        let g = InMemorySceneGraph()
        XCTAssertEqual(g.backendId, "in-memory")
        try await g.add(SceneNode(nodeId: "n1", kind: "sprite", x: 0, y: 0, z: 0))
        try await g.add(SceneNode(nodeId: "n2", kind: "light", x: 1, y: 1, z: 1))
        var snap = await g.snapshot()
        XCTAssertEqual(Set(snap.map { $0.nodeId }), ["n1", "n2"])
        try await g.remove(nodeId: "n1")
        snap = await g.snapshot()
        XCTAssertEqual(snap.map { $0.nodeId }, ["n2"])
        do {
            try await g.add(SceneNode(nodeId: "  ", kind: "x", x: 0, y: 0, z: 0))
            XCTFail("expected nodeIdRequired")
        } catch { XCTAssertEqual(error as? GamesError, .nodeIdRequired) }
    }

    func testInputMapRaiseAndDispose() async throws {
        let map = InMemoryInputMap()
        XCTAssertEqual(map.backendId, "in-memory")
        let collector = Collector<InputEvent>()
        let sub = map.subscribe { ev in collector.add(ev) }
        XCTAssertEqual(map.subscriberCount, 1)
        map.raise(InputEvent(action: "left"))
        map.raise(InputEvent(action: "right"))
        try await Task.sleep(nanoseconds: 40_000_000)
        XCTAssertEqual(Set(collector.all.map { $0.action }), ["left", "right"])
        sub.dispose()
        XCTAssertEqual(map.subscriberCount, 0)
        sub.dispose() // idempotent
        map.raise(InputEvent(action: "ignored"))
        try await Task.sleep(nanoseconds: 20_000_000)
        XCTAssertFalse(collector.all.map { $0.action }.contains("ignored"))
    }

    func testTimerGameLoopFansOutTicksAndStops() async throws {
        let loop = TimerGameLoop()
        XCTAssertEqual(loop.backendId, "timer")
        let collector = Collector<GameTick>()
        let sub = loop.subscribe { tick in collector.add(tick) }
        try await loop.start(targetFps: 200) // ~5ms/frame
        try await Task.sleep(nanoseconds: 80_000_000) // ~16 frames
        await loop.stop()
        let seen = collector.count
        XCTAssertGreaterThan(seen, 0, "expected at least one tick")
        // Frames are monotonically increasing starting at 1.
        let frames = collector.all.map { $0.frame }
        XCTAssertEqual(frames.first, 1)
        XCTAssertEqual(frames, frames.sorted())
        sub.dispose()
        // After stop, no further ticks arrive.
        try await Task.sleep(nanoseconds: 40_000_000)
        XCTAssertEqual(collector.count, seen)
    }

    func testTimerGameLoopGuards() async throws {
        let loop = TimerGameLoop()
        do {
            try await loop.start(targetFps: 0)
            XCTFail("expected invalidTargetFps")
        } catch { XCTAssertEqual(error as? GamesError, .invalidTargetFps) }
        try await loop.start(targetFps: 60)
        do {
            try await loop.start(targetFps: 60)
            XCTFail("expected alreadyStarted")
        } catch { XCTAssertEqual(error as? GamesError, .alreadyStarted) }
        await loop.dispose() // stops; can start again
        try await loop.start(targetFps: 60)
        await loop.stop()
    }

    func testNullImplementationsAreInert() async throws {
        let loop = NullGameLoop()
        XCTAssertEqual(loop.backendId, "null")
        try await loop.start()
        await loop.stop()
        _ = loop.subscribe { _ in }
        await loop.dispose()

        let map = NullInputMap.shared
        XCTAssertEqual(map.backendId, "null")
        let sub = map.subscribe { _ in }
        sub.dispose()

        let g = NullSceneGraph.shared
        XCTAssertEqual(g.backendId, "null")
        try await g.add(SceneNode(nodeId: "n1", kind: "x", x: 0, y: 0, z: 0))
        try await g.remove(nodeId: "n1")
        let snap = await g.snapshot()
        XCTAssertTrue(snap.isEmpty)
    }
}
