// BiometricMatcherTests.swift
// Validates BiometricMatcher.cosineSimilarity and BiometricMatcher.isMatch
// against all vectors in fixtures/facex_biometric_vectors.json.
// Also covers FaceAffectMapper affect-mapper vectors from the same fixture.

import XCTest
import Foundation
@testable import CircleAI

final class BiometricMatcherTests: XCTestCase {

    private let fixturesDir: URL = {
        URL(fileURLWithPath: #file)
            .deletingLastPathComponent()   // Tests/CircleAITests/
            .deletingLastPathComponent()   // Tests/
            .deletingLastPathComponent()   // swift/
            .deletingLastPathComponent()   // CircleAI/ (repo root)
            .appendingPathComponent("fixtures")
    }()

    // ── Helpers ──────────────────────────────────────────────────────────────

    private func floats(_ any: Any) -> [Float] {
        return (any as! [NSNumber]).map { Float(truncating: $0) }
    }

    private func makeAffect(from dict: [String: Any]) -> AffectState {
        let s = AffectState(userId: "test")
        s.curiosity   = Float(truncating: dict["curiosity"]   as! NSNumber)
        s.engagement  = Float(truncating: dict["engagement"]  as! NSNumber)
        s.uncertainty = Float(truncating: dict["uncertainty"] as! NSNumber)
        s.rapport     = Float(truncating: dict["rapport"]     as! NSNumber)
        s.energy      = Float(truncating: dict["energy"]      as! NSNumber)
        return s
    }

    private func makeLandmarks136() -> [Float] {
        // 68 (x,y) pairs all set to 0.5 — valid placeholder
        return [Float](repeating: 0.5, count: 136)
    }

    // ── CosineSimilarity — fixture-driven ────────────────────────────────────

    func testCosineSimilarityFixture() throws {
        let url = fixturesDir.appendingPathComponent("facex_biometric_vectors.json")
        let data = try Data(contentsOf: url)
        let json = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        let vectors = json["cosine_similarity_vectors"] as! [[String: Any]]

        for vec in vectors {
            let id        = vec["id"]                  as! String
            let a         = floats(vec["a"]!)
            let b         = floats(vec["b"]!)
            let expected  = Double(truncating: vec["expected_similarity"] as! NSNumber)
            let tolerance = Double(truncating: vec["tolerance"]           as! NSNumber)

            let actual = BiometricMatcher.cosineSimilarity(a, b)
            XCTAssertEqual(actual, expected, accuracy: tolerance,
                           "cosineSimilarity mismatch for vector '\(id)': expected \(expected), got \(actual)")

            // Validate isMatch when the fixture provides it
            if let expectedMatch = vec["expected_is_match_at_threshold_0_85"] as? Bool {
                let threshold = Float(json["match_threshold_default"] as! NSNumber)
                let profile = BiometricProfile(
                    identityId: "fixture",
                    embeddingVector: b,
                    matchThreshold: threshold,
                    enrolledAt: Date()
                )
                let matched = BiometricMatcher.isMatch(a, against: profile)
                XCTAssertEqual(matched, expectedMatch,
                               "isMatch mismatch for vector '\(id)': expected \(expectedMatch), got \(matched)")
            }
        }
    }

    // ── CosineSimilarity — inline unit tests ─────────────────────────────────

    func testIdentical() {
        let v: [Float] = [0.6, 0.8]
        XCTAssertEqual(BiometricMatcher.cosineSimilarity(v, v), 1.0, accuracy: 1e-5)
    }

    func testOrthogonal() {
        XCTAssertEqual(BiometricMatcher.cosineSimilarity([1.0, 0.0], [0.0, 1.0]), 0.0, accuracy: 1e-5)
    }

    func testOpposite() {
        XCTAssertEqual(BiometricMatcher.cosineSimilarity([1.0, 0.0], [-1.0, 0.0]), -1.0, accuracy: 1e-5)
    }

    func testSameFace4D() {
        let a: [Float] = [0.5257, 0.7236, 0.2425, 0.3780]
        let b: [Float] = [0.5133, 0.7340, 0.2511, 0.3692]
        XCTAssertEqual(BiometricMatcher.cosineSimilarity(a, b), 0.999794, accuracy: 1e-4)
    }

    func testDifferentFace4D() {
        let a: [Float] = [0.5257,  0.7236,  0.2425,  0.3780]
        let b: [Float] = [-0.3015, 0.6547,  0.5893, -0.3812]
        XCTAssertEqual(BiometricMatcher.cosineSimilarity(a, b), 0.311911, accuracy: 1e-4)
    }

    func testEmptyVectorsReturn0() {
        XCTAssertEqual(BiometricMatcher.cosineSimilarity([], []), 0.0, accuracy: 1e-10)
    }

    func testMismatchedLengthsReturn0() {
        XCTAssertEqual(BiometricMatcher.cosineSimilarity([1.0], [1.0, 2.0]), 0.0, accuracy: 1e-10)
    }

    func testZeroVectorReturn0() {
        XCTAssertEqual(BiometricMatcher.cosineSimilarity([0.0, 0.0], [0.0, 0.0]), 0.0, accuracy: 1e-10)
    }

    // ── isMatch ───────────────────────────────────────────────────────────────

    func testIsMatchAboveThreshold() {
        let enrolled: [Float] = [0.6, 0.8]
        let candidate: [Float] = [0.6, 0.8]
        let profile = BiometricProfile(
            identityId: "user-1",
            embeddingVector: enrolled,
            matchThreshold: 0.85,
            enrolledAt: Date()
        )
        XCTAssertTrue(BiometricMatcher.isMatch(candidate, against: profile))
    }

    func testIsMatchBelowThreshold() {
        let a: [Float] = [0.5257,  0.7236,  0.2425,  0.3780]
        let b: [Float] = [-0.3015, 0.6547,  0.5893, -0.3812]
        let profile = BiometricProfile(
            identityId: "user-2",
            embeddingVector: b,
            matchThreshold: 0.85,
            enrolledAt: Date()
        )
        XCTAssertFalse(BiometricMatcher.isMatch(a, against: profile))
    }

    func testIsMatchExactlyAtThreshold() {
        // Identical vectors → similarity 1.0 ≥ 0.85
        let v: [Float] = [0.7071, 0.7071]
        let profile = BiometricProfile(
            identityId: "user-3",
            embeddingVector: v,
            matchThreshold: 0.85,
            enrolledAt: Date()
        )
        XCTAssertTrue(BiometricMatcher.isMatch(v, against: profile))
    }

    // ── FaceAffectMapper — fixture-driven ────────────────────────────────────

    func testAffectMapperFixture() throws {
        let url = fixturesDir.appendingPathComponent("facex_biometric_vectors.json")
        let data = try Data(contentsOf: url)
        let json = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        let vectors = json["affect_mapper_vectors"] as! [[String: Any]]

        let epsilon: Float = 1e-5

        for vec in vectors {
            let id         = vec["id"]              as! String
            let initialRaw = vec["initial_affect"]  as! [String: Any]
            let confidence = Float(truncating: vec["confidence"] as! NSNumber)
            let expRaw     = vec["expression"]      as! String
            let expectedRaw = vec["expected_affect"] as! [String: Any]

            let affect = makeAffect(from: initialRaw)

            // Build a FacialMetricMatrix with matching expression and confidence
            let expression = faceExpression(from: expRaw)
            let matrix = FacialMetricMatrix(
                landmarks: makeLandmarks136(),
                boundingBox: FaceBoundingBox(x: 0.2, y: 0.1, width: 0.4, height: 0.5),
                expression: expression,
                confidenceScore: confidence
            )

            FaceAffectMapper.apply(matrix, to: affect)

            let ec = Float(truncating: expectedRaw["curiosity"]   as! NSNumber)
            let ee = Float(truncating: expectedRaw["engagement"]  as! NSNumber)
            let eu = Float(truncating: expectedRaw["uncertainty"] as! NSNumber)
            let er = Float(truncating: expectedRaw["rapport"]     as! NSNumber)
            let en = Float(truncating: expectedRaw["energy"]      as! NSNumber)

            XCTAssertTrue(abs(affect.curiosity   - ec) < epsilon, "\(id).curiosity: expected \(ec), got \(affect.curiosity)")
            XCTAssertTrue(abs(affect.engagement  - ee) < epsilon, "\(id).engagement: expected \(ee), got \(affect.engagement)")
            XCTAssertTrue(abs(affect.uncertainty - eu) < epsilon, "\(id).uncertainty: expected \(eu), got \(affect.uncertainty)")
            XCTAssertTrue(abs(affect.rapport     - er) < epsilon, "\(id).rapport: expected \(er), got \(affect.rapport)")
            XCTAssertTrue(abs(affect.energy      - en) < epsilon, "\(id).energy: expected \(en), got \(affect.energy)")
        }
    }

    // ── FaceAffectMapper — inline unit tests ─────────────────────────────────

    func testHappyDelta() {
        let affect = AffectState()
        let matrix = FacialMetricMatrix(
            landmarks: makeLandmarks136(),
            boundingBox: FaceBoundingBox(x: 0, y: 0, width: 1, height: 1),
            expression: .happy,
            confidenceScore: 0.9
        )
        FaceAffectMapper.apply(matrix, to: affect)
        XCTAssertEqual(affect.engagement, 0.53, accuracy: 1e-5)
        XCTAssertEqual(affect.energy,     0.52, accuracy: 1e-5)
    }

    func testConfusedDelta() {
        let affect = AffectState()
        let matrix = FacialMetricMatrix(
            landmarks: makeLandmarks136(),
            boundingBox: FaceBoundingBox(x: 0, y: 0, width: 1, height: 1),
            expression: .confused,
            confidenceScore: 0.75
        )
        FaceAffectMapper.apply(matrix, to: affect)
        XCTAssertEqual(affect.uncertainty, 0.25, accuracy: 1e-5)
    }

    func testLowConfidenceDiscarded() {
        let affect = AffectState()
        let before = (affect.engagement, affect.uncertainty, affect.energy)
        let matrix = FacialMetricMatrix(
            landmarks: makeLandmarks136(),
            boundingBox: FaceBoundingBox(x: 0, y: 0, width: 1, height: 1),
            expression: .stressed,
            confidenceScore: 0.49
        )
        FaceAffectMapper.apply(matrix, to: affect)
        XCTAssertEqual(affect.engagement,  before.0, accuracy: 1e-5)
        XCTAssertEqual(affect.uncertainty, before.1, accuracy: 1e-5)
        XCTAssertEqual(affect.energy,      before.2, accuracy: 1e-5)
    }

    // ── FaceCompanionBridge ───────────────────────────────────────────────────

    func testBridgeConfusionEventFired() {
        // Set uncertainty just below threshold, use .confused to push it over
        let affect = AffectState()
        affect.uncertainty = 0.67          // 0.67 + 0.05 = 0.72 ≥ 0.70

        let matrix = FacialMetricMatrix(
            landmarks: makeLandmarks136(),
            boundingBox: FaceBoundingBox(x: 0, y: 0, width: 1, height: 1),
            expression: .confused,
            confidenceScore: 0.8
        )
        let event = FaceCompanionBridge.observe(
            matrix,
            affect: affect,
            sessionId: "sess-1",
            identityId: "user-1",
            surface: .mobile
        )
        XCTAssertNotNil(event, "Expected confusion event to be emitted")
        XCTAssertEqual(event?.triggerName, "face.confusion_detected")
        XCTAssertTrue(event?.message.contains("tricky") ?? false)
        XCTAssertEqual(event?.interface, .mobile)
    }

    func testBridgeNoEventWhenBelowThreshold() {
        let affect = AffectState()
        affect.uncertainty = 0.5           // 0.5 + 0.05 = 0.55 < 0.70

        let matrix = FacialMetricMatrix(
            landmarks: makeLandmarks136(),
            boundingBox: FaceBoundingBox(x: 0, y: 0, width: 1, height: 1),
            expression: .confused,
            confidenceScore: 0.8
        )
        let event = FaceCompanionBridge.observe(
            matrix,
            affect: affect,
            sessionId: "sess-2",
            identityId: "user-2",
            surface: .desktop
        )
        XCTAssertNil(event, "Expected no event when uncertainty stays below threshold")
    }

    func testBridgeNoEventForHappyExpression() {
        // Happy expression should not trigger a confusion event even with high uncertainty
        let affect = AffectState()
        affect.uncertainty = 0.90

        let matrix = FacialMetricMatrix(
            landmarks: makeLandmarks136(),
            boundingBox: FaceBoundingBox(x: 0, y: 0, width: 1, height: 1),
            expression: .happy,
            confidenceScore: 0.95
        )
        let event = FaceCompanionBridge.observe(
            matrix,
            affect: affect,
            sessionId: "sess-3",
            identityId: "user-3",
            surface: .mobile
        )
        XCTAssertNil(event, "Happy expression must not trigger confusion event")
    }

    // ── FacialMetricMatrix construction ──────────────────────────────────────

    func testFacialMetricMatrixGetLandmark() {
        var lms = [Float](repeating: 0.0, count: 136)
        // landmark 5 → indices 10, 11
        lms[10] = 0.25
        lms[11] = 0.75
        let matrix = FacialMetricMatrix(
            landmarks: lms,
            boundingBox: FaceBoundingBox(x: 0, y: 0, width: 1, height: 1),
            expression: .neutral,
            confidenceScore: 0.99
        )
        let pt = matrix.getLandmark(at: 5)
        XCTAssertEqual(pt.x, 0.25, accuracy: 1e-6)
        XCTAssertEqual(pt.y, 0.75, accuracy: 1e-6)
    }

    func testFacialMetricMatrixLandmarkCount() {
        let matrix = FacialMetricMatrix(
            landmarks: makeLandmarks136(),
            boundingBox: FaceBoundingBox(x: 0, y: 0, width: 1, height: 1),
            expression: .neutral,
            confidenceScore: 0.5
        )
        XCTAssertEqual(matrix.landmarks.count, 136)
    }

    // ── BiometricProfile ──────────────────────────────────────────────────────

    func testBiometricProfileDimension() {
        let p = BiometricProfile(
            identityId: "user-1",
            embeddingVector: [Float](repeating: 0.1, count: 512),
            enrolledAt: Date()
        )
        XCTAssertEqual(p.embeddingDimension, 512)
    }

    func testBiometricProfileDefaultThreshold() {
        let p = BiometricProfile(
            identityId: "x",
            embeddingVector: [0.6, 0.8],
            enrolledAt: Date()
        )
        XCTAssertEqual(p.matchThreshold, 0.85, accuracy: 1e-6)
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private func faceExpression(from raw: String) -> FaceExpressionClassification {
        switch raw {
        case "Happy":     return .happy
        case "Surprised": return .surprised
        case "Confused":  return .confused
        case "Stressed":  return .stressed
        case "Angry":     return .angry
        case "Neutral":   return .neutral
        case "Sad":       return .sad
        default:          return .unknown
        }
    }
}
