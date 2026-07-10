// MultiTenantTests.swift
//
// Exercises the ported CircleAI.Core.MultiTenant contracts: NullTenantContext
// (throws on read) and SingleTenantContext (fixed id).

import XCTest
@testable import CircleAI

final class MultiTenantTests: XCTestCase {

    func testNullTenantContextThrowsOnRead() {
        let ctx = NullTenantContext.instance
        XCTAssertFalse(ctx.hasTenant)
        XCTAssertThrowsError(try ctx.currentTenantId()) { err in
            XCTAssertTrue(err is NoTenantInScopeError)
        }
    }

    func testNullTenantContextValueSemantics() {
        // Two instances behave identically (value type).
        let a = NullTenantContext()
        let b = NullTenantContext.instance
        XCTAssertFalse(a.hasTenant)
        XCTAssertFalse(b.hasTenant)
    }

    func testSingleTenantContextReturnsFixedId() throws {
        let ctx = SingleTenantContext(tenantId: "tenant-42")
        XCTAssertTrue(ctx.hasTenant)
        XCTAssertEqual(try ctx.currentTenantId(), "tenant-42")
        // Repeated reads are stable and never throw.
        XCTAssertEqual(try ctx.currentTenantId(), "tenant-42")
    }

    func testTenantContextIsUsablePolymorphically() throws {
        let contexts: [any ICircleAITenantContext] = [
            SingleTenantContext(tenantId: "x"),
            NullTenantContext.instance,
        ]
        XCTAssertTrue(contexts[0].hasTenant)
        XCTAssertFalse(contexts[1].hasTenant)
        XCTAssertEqual(try contexts[0].currentTenantId(), "x")
        XCTAssertThrowsError(try contexts[1].currentTenantId())
    }
}
