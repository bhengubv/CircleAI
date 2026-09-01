// Career.kt
//
// Kotlin port of CircleAI.Career — the C# reference is the EXACT spec.
//
// Everything the app knows about somebody working life, the interview that
// fills it in, and turning it into a CV.
//
// Fidelity notes:
//   * C# `record` -> `data class`; `long` ids stay `Long`.
//   * The COMPLETENESS WEIGHTS are carried across unchanged. A name and a phone
//     number weigh 3 each and work history 4, because without those an employer
//     cannot call you and has nothing to read; a port that tidied them would
//     show a different percentage for the same person.
//   * The interview script is reproduced VERBATIM - the wording is the product.

package com.bhengubv.circleai.career

import kotlinx.serialization.Serializable

@Serializable
data class ProfileIdentity(
    val fullName: String = "",
    val headline: String = "",
    val phone: String? = null,
    val email: String? = null,
    val location: String? = null,
    val summary: String? = null,
)

@Serializable
data class ProfileSkill(
    val name: String,
    val years: Double? = null,
    val evidenceHistoryId: Long? = null,
    val id: Long = 0,
)

@Serializable
data class ProfileHistory(
    val role: String,
    val organisation: String? = null,
    /**
     * Whether this was formal employment. FALSE IS NOT A GAP - see
     * [ProfileToCv], which prints Self-employed rather than leaving it blank.
     */
    val formal: Boolean = true,
    val start: String? = null,
    val end: String? = null,
    val achievements: List<String>? = null,
    val id: Long = 0,
)

@Serializable
data class ProfileEducation(
    val qualification: String,
    val institution: String? = null,
    val year: String? = null,
    val completed: Boolean = true,
    val id: Long = 0,
)

@Serializable
data class ProfileCertification(
    val name: String,
    val issuer: String? = null,
    val year: String? = null,
    val expires: String? = null,
    val id: Long = 0,
)

@Serializable
data class ProfileLanguage(val name: String, val level: String? = null, val id: Long = 0)

/** Everything the app knows about somebody working life. */
@Serializable
data class CareerProfile(
    val identity: ProfileIdentity = ProfileIdentity(),
    val history: List<ProfileHistory> = emptyList(),
    val skills: List<ProfileSkill> = emptyList(),
    val education: List<ProfileEducation> = emptyList(),
    val certifications: List<ProfileCertification> = emptyList(),
    val languages: List<ProfileLanguage> = emptyList(),
) {
    /**
     * How complete this profile is, 0..1.
     *
     * THE WEIGHTS ARE THE C# WEIGHTS. A name and a phone number weigh 3 each
     * and work history 4, because without those an employer cannot call you
     * and has nothing to read. Education weighs 1. Carrying the numbers across
     * unchanged is the point - a port that tidied them would show a different
     * percentage for the same person.
     */
    fun completeness(): Double {
        var score = 0.0
        var total = 0.0
        fun weigh(have: Boolean, weight: Double) {
            total += weight
            if (have) score += weight
        }
        fun filled(s: String?) = !s.isNullOrBlank()

        weigh(filled(identity.fullName), 3.0)
        weigh(filled(identity.phone), 3.0)
        weigh(filled(identity.headline), 2.0)
        weigh(filled(identity.location), 1.0)
        weigh(history.isNotEmpty(), 4.0)
        weigh(skills.isNotEmpty(), 2.0)
        weigh(education.isNotEmpty(), 1.0)
        weigh(certifications.isNotEmpty(), 1.0)
        weigh(languages.isNotEmpty(), 1.0)
        return if (total <= 0) 0.0 else score / total
    }
}

/** Which part of the profile a question is filling in. */
enum class ProfileField {
    FULL_NAME, PHONE, HEADLINE, LOCATION,
    WORK_ROLE, WORK_ORGANISATION, WORK_WHEN, WORK_DID,
    SKILLS, EDUCATION, CERTIFICATION, LANGUAGES, SUMMARY
}

/** One question, why it is being asked, and how long an answer usually takes. */
data class InterviewQuestion(
    val field: ProfileField,
    /** What the person is asked. */
    val ask: String,
    /** Why it matters - shown to THEM, not to us. */
    val why: String,
    /** Whether the answer should be read back for confirmation. */
    val verify: Boolean = false,
    /** Rough seconds to answer, used to estimate the interview length. */
    val seconds: Int = 30,
)

/** The scripted interview that fills a [CareerProfile] by asking. */
object CareerInterview {

    /**
     * The script, in order.
     *
     * WORDED FOR THE PERSON IT IS FOR. What is the last work you did? It does
     * not have to be a formal job is NOT the same question as What was your
     * last job, and the difference is the whole reason this product exists for
     * somebody whose work was piece work, a stall, or a family business. The
     * strings are carried over verbatim.
     */
    val script: List<InterviewQuestion> = listOf(
        InterviewQuestion(ProfileField.FULL_NAME,
            "What is your full name?",
            "It goes at the top of your CV, spelled the way you want it.",
            verify = true, seconds = 20),
        InterviewQuestion(ProfileField.PHONE,
            "What number should an employer call?",
            "Without this nobody can offer you the job.",
            verify = true, seconds = 25),
        InterviewQuestion(ProfileField.HEADLINE,
            "What kind of work are you looking for?",
            "It tells the employer in three words what you are, before they read anything else.",
            seconds = 25),
        InterviewQuestion(ProfileField.LOCATION,
            "Where do you live? Just the area and the city.",
            "Employers filter by who can get to work.",
            verify = true, seconds = 20),
        InterviewQuestion(ProfileField.WORK_ROLE,
            "What is the last work you did? It does not have to be a formal job.",
            "Piece work, a stall, helping in a family business - all of it counts.",
            seconds = 40),
        InterviewQuestion(ProfileField.WORK_ORGANISATION,
            "Who was that for? Say skip if you worked for yourself.",
            "A name an employer recognises helps, but working for yourself is not a gap.",
            seconds = 25),
        InterviewQuestion(ProfileField.WORK_WHEN,
            "Roughly when was that, and are you still doing it?",
            "Approximate is fine - about two years, until last winter.",
            seconds = 30),
        InterviewQuestion(ProfileField.WORK_DID,
            "What did you actually do there? Tell me two or three things.",
            "This is the part that gets read. What you did beats what you were called.",
            seconds = 70),
        InterviewQuestion(ProfileField.SKILLS,
            "What are you good at? Machines, tools, systems, dealing with people.",
            "These are what a job advert matches against.",
            seconds = 60),
        InterviewQuestion(ProfileField.CERTIFICATION,
            "Do you have a licence or certificate? A driver code, PSIRA, first aid?",
            "For a lot of jobs this is the thing that decides it.",
            seconds = 40),
        InterviewQuestion(ProfileField.EDUCATION,
            "What school or training did you finish, and when?",
            "If you did not finish, say so - it is still worth putting down.",
            seconds = 40),
        InterviewQuestion(ProfileField.LANGUAGES,
            "Which languages do you speak?",
            "In this country that is a qualification, not a detail.",
            seconds = 30),
        InterviewQuestion(ProfileField.SUMMARY,
            "Anything else an employer should know about you?",
            "One or two sentences in your own words.",
            seconds = 45),
    )

    /** Roughly how long the whole interview takes, in seconds. */
    val lengthSeconds: Int get() = script.sumOf { it.seconds }

    /** The next unanswered question, or null when the profile is complete. */
    fun next(profile: CareerProfile): InterviewQuestion? =
        script.firstOrNull { !answered(profile, it.field) }

    /** Whether this profile already holds an answer for [field]. */
    fun answered(p: CareerProfile, field: ProfileField): Boolean {
        fun filled(s: String?) = !s.isNullOrBlank()
        return when (field) {
            ProfileField.FULL_NAME -> filled(p.identity.fullName)
            ProfileField.PHONE -> filled(p.identity.phone)
            ProfileField.HEADLINE -> filled(p.identity.headline)
            ProfileField.LOCATION -> filled(p.identity.location)
            ProfileField.WORK_ROLE -> p.history.isNotEmpty()
            ProfileField.WORK_ORGANISATION -> p.history.isNotEmpty() && p.history[0].organisation != null
            ProfileField.WORK_WHEN -> p.history.isNotEmpty() && p.history[0].start != null
            ProfileField.WORK_DID ->
                p.history.isNotEmpty() && !(p.history[0].achievements ?: emptyList()).isEmpty()
            ProfileField.SKILLS -> p.skills.isNotEmpty()
            ProfileField.EDUCATION -> p.education.isNotEmpty()
            ProfileField.CERTIFICATION -> p.certifications.isNotEmpty()
            ProfileField.LANGUAGES -> p.languages.isNotEmpty()
            // Always asked: there is no way to tell a written summary from a
            // skipped one, and asking twice is better than losing it.
            ProfileField.SUMMARY -> false
        }
    }

    /**
     * Whether an answer means skip this one.
     *
     * IN THEIR LANGUAGE, NOT JUST IN ENGLISH. cha and hayi (isiZulu /
     * isiXhosa), nee (Afrikaans), aowa and tjhe (Sesotho / Setswana) are all a
     * person declining, and an interview that only understood no would record
     * the word itself as their answer.
     */
    fun isDecline(answer: String?): Boolean {
        if (answer == null) return true
        val a = answer.trim().lowercase()
        if (a.isEmpty()) return true
        return a in setOf("skip", "none", "no", "nothing", "next", "pass",
            "cha", "hayi", "nee", "aowa", "tjhe")
    }
}
