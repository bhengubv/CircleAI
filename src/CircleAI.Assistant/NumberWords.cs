// NumberWords.cs
//
// Turns spoken number words into digits - "three hundred forty seven" -> "347" -
// so ArithmeticIntent can work out a sum a person SAID, not only one they typed.
// Whisper transcribes small numbers as words at least as often as digits, so
// without this the voice path could never answer "three forty seven times eighty
// nine" the way the typed path answers "347 times 89".
//
// Pure BCL, in CircleAI.Assistant, so the web head and the phone digitise
// identically. It only REWRITES runs of number words to their value; every other
// word passes through untouched, so the conservative gate in ArithmeticIntent
// still throws out anything that is not, in the end, a bare sum.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace CircleAI.Assistant;

/// <summary>Rewrites runs of English number words in a string as digits.</summary>
public static class NumberWords
{
    // Below a hundred: units, teens and tens. "a"/"an" become one, but only next
    // to a scale (handled in the scan), so "a joke" is never a number.
    private static readonly Dictionary<string, long> Small = new(StringComparer.Ordinal)
    {
        ["zero"] = 0,
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
        ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
        ["eighteen"] = 18, ["nineteen"] = 19,
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    private const string Hundred = "hundred";

    private static readonly Dictionary<string, long> Scales = new(StringComparer.Ordinal)
    {
        ["thousand"] = 1000, ["million"] = 1000000, ["billion"] = 1000000000,
    };

    private static bool IsNumberWord(string w)
        => Small.ContainsKey(w) || w == Hundred || Scales.ContainsKey(w);

    private static bool SoftOne(string[] t, int i)
        => (t[i] == "a" || t[i] == "an")
           && i + 1 < t.Length && (t[i + 1] == Hundred || Scales.ContainsKey(t[i + 1]));

    /// <summary>Replace each run of number words in <paramref name="text"/> with its value.</summary>
    public static string Digitise(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var outp = new List<string>(tokens.Length);

        int i = 0;
        while (i < tokens.Length)
        {
            if (!StartsRun(tokens, i)) { outp.Add(tokens[i]); i++; continue; }

            var run = new List<string>();
            int j = i;
            while (j < tokens.Length && InRun(tokens, j))
            {
                if (SoftOne(tokens, j)) run.Add("one");           // "a hundred" -> one hundred
                else if (tokens[j] != "and") run.Add(tokens[j]);  // "and" is a connector, not a value
                j++;
            }

            outp.Add(Value(run).ToString(CultureInfo.InvariantCulture));
            i = j;
        }

        return string.Join(' ', outp);
    }

    // A run may start at a real number word, or at "a"/"an" right before a scale.
    private static bool StartsRun(string[] t, int i)
        => IsNumberWord(t[i]) || SoftOne(t, i);

    private static bool InRun(string[] t, int i)
    {
        if (IsNumberWord(t[i]) || SoftOne(t, i)) return true;

        // "and" stays in a run only BETWEEN number words ("three hundred and
        // forty"), never trailing into ordinary speech ("a hundred and I said").
        return t[i] == "and" && i + 1 < t.Length && (IsNumberWord(t[i + 1]) || SoftOne(t, i + 1));
    }

    // The usual accumulator: units and tens add into the current group, "hundred"
    // scales it, and a thousand/million/billion closes the group into the result.
    private static long Value(List<string> words)
    {
        long result = 0, current = 0;
        foreach (var w in words)
        {
            if (Small.TryGetValue(w, out var n)) current += n;
            else if (w == Hundred) current = (current == 0 ? 1 : current) * 100;
            else if (Scales.TryGetValue(w, out var scale))
            {
                current = (current == 0 ? 1 : current) * scale;
                result += current;
                current = 0;
            }
        }
        return result + current;
    }
}
