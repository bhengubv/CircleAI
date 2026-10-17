// AnswerGuard.cs
//
// THE LAST LOOK AT AN ANSWER BEFORE ANYBODY HEARS IT.
//
// On the P30 on 2026-10-17 the recorded garble "Placeculeeai." went to the 0.8B and came
// back as "No." - and the quality run passed it, because nothing it checked for was in
// two letters and a full stop. A yes or a no answers a yes/no question and nothing else;
// said to anything else it is the model guessing at a sound, and the honest reply is to
// ask again. The same run answered a meal question "based on your background": the notes
// put in front of the model, quoted back as if the person had written them.

using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace CircleAI.Assistant.Requests;

/// <summary>Checks a finished answer against the question it answers.</summary>
public static partial class AnswerGuard
{
    private static readonly string[] YesNo = ["yes", "no", "nope", "yeah", "yep", "yup", "nah"];

    // The words a yes/no question opens with.
    private static readonly string[] Auxiliaries =
    [
        "is", "are", "am", "was", "were", "do", "does", "did", "can", "could", "will", "would",
        "should", "shall", "have", "has", "had", "may", "might", "must",
        "isn't", "aren't", "wasn't", "weren't", "don't", "doesn't", "didn't", "can't", "couldn't",
        "won't", "wouldn't", "shouldn't", "haven't", "hasn't", "hadn't", "mustn't",
    ];

    // "Based on your background, ..." / "Based on the information provided: ..." /
    // "Here is the summary of the facts provided: ..." (the meal tile, 2026-10-17 10:30).
    [GeneratedRegex(@"^\s*(?:based\s+on|according\s+to|here\s+is\s+(?:the|a)\s+(?:short\s+|quick\s+)?summary\s+of)\s+(?:the|your)\s+(?:background|facts|notes|information|context)(?:\s+(?:provided|given|above|you\s+gave(?:\s+me)?))?\s*[,:]\s*",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotesLead();

    /// <summary>The answer to give, given what the model said to <paramref name="asked"/>.</summary>
    /// <param name="asked">The question as the person asked it.</param>
    /// <param name="answer">The cleaned answer.</param>
    public static string Check(string? asked, string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return answer;

        if (IsBareYesNo(answer) && !IsYesNoQuestion(asked))
            return AssistantPersona.DidNotCatchThat;

        var lead = NotesLead().Match(answer);
        if (lead.Success)
        {
            var rest = answer[lead.Length..].TrimStart();
            if (rest.Length > 0) return char.ToUpperInvariant(rest[0]) + rest[1..];
        }

        return answer;
    }

    /// <summary>Whether <paramref name="answer"/> is nothing but a yes or a no.</summary>
    public static bool IsBareYesNo(string answer)
    {
        var words = Said.Words(answer);
        return words.Length is > 0 and <= 2
            && words.All(w => YesNo.Contains(w, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Whether <paramref name="asked"/> asks for a yes or a no.</summary>
    /// <remarks>
    /// Any sentence of it opening with an auxiliary ("Hey, B. Is it Friday?"), or a
    /// question mark on something that is not a wh- question ("You're Circle AI?").
    /// </remarks>
    public static bool IsYesNoQuestion(string? asked)
    {
        if (string.IsNullOrWhiteSpace(asked)) return false;

        foreach (var sentence in asked.Split(['.', '!', '?', ',', ';', ':', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var first = Said.Words(sentence).FirstOrDefault()?.Replace('’', '\'');
            if (first is not null && Auxiliaries.Contains(first, StringComparer.OrdinalIgnoreCase)) return true;
        }

        if (!asked.TrimEnd().EndsWith('?')) return false;
        var last = asked.TrimEnd('?', ' ').Split(['.', '!', '?', ';', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        var opener = Said.Words(last).FirstOrDefault()?.ToLowerInvariant();
        return opener is not ("what" or "what's" or "when" or "where" or "who" or "whom" or "whose" or "which" or "why" or "how");
    }
}
