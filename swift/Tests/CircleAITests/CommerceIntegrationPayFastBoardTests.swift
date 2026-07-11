// CommerceIntegrationPayFastBoardTests.swift
//
// Exercises the PayFast records' Codable round-trips and the deterministic
// behaviour of InMemoryPayFastBoard — the MD5 signature builder (against
// reference vectors computed with md5sum), the %-encoder edge cases
// (space→'+', reserved→uppercase %XX, UTF-8 multibyte), ITN verification, and
// reverse-ordered webhook history. Mirrors
// CircleAI.Commerce.Integration.PayFast/PayFastPrimitives.cs.

import XCTest
import Foundation
@testable import CircleAI

final class CommerceIntegrationPayFastBoardTests: XCTestCase {

    private let cfgNoPass = PayFastConfig(merchantId: "10000100", merchantKey: "k", passphrase: "", sandbox: true)
    private let cfgPass = PayFastConfig(merchantId: "10000100", merchantKey: "k", passphrase: "secret", sandbox: true)

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testConfigCodableRoundTrip() throws {
        XCTAssertEqual(try JSONDecoder().decode(PayFastConfig.self, from: try JSONEncoder().encode(cfgPass)), cfgPass)
    }

    func testItnPayloadCodableRoundTrip() throws {
        let p = PayFastItnPayload(merchantId: "10000100", paymentId: "PF1", paymentStatus: "COMPLETE",
                                  amount: 100.00, mPaymentId: "M1", signature: "abc")
        XCTAssertEqual(try JSONDecoder().decode(PayFastItnPayload.self, from: try JSONEncoder().encode(p)), p)
    }

    // ── Signature (reference vectors) ────────────────────────────────────────

    func testSignatureWithoutPassphrase() {
        let board = InMemoryPayFastBoard(cfgNoPass)
        let sig = board.signatureFor([
            ("merchant_id", "10000100"),
            ("amount", "100.00"),
            ("item_name", "Test Item")   // space → '+'
        ])
        // md5("merchant_id=10000100&amount=100.00&item_name=Test+Item")
        XCTAssertEqual(sig, "20b42403dd92e66522fe0de4e5e99e44")
    }

    func testSignatureWithPassphraseAppended() {
        let board = InMemoryPayFastBoard(cfgPass)
        let sig = board.signatureFor([
            ("merchant_id", "10000100"),
            ("amount", "100.00"),
            ("item_name", "Test Item")
        ])
        // md5("merchant_id=10000100&amount=100.00&item_name=Test+Item&passphrase=secret")
        XCTAssertEqual(sig, "69eca130ff33cef62f2833783ad28298")
    }

    func testSignatureEncodesReservedAndMultibyte() {
        let board = InMemoryPayFastBoard(cfgNoPass)
        // value "a b&cé" → a+b%26c%C3%A9 (space→'+', '&'→%26, 'é'→%C3%A9)
        let sig = board.signatureFor([("x", "a b&cé")])
        XCTAssertEqual(sig, "246629fd9fcbd63dce3d01fc139d67c6")
    }

    func testUrlEncoderUnitCases() {
        XCTAssertEqual(InMemoryPayFastBoard.payFastUrlEncode("Test Item"), "Test+Item")
        XCTAssertEqual(InMemoryPayFastBoard.payFastUrlEncode("a&b"), "a%26b")
        XCTAssertEqual(InMemoryPayFastBoard.payFastUrlEncode("keep-_.safe"), "keep-_.safe")
        XCTAssertEqual(InMemoryPayFastBoard.payFastUrlEncode("é"), "%C3%A9")
        XCTAssertEqual(InMemoryPayFastBoard.payFastUrlEncode(""), "")
    }

    // ── ITN verification ─────────────────────────────────────────────────────

    func testVerifyItnChecksMerchantId() {
        let board = InMemoryPayFastBoard(cfgNoPass)
        let ok = PayFastItnPayload(merchantId: "10000100", paymentId: "P", paymentStatus: "COMPLETE",
                                   amount: 1, mPaymentId: "M", signature: "s")
        let bad = PayFastItnPayload(merchantId: "99999999", paymentId: "P", paymentStatus: "COMPLETE",
                                    amount: 1, mPaymentId: "M", signature: "s")
        XCTAssertTrue(board.verifyItn(ok))
        XCTAssertFalse(board.verifyItn(bad))
    }

    // ── Webhooks ─────────────────────────────────────────────────────────────

    func testRecentWebhooksReverseOrderAndLimit() {
        let board = InMemoryPayFastBoard(cfgNoPass)
        for i in 0..<5 {
            board.recordWebhook(PayFastItnPayload(merchantId: "10000100", paymentId: "P\(i)",
                                                  paymentStatus: "COMPLETE", amount: Decimal(i),
                                                  mPaymentId: "M\(i)", signature: "s"))
        }
        XCTAssertEqual(board.recentWebhooks(limit: 2).map { $0.paymentId }, ["P4", "P3"])
        XCTAssertEqual(board.recentWebhooks().count, 5) // default limit 20
    }

    // ── Domain context ───────────────────────────────────────────────────────

    func testDomainContextConstants() {
        XCTAssertTrue(CommerceIntegrationPayFastDomainContext.systemPromptSnippet.hasPrefix("[DOMAIN: Commerce.Integration.PayFast]"))
        XCTAssertTrue(CommerceIntegrationPayFastDomainContext.complianceFlags.contains("PCI_DSS"))
    }
}
