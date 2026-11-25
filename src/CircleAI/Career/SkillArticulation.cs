#nullable enable

// SkillArticulation.cs
//
// Helping somebody say what they already said, in the words a CV is read in.
//
// FOR THE PERSON WHO CAN DO THE WORK BUT NOT DESCRIBE IT. "I fix cars for my
// uncle" is a real skill, written the way it is spoken; an employer scanning a
// stack of CVs reads it as less than "Vehicle maintenance and repair". The gap
// between the two is not ability - it is phrasing, and phrasing is the one part
// of a CV a model can help with honestly.
//
// IT SUGGESTS, IT DOES NOT DECIDE. What this builds is a PROPOSAL the person sees
// beside their own words and accepts, edits or rejects before anything is stored.
// That is the whole safety of it: the model may only ever reword, never add, and
// the person confirms every word they will have to defend in an interview.
// ProfileToCv still invents nothing - this only changes how the person's own
// answer is worded, and only with their say-so.
//
// ONLY WHERE WORDING IS THE WORK. A name, a phone number, a place and a date are
// facts to get exactly right, not to make eloquent - those questions are marked
// Verify and left alone. The descriptive answers - a headline, what they did,
// what they are good at, a closing word about themselves - are the ones where a
// better sentence is worth offering.
//
// BUILT FOR A SMALL MODEL ON A CHEAP PHONE. The prompt is short and the parse is
// forgiving: a model that answers with prose, a refusal, or silence yields no
// suggestion, and the person simply keeps their own words. A missing suggestion
// is not an error - it is the honest default.

using System;

namespace CircleAI.Career;

/// <summary>Proposes a CV-ready wording of a spoken answer, for the person to confirm.</summary>
public static class SkillArticulation
{
    /// <summary>Whether this field is one where wording - not fact - is the work.</summary>
    /// <remarks>
    /// Names, numbers, places and dates are left exactly as given: they are marked
    /// Verify for read-back, and "making them eloquent" would only put the one
    /// thing that must be right at risk.
    /// </remarks>
    public static bool Applies(ProfileField field) => field switch
    {
        ProfileField.Headline => true,
        ProfileField.WorkDid  => true,
        ProfileField.Skills   => true,
        ProfileField.Summary  => true,
        _ => false,
    };

    /// <summary>
    /// The question put to the model: reword what they said, and nothing more.
    /// </summary>
    /// <remarks>
    /// THE RULES ARE SPELLED OUT AND THE FIELD IS NAMED, because a small model does
    /// better with a shape to fill than with an open instruction. The no-invention
    /// rules are advice here, not a mechanism - the mechanism is that the person
    /// sees the result and confirms it. See the file header.
    /// </remarks>
    public static string BuildPrompt(InterviewQuestion question, string rawAnswer)
    {
        ArgumentNullException.ThrowIfNull(question);

        var shape = question.Field switch
        {
            ProfileField.Skills =>
                "Write it as a short, comma-separated list of skills, as they would read on a CV.",
            ProfileField.WorkDid =>
                "Write it as two or three short lines, each one plain thing they did.",
            ProfileField.Headline =>
                "Write it as a short job headline of three to five words.",
            _ =>
                "Write it as one or two short, plain sentences.",
        };

        return
            "You help someone put THEIR OWN words onto a CV, in clear, professional language.\n" +
            "\n" +
            "Rules:\n" +
            "- Use only what they said. Do not add skills, jobs, places, numbers or results they did not mention.\n" +
            "- Keep it plain. No long words they would not use in an interview.\n" +
            "- Write in the same language they used.\n" +
            "- Answer with the rewritten version only. No preamble, no quotes, no explanation.\n" +
            "\n" +
            $"They were asked: {question.Ask}\n" +
            $"They said: {rawAnswer.Trim()}\n" +
            "\n" +
            shape;
    }

    /// <summary>
    /// The usable suggestion in the model's reply, or null to keep the person's words.
    /// </summary>
    /// <remarks>
    /// NOTHING USABLE IS NOT AN ERROR. A small model on a cheap phone will sometimes
    /// refuse, explain itself, or run on; a reply that is empty, far too long, or the
    /// same as what they said is dropped, and the screen offers nothing rather than
    /// something worse than the original. The same resilience ProfileTailoring.Parse
    /// is built on.
    /// </remarks>
    public static string? Parse(string? modelReply, string rawAnswer)
    {
        var raw = (rawAnswer ?? "").Trim();
        var s = (modelReply ?? "").Trim();
        if (s.Length == 0) return null;

        // A small model likes to wrap its answer in quotes. Take them off.
        s = s.Trim('"', '“', '”', '\'', '`').Trim();
        if (s.Length == 0) return null;

        // Ran away - prose, a lecture, or the whole prompt echoed back.
        var ceiling = Math.Max(240, raw.Length * 4);
        if (s.Length > ceiling) return null;

        // Nothing gained is not worth a tap.
        if (string.Equals(s, raw, StringComparison.OrdinalIgnoreCase)) return null;

        return s;
    }
}
