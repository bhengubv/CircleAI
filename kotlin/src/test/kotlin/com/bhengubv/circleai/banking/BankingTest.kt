// BankingTest.kt
//
// Verifies the CircleAI.Banking port against the C# reference semantics:
//   - seed/get/listForOwner; append mutates balance + records ledger
//   - read orders by atUtc DESC and caps at limit
//   - processPayment: positive-amount / unknown-account / currency / funds
//     guards; a successful payment posts a matching debit + credit (double
//     entry) and moves balances
//   - reader/ledger/payment async facades delegate to the shared bank
//   - Null implementations are inert / fail-closed

package com.bhengubv.circleai.banking

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class BankingTest {

    private fun money(s: String) = BigDecimal(s)
    private fun assertMoney(expected: String, actual: BigDecimal) =
        assertTrue(BigDecimal(expected).compareTo(actual) == 0, "expected $expected but was $actual")

    private fun bankWith(vararg accounts: Account): InMemoryBank {
        val b = InMemoryBank()
        accounts.forEach { b.seedAccount(it) }
        return b
    }

    @Test
    fun `seed, get and list for owner`() {
        val b = bankWith(
            Account("a1", "owner1", "ZAR", money("100")),
            Account("a2", "owner1", "ZAR", money("50")),
            Account("a3", "owner2", "ZAR", money("10")),
        )
        assertEquals("owner1", b.get("a1")!!.ownerId)
        assertNull(b.get("missing"))
        assertEquals(setOf("a1", "a2"), b.listForOwner("owner1").map { it.accountId }.toSet())
    }

    @Test
    fun `append mutates balance and read is newest-first capped`() {
        val b = bankWith(Account("a1", "o", "ZAR", money("0")))
        b.append(LedgerEntry("t1", "a1", money("100"), "m1", Instant.parse("2026-07-01T00:00:00Z")))
        b.append(LedgerEntry("t2", "a1", money("-30"), "m2", Instant.parse("2026-07-02T00:00:00Z")))
        b.append(LedgerEntry("t3", "a1", money("5"), "m3", Instant.parse("2026-07-03T00:00:00Z")))
        assertMoney("75", b.get("a1")!!.balance)
        // newest first
        assertEquals(listOf("t3", "t2", "t1"), b.read("a1", 100).map { it.txId })
        // limit caps
        assertEquals(listOf("t3", "t2"), b.read("a1", 2).map { it.txId })
        // unknown account ledger is empty
        assertTrue(b.read("ghost", 10).isEmpty())
    }

    @Test
    fun `payment guards`() {
        val b = bankWith(
            Account("a1", "o", "ZAR", money("100")),
            Account("a2", "o", "ZAR", money("0")),
            Account("usd", "o", "USD", money("100")),
        )
        assertFalse(b.processPayment(PaymentRequest("a1", "a2", money("0"), "ZAR", "x")).accepted)
        assertFalse(b.processPayment(PaymentRequest("ghost", "a2", money("5"), "ZAR", "x")).accepted)
        assertFalse(b.processPayment(PaymentRequest("a1", "ghost", money("5"), "ZAR", "x")).accepted)
        assertFalse(b.processPayment(PaymentRequest("a1", "usd", money("5"), "ZAR", "x")).accepted)
        assertFalse(b.processPayment(PaymentRequest("a1", "a2", money("999"), "ZAR", "x")).accepted)
    }

    @Test
    fun `successful payment posts double entry and moves balances`() {
        val b = bankWith(
            Account("a1", "o", "ZAR", money("100")),
            Account("a2", "o", "ZAR", money("0")),
        )
        val r = b.processPayment(PaymentRequest("a1", "a2", money("40"), "ZAR", "lunch"))
        assertTrue(r.accepted)
        assertNull(r.failureReason)
        assertMoney("60", b.get("a1")!!.balance)
        assertMoney("40", b.get("a2")!!.balance)
        // Both ledger legs share the tx id.
        assertEquals(r.txId, b.read("a1", 1).single().txId)
        assertMoney("-40", b.read("a1", 1).single().amount)
        assertMoney("40", b.read("a2", 1).single().amount)
    }

    @Test
    fun `async facades delegate to shared bank`() = runTest {
        val bank = bankWith(Account("a1", "o", "ZAR", money("100")), Account("a2", "o", "ZAR", money("0")))
        val reader = InMemoryAccountReader(bank)
        val ledger = InMemoryLedgerWriter(bank)
        val pay = InMemoryPaymentProcessor(bank)
        assertEquals("in-memory", reader.backendId)
        assertEquals("in-memory", ledger.backendId)
        assertEquals("in-memory", pay.backendId)

        assertMoney("100", reader.getAccountAsync("a1")!!.balance)
        assertEquals(2, reader.listForOwnerAsync("o").size)

        val res = pay.processAsync(PaymentRequest("a1", "a2", money("10"), "ZAR", "m"))
        assertTrue(res.accepted)
        assertMoney("90", reader.getAccountAsync("a1")!!.balance)
        assertEquals(1, ledger.readAsync("a1", 100).size)
    }

    @Test
    fun `null implementations are fail-closed`() = runTest {
        assertEquals("null", NullAccountReader.Instance.backendId)
        assertNull(NullAccountReader.Instance.getAccountAsync("x"))
        assertTrue(NullAccountReader.Instance.listForOwnerAsync("x").isEmpty())

        val e = LedgerEntry("t", "a", money("1"), "m", Instant.EPOCH)
        assertEquals(e, NullLedgerWriter.Instance.appendAsync(e))
        assertTrue(NullLedgerWriter.Instance.readAsync("a").isEmpty())

        val r = NullPaymentProcessor.Instance.processAsync(PaymentRequest("a", "b", money("1"), "ZAR", "m"))
        assertFalse(r.accepted)
        assertEquals("00000000-0000-0000-0000-000000000000", r.txId)
    }
}
