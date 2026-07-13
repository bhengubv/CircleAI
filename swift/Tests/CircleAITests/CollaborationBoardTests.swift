// CollaborationBoardTests.swift
//
// Exercises the Collaboration port: channel upsert + team listing (name-ordered),
// message post + newest-first read with limit (+ default overload), presence
// set/get, and the null backends. Mirrors CircleAI.Collaboration/*.

import XCTest
import Foundation
@testable import CircleAI

final class CollaborationBoardTests: XCTestCase {

    // ── DTO ─────────────────────────────────────────────────────────────────

    func testMessageCodableRoundTrip() throws {
        let m = Message(messageId: "m1", channelId: "c1", authorId: "u1", body: "hi",
                        atUtc: Date(timeIntervalSince1970: 7))
        XCTAssertEqual(try JSONDecoder().decode(Message.self, from: try JSONEncoder().encode(m)), m)
    }

    // ── Channels ────────────────────────────────────────────────────────────────

    func testChannelUpsertAndTeamListing() async {
        let store = InMemoryChannelStore()
        XCTAssertEqual(store.backendId, "in-memory")
        store.upsert(Channel(channelId: "c1", name: "zebra", teamId: "t1"))
        store.upsert(Channel(channelId: "c2", name: "alpha", teamId: "t1"))
        store.upsert(Channel(channelId: "c3", name: "gamma", teamId: "t2"))
        let c1 = await store.get("c1")
        XCTAssertEqual(c1?.name, "zebra")
        // Team listing ordered by name.
        let t1 = await store.listForTeam("t1")
        XCTAssertEqual(t1.map { $0.name }, ["alpha", "zebra"])
    }

    func testChannelUpsertReplaces() async {
        let store = InMemoryChannelStore()
        store.upsert(Channel(channelId: "c1", name: "old", teamId: "t"))
        store.upsert(Channel(channelId: "c1", name: "new", teamId: "t"))
        let c1 = await store.get("c1")
        XCTAssertEqual(c1?.name, "new")
        let teamChannels = await store.listForTeam("t")
        XCTAssertEqual(teamChannels.count, 1)
    }

    // ── Messages ─────────────────────────────────────────────────────────────────

    func testMessagePostAndReadNewestFirst() async {
        let store = InMemoryMessageStore()
        for i in 0..<5 {
            _ = await store.post(Message(messageId: "m\(i)", channelId: "c", authorId: "u", body: "b",
                                         atUtc: Date(timeIntervalSince1970: TimeInterval(i))))
        }
        let recent = await store.read("c", limit: 2)
        XCTAssertEqual(recent.map { $0.messageId }, ["m4", "m3"])
    }

    func testMessageReadDefaultLimitOverload() async {
        let store = InMemoryMessageStore()
        _ = await store.post(Message(messageId: "m0", channelId: "c", authorId: "u", body: "b", atUtc: Date()))
        let inChannel = await store.read("c")
        XCTAssertEqual(inChannel.count, 1)
        let inEmpty = await store.read("empty")
        XCTAssertTrue(inEmpty.isEmpty)
    }

    // ── Presence ──────────────────────────────────────────────────────────────────

    func testPresenceSetAndGet() async {
        let p = InMemoryPresence()
        p.set(PresenceState(userId: "u1", online: true, lastSeenUtc: Date(timeIntervalSince1970: 3)))
        let got = await p.get("u1")
        XCTAssertEqual(got?.online, true)
        let u2 = await p.get("u2")
        XCTAssertNil(u2)
    }

    // ── Null ──────────────────────────────────────────────────────────────────

    func testNullBackends() async {
        let nullChannel = await NullChannelStore.instance.get("x")
        XCTAssertNil(nullChannel)
        let nullTeam = await NullChannelStore.instance.listForTeam("t")
        XCTAssertTrue(nullTeam.isEmpty)
        let m = Message(messageId: "m", channelId: "c", authorId: "u", body: "b", atUtc: Date())
        let echoed = await NullMessageStore.instance.post(m)
        XCTAssertEqual(echoed, m)  // echoes
        let nullMessages = await NullMessageStore.instance.read("c")
        XCTAssertTrue(nullMessages.isEmpty)
        let nullPresence = await NullPresence.instance.get("u")
        XCTAssertNil(nullPresence)
    }
}
