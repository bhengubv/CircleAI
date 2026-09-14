// ArithmeticIntent.cs
//
// Recognises a question that is PLAINLY a sum and answers it from the engine,
// so a number the 0.6B cannot work out - measured on a P30 2026-09-14, "347
// times 89" came back "$30621" (real 30883) and the model did NOT call the
// offered calculator - is computed exactly and never reaches the model at all.
//
// This is the deterministic arithmetic path: correctness that depends on neither
// the model doing the sum nor the model choosing to delegate it. The engine
// recognises the sum, works it out, and answers. Both heads reach it through the
// one shared turn (BrainToolFlow), and it lives in CircleAI.Assistant beside the
// evaluator so the web head and the phone answer identically.
//
// CONSERVATIVE BY CONSTRUCTION. It answers only when, after every operator word
// it knows has become a symbol, NOTHING is left but digits, operators and
// brackets. So "what time is it", "how many times did I call", "times square"
// and every other sentence that merely CONTAINS an operator word fall straight
// through to the model. The asymmetry is the whole design: a false positive
// answers a real question with a number, which is bad; a false negative just
// runs the model, which is exactly what happened before. So the gate is strict,
// and the only claims it makes are ones the evaluator can prove.

using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace CircleAI.Assistant;

/// <summary>Answers a question that is plainly a sum, from the engine, or declines.</summary>
public static class ArithmeticIntent
{
    /// <summary>
    /// If <paramref name="question"/> is plainly arithmetic, sets
    /// <paramref name="answer"/> to the worked-out result and returns true.
    /// Otherwise returns false and the caller carries on to the model.
    /// </summary>
    public static bool TryAnswer(string? question, out string answer)
    {
        answer = "";
        if (string.IsNullOrWhiteSpace(question)) return false;

        var expr = ToExpression(question!);
        if (expr is null) return false;

        // The evaluator is the final arbiter: even a normalisation that produced
        // a malformed expression (a double operator, a dangling bracket) declines
        // here rather than guessing, and the model runs as before.
        if (!Arithmetic.TryEvaluate(expr, out var value)) return false;

        answer = Arithmetic.Format(value);
        return true;
    }

    // Whole-word lead-ins and lead-outs a person wraps a sum in. Longest first so
    // "what is" is taken before any shorter prefix could be.
    private static readonly string[] LeadIns =
    {
        "what is", "what's", "whats", "what does", "how much is", "how many is",
        "work out", "the answer to", "tell me", "give me", "calculate", "compute",
        "please",
    };

    private static readonly string[] LeadOuts =
    {
        "equal to", "equals", "equal", "please",
    };

    // Operator WORDS to symbols. Longest first, so "multiplied by" wins over a
    // bare word and "divided by" is one operator rather than a divide then a word.
    private static readonly (string Word, string Symbol)[] Operators =
    {
        ("multiplied by", " * "),
        ("divided by",    " / "),
        ("divide by",     " / "),
        ("times",         " * "),
        ("plus",          " + "),
        ("minus",         " - "),
        ("over",          " / "),
    };

    // "20 percent of 80" / "20% of 80" -> "(20/100)*80". Consumes the "of" here so
    // it is not left as a stray word for the gate to reject.
    private static readonly Regex PercentOf = new(
        @"(\d+(?:\.\d+)?)\s*(?:percent|%)\s+of\s+(\d+(?:\.\d+)?)",
        RegexOptions.CultureInvariant);

    // "3 x 4" / "6x4" -> a multiply, but only between digits, so "box" is untouched.
    private static readonly Regex LetterX = new(
        @"(?<=\d)\s*x\s*(?=\d)", RegexOptions.CultureInvariant);

    // THE GATE. Only digits, whitespace, the five operators, brackets and a dot.
    private static readonly Regex Gate = new(
        @"^[\d\s+\-*/().]+$", RegexOptions.CultureInvariant);

    private static string? ToExpression(string question)
    {
        var s = question.Trim().ToLowerInvariant();

        // Punctuation that is never part of a sum here. A comma is a thousands
        // separator ("1,000" -> "1000"); a trailing dot is a sentence full stop,
        // not a decimal point.
        s = s.Replace("?", " ").Replace(",", "");
        if (s.EndsWith('.')) s = s[..^1];

        s = StripPhrases(s, LeadIns, atStart: true);
        s = StripPhrases(s, LeadOuts, atStart: false);

        s = PercentOf.Replace(s, m => $"({m.Groups[1].Value}/100)*{m.Groups[2].Value}");

        foreach (var (word, symbol) in Operators)
            s = Regex.Replace(s, $@"\b{Regex.Escape(word)}\b", symbol);

        s = s.Replace('×', '*').Replace('÷', '/');
        s = LetterX.Replace(s, "*");

        s = Regex.Replace(s, @"\s+", " ").Trim();

        // Anything left that is not a number or an operator means this was a
        // sentence that merely contained an operator word, not a sum.
        if (!Gate.IsMatch(s)) return null;

        // And it must actually BE arithmetic: a number, and something to do to it.
        if (!s.Any(char.IsDigit)) return null;
        if (!s.Any(c => c is '+' or '-' or '*' or '/')) return null;

        return s;
    }

    // Removes a known phrase from the start (or end) of the string, repeatedly, so
    // "please calculate 2 + 2" sheds both words. A phrase matches only on a word
    // boundary, so it can never eat part of a number or a neighbouring word.
    private static string StripPhrases(string s, string[] phrases, bool atStart)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var p in phrases)
            {
                if (atStart)
                {
                    if (s.Length >= p.Length && s.StartsWith(p, StringComparison.Ordinal)
                        && (s.Length == p.Length || !char.IsLetterOrDigit(s[p.Length])))
                    {
                        s = s[p.Length..].TrimStart();
                        changed = true;
                    }
                }
                else
                {
                    if (s.Length >= p.Length && s.EndsWith(p, StringComparison.Ordinal)
                        && (s.Length == p.Length || !char.IsLetterOrDigit(s[s.Length - p.Length - 1])))
                    {
                        s = s[..^p.Length].TrimEnd();
                        changed = true;
                    }
                }
            }
        }
        return s;
    }
}
