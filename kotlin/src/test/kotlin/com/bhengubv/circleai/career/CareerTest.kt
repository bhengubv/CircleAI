package com.bhengubv.circleai.career

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** The profile, its completeness, and the interview that fills it. */
class CareerTest {

    private fun full() = CareerProfile(
        identity = ProfileIdentity("Nandi Dlamini", "Forklift operator", "+27825550142",
            location = "Umlazi, Durban"),
        history = listOf(ProfileHistory("Forklift operator", "Acme", start = "2020",
            achievements = listOf("moved stock"))),
        skills = listOf(ProfileSkill("forklift")),
        education = listOf(ProfileEducation("Matric")),
        certifications = listOf(ProfileCertification("Code 14")),
        languages = listOf(ProfileLanguage("isiZulu")),
    )

    @Test fun `an empty profile is zero complete`() {
        assertEquals(0.0, CareerProfile().completeness())
    }

    @Test fun `a full profile is one`() {
        assertEquals(1.0, full().completeness())
    }

    // A name and a phone weigh 3 each and work history 4, because without those
    // an employer cannot call you and has nothing to read.
    @Test fun `the weights favour what an employer actually needs`() {
        val nameOnly = CareerProfile(identity = ProfileIdentity(fullName = "Nandi"))
        val educationOnly = CareerProfile(education = listOf(ProfileEducation("Matric")))
        assertTrue(nameOnly.completeness() > educationOnly.completeness(),
            "a name must be worth more than a qualification")

        val historyOnly = CareerProfile(history = listOf(ProfileHistory("role")))
        assertTrue(historyOnly.completeness() > nameOnly.completeness(),
            "work history is the heaviest single field")
    }

    // 3 of 18 total weight.
    @Test fun `the total weight is eighteen`() {
        val nameOnly = CareerProfile(identity = ProfileIdentity(fullName = "Nandi"))
        assertEquals(3.0 / 18.0, nameOnly.completeness(), 1e-9)
    }

    @Test fun `a blank name does not count as filled`() {
        assertEquals(0.0, CareerProfile(identity = ProfileIdentity(fullName = "   ")).completeness())
    }

    // ── The interview ───────────────────────────────────────────────────────

    @Test fun `the script covers every field in order`() {
        assertEquals(13, CareerInterview.script.size)
        assertEquals(ProfileField.FULL_NAME, CareerInterview.script.first().field)
        assertEquals(ProfileField.SUMMARY, CareerInterview.script.last().field)
    }

    // The wording IS the product: this is not the same question as
    // "what was your last job".
    @Test fun `the work question does not assume a formal job`() {
        val q = CareerInterview.script.first { it.field == ProfileField.WORK_ROLE }
        assertTrue(q.ask.contains("does not have to be a formal job"))
        assertTrue(q.why.contains("Piece work"))
    }

    @Test fun `the name and phone answers are read back`() {
        val verified = CareerInterview.script.filter { it.verify }.map { it.field }
        assertTrue(verified.contains(ProfileField.FULL_NAME))
        assertTrue(verified.contains(ProfileField.PHONE))
    }

    @Test fun `the interview length is the sum of its questions`() {
        assertEquals(CareerInterview.script.sumOf { it.seconds }, CareerInterview.lengthSeconds)
        assertTrue(CareerInterview.lengthSeconds in 300..900, "roughly five to fifteen minutes")
    }

    @Test fun `the next question is the first unanswered one`() {
        assertEquals(ProfileField.FULL_NAME, CareerInterview.next(CareerProfile())!!.field)

        val named = CareerProfile(identity = ProfileIdentity(fullName = "Nandi"))
        assertEquals(ProfileField.PHONE, CareerInterview.next(named)!!.field)
    }

    // Summary is ALWAYS asked: there is no way to tell a written summary from a
    // skipped one, and asking twice beats losing it.
    @Test fun `a complete profile still gets asked for a summary`() {
        assertEquals(ProfileField.SUMMARY, CareerInterview.next(full())!!.field)
    }

    @Test fun `work follow-ups are answered only once there is history`() {
        val bare = CareerProfile()
        assertFalse(CareerInterview.answered(bare, ProfileField.WORK_ORGANISATION))
        assertFalse(CareerInterview.answered(bare, ProfileField.WORK_WHEN))
        assertFalse(CareerInterview.answered(bare, ProfileField.WORK_DID))

        val withRoleOnly = CareerProfile(history = listOf(ProfileHistory("driver")))
        assertTrue(CareerInterview.answered(withRoleOnly, ProfileField.WORK_ROLE))
        assertFalse(CareerInterview.answered(withRoleOnly, ProfileField.WORK_ORGANISATION),
            "a role with no employer has not answered the employer question")
    }

    // IN THEIR LANGUAGE. An interview that only understood "no" would record
    // the word itself as somebody answer.
    @Test fun `declining is understood in more than english`() {
        for (word in listOf("skip", "none", "no", "nothing", "next", "pass")) {
            assertTrue(CareerInterview.isDecline(word), word)
        }
        for (word in listOf("cha", "hayi", "nee", "aowa", "tjhe")) {
            assertTrue(CareerInterview.isDecline(word), word + " must be understood as a decline")
        }
    }

    @Test fun `declining ignores case and surrounding space`() {
        assertTrue(CareerInterview.isDecline("  HAYI  "))
        assertTrue(CareerInterview.isDecline("Skip"))
    }

    @Test fun `nothing said at all is a decline`() {
        assertTrue(CareerInterview.isDecline(null))
        assertTrue(CareerInterview.isDecline(""))
        assertTrue(CareerInterview.isDecline("   "))
    }

    // A real answer must NOT be swallowed as a decline.
    @Test fun `a real answer is not a decline`() {
        assertFalse(CareerInterview.isDecline("Nandi Dlamini"))
        assertFalse(CareerInterview.isDecline("no formal job but I ran a stall"))
    }

    // Working for yourself is NOT a gap.
    @Test fun `informal work is still work`() {
        val h = ProfileHistory("stall owner", formal = false)
        assertFalse(h.formal)
        val p = CareerProfile(history = listOf(h))
        assertTrue(CareerInterview.answered(p, ProfileField.WORK_ROLE))
    }
}
