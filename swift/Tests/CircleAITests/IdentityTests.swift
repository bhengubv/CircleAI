// IdentityTests.swift
// Tests IdentityTier enum, CircleIdentity/RegisteredDevice struct construction,
// and validates 3 fixture examples from fixtures/identity.json.

import XCTest
import Foundation
@testable import CircleAI

final class IdentityTests: XCTestCase {

    private let fixturesDir: URL = {
        URL(fileURLWithPath: #file)
            .deletingLastPathComponent()   // Tests/CircleAITests/
            .deletingLastPathComponent()   // Tests/
            .deletingLastPathComponent()   // swift/
            .deletingLastPathComponent()   // CircleAI/ (repo root)
            .appendingPathComponent("fixtures")
    }()

    // ── IdentityTier ─────────────────────────────────────────────────────────

    func testTierCases() {
        // Fixture asserts tier order: Anonymous, Pseudonymous, Verified
        let cases = IdentityTier.allCases
        XCTAssertEqual(cases.count, 3)
        XCTAssertTrue(cases.contains(.anonymous))
        XCTAssertTrue(cases.contains(.pseudonymous))
        XCTAssertTrue(cases.contains(.verified))
    }

    func testTierRawValues() {
        XCTAssertEqual(IdentityTier.anonymous.rawValue,    "anonymous")
        XCTAssertEqual(IdentityTier.pseudonymous.rawValue, "pseudonymous")
        XCTAssertEqual(IdentityTier.verified.rawValue,     "verified")
    }

    // ── CircleIdentity construction ───────────────────────────────────────────

    func testCircleIdentityInit() {
        let now = Date()
        let identity = CircleIdentity(
            identityId: "test-id",
            displayName: "Test User",
            preferredLanguage: "en",
            tier: .pseudonymous,
            deviceIds: ["dev-1", "dev-2"],
            createdAt: now,
            lastSeenAt: now
        )
        XCTAssertEqual(identity.identityId,        "test-id")
        XCTAssertEqual(identity.displayName,       "Test User")
        XCTAssertEqual(identity.preferredLanguage, "en")
        XCTAssertEqual(identity.tier,              .pseudonymous)
        XCTAssertEqual(identity.deviceIds.count,   2)
        XCTAssertEqual(identity.deviceIds[0],      "dev-1")
    }

    func testCircleIdentityNilLanguage() {
        let identity = CircleIdentity(
            identityId: "x",
            displayName: "Guest",
            preferredLanguage: nil,
            tier: .anonymous,
            deviceIds: [],
            createdAt: Date(),
            lastSeenAt: Date()
        )
        XCTAssertNil(identity.preferredLanguage)
    }

    // ── RegisteredDevice construction ─────────────────────────────────────────

    func testRegisteredDeviceInit() {
        let now = Date()
        let device = RegisteredDevice(
            deviceId: "d-001",
            identityId: "i-001",
            platform: "android",
            deviceName: "Pixel 8",
            registeredAt: now,
            lastActiveAt: now
        )
        XCTAssertEqual(device.deviceId,    "d-001")
        XCTAssertEqual(device.identityId,  "i-001")
        XCTAssertEqual(device.platform,    "android")
        XCTAssertEqual(device.deviceName,  "Pixel 8")
    }

    func testRegisteredDeviceNilName() {
        let device = RegisteredDevice(
            deviceId: "d-iot",
            identityId: "i-iot",
            platform: "iot",
            deviceName: nil,
            registeredAt: Date(),
            lastActiveAt: Date()
        )
        XCTAssertNil(device.deviceName)
    }

    // ── Fixture-driven example assertions ────────────────────────────────────

    func testFixtureExamples() throws {
        let url = fixturesDir.appendingPathComponent("identity.json")
        let data = try Data(contentsOf: url)
        let json = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        let examples = json["examples"] as! [[String: Any]]

        XCTAssertEqual(examples.count, 3, "Expected 3 fixture examples")

        // ── Example 0: verified_multi_device ────────────────────────────────
        let ex0 = examples[0]
        let id0 = ex0["identity"] as! [String: Any]
        XCTAssertEqual(ex0["id"] as? String, "verified_multi_device")
        XCTAssertEqual(id0["displayName"]       as? String, "Sipho Dlamini")
        XCTAssertEqual(id0["preferredLanguage"] as? String, "zu")
        XCTAssertEqual(id0["tier"]              as? String, "Verified")
        let deviceIds0 = id0["deviceIds"] as! [String]
        XCTAssertEqual(deviceIds0.count, 3)
        let devices0 = ex0["devices"] as! [[String: Any]]
        XCTAssertEqual(devices0.count, 3)
        let platforms0 = devices0.map { $0["platform"] as! String }
        XCTAssertTrue(platforms0.contains("android"))
        XCTAssertTrue(platforms0.contains("watch"))
        XCTAssertTrue(platforms0.contains("windows"))

        // ── Example 1: pseudonymous_single_device ────────────────────────────
        let ex1 = examples[1]
        let id1 = ex1["identity"] as! [String: Any]
        XCTAssertEqual(ex1["id"] as? String, "pseudonymous_single_device")
        XCTAssertEqual(id1["tier"] as? String, "Pseudonymous")
        XCTAssertEqual(id1["preferredLanguage"] as? String, "en")
        let deviceIds1 = id1["deviceIds"] as! [String]
        XCTAssertEqual(deviceIds1.count, 1)
        let devices1 = ex1["devices"] as! [[String: Any]]
        XCTAssertEqual(devices1[0]["platform"] as? String, "ios")

        // ── Example 2: anonymous_iot ────────────────────────────────────────
        let ex2 = examples[2]
        let id2 = ex2["identity"] as! [String: Any]
        XCTAssertEqual(ex2["id"] as? String, "anonymous_iot")
        XCTAssertEqual(id2["tier"] as? String, "Anonymous")
        XCTAssertNil(id2["preferredLanguage"] as? String)
        XCTAssertEqual(id2["displayName"] as? String, "Guest")
        let devices2 = ex2["devices"] as! [[String: Any]]
        XCTAssertEqual(devices2[0]["platform"] as? String, "iot")
        XCTAssertNil(devices2[0]["deviceName"] as? String)
    }

    // ── Valid platforms ───────────────────────────────────────────────────────

    func testAllFixturePlatformsAreKnown() throws {
        let url = fixturesDir.appendingPathComponent("identity.json")
        let data = try Data(contentsOf: url)
        let json = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        let knownPlatforms = Set(json["platforms"] as! [String])

        let examples = json["examples"] as! [[String: Any]]
        for ex in examples {
            let devices = ex["devices"] as! [[String: Any]]
            for device in devices {
                let platform = device["platform"] as! String
                XCTAssertTrue(knownPlatforms.contains(platform), "Unknown platform: \(platform)")
            }
        }
    }
}
