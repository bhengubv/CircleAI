// MediaHubTests.swift
//
// Validates the CircleAI.MediaHub port (MediaHub.swift): MediaItem /
// PlaybackPosition Codable, InMemoryHubMediaLibrary (backend id, get + arg
// guards, title-ascending case-insensitive search, topK), InMemorySyncedPlayback
// (join, broadcast fan-out to subscribers, unsubscribe via the returned handle,
// broadcast to unknown session is a no-op, and — the concurrency-safety case — a
// handler that re-enters the service during broadcast does not deadlock), and the
// Null defaults.

import XCTest
@testable import CircleAI

final class MediaHubTests: XCTestCase {

    // ── DTO Codable ──────────────────────────────────────────────────────────

    func testMediaItemCodableRoundTrip() throws {
        let m = MediaItem(itemId: "i1", title: "Track", kind: "audio", duration: 200, mimeType: "audio/mpeg")
        let back = try JSONDecoder().decode(MediaItem.self, from: try JSONEncoder().encode(m))
        XCTAssertEqual(m, back)
    }

    func testPlaybackPositionCodableRoundTrip() throws {
        let p = PlaybackPosition(itemId: "i1", position: 42.5, atUtc: Date(timeIntervalSince1970: 9))
        let back = try JSONDecoder().decode(PlaybackPosition.self, from: try JSONEncoder().encode(p))
        XCTAssertEqual(p, back)
    }

    // ── InMemoryHubMediaLibrary ──────────────────────────────────────────────

    private func item(_ id: String, _ title: String) -> MediaItem {
        MediaItem(itemId: id, title: title, kind: "audio", duration: 1, mimeType: "x")
    }

    func testHubLibraryBackendIdAndGet() async throws {
        let lib = InMemoryHubMediaLibrary()
        XCTAssertEqual(lib.backendId, "in-memory")
        let missing = try await lib.get("nope")
        XCTAssertNil(missing)
        let m = item("i1", "Hello")
        lib.add(m)
        let fetched = try await lib.get("i1")
        XCTAssertEqual(fetched, m)
    }

    func testHubLibraryGetThrowsOnBlankId() async {
        let lib = InMemoryHubMediaLibrary()
        do {
            _ = try await lib.get("  ")
            XCTFail("expected throw")
        } catch {
            XCTAssertEqual(error as? MediaHubError, .idRequired)
        }
    }

    func testHubLibrarySearchTitleAscendingCaseInsensitive() async throws {
        let lib = InMemoryHubMediaLibrary()
        lib.add(item("1", "banana"))
        lib.add(item("2", "Apple"))
        lib.add(item("3", "cherry apple"))
        lib.add(item("4", "Zebra"))
        // Case-insensitive substring "apple" → "Apple", "cherry apple"; ascending.
        let hits = try await lib.search("apple")
        XCTAssertEqual(hits.map { $0.title }, ["Apple", "cherry apple"])
    }

    func testHubLibrarySearchOrdersAllAscending() async throws {
        let lib = InMemoryHubMediaLibrary()
        lib.add(item("1", "delta"))
        lib.add(item("2", "Alpha"))
        lib.add(item("3", "charlie"))
        let hits = try await lib.search("a") // matches all three
        XCTAssertEqual(hits.map { $0.title }, ["Alpha", "charlie", "delta"])
    }

    func testHubLibrarySearchTopKCapAndGuard() async throws {
        let lib = InMemoryHubMediaLibrary()
        for i in 0..<5 { lib.add(item("\(i)", "song\(i)")) }
        let capped = try await lib.search("song", topK: 2)
        XCTAssertEqual(capped.count, 2)
        do {
            _ = try await lib.search("song", topK: 0)
            XCTFail("expected throw")
        } catch {
            XCTAssertEqual(error as? MediaHubError, .topKOutOfRange)
        }
    }

    // ── InMemorySyncedPlayback ───────────────────────────────────────────────

    /// Thread-safe collector of received positions.
    private final class Sink: @unchecked Sendable {
        private let lock = NSLock()
        private var items: [PlaybackPosition] = []
        func add(_ p: PlaybackPosition) { lock.lock(); items.append(p); lock.unlock() }
        var all: [PlaybackPosition] { lock.lock(); defer { lock.unlock() }; return items }
    }

    private func pos(_ item: String, _ p: TimeInterval) -> PlaybackPosition {
        PlaybackPosition(itemId: item, position: p, atUtc: Date(timeIntervalSince1970: p))
    }

    func testSyncedPlaybackBackendIdAndJoin() async throws {
        let sp = InMemorySyncedPlayback()
        XCTAssertEqual(sp.backendId, "in-memory")
        try await sp.joinSession(sessionId: "s1", userId: "u1")
        try await sp.joinSession(sessionId: "s1", userId: "u2") // no throw, idempotent set
    }

    func testJoinGuards() async {
        let sp = InMemorySyncedPlayback()
        do { try await sp.joinSession(sessionId: " ", userId: "u"); XCTFail() }
        catch { XCTAssertEqual(error as? MediaHubError, .sessionIdRequired) }
        do { try await sp.joinSession(sessionId: "s", userId: "  "); XCTFail() }
        catch { XCTAssertEqual(error as? MediaHubError, .userIdRequired) }
    }

    func testBroadcastReachesSubscribers() async throws {
        let sp = InMemorySyncedPlayback()
        let sink = Sink()
        let sub = sp.subscribe(sessionId: "s1") { p in sink.add(p) }
        try await sp.broadcastPosition(sessionId: "s1", pos: pos("i1", 10))
        try await sp.broadcastPosition(sessionId: "s1", pos: pos("i1", 20))
        XCTAssertEqual(sink.all.map { $0.position }, [10, 20])
        sub.dispose()
    }

    func testUnsubscribeStopsDelivery() async throws {
        let sp = InMemorySyncedPlayback()
        let sink = Sink()
        let sub = sp.subscribe(sessionId: "s1") { p in sink.add(p) }
        try await sp.broadcastPosition(sessionId: "s1", pos: pos("i1", 1))
        sub.dispose()
        try await sp.broadcastPosition(sessionId: "s1", pos: pos("i1", 2)) // after dispose
        XCTAssertEqual(sink.all.map { $0.position }, [1]) // only the pre-dispose one
    }

    func testDisposeIsIdempotent() async throws {
        let sp = InMemorySyncedPlayback()
        let sink = Sink()
        let sub = sp.subscribe(sessionId: "s1") { p in sink.add(p) }
        sub.dispose()
        sub.dispose() // second dispose is a no-op, must not crash
        try await sp.broadcastPosition(sessionId: "s1", pos: pos("i1", 5))
        XCTAssertTrue(sink.all.isEmpty)
    }

    func testBroadcastToUnknownSessionIsNoOp() async throws {
        let sp = InMemorySyncedPlayback()
        try await sp.broadcastPosition(sessionId: "never-joined", pos: pos("i1", 1)) // no throw
    }

    func testBroadcastGuardsSessionId() async {
        let sp = InMemorySyncedPlayback()
        do { try await sp.broadcastPosition(sessionId: "  ", pos: pos("i1", 1)); XCTFail() }
        catch { XCTAssertEqual(error as? MediaHubError, .sessionIdRequired) }
    }

    func testMultipleSubscribersAllReceive() async throws {
        let sp = InMemorySyncedPlayback()
        let s1 = Sink(); let s2 = Sink()
        let a = sp.subscribe(sessionId: "s1") { p in s1.add(p) }
        let b = sp.subscribe(sessionId: "s1") { p in s2.add(p) }
        try await sp.broadcastPosition(sessionId: "s1", pos: pos("i1", 7))
        XCTAssertEqual(s1.all.count, 1)
        XCTAssertEqual(s2.all.count, 1)
        a.dispose(); b.dispose()
    }

    /// A handler that unsubscribes itself *during* the broadcast fan-out. If the
    /// service held its lock across the awaited handler this would deadlock the
    /// non-reentrant NSLock; the snapshot-release-await ordering makes it safe.
    func testHandlerCanUnsubscribeDuringBroadcastWithoutDeadlock() async throws {
        let sp = InMemorySyncedPlayback()
        let sink = Sink()
        // Box the token so the handler can dispose it after it is assigned.
        final class Box: @unchecked Sendable { var token: (any Disposable)? }
        let box = Box()
        box.token = sp.subscribe(sessionId: "s1") { p in
            sink.add(p)
            box.token?.dispose() // re-enter the service from inside the fan-out
        }
        try await sp.broadcastPosition(sessionId: "s1", pos: pos("i1", 1))
        // Delivered once; the self-dispose removed it before the next broadcast.
        try await sp.broadcastPosition(sessionId: "s1", pos: pos("i1", 2))
        XCTAssertEqual(sink.all.map { $0.position }, [1])
    }

    func testSubscribeWithBlankSessionReturnsNoOpHandle() async throws {
        let sp = InMemorySyncedPlayback()
        let sink = Sink()
        let sub = sp.subscribe(sessionId: " ") { p in sink.add(p) }
        // No session was registered; disposing the no-op handle is harmless.
        sub.dispose()
        XCTAssertTrue(sink.all.isEmpty)
    }

    // ── Null defaults ────────────────────────────────────────────────────────

    func testNullHubMediaLibrary() async throws {
        let lib = NullHubMediaLibrary.instance
        XCTAssertEqual(lib.backendId, "null")
        let nullGet = try await lib.get("x")
        XCTAssertNil(nullGet)
        let nullSearch = try await lib.search("anything")
        XCTAssertTrue(nullSearch.isEmpty)
    }

    func testNullSyncedPlaybackNeverDelivers() async throws {
        let sp = NullSyncedPlayback.instance
        XCTAssertEqual(sp.backendId, "null")
        let sink = Sink()
        let sub = sp.subscribe(sessionId: "s1") { p in sink.add(p) }
        try await sp.joinSession(sessionId: "s1", userId: "u1")
        try await sp.broadcastPosition(sessionId: "s1", pos: pos("i1", 1))
        sub.dispose()
        XCTAssertTrue(sink.all.isEmpty)
    }
}
