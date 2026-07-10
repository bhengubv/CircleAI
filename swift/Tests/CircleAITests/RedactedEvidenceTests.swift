// RedactedEvidenceTests.swift
//
// Validates RedactedEvidenceJsonConverter ported from
// RedactedEvidenceJsonConverter.cs and the redacting Encodable conformance on
// AnomalySignal — evidence VALUES are hashed (sha256:hex) on serialisation while
// KEYS are preserved; raw values never appear in the JSON.

import XCTest
import Foundation
import CryptoKit
@testable import CircleAI

final class RedactedEvidenceTests: XCTestCase {

    private func sha256HexLower(_ s: String) -> String {
        SHA256.hash(data: Data(s.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    // ── hashRedacted ─────────────────────────────────────────────────────────

    func testHashRedactedProducesPrefixedLowerHex() {
        let out = RedactedEvidenceJsonConverter.hashRedacted("session-token-abc")
        XCTAssertEqual(out, "sha256:" + sha256HexLower("session-token-abc"))
        XCTAssertTrue(out.hasPrefix("sha256:"))
    }

    func testHashRedactedEmptyAndNil() {
        XCTAssertEqual(RedactedEvidenceJsonConverter.hashRedacted(""), "sha256:")
        XCTAssertEqual(RedactedEvidenceJsonConverter.hashRedacted(nil), "sha256:")
    }

    func testHashRedactedIsLowercaseHex() {
        let out = RedactedEvidenceJsonConverter.hashRedacted("X")
        let hexPart = String(out.dropFirst("sha256:".count))
        XCTAssertEqual(hexPart, hexPart.lowercased())
        XCTAssertEqual(hexPart.count, 64) // 32 bytes hex
    }

    // ── redact map ───────────────────────────────────────────────────────────

    func testRedactPreservesKeysHashesValues() {
        let evidence = ["token": "secret-123", "pid": "4242"]
        let redacted = RedactedEvidenceJsonConverter.redact(evidence)
        XCTAssertEqual(Set(redacted.keys), Set(evidence.keys))
        XCTAssertEqual(redacted["token"], "sha256:" + sha256HexLower("secret-123"))
        XCTAssertEqual(redacted["pid"], "sha256:" + sha256HexLower("4242"))
        // Raw values are gone.
        XCTAssertFalse(redacted.values.contains("secret-123"))
    }

    func testDecodeToEmptyIsEmpty() {
        XCTAssertTrue(RedactedEvidenceJsonConverter.decodeToEmpty().isEmpty)
    }

    // ── AnomalySignal Encodable redaction ────────────────────────────────────

    func testEncodingAnomalySignalRedactsEvidenceValues() throws {
        let signal = AnomalySignal.create(
            vector: .stateCorruption, confidence: 0.8,
            affectedModule: "CircleAI.Memory", description: "state mutated",
            evidence: ["secret": "leak-me", "callSite": "AffectState.apply"])
        let data = try JSONEncoder().encode(signal)
        let json = String(decoding: data, as: UTF8.self)

        // Raw evidence value must NOT be present.
        XCTAssertFalse(json.contains("leak-me"))
        XCTAssertFalse(json.contains("AffectState.apply"))
        // Its hashed form MUST be present.
        XCTAssertTrue(json.contains("sha256:" + sha256HexLower("leak-me")))
        XCTAssertTrue(json.contains("sha256:" + sha256HexLower("AffectState.apply")))
        // Keys are preserved.
        XCTAssertTrue(json.contains("secret"))
        XCTAssertTrue(json.contains("callSite"))
    }

    func testEncodingAnomalySignalUsesPascalCaseKeys() throws {
        let signal = AnomalySignal.create(
            vector: .memoryAnomaly, confidence: 0.5,
            affectedModule: "m", description: "d")
        let data = try JSONEncoder().encode(signal)
        let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        XCTAssertNotNil(obj?["Id"])
        XCTAssertNotNil(obj?["Vector"])
        XCTAssertNotNil(obj?["Confidence"])
        XCTAssertNotNil(obj?["AffectedModule"])
        XCTAssertNotNil(obj?["Description"])
        XCTAssertNotNil(obj?["Evidence"])
        XCTAssertNotNil(obj?["DetectedAt"])
    }

    func testEncodingEmptyEvidenceYieldsEmptyObject() throws {
        let signal = AnomalySignal.create(
            vector: .unknown, confidence: 0.5, affectedModule: "m", description: "d")
        let data = try JSONEncoder().encode(signal)
        let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        let evidence = obj?["Evidence"] as? [String: Any]
        XCTAssertEqual(evidence?.count, 0)
    }
}
