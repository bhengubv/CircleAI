// AccessibilityBoardTests.swift
//
// Exercises the Accessibility records/enum Codable round-trips and the
// deterministic behaviour of InMemoryAccessibilityBoard — profile storage and
// the ordered adaptation-hint derivation (contrast, motion, aria, text-scale
// formatted to 2 dp, then one hint per need). Also checks the
// AccessibilityDomainContext constants. Mirrors CircleAI.Accessibility/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class AccessibilityBoardTests: XCTestCase {

    func testAccessibilityNeedCodableRoundTrip() throws {
        for n in AccessibilityNeed.allCases {
            XCTAssertEqual(try JSONDecoder().decode(AccessibilityNeed.self, from: try JSONEncoder().encode(n)), n)
        }
        XCTAssertEqual(AccessibilityNeed.visual.rawValue, "Visual")
        XCTAssertEqual(AccessibilityNeed.cognitive.rawValue, "Cognitive")
    }

    func testProfileCodableRoundTrip() throws {
        let p = UserAccessibilityProfile(userId: "u1", needs: [.visual, .motor], textScale: 1.5, highContrast: true, reducedMotion: false, screenReader: true)
        XCTAssertEqual(try JSONDecoder().decode(UserAccessibilityProfile.self, from: try JSONEncoder().encode(p)), p)
    }

    func testNoProfileYieldsNoHints() {
        let b = InMemoryAccessibilityBoard()
        XCTAssertTrue(b.hintsFor(userId: "nobody").isEmpty)
    }

    func testHintsOrderAndTextScaleFormatting() {
        let b = InMemoryAccessibilityBoard()
        b.setProfile(UserAccessibilityProfile(userId: "u1", needs: [.visual, .hearing], textScale: 1.5, highContrast: true, reducedMotion: true, screenReader: true))
        XCTAssertEqual(b.getProfile(userId: "u1")?.textScale, 1.5)
        let hints = b.hintsFor(userId: "u1")
        XCTAssertEqual(hints, [
            AdaptationHint(kind: "contrast", value: "high"),
            AdaptationHint(kind: "motion", value: "reduced"),
            AdaptationHint(kind: "aria", value: "verbose"),
            AdaptationHint(kind: "text-scale", value: "1.50"),
            AdaptationHint(kind: "need", value: "Visual"),
            AdaptationHint(kind: "need", value: "Hearing")
        ])
    }

    func testTextScaleAtOrBelowOneIsOmitted() {
        let b = InMemoryAccessibilityBoard()
        b.setProfile(UserAccessibilityProfile(userId: "u2", needs: [], textScale: 1.0, highContrast: false, reducedMotion: false, screenReader: false))
        XCTAssertTrue(b.hintsFor(userId: "u2").isEmpty)
    }

    func testDomainContext() {
        XCTAssertTrue(AccessibilityDomainContext.systemPromptSnippet.contains("[DOMAIN: Accessibility]"))
        XCTAssertEqual(AccessibilityDomainContext.complianceFlags, ["WCAG_2_2", "UNCRPD", "Equality_Act", "POPIA"])
        XCTAssertEqual(AccessibilityDomainContext.suggestedTools, ["screen_reader_test", "document_editor", "web_audit", "analytics"])
    }
}
