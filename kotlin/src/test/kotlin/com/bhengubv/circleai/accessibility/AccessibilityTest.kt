// AccessibilityTest.kt — verifies the CircleAI.Accessibility port against the C# reference.

package com.bhengubv.circleai.accessibility

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class AccessibilityTest {

    @Test
    fun `hints derived in fixed order`() {
        val b = InMemoryAccessibilityBoard()
        b.setProfile(
            UserAccessibilityProfile(
                userId = "u1",
                needs = listOf(AccessibilityNeed.Visual, AccessibilityNeed.Motor),
                textScale = 1.5,
                highContrast = true,
                reducedMotion = true,
                screenReader = true,
            ),
        )
        val hints = b.hintsFor("u1")
        assertEquals(
            listOf(
                "contrast" to "high",
                "motion" to "reduced",
                "aria" to "verbose",
                "text-scale" to "1.50",
                "need" to "Visual",
                "need" to "Motor",
            ),
            hints.map { it.kind to it.value },
        )
        assertTrue(b.hintsFor("missing").isEmpty())
    }

    @Test
    fun `text scale omitted when not enlarged`() {
        val b = InMemoryAccessibilityBoard()
        b.setProfile(UserAccessibilityProfile("u2", emptyList(), 1.0, false, false, false))
        assertTrue(b.hintsFor("u2").isEmpty())
    }

    @Test
    fun `domain context and both audit overloads`() = runTest {
        assertTrue(AccessibilityDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Accessibility]"))
        assertTrue("WCAG_2_2" in AccessibilityDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = AccessibilityCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Accessibility]"))

        a.auditWcagAsync("<div>")
        assertTrue(fake.lastMessage!!.contains("Audit this interface for WCAG 2.2 AA compliance"))
        a.auditWcagAsync("<div>", "AAA")
        assertTrue(fake.lastMessage!!.contains("Audit this content/UI for WCAG 2.2 AAA compliance"))
        a.simplifyLanguageAsync("jargon heavy text")
        assertTrue(fake.lastMessage!!.contains("Rewrite this for plain English"))
    }
}
