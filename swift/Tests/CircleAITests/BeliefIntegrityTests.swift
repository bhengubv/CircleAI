// BeliefIntegrityTests.swift
// Verifies attribution discipline (self/other/world) and SelfBeliefStore
// filtering, revision (supersede), correction (retract), and provenance. The
// headline guarantee: "my mother is diabetic" never becomes a user fact.

import XCTest
@testable import CircleAI

final class BeliefIntegrityTests: XCTestCase {
    private let ex = HeuristicBeliefExtractor()

    private func one(_ text: String) async throws -> PersonalBelief {
        let beliefs = try await ex.extract(text: text, source: "turn")
        XCTAssertEqual(beliefs.count, 1, "expected one belief from \"\(text)\"")
        return beliefs[0]
    }

    func testMyMotherOther() async throws {
        let b = try await one("my mother is diabetic")
        XCTAssertEqual(b.attribution, .other)
        XCTAssertEqual(b.subject, "mother")
        XCTAssertEqual(b.object, "diabetic")
    }

    func testIAmSelf() async throws {
        let b = try await one("i am vegetarian")
        XCTAssertEqual(b.attribution, .selfBelief)
        XCTAssertEqual(b.subject, "user")
        XCTAssertEqual(b.object, "vegetarian")
    }

    func testMyCarSelf() async throws {
        let b = try await one("my car is fast")
        XCTAssertEqual(b.attribution, .selfBelief)
        XCTAssertEqual(b.subject, "user")
    }

    func testBareRelationOther() async throws {
        let b = try await one("brother lives in Cape Town")
        XCTAssertEqual(b.attribution, .other)
        XCTAssertEqual(b.subject, "brother")
    }

    func testWorld() async throws {
        let b = try await one("paris is beautiful")
        XCTAssertEqual(b.attribution, .world)
        XCTAssertEqual(b.subject, "paris")
    }

    func testOnlySelfBecomeFacts() async throws {
        let store = SelfBeliefStore()
        for b in try await ex.extract(text: "my mother is diabetic", source: "t1") { store.record(b) }
        for b in try await ex.extract(text: "i am vegetarian", source: "t2") { store.record(b) }
        let facts = store.selfFacts()
        XCTAssertEqual(facts.count, 1)
        XCTAssertEqual(facts[0].object, "vegetarian")
        XCTAssertFalse(facts.contains { $0.object.contains("diabetic") })
        XCTAssertTrue(store.nonSelf().contains { $0.object == "diabetic" })
    }

    func testSupersede() {
        let store = SelfBeliefStore()
        func mk(_ obj: String) -> PersonalBelief {
            PersonalBelief(attribution: .selfBelief, subject: "user", predicate: "isAbout",
                           object: obj, confidence: 0.6, source: "t", recordedAt: Date())
        }
        store.record(mk("vegetarian"))
        store.record(mk("vegan"))
        let facts = store.selfFacts()
        XCTAssertEqual(facts.count, 1)
        XCTAssertEqual(facts[0].object, "vegan")
    }

    func testRetract() async throws {
        let store = SelfBeliefStore()
        for b in try await ex.extract(text: "i am vegetarian", source: "t1") { store.record(b) }
        let removed = store.retract(objectContains: "vegetarian")
        XCTAssertEqual(removed, 1)
        XCTAssertEqual(store.selfFacts().count, 0)
    }

    func testProvenance() {
        let store = SelfBeliefStore()
        func mk(_ obj: String, _ pred: String, _ src: String) -> PersonalBelief {
            PersonalBelief(attribution: .selfBelief, subject: "user", predicate: pred,
                           object: obj, confidence: 0.6, source: src, recordedAt: Date())
        }
        store.record(mk("vegetarian", "diet", "t1"))
        store.record(mk("hiking", "hobby", "t2"))
        XCTAssertEqual(store.provenance().sorted(), ["t1", "t2"])
    }
}
