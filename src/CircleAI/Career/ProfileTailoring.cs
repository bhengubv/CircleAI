#nullable enable

// ProfileTailoring.cs
//
// Putting the best foot forward, without lying.
//
// THE MODEL CHOOSES; IT DOES NOT WRITE THE FACTS. Tailoring a CV to a job means
// deciding which of somebody's real experience to lead with, which skills the
// advert is actually asking for, and what to leave out because it is not
// relevant to this employer. That is selection and ordering. It is not
// invention, and the difference between the two is the difference between a
// person walking into an interview confident and a person walking into one
// about to be caught out.
//
// SO THE BOUNDARY IS ENFORCED IN CODE, NOT IN A PROMPT. The model returns IDS,
// not prose: the ids of the history rows and skills to include, in order. The
// document is then rendered from the stored facts belonging to those ids. A
// model that hallucinates an employer produces an id that does not exist, and an
// id that does not exist is dropped. There is no path by which a sentence the
// model invented reaches the page.
//
// A prompt saying "do not invent facts" is advice. This is a mechanism.
//
// WHAT IS LEFT OUT IS AS IMPORTANT AS WHAT IS KEPT. A CV that lists eleven jobs
// for a job needing two reads as unfocused; the same person with the three
// relevant ones reads as a fit. Leaving things out is the service being
// performed — and because nothing is deleted from the profile, the next
// application starts from everything again.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CircleAI.Career;

/// <summary>What the model decided to put forward, and why.</summary>
/// <param name="HistoryIds">Work to include, most relevant first.</param>
/// <param name="SkillIds">Skills to include, most relevant first.</param>
/// <param name="Headline">
/// A headline aimed at this advert, or null to keep the profile's own.
/// </param>
/// <param name="Reasoning">
/// One line the PERSON reads, not a log — "led with the security work because
/// the advert asks for PSIRA". They are about to put their name on this.
/// </param>
public sealed record TailoringChoice(
    IReadOnlyList<long> HistoryIds,
    IReadOnlyList<long> SkillIds,
    string?             Headline,
    string              Reasoning);

/// <summary>Matching a profile to one job advert.</summary>
public static class ProfileTailoring
{
    /// <summary>
    /// The question put to the model. Ids in, ids out.
    /// </summary>
    /// <remarks>
    /// The profile is rendered as a numbered inventory rather than as prose so
    /// that answering with anything except ids is obviously wrong — a model
    /// inclined to write a CV instead of choosing one has nothing here that
    /// looks like a place to start writing.
    /// </remarks>
    public static string BuildPrompt(CareerProfile profile, JobSpec spec)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(spec);

        var sb = new StringBuilder();
        sb.AppendLine("You are choosing which of a person's REAL experience to put on a CV for one job.");
        sb.AppendLine("You may only choose from the numbered items. You may not add anything.");
        sb.AppendLine();
        sb.AppendLine("THE JOB:");
        sb.AppendLine(spec.Title + (spec.Employer is null ? "" : $" at {spec.Employer}"));
        sb.AppendLine(Trim(spec.Text, 1200));
        sb.AppendLine();

        sb.AppendLine("THEIR WORK (id: what they did):");
        foreach (var h in profile.History)
        {
            var org = string.IsNullOrWhiteSpace(h.Organisation) ? "self-employed" : h.Organisation;
            var did = h.Achievements is { Count: > 0 } ? " — " + string.Join("; ", h.Achievements) : "";
            sb.AppendLine($"{h.Id}: {h.Role} at {org}{did}");
        }
        sb.AppendLine();

        sb.AppendLine("THEIR SKILLS (id: skill):");
        foreach (var s in profile.Skills) sb.AppendLine($"{s.Id}: {s.Name}");
        sb.AppendLine();

        sb.AppendLine("Answer with ONLY these three lines and nothing else:");
        sb.AppendLine("WORK: comma-separated ids, most relevant first");
        sb.AppendLine("SKILLS: comma-separated ids, most relevant first");
        sb.AppendLine("WHY: one short sentence for the applicant");

        return sb.ToString();
    }

    /// <summary>
    /// Reads the model's answer, keeping only ids that actually exist.
    /// </summary>
    /// <remarks>
    /// EVERY ID IS CHECKED AGAINST THE PROFILE. This is the mechanism the whole
    /// design rests on: an invented job cannot survive, because it arrives as a
    /// number with nothing behind it and is dropped. A model that returns
    /// nothing usable falls back to the profile in its own order — the person
    /// gets their honest CV rather than an error.
    /// </remarks>
    public static TailoringChoice Parse(string? answer, CareerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var validHistory = profile.History.Select(h => h.Id).ToHashSet();
        var validSkills  = profile.Skills.Select(s => s.Id).ToHashSet();

        var work   = new List<long>();
        var skills = new List<long>();
        var why    = "";

        foreach (var raw in (answer ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("WORK:", StringComparison.OrdinalIgnoreCase))
                work.AddRange(Ids(line[5..], validHistory));
            else if (line.StartsWith("SKILLS:", StringComparison.OrdinalIgnoreCase))
                skills.AddRange(Ids(line[7..], validSkills));
            else if (line.StartsWith("WHY:", StringComparison.OrdinalIgnoreCase))
                why = line[4..].Trim();
        }

        // NOTHING USABLE IS NOT AN ERROR. A small model on a cheap phone will
        // sometimes answer with prose. The person still gets a complete, honest
        // CV — just not a tailored one — and that is a far better failure than a
        // blank screen at the moment they were promised a document.
        if (work.Count == 0) work = profile.History.Select(h => h.Id).ToList();
        if (skills.Count == 0) skills = profile.Skills.Select(s => s.Id).ToList();

        return new TailoringChoice(
            work.Distinct().ToList(),
            skills.Distinct().ToList(),
            Headline: null,
            Reasoning: string.IsNullOrWhiteSpace(why)
                ? "Kept everything — this is your full history."
                : why);
    }

    /// <summary>The ids a choice selected, for storing beside an approval.</summary>
    public static IReadOnlyList<long> SelectedFacts(TailoringChoice choice) =>
        choice.HistoryIds.Concat(choice.SkillIds).ToList();

    private static IEnumerable<long> Ids(string csv, HashSet<long> valid) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
           .Select(s => long.TryParse(s.Trim(), NumberStyles.Integer,
                                      CultureInfo.InvariantCulture, out var v) ? v : -1)
           .Where(valid.Contains);

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
