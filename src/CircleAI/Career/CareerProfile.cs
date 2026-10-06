#nullable enable

// CareerProfile.cs
//
// What somebody has done, kept as facts rather than as a document.
//
// THE PROFILE IS THE SOURCE OF TRUTH AND A CV IS ONE PROJECTION OF IT. That
// distinction is the whole design. A CV is a document aimed at one reader; the
// same person applying for security work and for a driving job needs the same
// facts arranged two different ways. Storing the document and editing it for
// each application loses the facts and drifts — three CVs on a phone, each
// slightly disagreeing about when someone left a job. Storing the FACTS and
// generating the document means every version is traceable to something the
// person actually said.
//
// It is also why the tailoring can be honest. "Best foot forward" is selection,
// ordering and emphasis over a fixed set of facts. If the generator can only
// read from here, it cannot invent an employer — the worst it can do is choose
// badly, and a person reviewing it will see that.
//
// SHAPED FOR THE WORK PEOPLE ACTUALLY HAVE. A schema built around
// title/company/start/end fails most of the people this is for: piece work,
// informal trade, a family business, seasonal jobs, gaps. So Organisation is
// optional, dates are free text, and Formal is a flag rather than an
// assumption. Someone who has never had a payslip still has a work history, and
// it has to record that without making them feel like a bad fit for their own
// phone.
//
// NONE OF IT LEAVES THE DEVICE. Employment history, contact details and a
// location are personal information under POPIA, and this is the one file most
// able to cause harm if it were synced. There is no sync path here — not a
// disabled one, not a configurable one. Adding one is a decision somebody has
// to make deliberately, in the open, with consent attached.

using System;
using System.Collections.Generic;

namespace CircleAI.Career;

/// <summary>Who somebody is, in the fields a CV needs.</summary>
/// <param name="FullName">As they want it printed.</param>
/// <param name="Headline">"Security officer", "Driver", "Bookkeeper".</param>
/// <param name="Phone">The number an employer would call.</param>
/// <param name="Email">Optional — many people applying for work have none.</param>
/// <param name="Location">"Soweto, Johannesburg". Employers filter on it.</param>
/// <param name="Summary">A few lines in their own words, if they gave any.</param>
public sealed record ProfileIdentity(
    string  FullName  = "",
    string  Headline  = "",
    string? Phone     = null,
    string? Email     = null,
    string? Location  = null,
    string? Summary   = null);

/// <summary>Something somebody can do.</summary>
/// <param name="Name">"Forklift", "Cash handling", "isiZulu", "Excel".</param>
/// <param name="Years">Roughly how long, or null when they did not say.</param>
/// <param name="EvidenceHistoryId">
/// Which job this was demonstrated in, when they tied it to one.
/// </param>
/// <remarks>
/// EVIDENCE, NOT ADJECTIVES. A skill that points at the job where it was used
/// can be defended in an interview and can be cited in a tailored CV — "operated
/// a forklift at Massmart for two years" rather than "forklift: advanced". The
/// second is a claim; the first is a fact with a witness.
/// </remarks>
public sealed record ProfileSkill(
    string  Name,
    double? Years             = null,
    long?   EvidenceHistoryId = null,
    long    Id                = 0);

/// <summary>A period of work, formal or not.</summary>
/// <param name="Role">What they did. Never blank.</param>
/// <param name="Organisation">Who for. Blank for self-employed or piece work.</param>
/// <param name="Formal">
/// Whether it was a registered job with a payslip. Recorded because it changes
/// how it should be written, not because informal work counts for less.
/// </param>
/// <param name="Start">Free text — "2019", "March 2021", "about three years ago".</param>
/// <param name="End">Free text, or null for still there.</param>
/// <param name="Achievements">
/// What they actually did, in their words. The raw material a tailored CV picks
/// from.
/// </param>
public sealed record ProfileHistory(
    string       Role,
    string?      Organisation  = null,
    bool         Formal        = true,
    string?      Start         = null,
    string?      End           = null,
    IReadOnlyList<string>? Achievements = null,
    long         Id            = 0);

/// <summary>School, college or a course.</summary>
public sealed record ProfileEducation(
    string  Qualification,
    string? Institution = null,
    string? Year        = null,
    bool    Completed   = true,
    long    Id          = 0);

/// <summary>A licence, ticket or certificate.</summary>
/// <remarks>
/// Its own type rather than a skill because in this market these are often the
/// thing that decides an application outright — a driver's licence code, a PSIRA
/// grade, a first-aid certificate. They deserve to be asked about directly.
/// </remarks>
public sealed record ProfileCertification(
    string  Name,
    string? Issuer  = null,
    string? Year    = null,
    string? Expires = null,
    long    Id      = 0);

/// <summary>A language and how well it is spoken.</summary>
/// <remarks>
/// First-class in a country with eleven official languages, where being able to
/// serve customers in isiZulu and Sesotho is a qualification and not a footnote.
/// </remarks>
public sealed record ProfileLanguage(string Name, string? Level = null, long Id = 0);

/// <summary>Everything known about somebody, at one moment.</summary>
public sealed record CareerProfile(
    ProfileIdentity                     Identity,
    IReadOnlyList<ProfileHistory>       History,
    IReadOnlyList<ProfileSkill>         Skills,
    IReadOnlyList<ProfileEducation>     Education,
    IReadOnlyList<ProfileCertification> Certifications,
    IReadOnlyList<ProfileLanguage>      Languages)
{
    /// <summary>An empty profile, before anybody has answered anything.</summary>
    public static CareerProfile Empty { get; } = new(
        new ProfileIdentity(),
        Array.Empty<ProfileHistory>(),
        Array.Empty<ProfileSkill>(),
        Array.Empty<ProfileEducation>(),
        Array.Empty<ProfileCertification>(),
        Array.Empty<ProfileLanguage>());

    /// <summary>Roughly how complete this is, 0..1, for showing progress.</summary>
    /// <remarks>
    /// Weighted by what an employer looks for first, not by field count. A name
    /// and a phone number with one job is a usable CV; ten skills and no way to
    /// contact anybody is not. This drives the "your CV is taking shape" feeling
    /// during the interview, so it has to move when something important lands.
    /// </remarks>
    public double Completeness()
    {
        double score = 0, total = 0;

        void Weigh(bool have, double weight) { total += weight; if (have) score += weight; }

        Weigh(!string.IsNullOrWhiteSpace(Identity.FullName), 3);
        Weigh(!string.IsNullOrWhiteSpace(Identity.Phone),    3);
        Weigh(!string.IsNullOrWhiteSpace(Identity.Headline), 2);
        Weigh(!string.IsNullOrWhiteSpace(Identity.Location), 1);
        Weigh(History.Count > 0,        4);
        Weigh(Skills.Count > 0,         2);
        Weigh(Education.Count > 0,      1);
        Weigh(Certifications.Count > 0, 1);
        Weigh(Languages.Count > 0,      1);

        return total <= 0 ? 0 : score / total;
    }
}
