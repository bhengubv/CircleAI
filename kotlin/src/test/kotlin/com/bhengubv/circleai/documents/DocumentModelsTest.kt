package com.bhengubv.circleai.documents

import java.math.BigDecimal
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class CoverLetterGreetingTest {

    private val contact = CvContact(email = "t@example.co.za", phone = "071 000 0000")

    private fun letter(
        greeting: String? = null,
        recipientName: String? = null,
        closing: String? = null,
        signature: String? = null,
    ) = CoverLetter(
        senderName = "Thabo Mokoena",
        senderContact = contact,
        date = "1 September 2026",
        recipientName = recipientName,
        recipientCompany = "Shoprite Checkers",
        subject = "Application: Forklift Operator",
        greeting = greeting,
        closing = closing,
        signatureName = signature,
    )

    @Test
    fun anExplicitGreetingWins() {
        assertEquals("Goeiedag,", letter(greeting = "Goeiedag,").effectiveGreeting)
    }

    @Test
    fun aNamedRecipientIsGreetedByName() {
        assertEquals("Dear Ms Dlamini,", letter(recipientName = "Ms Dlamini").effectiveGreeting)
    }

    @Test
    fun noNameFallsBackToTheFormalGreeting() {
        assertEquals("Dear Sir or Madam,", letter().effectiveGreeting)
    }

    @Test
    fun aBlankGreetingIsNotAGreeting() {
        // Whitespace is not null, so a nullable check alone would print three
        // spaces where the salutation belongs. The C# tests IsNullOrWhiteSpace.
        assertEquals("Dear Sir or Madam,", letter(greeting = "   ").effectiveGreeting)
        assertEquals("Dear Sir or Madam,", letter(greeting = "\t\n").effectiveGreeting)
    }

    @Test
    fun aBlankRecipientNameAlsoFallsThroughToTheFormalGreeting() {
        assertEquals("Dear Sir or Madam,", letter(recipientName = "  ").effectiveGreeting)
    }

    @Test
    fun anExplicitGreetingBeatsANameThatIsAlsoPresent() {
        val l = letter(greeting = "Hi Nomsa,", recipientName = "Ms Dlamini")
        assertEquals("Hi Nomsa,", l.effectiveGreeting)
    }

    @Test
    fun theClosingAndSignatureHaveTheirOwnFallbacks() {
        assertEquals("Yours sincerely,", letter().effectiveClosing)
        assertEquals("Yours sincerely,", letter(closing = " ").effectiveClosing)
        assertEquals("Kind regards,", letter(closing = "Kind regards,").effectiveClosing)

        // The signature falls back to the SENDER, not to a placeholder.
        assertEquals("Thabo Mokoena", letter().effectiveSignature)
        assertEquals("Thabo Mokoena", letter(signature = "   ").effectiveSignature)
        assertEquals("T. Mokoena", letter(signature = "T. Mokoena").effectiveSignature)
    }

    @Test
    fun theMinimalLetterStillProducesEveryDerivedLine() {
        val l = CoverLetter.minimal("Nomsa Khumalo", contact, "1 September 2026", "Woolworths", "Application")
        assertEquals("Dear Sir or Madam,", l.effectiveGreeting)
        assertEquals("Yours sincerely,", l.effectiveClosing)
        assertEquals("Nomsa Khumalo", l.effectiveSignature)
        assertTrue(l.body.isEmpty())
        assertNull(l.recipientName)
    }
}

class CvModelTest {

    @Test
    fun theMinimalCvIsANameAHeadlineAndAWayToReachYou() {
        val cv = CvDocument.minimal(
            "Thabo Mokoena",
            "Forklift Operator (Code 14)",
            CvContact(phone = "071 000 0000", location = "Khayelitsha, Cape Town"),
        )
        assertEquals("Thabo Mokoena", cv.fullName)
        assertTrue(cv.experience.isEmpty())
        assertTrue(cv.education.isEmpty())
        assertTrue(cv.skills.isEmpty())

        // Absent, not empty. A CV with no certifications section is not a CV
        // with an empty certifications section, and the template reads the
        // difference to decide whether to print the heading.
        assertNull(cv.certifications)
        assertNull(cv.languages)
    }

    @Test
    fun aCurrentRoleHasNoEndDate() {
        val e = CvExperience(
            title = "Forklift Operator",
            organisation = "Shoprite DC",
            startDate = "Mar 2021",
            highlights = listOf("Ran the reach truck on nights"),
        )
        assertNull(e.endDate)
        assertEquals(1, e.highlights.size)
    }
}
