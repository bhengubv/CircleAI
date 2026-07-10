// ContextWindowBudgetManagerTests.swift

import XCTest
@testable import CircleAI

final class ContextWindowBudgetManagerTests: XCTestCase {

    func testConstructorValidatesContextSize() {
        XCTAssertThrowsError(try ContextWindowBudgetManager(contextSize: 0)) { err in
            XCTAssertEqual(err as? ContextWindowBudgetError, .contextSizeNotPositive)
        }
        XCTAssertThrowsError(try ContextWindowBudgetManager(contextSize: -5)) { err in
            XCTAssertEqual(err as? ContextWindowBudgetError, .contextSizeNotPositive)
        }
    }

    func testConstructorValidatesThreshold() {
        XCTAssertThrowsError(try ContextWindowBudgetManager(contextSize: 100, evictionThreshold: 1.5)) { err in
            XCTAssertEqual(err as? ContextWindowBudgetError, .evictionThresholdOutOfRange)
        }
        XCTAssertThrowsError(try ContextWindowBudgetManager(contextSize: 100, evictionThreshold: -0.1)) { err in
            XCTAssertEqual(err as? ContextWindowBudgetError, .evictionThresholdOutOfRange)
        }
    }

    func testDefaultThresholdIs085() throws {
        let m = try ContextWindowBudgetManager(contextSize: 1000)
        XCTAssertEqual(m.evictionThreshold, 0.85, accuracy: 1e-9)
    }

    func testRecordExchangeAccumulatesUsedTokens() throws {
        let m = try ContextWindowBudgetManager(contextSize: 1000)
        try m.recordExchange(promptTokens: 100, completionTokens: 50)
        XCTAssertEqual(m.used, 150)
        XCTAssertEqual(m.remainingTokens, 850)
        XCTAssertEqual(m.fillRatio, 0.15, accuracy: 1e-9)
        try m.recordExchange(promptTokens: 25, completionTokens: 25)
        XCTAssertEqual(m.used, 200)
    }

    func testRecordExchangeRejectsNegative() throws {
        let m = try ContextWindowBudgetManager(contextSize: 1000)
        XCTAssertThrowsError(try m.recordExchange(promptTokens: -1, completionTokens: 0)) { err in
            XCTAssertEqual(err as? ContextWindowBudgetError, .negativeTokenCount)
        }
        XCTAssertThrowsError(try m.recordExchange(promptTokens: 0, completionTokens: -1)) { err in
            XCTAssertEqual(err as? ContextWindowBudgetError, .negativeTokenCount)
        }
    }

    func testShouldEvictCrossesThreshold() throws {
        let m = try ContextWindowBudgetManager(contextSize: 100, evictionThreshold: 0.8)
        try m.recordExchange(promptTokens: 79, completionTokens: 0)
        XCTAssertFalse(m.shouldEvict)
        try m.recordExchange(promptTokens: 1, completionTokens: 0) // now 80 -> 0.80 >= 0.80
        XCTAssertTrue(m.shouldEvict)
    }

    func testCalculateEvictionCount() throws {
        let m = try ContextWindowBudgetManager(contextSize: 100)
        try m.recordExchange(promptTokens: 90, completionTokens: 0)
        // Target 0.5 -> targetUsed 50 -> evict 40.
        XCTAssertEqual(try m.calculateEvictionCount(), 40)
        // Target already at/above current -> 0.
        XCTAssertEqual(try m.calculateEvictionCount(targetFillRatio: 0.95), 0)
    }

    func testCalculateEvictionCountValidatesTarget() throws {
        let m = try ContextWindowBudgetManager(contextSize: 100)
        XCTAssertThrowsError(try m.calculateEvictionCount(targetFillRatio: 2.0)) { err in
            XCTAssertEqual(err as? ContextWindowBudgetError, .targetFillRatioOutOfRange)
        }
    }

    func testResetClearsUsage() throws {
        let m = try ContextWindowBudgetManager(contextSize: 100)
        try m.recordExchange(promptTokens: 50, completionTokens: 0)
        m.reset()
        XCTAssertEqual(m.used, 0)
        XCTAssertEqual(m.remainingTokens, 100)
    }
}
