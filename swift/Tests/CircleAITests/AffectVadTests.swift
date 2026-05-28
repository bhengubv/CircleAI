// AffectVadTests.swift
//
// Validates AffectVad.from(AffectState) — the canonical 5-dim → VAD projection.
// Both inline vectors (per the porting brief) and the cross-language fixture
// at fixtures/affect_vad_derivation.json are exercised here.

import XCTest
import Foundation
@testable import CircleAI

final class AffectVadTests: XCTestCase {

    private let epsilon: Float = 1e-5

    // ── Helpers ──────────────────────────────────────────────────────────────

    private let fixturesDir: URL = {
        URL(fileURLWithPath: #file)
            .deletingLastPathComponent()   // Tests/CircleAITests/
            .deletingLastPathComponent()   // Tests/
            .deletingLastPathComponent()   // swift/
            .deletingLastPathComponent()   // CircleAI/  (repo root)
            .appendingPathComponent("fixtures")
    }()

    private func makeAffect(
        curiosity: Float, engagement: Float, uncertainty: Float,
        rapport: Float, energy: Float
    ) -> AffectState {
        let s = AffectState(userId: "vad-test")
        s.curiosity   = curiosity
        s.engagement  = engagement
        s.uncertainty = uncertainty
        s.rapport     = rapport
        s.energy      = energy
        return s
    }

    private func assertVad(
        _ vad: AffectVad,
        valence: Float, arousal: Float, dominance: Float,
        id: String,
        file: StaticString = #file, line: UInt = #line
    ) {
        XCTAssertEqual(vad.valence,   valence,   accuracy: epsilon,
                       "\(id).valence",   file: file, line: line)
        XCTAssertEqual(vad.arousal,   arousal,   accuracy: epsilon,
                       "\(id).arousal",   file: file, line: line)
        XCTAssertEqual(vad.dominance, dominance, accuracy: epsilon,
                       "\(id).dominance", file: file, line: line)
    }

    // ── Inline vectors from the porting brief ────────────────────────────────

    func testDefaultState() {
        let s = makeAffect(curiosity: 0.5, engagement: 0.5, uncertainty: 0.2,
                           rapport: 0.0, energy: 0.5)
        assertVad(AffectVad.from(s),
                  valence: 0.43333333, arousal: 0.425, dominance: 0.65,
                  id: "default_state")
    }

    func testAllMax() {
        let s = makeAffect(curiosity: 1.0, engagement: 1.0, uncertainty: 0.0,
                           rapport: 1.0, energy: 1.0)
        assertVad(AffectVad.from(s),
                  valence: 1.0, arousal: 0.75, dominance: 1.0,
                  id: "all_max")
    }

    func testAllMinHighUncertainty() {
        let s = makeAffect(curiosity: 0.0, engagement: 0.0, uncertainty: 1.0,
                           rapport: 0.0, energy: 0.0)
        assertVad(AffectVad.from(s),
                  valence: 0.0, arousal: 0.25, dominance: 0.0,
                  id: "all_min_high_uncertainty")
    }

    func testHighEngagementWarm() {
        let s = makeAffect(curiosity: 0.6, engagement: 0.9, uncertainty: 0.1,
                           rapport: 0.8, energy: 0.7)
        assertVad(AffectVad.from(s),
                  valence: 0.86666667, arousal: 0.525, dominance: 0.9,
                  id: "high_engagement_warm")
    }

    func testStressedLowEnergy() {
        let s = makeAffect(curiosity: 0.3, engagement: 0.2, uncertainty: 0.8,
                           rapport: 0.0, energy: 0.2)
        assertVad(AffectVad.from(s),
                  valence: 0.13333333, arousal: 0.375, dominance: 0.2,
                  id: "stressed_low_energy")
    }

    func testEnergeticCurious() {
        let s = makeAffect(curiosity: 0.9, engagement: 0.6, uncertainty: 0.3,
                           rapport: 0.4, energy: 0.9)
        assertVad(AffectVad.from(s),
                  valence: 0.56666667, arousal: 0.75, dominance: 0.65,
                  id: "energetic_curious")
    }

    // ── Extension method parity ──────────────────────────────────────────────

    func testToVadExtensionMatchesStaticFactory() {
        let s = makeAffect(curiosity: 0.4, engagement: 0.7, uncertainty: 0.3,
                           rapport: 0.5, energy: 0.6)
        XCTAssertEqual(s.toVad(), AffectVad.from(s))
    }

    func testEquatable() {
        let a = AffectVad.from(makeAffect(curiosity: 0.5, engagement: 0.5,
                                          uncertainty: 0.2, rapport: 0.0, energy: 0.5))
        let b = AffectVad.from(makeAffect(curiosity: 0.5, engagement: 0.5,
                                          uncertainty: 0.2, rapport: 0.0, energy: 0.5))
        XCTAssertEqual(a, b)
    }

    // ── Cross-language fixture ───────────────────────────────────────────────
    // Walks every vector in fixtures/affect_vad_derivation.json and confirms
    // the Swift implementation matches the canonical math byte-identically.

    func testAllFixtureVectors() throws {
        let url = fixturesDir.appendingPathComponent("affect_vad_derivation.json")
        let data = try Data(contentsOf: url)
        let json = try JSONSerialization.jsonObject(with: data) as! [String: Any]

        let fixtureEpsilon = Float(truncating: json["epsilon"] as! NSNumber)
        let vectors = json["vectors"] as! [[String: Any]]

        for vec in vectors {
            let id       = vec["id"]       as! String
            let input    = vec["input"]    as! [String: Any]
            let expected = vec["expected"] as! [String: Any]

            let state = makeAffect(
                curiosity:   Float(truncating: input["curiosity"]   as! NSNumber),
                engagement:  Float(truncating: input["engagement"]  as! NSNumber),
                uncertainty: Float(truncating: input["uncertainty"] as! NSNumber),
                rapport:     Float(truncating: input["rapport"]     as! NSNumber),
                energy:      Float(truncating: input["energy"]      as! NSNumber)
            )

            let vad = AffectVad.from(state)

            let expV = Float(truncating: expected["valence"]   as! NSNumber)
            let expA = Float(truncating: expected["arousal"]   as! NSNumber)
            let expD = Float(truncating: expected["dominance"] as! NSNumber)

            XCTAssertEqual(vad.valence,   expV, accuracy: fixtureEpsilon,
                           "\(id).valence: expected \(expV), got \(vad.valence)")
            XCTAssertEqual(vad.arousal,   expA, accuracy: fixtureEpsilon,
                           "\(id).arousal: expected \(expA), got \(vad.arousal)")
            XCTAssertEqual(vad.dominance, expD, accuracy: fixtureEpsilon,
                           "\(id).dominance: expected \(expD), got \(vad.dominance)")
        }
    }
}
