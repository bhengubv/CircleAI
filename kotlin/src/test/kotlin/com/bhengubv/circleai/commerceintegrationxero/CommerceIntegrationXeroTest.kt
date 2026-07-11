// CommerceIntegrationXeroTest.kt
//
// Verifies the CircleAI.Commerce.Integration.Xero port against the C#
// reference:
//   - storeTokens/getTokens; tokensExpired true when absent, else now>=expiry
//   - addTenant de-duplicates by tenant id per user
//   - recordWebhook + recentEvents ordered by atUtc DESC, capped
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.commerceintegrationxero

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class CommerceIntegrationXeroTest {

    private val expiry = Instant.parse("2026-07-10T12:00:00Z")

    @Test
    fun `tokens store and expiry semantics`() {
        val b = InMemoryXeroBoard()
        assertNull(b.getTokens("u1"))
        assertTrue(b.tokensExpired("u1", Instant.EPOCH)) // absent -> expired

        b.storeTokens("u1", XeroTokens("acc", "ref", expiry, "id"))
        assertEquals("acc", b.getTokens("u1")!!.accessToken)
        assertFalse(b.tokensExpired("u1", expiry.minusSeconds(1)))
        assertTrue(b.tokensExpired("u1", expiry)) // now >= expiry -> expired
        assertTrue(b.tokensExpired("u1", expiry.plusSeconds(1)))
    }

    @Test
    fun `add tenant deduplicates by id`() {
        val b = InMemoryXeroBoard()
        b.addTenant("u1", XeroTenant("t1", "Org One", "ORGANISATION"))
        b.addTenant("u1", XeroTenant("t1", "Org One (dup)", "ORGANISATION"))
        b.addTenant("u1", XeroTenant("t2", "Org Two", "ORGANISATION"))
        assertEquals(listOf("t1", "t2"), b.tenantsFor("u1").map { it.tenantId })
        assertTrue(b.tenantsFor("other").isEmpty())
    }

    @Test
    fun `recent events newest first capped`() {
        val b = InMemoryXeroBoard()
        b.recordWebhook(XeroWebhookEvent("t1", "INVOICE", "r1", Instant.parse("2026-07-01T00:00:00Z")))
        b.recordWebhook(XeroWebhookEvent("t1", "INVOICE", "r2", Instant.parse("2026-07-03T00:00:00Z")))
        b.recordWebhook(XeroWebhookEvent("t1", "CONTACT", "r3", Instant.parse("2026-07-02T00:00:00Z")))
        assertEquals(listOf("r2", "r3", "r1"), b.recentEvents().map { it.resourceId })
        assertEquals(listOf("r2", "r3"), b.recentEvents(2).map { it.resourceId })
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(CommerceIntegrationXeroDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Commerce.Integration.Xero]"))
        assertTrue("SARS" in CommerceIntegrationXeroDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = CommerceIntegrationXeroCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Commerce.Integration.Xero]"))
        a.explainXeroCodeAsync("ACC-200")
        assertTrue(fake.lastMessage!!.contains("Xero transaction code 'ACC-200'"))
    }
}
