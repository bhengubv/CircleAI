// CreativeBoardTests.swift
//
// Exercises the Creative records' Codable round-trips and the deterministic
// behaviour of InMemoryCreativeBoard — works by tag, inspiration (recent, desc,
// limited), and average critique score (0 when none). Also checks the
// CreativeDomainContext constants. Mirrors CircleAI.Creative/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class CreativeBoardTests: XCTestCase {

    func testCreativeWorkCodableRoundTrip() throws {
        let w = CreativeWork(workId: "w1", title: "Poem", medium: "text", author: "A", createdUtc: Date(timeIntervalSince1970: 5), tags: ["poetry", "draft"])
        XCTAssertEqual(try JSONDecoder().decode(CreativeWork.self, from: try JSONEncoder().encode(w)), w)
    }

    func testWorksByTagCaseInsensitive() {
        let b = InMemoryCreativeBoard()
        b.addWork(CreativeWork(workId: "w1", title: "A", medium: "text", author: "x", createdUtc: Date(timeIntervalSince1970: 1), tags: ["Poetry"]))
        b.addWork(CreativeWork(workId: "w2", title: "B", medium: "text", author: "x", createdUtc: Date(timeIntervalSince1970: 1), tags: ["poetry", "epic"]))
        b.addWork(CreativeWork(workId: "w3", title: "C", medium: "img", author: "x", createdUtc: Date(timeIntervalSince1970: 1), tags: ["sketch"]))
        XCTAssertEqual(b.getWork("w1")?.title, "A")
        XCTAssertEqual(Set(b.worksByTag("POETRY").map { $0.workId }), ["w1", "w2"])
    }

    func testRecentInspirationDescendingLimited() {
        let b = InMemoryCreativeBoard()
        b.recordInspiration(Inspiration(inspirationId: "i1", promptText: "a", sourceUrl: "u", seenUtc: Date(timeIntervalSince1970: 10)))
        b.recordInspiration(Inspiration(inspirationId: "i2", promptText: "b", sourceUrl: "u", seenUtc: Date(timeIntervalSince1970: 30)))
        b.recordInspiration(Inspiration(inspirationId: "i3", promptText: "c", sourceUrl: "u", seenUtc: Date(timeIntervalSince1970: 20)))
        XCTAssertEqual(b.recentInspiration().map { $0.inspirationId }, ["i2", "i3", "i1"])
        XCTAssertEqual(b.recentInspiration(limit: 1).map { $0.inspirationId }, ["i2"])
    }

    func testAvgScore() {
        let b = InMemoryCreativeBoard()
        b.addCritique(Critique(critiqueId: "c1", workId: "w1", reviewer: "r", body: "good", score: 8))
        b.addCritique(Critique(critiqueId: "c2", workId: "w1", reviewer: "r", body: "great", score: 10))
        b.addCritique(Critique(critiqueId: "c3", workId: "w2", reviewer: "r", body: "meh", score: 4))
        XCTAssertEqual(b.avgScore(workId: "w1"), 9, accuracy: 1e-9)
        XCTAssertEqual(b.avgScore(workId: "none"), 0, accuracy: 1e-9)
    }

    func testDomainContext() {
        XCTAssertTrue(CreativeDomainContext.systemPromptSnippet.contains("[DOMAIN: Creative]"))
        XCTAssertEqual(CreativeDomainContext.complianceFlags, ["Copyright_Act_98_1978", "POPIA"])
        XCTAssertEqual(CreativeDomainContext.suggestedTools, ["writing_tools", "image_tools", "music_tools", "document_editor"])
    }
}
