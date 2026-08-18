#nullable enable

// CareerInterview.cs
//
// The questions, in the order that makes the CV useful soonest.
//
// ORDERED BY WHAT AN EMPLOYER READS FIRST, not by what is easy to ask. A name
// and a phone number and one job is already a document somebody can hand in; ten
// skills with no way to contact anybody is not. So the early questions buy the
// most completeness per answer, and the CV on screen becomes recognisably a CV
// within about a minute of starting. That is what makes the wait feel like
// progress instead of an interrogation.
//
// IT ASSUMES INFORMAL WORK IS NORMAL, because for most of the people this is for
// it is. A script built around "job title, company, start date, end date" fails
// anybody whose work was piece jobs, a stall, a family business or seasons on a
// farm — and worse, it makes them feel like the wrong kind of person for their
// own phone. So the work question asks what they DID and who for is optional,
// and nothing in the wording implies a payslip.
//
// SOME ANSWERS MUST BE CONFIRMED RATHER THAN TRANSCRIBED. Whisper will not
// reliably hear a surname, a phone number, or "eThekwini". Those fields are
// marked Verify, and a wrong one is worse than a blank: a CV with the wrong
// number cannot be answered, and a misspelt surname reads as carelessness by the
// applicant. Everything descriptive can be spoken freely.
//
// IT STOPS. Fifteen minutes, not sixty. The download outlasts the interview on
// purpose — what fills the remaining time is reviewing and tailoring the
// document, which is work with something to show for it, not more questions.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Career;

/// <summary>Which part of the profile an answer belongs to.</summary>
public enum ProfileField
{
    FullName, Phone, Headline, Location,
    WorkRole, WorkOrganisation, WorkWhen, WorkDid,
    Skills, Education, Certification, Languages, Summary,
}

/// <summary>One thing to ask.</summary>
/// <param name="Field">Where the answer goes.</param>
/// <param name="Ask">The question, as it will be spoken and shown.</param>
/// <param name="Why">
/// Shown under the question when somebody hesitates. Every question a stranger
/// asks about your employment deserves a reason.
/// </param>
/// <param name="Verify">
/// Whether the answer must be confirmed before it is stored — names, numbers and
/// places, where mishearing does real damage.
/// </param>
/// <param name="Seconds">Rough time to answer, for fitting the script to the wait.</param>
public sealed record InterviewQuestion(
    ProfileField Field, string Ask, string Why, bool Verify = false, int Seconds = 30);

/// <summary>The script, and where somebody has got to in it.</summary>
public static class CareerInterview
{
    /// <summary>Every question, in order.</summary>
    public static IReadOnlyList<InterviewQuestion> Script { get; } = new[]
    {
        new InterviewQuestion(ProfileField.FullName,
            "What is your full name?",
            "It goes at the top of your CV, spelled the way you want it.",
            Verify: true, Seconds: 20),

        new InterviewQuestion(ProfileField.Phone,
            "What number should an employer call?",
            "Without this nobody can offer you the job.",
            Verify: true, Seconds: 25),

        new InterviewQuestion(ProfileField.Headline,
            "What kind of work are you looking for?",
            "It tells the employer in three words what you are, before they read anything else.",
            Seconds: 25),

        new InterviewQuestion(ProfileField.Location,
            "Where do you live? Just the area and the city.",
            "Employers filter by who can get to work.",
            Verify: true, Seconds: 20),

        // The work questions, asked as work rather than as employment.
        new InterviewQuestion(ProfileField.WorkRole,
            "What is the last work you did? It does not have to be a formal job.",
            "Piece work, a stall, helping in a family business — all of it counts.",
            Seconds: 40),

        new InterviewQuestion(ProfileField.WorkOrganisation,
            "Who was that for? Say skip if you worked for yourself.",
            "A name an employer recognises helps, but working for yourself is not a gap.",
            Seconds: 25),

        new InterviewQuestion(ProfileField.WorkWhen,
            "Roughly when was that, and are you still doing it?",
            "Approximate is fine — 'about two years, until last winter'.",
            Seconds: 30),

        new InterviewQuestion(ProfileField.WorkDid,
            "What did you actually do there? Tell me two or three things.",
            "This is the part that gets read. What you did beats what you were called.",
            Seconds: 70),

        new InterviewQuestion(ProfileField.Skills,
            "What are you good at? Machines, tools, systems, dealing with people.",
            "These are what a job advert matches against.",
            Seconds: 60),

        new InterviewQuestion(ProfileField.Certification,
            "Do you have a licence or certificate? A driver's code, PSIRA, first aid?",
            "For a lot of jobs this is the thing that decides it.",
            Seconds: 40),

        new InterviewQuestion(ProfileField.Education,
            "What school or training did you finish, and when?",
            "If you did not finish, say so — it is still worth putting down.",
            Seconds: 40),

        new InterviewQuestion(ProfileField.Languages,
            "Which languages do you speak?",
            "In this country that is a qualification, not a detail.",
            Seconds: 30),

        new InterviewQuestion(ProfileField.Summary,
            "Anything else an employer should know about you?",
            "One or two sentences in your own words.",
            Seconds: 45),
    };

    /// <summary>How long the whole script takes, roughly.</summary>
    public static TimeSpan Length =>
        TimeSpan.FromSeconds(Script.Sum(q => q.Seconds));

    /// <summary>
    /// The next question worth asking, given what is already known.
    /// </summary>
    /// <remarks>
    /// Skips what the profile already answers, so the interview is resumable —
    /// somebody who stopped halfway through yesterday is not asked their name
    /// again today. Returns null when there is nothing left worth asking.
    /// </remarks>
    public static InterviewQuestion? Next(CareerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Script.FirstOrDefault(q => !Answered(profile, q.Field));
    }

    /// <summary>Whether the profile already holds an answer for this field.</summary>
    public static bool Answered(CareerProfile p, ProfileField field) => field switch
    {
        ProfileField.FullName  => !string.IsNullOrWhiteSpace(p.Identity.FullName),
        ProfileField.Phone     => !string.IsNullOrWhiteSpace(p.Identity.Phone),
        ProfileField.Headline  => !string.IsNullOrWhiteSpace(p.Identity.Headline),
        ProfileField.Location  => !string.IsNullOrWhiteSpace(p.Identity.Location),
        ProfileField.Summary   => !string.IsNullOrWhiteSpace(p.Identity.Summary),

        // The work questions are answered as a group: once there is a job with
        // something in it, the script moves on rather than interrogating one
        // role four times.
        ProfileField.WorkRole         => p.History.Count > 0,
        ProfileField.WorkOrganisation => p.History.Count > 0 && p.History[0].Organisation is not null,
        ProfileField.WorkWhen         => p.History.Count > 0 && p.History[0].Start is not null,
        ProfileField.WorkDid          => p.History.Count > 0 && (p.History[0].Achievements?.Count ?? 0) > 0,

        ProfileField.Skills        => p.Skills.Count > 0,
        ProfileField.Education     => p.Education.Count > 0,
        ProfileField.Certification => p.Certifications.Count > 0,
        ProfileField.Languages     => p.Languages.Count > 0,
        _ => false,
    };

    /// <summary>
    /// Whether an answer means "I do not have one of those".
    /// </summary>
    /// <remarks>
    /// Asked of every answer, because several questions have a legitimate empty
    /// answer — no certificate, no formal employer, no schooling finished — and a
    /// script that will not accept "no" traps somebody on a question they cannot
    /// answer. Recognised in the languages people actually say it in.
    /// </remarks>
    public static bool IsDecline(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return true;

        var a = answer.Trim().ToLowerInvariant();
        return a is "skip" or "none" or "no" or "nothing" or "next" or "pass"
                 or "cha" or "hayi"          // isiZulu / isiXhosa
                 or "nee"                    // Afrikaans
                 or "aowa" or "tjhe";        // Sesotho / Setswana
    }
}
