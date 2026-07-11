// GamingBoardTests.swift
//
// Exercises the Gaming records' Codable round-trips and the deterministic
// behaviour of InMemoryGamingBoard — titles by genre, session play-time totals,
// achievements (desc), and most-played ranking (topK). Also checks the
// GamingDomainContext constants. Mirrors CircleAI.Gaming/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class GamingBoardTests: XCTestCase {

    func testPlaySessionCodableRoundTrip() throws {
        let s = PlaySession(sessionId: "s1", userId: "u1", titleId: "t1", duration: 3600, atUtc: Date(timeIntervalSince1970: 5))
        XCTAssertEqual(try JSONDecoder().decode(PlaySession.self, from: try JSONEncoder().encode(s)), s)
    }

    func testTitlesByGenreAndTotalPlayTime() {
        let b = InMemoryGamingBoard()
        b.addTitle(GameTitle(titleId: "t1", name: "Ori", genre: "Platformer", platform: "PC"))
        b.addTitle(GameTitle(titleId: "t2", name: "Celeste", genre: "platformer", platform: "PC"))
        b.addTitle(GameTitle(titleId: "t3", name: "Civ", genre: "Strategy", platform: "PC"))
        XCTAssertEqual(Set(b.titlesByGenre("PLATFORMER").map { $0.titleId }), ["t1", "t2"])
        b.recordSession(PlaySession(sessionId: "s1", userId: "u1", titleId: "t1", duration: 1000, atUtc: Date(timeIntervalSince1970: 1)))
        b.recordSession(PlaySession(sessionId: "s2", userId: "u1", titleId: "t1", duration: 500, atUtc: Date(timeIntervalSince1970: 2)))
        b.recordSession(PlaySession(sessionId: "s3", userId: "other", titleId: "t1", duration: 9999, atUtc: Date(timeIntervalSince1970: 3)))
        XCTAssertEqual(b.totalPlayTime(userId: "u1", titleId: "t1"), 1500, accuracy: 1e-9)
        XCTAssertEqual(b.totalPlayTime(userId: "u1", titleId: "t9"), 0, accuracy: 1e-9)
    }

    func testAchievementsDescending() {
        let b = InMemoryGamingBoard()
        b.unlock(AchievementUnlock(unlockId: "x1", userId: "u1", titleId: "t1", achievement: "First Win", atUtc: Date(timeIntervalSince1970: 10)))
        b.unlock(AchievementUnlock(unlockId: "x2", userId: "u1", titleId: "t1", achievement: "Speedrun", atUtc: Date(timeIntervalSince1970: 30)))
        b.unlock(AchievementUnlock(unlockId: "x3", userId: "other", titleId: "t1", achievement: "N/A", atUtc: Date(timeIntervalSince1970: 99)))
        XCTAssertEqual(b.achievementsFor(userId: "u1").map { $0.unlockId }, ["x2", "x1"])
    }

    func testMostPlayedRankingAndTopK() throws {
        let b = InMemoryGamingBoard()
        b.addTitle(GameTitle(titleId: "t1", name: "A", genre: "g", platform: "PC"))
        b.addTitle(GameTitle(titleId: "t2", name: "B", genre: "g", platform: "PC"))
        b.addTitle(GameTitle(titleId: "t3", name: "C", genre: "g", platform: "PC"))
        b.recordSession(PlaySession(sessionId: "s1", userId: "u1", titleId: "t1", duration: 100, atUtc: Date(timeIntervalSince1970: 1)))
        b.recordSession(PlaySession(sessionId: "s2", userId: "u1", titleId: "t2", duration: 300, atUtc: Date(timeIntervalSince1970: 1)))
        b.recordSession(PlaySession(sessionId: "s3", userId: "u1", titleId: "t3", duration: 200, atUtc: Date(timeIntervalSince1970: 1)))
        XCTAssertEqual(try b.mostPlayed(userId: "u1").map { $0.titleId }, ["t2", "t3", "t1"])
        XCTAssertEqual(try b.mostPlayed(userId: "u1", topK: 2).map { $0.titleId }, ["t2", "t3"])
        XCTAssertThrowsError(try b.mostPlayed(userId: "u1", topK: 0)) { XCTAssertEqual($0 as? GamingError, .invalidTopK) }
    }

    func testDomainContext() {
        XCTAssertTrue(GamingDomainContext.systemPromptSnippet.contains("[DOMAIN: Gaming]"))
        XCTAssertEqual(GamingDomainContext.complianceFlags, ["POPIA", "WASPA", "Child_Protection"])
        XCTAssertEqual(GamingDomainContext.suggestedTools, ["game_db", "community_tools", "analytics", "web_search"])
    }
}
