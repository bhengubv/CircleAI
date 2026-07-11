// CommerceIntegrationPayFastTest.kt
//
// Verifies the CircleAI.Commerce.Integration.PayFast port against the C#
// reference. The three golden signatures below were computed by running the
// C# InMemoryPayFastBoard.SignatureFor algorithm on .NET 10 (WebUtility.
// UrlEncode + MD5); the Kotlin port MUST reproduce them byte-for-byte, which is
// the cross-language signature-parity gate.
//   - signatureFor with passphrase (spaces/&/accents/% in values)  -> golden A
//   - signatureFor without passphrase (trailing '&' trimmed)       -> golden B
//   - signatureFor single field, no passphrase                     -> golden C
//   - verifyItn matches on merchant id; recentWebhooks is newest-first capped

package com.bhengubv.circleai.commerceintegrationpayfast

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class CommerceIntegrationPayFastTest {

    private fun cfg(pass: String) = PayFastConfig("10000100", "46f0cd694581a", pass, sandbox = true)

    @Test
    fun `signature with passphrase matches dotnet reference`() {
        val board = InMemoryPayFastBoard(cfg("my secret passphrase"))
        val fields = linkedMapOf(
            "merchant_id" to "10000100",
            "merchant_key" to "46f0cd694581a",
            "amount" to "100.00",
            "item_name" to "Café & Crème 50%",
            "return_url" to "https://x.test/return?a=b&c=d",
        )
        assertEquals("717a3bf7e6617deafa2dbc2c332e1704", board.signatureFor(fields))
    }

    @Test
    fun `signature without passphrase trims trailing ampersand and matches reference`() {
        val board = InMemoryPayFastBoard(cfg(""))
        val fields = linkedMapOf(
            "merchant_id" to "10000100",
            "item_name" to "Plain Item",
            "amount" to "9.99",
        )
        assertEquals("83b1ffc6bd671bdea340f121b5999ab8", board.signatureFor(fields))
    }

    @Test
    fun `signature single field no passphrase matches reference`() {
        val board = InMemoryPayFastBoard(cfg(""))
        assertEquals("d54675f368fd53465d76650ae7e198cb", board.signatureFor(linkedMapOf("m_payment_id" to "order-001")))
    }

    @Test
    fun `url encoder maps unreserved, space and specials like dotnet`() {
        // space -> '+', '&' -> %26, '=' -> %3D, accent -> UTF-8 %XX, '%' -> %25
        assertEquals("a+b%26c%3Dd", payFastUrlEncode("a b&c=d"))
        assertEquals("Caf%C3%A9+M%C3%B6ller", payFastUrlEncode("Café Möller"))
        assertEquals("50%25", payFastUrlEncode("50%"))
        // unreserved set passes through untouched
        assertEquals("Az09!()*-._", payFastUrlEncode("Az09!()*-._"))
    }

    @Test
    fun `verify itn and recent webhooks newest first`() {
        val board = InMemoryPayFastBoard(cfg("p"))
        fun payload(pid: String, merchant: String = "10000100") =
            PayFastItnPayload(merchant, pid, "COMPLETE", BigDecimal("1.00"), "m-$pid", "sig")

        assertTrue(board.verifyItn(payload("1")))
        assertFalse(board.verifyItn(payload("2", merchant = "99999999")))

        board.recordWebhook(payload("1"))
        board.recordWebhook(payload("2"))
        board.recordWebhook(payload("3"))
        assertEquals(listOf("3", "2", "1"), board.recentWebhooks().map { it.paymentId })
        assertEquals(listOf("3", "2"), board.recentWebhooks(2).map { it.paymentId })
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(CommerceIntegrationPayFastDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Commerce.Integration.PayFast]"))
        assertTrue("PCI_DSS" in CommerceIntegrationPayFastDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = CommerceIntegrationPayFastCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Commerce.Integration.PayFast]"))
        a.guideRefundAsync("tx-1", "duplicate")
        assertTrue(fake.lastMessage!!.contains("PayFast refund for transaction tx-1"))
    }
}
