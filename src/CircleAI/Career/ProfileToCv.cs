#nullable enable

// ProfileToCv.cs
//
// The profile, laid out as a CV. No model involved.
//
// THIS IS WHAT MAKES THE WAIT VISIBLE. Every answer during the interview
// produces a new document, rendered from whatever facts exist so far, so a
// person watches their CV assemble in front of them as they speak instead of
// watching a progress bar. That is the difference between showing value and
// promising it — and it works in the first two minutes, before any brain has
// finished downloading, because laying out known facts needs arithmetic and a
// template, not intelligence.
//
// The model's job comes later and is different: choosing WHICH facts to lead
// with for a particular job, and phrasing them in the advert's own words where
// that is honestly true. That is tailoring, and it is in ProfileTailoring. This
// file only ever arranges what is already there.
//
// NOTHING HERE INVENTS. Not a summary it wrote, not a skill it inferred, not a
// date it guessed. If a field is empty it stays empty and the template handles
// the gap. The guarantee that a CV contains only what its owner said is worth
// more than any sentence a generator could add to it.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Documents;

namespace CircleAI.Career;

/// <summary>Turns a stored profile into the document type the renderer takes.</summary>
public static class ProfileToCv
{
    /// <summary>Lays out everything known, in a sensible order.</summary>
    /// <param name="profile">The facts.</param>
    /// <param name="only">
    /// When given, the history and skill ids to include — how a tailored version
    /// narrows the document without touching the profile it came from.
    /// </param>
    public static CvDocument Render(CareerProfile profile, IReadOnlyCollection<long>? only = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var id = profile.Identity;

        // A NAME IS REQUIRED AND EVERYTHING ELSE IS NOT. The renderer needs
        // something at the top; an unnamed CV is not a document. Before the
        // first answer this reads "Your name" so the page looks like a CV
        // waiting for one rather than like an error.
        var name = string.IsNullOrWhiteSpace(id.FullName) ? "Your name" : id.FullName.Trim();

        var headline = string.IsNullOrWhiteSpace(id.Headline)
            ? MostRecentRole(profile) ?? ""
            : id.Headline.Trim();

        var history = profile.History
            .Where(h => only is null || only.Contains(h.Id))
            .Select(ToExperience)
            .ToList();

        var skills = profile.Skills
            .Where(s => only is null || only.Contains(s.Id))
            .Select(FormatSkill)
            .ToList();

        return new CvDocument(
            FullName:   name,
            Headline:   headline,
            Contact:    new CvContact(
                            Email:    Blank(id.Email),
                            Phone:    Blank(id.Phone),
                            Location: Blank(id.Location)),
            Summary:    Blank(id.Summary),
            Experience: history,
            Education:  profile.Education.Select(ToEducation).ToList(),
            Skills:     skills,
            Certifications: profile.Certifications.Count == 0
                            ? null
                            : profile.Certifications
                                .Select(c => new CvCertification(c.Name, c.Issuer, c.Year))
                                .ToList(),
            Languages:  profile.Languages.Count == 0
                            ? null
                            : profile.Languages
                                .Select(l => l.Level is null ? l.Name : $"{l.Name} ({l.Level})")
                                .ToList());
    }

    /// <summary>
    /// A period of work, written as work rather than as employment.
    /// </summary>
    /// <remarks>
    /// SELF-EMPLOYED IS NOT A BLANK. A history row with no organisation is
    /// somebody who worked for themselves, and printing an empty line there
    /// makes a CV look unfinished — which is the opposite of the truth about
    /// somebody who ran their own stall. It says so instead.
    /// </remarks>
    private static CvExperience ToExperience(ProfileHistory h) =>
        new(Title:        h.Role,
            Organisation: string.IsNullOrWhiteSpace(h.Organisation)
                            ? (h.Formal ? "" : "Self-employed")
                            : h.Organisation!,
            Location:     null,
            StartDate:    h.Start ?? "",
            EndDate:      h.End,
            Highlights:   h.Achievements ?? Array.Empty<string>());

    private static CvEducation ToEducation(ProfileEducation e) =>
        new(Qualification: e.Completed ? e.Qualification : e.Qualification + " (not completed)",
            Institution:   e.Institution ?? "",
            Location:      null,
            StartDate:     null,
            EndDate:       e.Year);

    /// <summary>A skill, with its years when they were given.</summary>
    private static string FormatSkill(ProfileSkill s) =>
        s.Years is > 0
            ? $"{s.Name} ({s.Years:0.#} yr{(s.Years >= 2 ? "s" : "")})"
            : s.Name;

    /// <summary>The most recent role, used as a headline when none was given.</summary>
    private static string? MostRecentRole(CareerProfile p) =>
        p.History.Count > 0 ? p.History[0].Role : null;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
