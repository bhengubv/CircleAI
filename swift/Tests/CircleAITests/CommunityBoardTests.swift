// CommunityBoardTests.swift
//
// Exercises the Community records' Codable round-trips and the deterministic
// behaviour of InMemoryCommunityBoard — groups + membership, announcements
// (desc, limited), and volunteer opportunities (future-only, asc). Also checks
// the CommunityDomainContext constants. Mirrors CircleAI.Community/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class CommunityBoardTests: XCTestCase {

    func testCommunityGroupCodableRoundTrip() throws {
        let g = CommunityGroup(groupId: "g1", name: "Cleanup Crew", purpose: "Litter", memberIds: ["u1", "u2"])
        XCTAssertEqual(try JSONDecoder().decode(CommunityGroup.self, from: try JSONEncoder().encode(g)), g)
    }

    func testGroupsForMember() {
        let b = InMemoryCommunityBoard()
        b.create(CommunityGroup(groupId: "g1", name: "A", purpose: "x", memberIds: ["u1", "u2"]))
        b.create(CommunityGroup(groupId: "g2", name: "B", purpose: "y", memberIds: ["u3"]))
        b.create(CommunityGroup(groupId: "g3", name: "C", purpose: "z", memberIds: ["u1"]))
        XCTAssertEqual(b.getGroup("g1")?.name, "A")
        XCTAssertEqual(Set(b.groupsForMember(memberId: "u1").map { $0.groupId }), ["g1", "g3"])
    }

    func testAnnouncementsDescendingLimited() {
        let b = InMemoryCommunityBoard()
        b.post(Announcement(announcementId: "a1", groupId: "g1", title: "T1", body: "b", atUtc: Date(timeIntervalSince1970: 10)))
        b.post(Announcement(announcementId: "a2", groupId: "g1", title: "T2", body: "b", atUtc: Date(timeIntervalSince1970: 30)))
        b.post(Announcement(announcementId: "a3", groupId: "g1", title: "T3", body: "b", atUtc: Date(timeIntervalSince1970: 20)))
        b.post(Announcement(announcementId: "a4", groupId: "g2", title: "Other", body: "b", atUtc: Date(timeIntervalSince1970: 99)))
        XCTAssertEqual(b.announcementsFor(groupId: "g1").map { $0.announcementId }, ["a2", "a3", "a1"])
        XCTAssertEqual(b.announcementsFor(groupId: "g1", limit: 1).map { $0.announcementId }, ["a2"])
    }

    func testOpportunitiesFutureOnlyAscending() {
        let b = InMemoryCommunityBoard()
        b.list(VolunteerOpportunity(oppId: "o2", groupId: "g1", description: "later", volunteersNeeded: 3, whenUtc: Date.distantFuture))
        b.list(VolunteerOpportunity(oppId: "o1", groupId: "g1", description: "soon", volunteersNeeded: 2, whenUtc: Date().addingTimeInterval(3600)))
        b.list(VolunteerOpportunity(oppId: "o0", groupId: "g1", description: "past", volunteersNeeded: 1, whenUtc: Date.distantPast))
        XCTAssertEqual(b.opportunities().map { $0.oppId }, ["o1", "o2"])
    }

    func testDomainContext() {
        XCTAssertTrue(CommunityDomainContext.systemPromptSnippet.contains("[DOMAIN: Community]"))
        XCTAssertEqual(CommunityDomainContext.complianceFlags, ["NPO_Act", "Fundraising_Act", "POPIA"])
        XCTAssertEqual(CommunityDomainContext.suggestedTools, ["event_manager", "document_editor", "communication_tools", "volunteer_tracker"])
    }
}
