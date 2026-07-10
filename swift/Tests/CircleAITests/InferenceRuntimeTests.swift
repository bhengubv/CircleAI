// InferenceRuntimeTests.swift
//
// KvCompressionMode + applier, PowerBudgetPolicy budget→knob mapping,
// VisionInput, and the ChatCapability.video flag.

import XCTest
@testable import CircleAI

final class InferenceRuntimeTests: XCTestCase {

    // MARK: - KvCompression

    func testKvApplierRecordsAndReadsBackMode() {
        let applier = InMemoryKvCompressionApplier()
        XCTAssertEqual(applier.get(), .off)
        XCTAssertEqual(applier.set(.turboQuant4Bit), .applied)
        XCTAssertEqual(applier.get(), .turboQuant4Bit)
        XCTAssertEqual(applier.set(.turboQuant2Bit), .applied)
        XCTAssertEqual(applier.get(), .turboQuant2Bit)
    }

    func testKvApplierInvalidHandleReturnsHandleInvalidAndReadsOff() {
        let applier = InMemoryKvCompressionApplier(handleValid: false)
        XCTAssertEqual(applier.set(.turboQuant3Bit), .handleInvalid)
        XCTAssertEqual(applier.get(), .off)
    }

    func testKvModeRawValuesMatchCAbi() {
        XCTAssertEqual(KvCompressionMode.off.rawValue, 0)
        XCTAssertEqual(KvCompressionMode.turboQuant4Bit.rawValue, 1)
        XCTAssertEqual(KvCompressionMode.turboQuant3Bit.rawValue, 2)
        XCTAssertEqual(KvCompressionMode.turboQuant2Bit.rawValue, 3)
    }

    // MARK: - PowerBudgetPolicy

    func testResolveNoneHonoursRequestedTokens() {
        let r = PowerBudgetPolicy.resolve(budget: .none, requestedMaxTokens: 5000)
        XCTAssertEqual(r.maxTokens, 5000)
        XCTAssertEqual(r.preferredKvMode, .turboQuant4Bit)
        XCTAssertFalse(r.preferSmallerModelInChain)
    }

    func testResolveLowCapsAt64AndPrefersSmaller() {
        let r = PowerBudgetPolicy.resolve(budget: .low, requestedMaxTokens: 512)
        XCTAssertEqual(r.maxTokens, 64)
        XCTAssertEqual(r.preferredKvMode, .turboQuant4Bit)
        XCTAssertTrue(r.preferSmallerModelInChain)
    }

    func testResolveNormalCapsAt512() {
        XCTAssertEqual(PowerBudgetPolicy.resolve(budget: .normal, requestedMaxTokens: 2000).maxTokens, 512)
        XCTAssertEqual(PowerBudgetPolicy.resolve(budget: .normal, requestedMaxTokens: 100).maxTokens, 100)
    }

    func testResolveHighCapsAt2048WithFullKv() {
        let r = PowerBudgetPolicy.resolve(budget: .high, requestedMaxTokens: 9999)
        XCTAssertEqual(r.maxTokens, 2048)
        XCTAssertEqual(r.preferredKvMode, .off)
    }

    func testNormalAutoDowngradesToLowBelow15Battery() {
        let r = PowerBudgetPolicy.resolve(budget: .normal, requestedMaxTokens: 512, batteryLevelPercent: 10)
        XCTAssertEqual(r.maxTokens, 64) // Low
        XCTAssertTrue(r.preferSmallerModelInChain)
    }

    func testNormalDoesNotDowngradeAt15Battery() {
        let r = PowerBudgetPolicy.resolve(budget: .normal, requestedMaxTokens: 512, batteryLevelPercent: 15)
        XCTAssertEqual(r.maxTokens, 512)
        XCTAssertFalse(r.preferSmallerModelInChain)
    }

    func testHighAutoThrottlesToNormalOnThermal() {
        let r = PowerBudgetPolicy.resolve(budget: .high, requestedMaxTokens: 4096, thermalThrottled: true)
        XCTAssertEqual(r.maxTokens, 512) // Normal cap
        XCTAssertEqual(r.preferredKvMode, .turboQuant4Bit)
    }

    // MARK: - VisionInput

    func testVisionInputHoldsBytesAndMime() {
        let bytes = Data([0xFF, 0xD8, 0xFF]) // JPEG SOI
        let vi = VisionInput(imageBytes: bytes, mimeType: "image/jpeg")
        XCTAssertEqual(vi.imageBytes, bytes)
        XCTAssertEqual(vi.mimeType, "image/jpeg")
        XCTAssertNil(VisionInput(imageBytes: bytes).mimeType)
    }

    // MARK: - ChatCapability.video

    func testVideoCapabilityFlagValueAndComposition() {
        XCTAssertEqual(ChatCapability.video.rawValue, 32)
        let combo: ChatCapability = [.vision, .video]
        XCTAssertTrue(combo.contains(.video))
        XCTAssertTrue(combo.contains(.vision))
        XCTAssertFalse(combo.contains(.tools))
    }
}
