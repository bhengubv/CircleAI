// PlainReply.cs
//
// An answer that is going to be spoken has no business containing asterisks.
//
// IT LEAKED ONTO THE SCREEN AND WOULD HAVE LEAKED INTO THE VOICE. On a P30 on
// 2026-10-02 the assistant answered "Tell me a joke" with
//
//     **Joke:** Why did the lobster jump into a swimming pool?
//     *(The lobster jumped in because he thought if he got wet, he'd drown too!)*
//
// rendered exactly like that in the chat bubble, asterisks and all - and the same
// string is what "Read it out" hands to the voice, which would say them.
//
// ONE OWNER, AT THE SERVICE, because the answer has three consumers and they must
// not each strip it their own way: the bubble, the voice, and the spoken turn that
// runs with the screen off. Cleaned where it is produced, so all three get the same
// sentence.
//
// CODE FENCES SURVIVE. "Answers questions and helps you write" includes writing code,
// and a backtick block is the one place these characters mean something. Stripping
// inside it would corrupt the only content whose punctuation is load-bearing.

using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CircleAI.Assistant;

/// <summary>Turns a model's markdown-flavoured reply into something sayable.</summary>
public static class PlainReply
{
    /// <summary>Strip the emphasis a chat bubble cannot render and a voice cannot say.</summary>
    /// <remarks>
    /// DELIBERATELY NOT A MARKDOWN PARSER. The job is to remove the handful of
    /// characters small models sprinkle around - bold, italics, headings, bullet
    /// stars - without touching anything whose meaning depends on them. A parser
    /// would also rewrite links, tables and lists, which is a much larger promise
    /// than the one being made here.
    /// </remarks>
    public static string Clean(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return string.Empty;

        var text = reply!;
        var outp = new StringBuilder(text.Length);
        var fenced = false;
        var i = 0;

        while (i < text.Length)
        {
            // A FENCE FLIPS THE RULE RATHER THAN BEING REMOVED. Inside one, every
            // character is content; outside, the markers are noise.
            if (Fence(text, i))
            {
                fenced = !fenced;
                outp.Append("```");
                i += 3;
                continue;
            }

            if (fenced) { outp.Append(text[i++]); continue; }

            var c = text[i];

            // Bold and italics, in both spellings. Doubles first so ** is never read
            // as two separate italics markers.
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c) { i += 2; continue; }
            if (c == '*' || c == '_')
            {
                // A LONE UNDERSCORE INSIDE A WORD IS A NAME, NOT EMPHASIS - file_name,
                // snake_case. Only strip one that sits at a word boundary.
                var before = i > 0 ? text[i - 1] : ' ';
                var after  = i + 1 < text.Length ? text[i + 1] : ' ';
                if (c == '_' && char.IsLetterOrDigit(before) && char.IsLetterOrDigit(after))
                {
                    outp.Append(c);
                    i++;
                    continue;
                }

                i++;
                continue;
            }

            // A heading's hashes, but only where a heading can start: at the
            // beginning of a line. A # mid-sentence is a number sign.
            if (c == '#' && AtLineStart(outp))
            {
                while (i < text.Length && text[i] == '#') i++;
                while (i < text.Length && text[i] == ' ') i++;
                continue;
            }

            outp.Append(c);
            i++;
        }

        // Models that have had their emphasis removed often leave the spacing behind.
        return outp.ToString().Trim();
    }

    /// <summary>What a voice can say of a piece of a reply; empty when there is nothing in it to say.</summary>
    /// <remarks>
    /// A SUM ON A PAGE IS NOT A SENTENCE. On the P30 on 2026-11-07 "How many legs..." came back as
    /// "$3 \times 2 = 6$", which is markup to the eye and a run of symbols to a voice. And a piece that
    /// is nothing but punctuation has no sound in it at all, so it is not given to a voice to fail on.
    /// </remarks>
    public static string ForTheEar(string? piece)
    {
        var text = Clean(piece);
        if (text.Length == 0) return text;

        if (text.Contains('\\') || Sum.IsMatch(text))
        {
            foreach (var (mark, word) in SumWords) text = text.Replace(mark, word, System.StringComparison.Ordinal);
            text = Fraction.Replace(text, "${top} over ${bottom}");
            text = Sum.Replace(text, m => m.Groups["sum"].Value.Replace(" = ", " equals ", System.StringComparison.Ordinal)
                                                               .Replace(" + ", " plus ", System.StringComparison.Ordinal));
            text = Spaces.Replace(text.Replace("\\", string.Empty, System.StringComparison.Ordinal), " ").Trim();
        }

        return text.Any(char.IsLetterOrDigit) ? text : string.Empty;
    }

    private static readonly (string Mark, string Word)[] SumWords =
    [
        ("\\times", " times "), ("\\cdot", " times "), ("\\div", " divided by "), ("\\approx", " about "), ("\\%", " percent"),
    ];

    private static readonly Regex Sum = new(@"\$(?<sum>[^$\n]{1,120})\$", RegexOptions.CultureInvariant);

    private static readonly Regex Fraction = new(@"\\frac\{(?<top>[^}]*)\}\{(?<bottom>[^}]*)\}", RegexOptions.CultureInvariant);

    private static readonly Regex Spaces = new(@"[ \t]{2,}", RegexOptions.CultureInvariant);

    private static bool Fence(string text, int at)
        => at + 2 < text.Length && text[at] == '`' && text[at + 1] == '`' && text[at + 2] == '`';

    private static bool AtLineStart(StringBuilder sb)
    {
        for (var i = sb.Length - 1; i >= 0; i--)
        {
            if (sb[i] == '\n') return true;
            if (sb[i] != ' ' && sb[i] != '\t') return false;
        }

        return true;
    }
}
