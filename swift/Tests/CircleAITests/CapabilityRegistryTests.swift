// CapabilityRegistryTests.swift
//
// Verifies ExternalCapabilityRegistry (CapabilityRegistry.swift): entry count,
// order, case-insensitive lookup, by-package filtering, and a couple of spot
// checks against the C# reference.

import XCTest
@testable import CircleAI

final class CapabilityRegistryTests: XCTestCase {

    func testEntryCount() {
        // The C# reference registers exactly 30 capabilities.
        XCTAssertEqual(ExternalCapabilityRegistry.all.count, 30)
    }

    func testFirstAndLastEntryOrder() {
        XCTAssertEqual(ExternalCapabilityRegistry.all.first?.id, "claude-mem")
        XCTAssertEqual(ExternalCapabilityRegistry.all.last?.id, "awesome-design-md")
    }

    func testFindCaseInsensitive() {
        let a = ExternalCapabilityRegistry.find("HippoRAG")
        let b = ExternalCapabilityRegistry.find("hipporag")
        XCTAssertNotNil(a)
        XCTAssertEqual(a?.id, b?.id)
        XCTAssertEqual(a?.repo, "OSU-NLP-Group/HippoRAG")
        XCTAssertEqual(a?.targetPackage, "CircleAI.Memory.HippoRAG")
    }

    func testFindUnknownReturnsNil() {
        XCTAssertNil(ExternalCapabilityRegistry.find("does-not-exist"))
    }

    func testByPackageGroupsMultiple() {
        // Two capabilities target CircleAI.Speech: Amphion and yapsnap.
        let speech = ExternalCapabilityRegistry.byPackage("CircleAI.Speech")
        XCTAssertEqual(Set(speech.map { $0.id }), ["Amphion", "yapsnap"])
    }

    func testByPackageCaseInsensitive() {
        let games = ExternalCapabilityRegistry.byPackage("circleai.games")
        XCTAssertEqual(Set(games.map { $0.id }), ["aimangastudio", "flame"])
    }

    func testValueBulletsPreserved() {
        let cm = ExternalCapabilityRegistry.find("claude-mem")
        XCTAssertEqual(cm?.valueBullets.count, 10)
        XCTAssertEqual(cm?.valueBullets.first, "Multi-platform memory adapter")
        XCTAssertEqual(cm?.license, "MIT")
        XCTAssertEqual(cm?.strategy, "pattern-port")
    }

    func testLicensesPresent() {
        // Spot-check the non-MIT licenses land correctly.
        XCTAssertEqual(ExternalCapabilityRegistry.find("gstack")?.license, "Apache-2.0")
        XCTAssertEqual(ExternalCapabilityRegistry.find("awesome-design-md")?.license, "CC-BY-4.0")
    }
}
