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

    // THE WAYS THE NOTES GET NAMED, as the P30's 0.8B named them: "based on your background"
    // (2026-10-17), "based on the facts provided", "using your provided facts", "as established
    // by the facts" (2026-10-20), and the shapes a review worked out from those on 2026-10-21 -
    // "the information you provided", "(as stated in the notes)". With no label on the notes
    // at all they became "guidelines" and "text": "...based on the provided guidelines:",
    // "...based on the provided text:" (2026-10-20).
    private const string Det = @"(?:the|your|these|those|my)";
    private const string Pre = @"(?:(?:provided|given|supplied|shared)\s+)";
    private const string Noun = @"(?:background|facts|notes|information|context|details|guidelines|text)";
    private const string Post = @"(?:(?:provided|given|supplied|shared|above|(?:that\s+)?you(?:'ve|’ve|\s+have)?\s+(?:gave|given|provided|shared|sent|supplied)(?:\s+me)?)(?:\s+above)?)";
    // Wording that only ever points at something handed over: "as established by", "as per".
    private const string AsPer = @"(?:as\s+(?:established|stated|given|mentioned|shown|listed|set\s+out|provided|outlined|described|noted)\s+(?:by|in)|as\s+per)";
    private const string Opener = @"(?:" + AsPer + @"|based\s+on|according\s+to|in\s+line\s+with|drawing\s+on|going\s+by|judging\s+by|using|from|per)";
    private const string Named = Det + @"\s+" + Pre + "?" + Noun + @"(?:\s+" + Post + ")?";

    // AT THE START OF THE ANSWER OR OF ANY SENTENCE IN IT: "Based on your background, ...",
    // "Here is the summary of the facts provided: ...", "Sure! Based on the facts provided,
    // here is a plan", "As established by the facts, plant ...". A lead-in comes out whole -
    // what follows it stands on its own - and the next word takes its capital.
    [GeneratedRegex(@"(?:^|(?<=[.!?]\s+))\s*(?:" + Opener + @"|here\s+is\s+(?:the|a)\s+(?:short\s+|quick\s+)?summary\s+of)\s+" + Named + @"\s*[,:]\s*(?<first>\p{L})?",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotesLead();

    // AN ASIDE: "Plant from October to December (as stated in the notes)."
    [GeneratedRegex(@"\s*[(\[]\s*" + Opener + @"\s+" + Named + @"\s*[)\]]",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotesInBrackets();

    // AN ASIDE BETWEEN DASHES: "...to December - as established by the facts - and keep..."
    [GeneratedRegex(@"(?:\s+-\s+|\s*[–—]\s*)" + Opener + @"\s+" + Named + @"(?:\s+-\s+|\s*[–—]\s*)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotesBetweenDashes();

    // MID-SENTENCE, ONLY WHERE THE WORDS SAY WHERE THEY CAME FROM. "Here is a plan for your
    // family meals based on the facts provided: ..." names the notes; "The CCMA decides based
    // on the facts." does not, and must survive. So the opener says so ("as established by
    // the facts"), or the words do ("the facts provided", "your provided facts", "the
    // information you gave me"), or the noun is one only the notes are ("the notes", "your
    // facts").
    [GeneratedRegex(@",?\s+(?:" + AsPer + @"\s+" + Named
                    + @"|" + Opener + @"\s+" + Det + @"\s+(?:" + Pre + Noun + @"(?:\s+" + Post + @")?|" + Noun + @"\s+" + Post + @")"
                    + @"|" + Opener + @"\s+(?:" + Det + @"\s+notes|(?:your|these|those)\s+(?:facts|guidelines))(?:\s+" + Post + @")?"
                    + @")(?=\s*[,:.;!?)\]]|\s*$)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotesMidSentence();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex RunOfSpaces();

    [GeneratedRegex(@"[ \t]+([,.;:!?])", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceBeforeStop();

    [GeneratedRegex(@"([.!?])\s*,", RegexOptions.CultureInvariant)]
    private static partial Regex CommaAfterStop();

    // "...to December - as established by the facts." loses the words and keeps the dash.
    [GeneratedRegex(@"\s+[-–—]\s*(?=[.!?,;:]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DanglingDash();

    /// <summary>The answer to give, given what the model said to <paramref name="asked"/>.</summary>
    /// <param name="asked">The question as the person asked it.</param>
    /// <param name="answer">The cleaned answer.</param>
    /// <param name="cutShort">Generation stopped at the token cap or the deadline, not at its own end.</param>
    /// <remarks>
    /// THE NOTES COME OUT FIRST, and the rules after judge what is left - what the person
    /// would hear. Checked the other way round, "Yes, based on your notes." was not a bare
    /// yes; with the notes gone it was one, and went out as one (review, 2026-10-21). An
    /// answer that was nothing but a lead-in comes back empty, so the caller says it could
    /// not answer instead of speaking "Based on the facts provided:".
    /// </remarks>
    public static string Check(string? asked, string answer, bool cutShort = false)
    {
        if (string.IsNullOrWhiteSpace(answer)) return answer;

        var spoken = WithoutTheNotes(answer);
        if (cutShort) spoken = WholeSentences(spoken);
        if (string.IsNullOrWhiteSpace(spoken)) return string.Empty;

        if (IsBareYesNo(spoken) && !IsYesNoQuestion(asked))
            return AssistantPersona.DidNotCatchThat;

        if (IsOneUnknownWord(asked) && SoundsLikeAMiss(spoken))
            return AssistantPersona.DidNotCatchThat;

        return spoken;
    }

    /// <summary><paramref name="answer"/> without the words naming the notes it was given; the same string when there are none.</summary>
    public static string WithoutTheNotes(string answer)
    {
        if (string.IsNullOrEmpty(answer)) return answer;

        var text = NotesLead().Replace(answer, m => m.Groups["first"].Success ? m.Groups["first"].Value.ToUpperInvariant() : string.Empty);
        text = NotesInBrackets().Replace(text, string.Empty);
        text = NotesBetweenDashes().Replace(text, " ");
        text = NotesMidSentence().Replace(text, string.Empty);
        if (string.Equals(text, answer, StringComparison.Ordinal)) return answer;

        text = DanglingDash().Replace(RunOfSpaces().Replace(text, " "), string.Empty);
        text = CommaAfterStop().Replace(SpaceBeforeStop().Replace(text, "$1"), "$1");
        return text.Trim();
    }

    /// <summary><paramref name="answer"/> without the piece of sentence a cut left at its end.</summary>
    /// <remarks>
    /// A SPOKEN ANSWER IS CUT AT 80 TOKENS, and a cut can land mid-word; read aloud, half a
    /// sentence sounds broken. So a CUT answer ends on its last whole sentence - a full
    /// stop, "!" or "?" followed by a space, never the point in "3.5" or the "2." of a
    /// list. With no whole sentence to end on, it is left as it is.
    /// </remarks>
    public static string WholeSentences(string answer)
    {
        var text = answer.TrimEnd();
        if (text.Length == 0 || ".!?\"')".Contains(text[^1])) return text;
        for (var i = text.Length - 2; i > 0; i--)
        {
            if (text[i] is not ('.' or '!' or '?') || !char.IsWhiteSpace(text[i + 1])) continue;
            if (text[i] == '.' && char.IsDigit(text[i - 1])) continue;   // "2." of a list, not a sentence end
            return text[..(i + 1)];
        }
        return text;
    }

    // What a model says when it has not understood a word but answers anyway.
    private static readonly string[] Misses =
    [
        "sorry", "cannot", "can't", "unable", "please provide", "please try again", "don't understand",
        "do not understand", "not sure what", "clarify",
    ];

    /// <summary>Whether <paramref name="asked"/> is a single word, and not a short one.</summary>
    /// <remarks>
    /// THE RECORDED GARBLE IS ONE WORD: "Placeculeeai." With the persona's instruction to ask
    /// what they meant in front of it, on 2026-10-20 the 0.8B said "I am sorry to hear about
    /// your loss; please try again later", "I am ready to assist you! Please provide the
    /// content you need" and "I cannot placeculeai". A one-word message answered with an
    /// apology or a refusal is a word the model did not know; asking again is the answer. A
    /// one-word message it DID know ("Pretoria") gets an answer, which this leaves alone.
    /// </remarks>
    public static bool IsOneUnknownWord(string? asked)
    {
        var words = Said.Words(asked);
        // "Hey, B. Placeculeeai." is still one word after the wake phrase.
        if (words.Length == 3 && string.Equals(words[0], "hey", StringComparison.OrdinalIgnoreCase) && words[1].Length <= 2)
            words = words[2..];
        return words.Length == 1 && words[0].Length >= 4 && words[0].All(char.IsLetter);
    }

    /// <summary>Whether <paramref name="answer"/> apologises, refuses or asks for something instead of answering.</summary>
    public static bool SoundsLikeAMiss(string answer) =>
        Misses.Any(m => answer.Contains(m, StringComparison.OrdinalIgnoreCase));

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
