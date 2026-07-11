// SocialBoardTests.swift
//
// Exercises the Social records' Codable round-trips and the deterministic
// behaviour of InMemorySocialBoard — posts, reaction counts, follow/unfollow
// (incl. self-follow guard), feed (following-only, desc, limited), and
// followers. Also checks the SocialDomainContext constants. Mirrors
// CircleAI.Social/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class SocialBoardTests: XCTestCase {

    func testSocialPostCodableRoundTrip() throws {
        let p = SocialPost(postId: "p1", authorId: "u1", body: "hi", atUtc: Date(timeIntervalSince1970: 5), tags: ["greeting"])
        XCTAssertEqual(try JSONDecoder().decode(SocialPost.self, from: try JSONEncoder().encode(p)), p)
    }

    func testReactionCountCaseInsensitive() {
        let b = InMemorySocialBoard()
        b.react(Reaction(postId: "p1", userId: "u1", kind: "Like", atUtc: Date(timeIntervalSince1970: 1)))
        b.react(Reaction(postId: "p1", userId: "u2", kind: "like", atUtc: Date(timeIntervalSince1970: 2)))
        b.react(Reaction(postId: "p1", userId: "u3", kind: "love", atUtc: Date(timeIntervalSince1970: 3)))
        b.react(Reaction(postId: "p2", userId: "u1", kind: "like", atUtc: Date(timeIntervalSince1970: 4)))
        XCTAssertEqual(b.reactionCount(postId: "p1", kind: "LIKE"), 2)
        XCTAssertEqual(b.reactionCount(postId: "p1", kind: "love"), 1)
    }

    func testFollowSelfGuardUnfollowAndFollowers() throws {
        let b = InMemorySocialBoard()
        XCTAssertThrowsError(try b.follow(Follow(followerId: "u1", followeeId: "u1", atUtc: Date(timeIntervalSince1970: 1)))) {
            XCTAssertEqual($0 as? SocialError, .cannotFollowSelf)
        }
        try b.follow(Follow(followerId: "u1", followeeId: "u2", atUtc: Date(timeIntervalSince1970: 1)))
        try b.follow(Follow(followerId: "u3", followeeId: "u2", atUtc: Date(timeIntervalSince1970: 2)))
        XCTAssertEqual(Set(b.followers(userId: "u2")), ["u1", "u3"])
        b.unfollow(followerId: "u1", followeeId: "u2")
        XCTAssertEqual(b.followers(userId: "u2"), ["u3"])
    }

    func testFeedFollowingOnlyDescendingLimitedAndValidation() throws {
        let b = InMemorySocialBoard()
        try b.follow(Follow(followerId: "u1", followeeId: "author", atUtc: Date(timeIntervalSince1970: 1)))
        b.post(SocialPost(postId: "p1", authorId: "author", body: "a", atUtc: Date(timeIntervalSince1970: 10), tags: []))
        b.post(SocialPost(postId: "p2", authorId: "author", body: "b", atUtc: Date(timeIntervalSince1970: 30), tags: []))
        b.post(SocialPost(postId: "p3", authorId: "stranger", body: "c", atUtc: Date(timeIntervalSince1970: 99), tags: [])) // not followed
        XCTAssertEqual(try b.feedFor(userId: "u1").map { $0.postId }, ["p2", "p1"])
        XCTAssertEqual(try b.feedFor(userId: "u1", limit: 1).map { $0.postId }, ["p2"])
        XCTAssertThrowsError(try b.feedFor(userId: "u1", limit: 0)) { XCTAssertEqual($0 as? SocialError, .invalidLimit) }
    }

    func testDomainContext() {
        XCTAssertTrue(SocialDomainContext.systemPromptSnippet.contains("[DOMAIN: Social]"))
        XCTAssertEqual(SocialDomainContext.complianceFlags, ["POPIA", "ASA_Advertising_Code", "Platform_Community_Standards"])
        XCTAssertEqual(SocialDomainContext.suggestedTools, ["social_media_api", "analytics", "content_planner", "image_tools"])
    }
}
