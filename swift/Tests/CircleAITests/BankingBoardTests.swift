// BankingBoardTests.swift
//
// Exercises the banking records' Codable round-trips and the deterministic
// double-entry behaviour of InMemoryBank + its in-memory / null backends —
// seeding, ledger append (incl. balance mutation + unknown-account throw),
// newest-first reads with limit, and the full payment engine (positivity,
// unknown accounts, currency mismatch, insufficient funds, and the successful
// two-leg transfer). Mirrors CircleAI.Banking/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class BankingBoardTests: XCTestCase {

    private func acct(_ id: String, _ owner: String = "o", _ ccy: String = "ZAR", _ bal: Decimal) -> BankAccount {
        BankAccount(accountId: id, ownerId: owner, currency: ccy, balance: bal)
    }

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testBankAccountCodableRoundTrip() throws {
        let a = acct("a1", "owner", "ZAR", 100)
        XCTAssertEqual(try JSONDecoder().decode(BankAccount.self, from: try JSONEncoder().encode(a)), a)
    }

    func testLedgerEntryCodableRoundTrip() throws {
        let e = LedgerEntry(txId: "t1", accountId: "a1", amount: -25, memo: "m", atUtc: Date(timeIntervalSince1970: 9))
        XCTAssertEqual(try JSONDecoder().decode(LedgerEntry.self, from: try JSONEncoder().encode(e)), e)
    }

    func testPaymentResultCodableRoundTrip() throws {
        let r = PaymentResult(txId: "t1", accepted: false, failureReason: "nope")
        XCTAssertEqual(try JSONDecoder().decode(PaymentResult.self, from: try JSONEncoder().encode(r)), r)
    }

    // ── Reader ───────────────────────────────────────────────────────────────

    func testReaderGetAndListForOwner() async {
        let bank = InMemoryBank()
        bank.seedAccount(acct("a1", "alice", "ZAR", 10))
        bank.seedAccount(acct("a2", "alice", "ZAR", 20))
        bank.seedAccount(acct("a3", "bob", "ZAR", 30))
        let reader = InMemoryAccountReader(bank)
        XCTAssertEqual(reader.backendId, "in-memory")
        let got = await reader.getAccount("a1")
        XCTAssertEqual(got?.balance, 10)
        let alice = await reader.listForOwner("alice")
        XCTAssertEqual(Set(alice.map { $0.accountId }), ["a1", "a2"])
    }

    // ── Ledger ───────────────────────────────────────────────────────────────

    func testAppendMutatesBalanceAndThrowsForUnknown() async throws {
        let bank = InMemoryBank()
        bank.seedAccount(acct("a1", "o", "ZAR", 100))
        let w = InMemoryLedgerWriter(bank)
        _ = try await w.append(LedgerEntry(txId: "t1", accountId: "a1", amount: -30, memo: "m",
                                           atUtc: Date(timeIntervalSince1970: 1)))
        XCTAssertEqual(bank.get("a1")?.balance, 70)

        do {
            _ = try await w.append(LedgerEntry(txId: "t2", accountId: "ghost", amount: 5, memo: "m", atUtc: Date()))
            XCTFail("expected throw")
        } catch {
            XCTAssertEqual(error as? BankingError, .unknownAccount("ghost"))
        }
    }

    func testReadNewestFirstHonoursLimit() async {
        let bank = InMemoryBank()
        bank.seedAccount(acct("a1", "o", "ZAR", 0))
        let w = InMemoryLedgerWriter(bank)
        for i in 0..<5 {
            _ = try? await w.append(LedgerEntry(txId: "t\(i)", accountId: "a1", amount: 1, memo: "m",
                                                atUtc: Date(timeIntervalSince1970: TimeInterval(i))))
        }
        let recent = await w.read("a1", limit: 2)
        XCTAssertEqual(recent.map { $0.txId }, ["t4", "t3"])
    }

    func testReadDefaultLimitOverload() async {
        let bank = InMemoryBank()
        bank.seedAccount(acct("a1", "o", "ZAR", 0))
        let w = InMemoryLedgerWriter(bank)
        _ = try? await w.append(LedgerEntry(txId: "t0", accountId: "a1", amount: 1, memo: "m", atUtc: Date()))
        let recent = await w.read("a1")
        XCTAssertEqual(recent.count, 1)
    }

    // ── Payments ─────────────────────────────────────────────────────────────

    func testPaymentSuccessDoubleEntry() async {
        let bank = InMemoryBank()
        bank.seedAccount(acct("src", "alice", "ZAR", 100))
        bank.seedAccount(acct("dst", "bob", "ZAR", 0))
        let proc = InMemoryPaymentProcessor(bank)
        let res = await proc.process(PaymentRequest(fromAccount: "src", toAccount: "dst",
                                                    amount: 40, currency: "ZAR", memo: "rent"))
        XCTAssertTrue(res.accepted)
        XCTAssertNil(res.failureReason)
        XCTAssertEqual(bank.get("src")?.balance, 60)
        XCTAssertEqual(bank.get("dst")?.balance, 40)
        // Both legs share the same txId.
        let srcLedger = bank.read("src", limit: 10)
        let dstLedger = bank.read("dst", limit: 10)
        XCTAssertEqual(srcLedger.first?.txId, res.txId)
        XCTAssertEqual(dstLedger.first?.txId, res.txId)
        XCTAssertEqual(srcLedger.first?.amount, -40)
        XCTAssertEqual(dstLedger.first?.amount, 40)
    }

    func testPaymentRejectsNonPositiveAmount() async {
        let bank = InMemoryBank()
        let proc = InMemoryPaymentProcessor(bank)
        let res = await proc.process(PaymentRequest(fromAccount: "a", toAccount: "b",
                                                    amount: 0, currency: "ZAR", memo: ""))
        XCTAssertFalse(res.accepted)
        XCTAssertEqual(res.failureReason, "Amount must be positive")
    }

    func testPaymentRejectsUnknownAccounts() async {
        let bank = InMemoryBank()
        bank.seedAccount(acct("src", "o", "ZAR", 100))
        let proc = InMemoryPaymentProcessor(bank)
        let noDst = await proc.process(PaymentRequest(fromAccount: "src", toAccount: "ghost",
                                                     amount: 10, currency: "ZAR", memo: ""))
        XCTAssertEqual(noDst.failureReason, "Unknown destination account")
        let noSrc = await proc.process(PaymentRequest(fromAccount: "ghost", toAccount: "src",
                                                     amount: 10, currency: "ZAR", memo: ""))
        XCTAssertEqual(noSrc.failureReason, "Unknown source account")
    }

    func testPaymentRejectsCurrencyMismatch() async {
        let bank = InMemoryBank()
        bank.seedAccount(acct("src", "o", "ZAR", 100))
        bank.seedAccount(acct("dst", "o", "USD", 0))
        let proc = InMemoryPaymentProcessor(bank)
        let res = await proc.process(PaymentRequest(fromAccount: "src", toAccount: "dst",
                                                    amount: 10, currency: "ZAR", memo: ""))
        XCTAssertEqual(res.failureReason, "Currency mismatch")
    }

    func testPaymentRejectsInsufficientFunds() async {
        let bank = InMemoryBank()
        bank.seedAccount(acct("src", "o", "ZAR", 5))
        bank.seedAccount(acct("dst", "o", "ZAR", 0))
        let proc = InMemoryPaymentProcessor(bank)
        let res = await proc.process(PaymentRequest(fromAccount: "src", toAccount: "dst",
                                                    amount: 10, currency: "ZAR", memo: ""))
        XCTAssertEqual(res.failureReason, "Insufficient funds")
        // No balances moved on failure.
        XCTAssertEqual(bank.get("src")?.balance, 5)
        XCTAssertEqual(bank.get("dst")?.balance, 0)
    }

    func testCurrencyCheckIsCaseInsensitive() async {
        let bank = InMemoryBank()
        bank.seedAccount(acct("src", "o", "zar", 100))
        bank.seedAccount(acct("dst", "o", "ZAR", 0))
        let proc = InMemoryPaymentProcessor(bank)
        let res = await proc.process(PaymentRequest(fromAccount: "src", toAccount: "dst",
                                                    amount: 10, currency: "Zar", memo: ""))
        XCTAssertTrue(res.accepted)
    }

    // ── Null backends ────────────────────────────────────────────────────────

    func testNullBackendsFailClosed() async throws {
        XCTAssertEqual(NullAccountReader.instance.backendId, "null")
        let acc = await NullAccountReader.instance.getAccount("x")
        XCTAssertNil(acc)
        let owned = await NullAccountReader.instance.listForOwner("x")
        XCTAssertTrue(owned.isEmpty)

        let entry = LedgerEntry(txId: "t", accountId: "a", amount: 1, memo: "m", atUtc: Date())
        let echoed = try await NullLedgerWriter.instance.append(entry)
        XCTAssertEqual(echoed, entry)
        let read = await NullLedgerWriter.instance.read("a")
        XCTAssertTrue(read.isEmpty)

        let res = await NullPaymentProcessor.instance.process(
            PaymentRequest(fromAccount: "a", toAccount: "b", amount: 1, currency: "ZAR", memo: ""))
        XCTAssertFalse(res.accepted)
        XCTAssertEqual(res.txId, "00000000-0000-0000-0000-000000000000")
        XCTAssertEqual(res.failureReason, "NullPaymentProcessor.")
    }
}
