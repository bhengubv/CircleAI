// Opener.cs
//
// "Hey B" is not a question.
//
// IT WAS PUT TO THE BRAIN AS ONE. On a P30 on 2026-10-03 somebody said "Hey B"
// into the app. Whisper returned the seven characters "Hey, B.", and those seven
// characters went to the model as the thing to answer. What came back took
// thirty-eight and a half seconds:
//
//     stt     : 25,6 s audio -> 7 chars
//     skills  : 3 hits - heygen-automation, heyreach-automation, heyzine-automation
//     prompt  : 573 tokens
//     prefill : 26 256 ms        decode: 1 018 ms
//
// Twenty-six seconds of prefill because "hey" is a substring of HeyGen. The
// substring half of that is fixed in SqliteSkillStore; this is the other half,
// and it is the one that matters more: a greeting should never have reached a
// language model at all. It has a known answer, it costs nothing to give, and
// every assistant a person has ever used replies to it instantly.
//
// NOT AN INTENT CLASSIFIER, and it must never become one. It recognises somebody
// opening their mouth and nothing else. "Hey, what is the weather" has a question
// in it and goes to the model like anything else.

namespace CircleAI.Assistant;

/// <summary>Recognises a greeting that carries no question with it.</summary>
public static class Opener
{
    /// <summary>
    /// The words people open with, and nothing more.
    /// </summary>
    /// <remarks>
    /// SHORT AND DELIBERATE. Every entry here is a phrase that a person says to
    /// GET attention rather than to ask for something, so answering it from a
    /// constant costs them nothing. Anything longer than these is a question
    /// until proven otherwise - the failure to avoid is a phone that stops
    /// answering real questions because they happened to start with "hi".
    /// </remarks>
    private static readonly string[] Hellos =
    [
        "hi", "hey", "hello", "heya", "yo", "howzit", "sawubona", "molo", "dumela",
        "good morning", "good afternoon", "good evening", "morning", "evening",
    ];

    /// <summary>Is this nothing but somebody saying hello?</summary>
    /// <param name="said">A transcript, as the recogniser returned it.</param>
    /// <param name="wakePhrase">What this device answers to, when it is known.</param>
    /// <remarks>
    /// THE WAKE PHRASE IS PASSED IN, NOT BAKED IN. It is a per-device setting -
    /// DeviceWakePhrases keeps it, and somebody can change it - so a copy here
    /// would be a second owner of a fact that already has one, and would stop
    /// recognising the moment they changed it.
    /// </remarks>
    public static bool IsNothingButHello(string? said, string? wakePhrase = null)
    {
        var words = Words(said);
        if (words.Length == 0) return false;

        // The wake phrase first: it is the one somebody says ON PURPOSE before
        // anything else, and on this device it is two words of which one is a
        // single letter.
        var wake = Words(wakePhrase);
        if (wake.Length > 0 && words.Length >= wake.Length && StartsWith(words, wake))
        {
            if (words.Length == wake.Length) return true;
            words = words[wake.Length..];   // "hey b, hello" is still just hello
        }

        // Whatever is left has to be a hello and nothing else. Longest first, so
        // "good morning" is not read as "good" plus a leftover word.
        return IsJustAHello(words);
    }

    /// <summary>
    /// Words that trail a greeting and ask for nothing.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM THE HELLOS, because they are not greetings and must not pass
    /// on their own merit - "there" alone is not somebody saying hello. They are
    /// only allowed AFTER one, which is the only place they ever appear: "hi
    /// there", "hello again". Kept to the handful that genuinely carry nothing,
    /// because every word added here is a word that can no longer start a
    /// question.
    ///
    /// AND THE WAKE PHRASE IS NOT ONE OF THEM. "b" was in this list for about a
    /// minute and the tests caught it: it would make "hi B" a greeting on a phone
    /// whose wake word is something else entirely, and it is a second copy of a
    /// fact the listener already owns. The wakePhrase argument is where that
    /// belongs.
    /// </remarks>
    private static readonly string[] Fillers = ["there", "again"];

    private static bool IsJustAHello(string[] words)
    {
        if (words.Length == 0) return false;

        var at = 0;
        var greeted = false;

        // Longest hello first, so "good morning" is never read as "good" with a
        // leftover word.
        while (at < words.Length)
        {
            var matched = 0;
            foreach (var hello in Hellos)
            {
                var parts = hello.Split(' ');
                if (parts.Length <= matched) continue;
                if (words.Length - at < parts.Length) continue;
                if (StartsWith(words[at..], parts)) matched = parts.Length;
            }

            if (matched == 0) break;
            at += matched;
            greeted = true;
        }

        if (!greeted) return false;

        // AND WHATEVER IS LEFT HAS TO ASK FOR NOTHING. One unexplained word is a
        // question - "hi, weather" is a person asking about the weather.
        for (; at < words.Length; at++)
            if (!Fillers.Contains(words[at], StringComparer.OrdinalIgnoreCase))
                return false;

        return true;
    }

    private static bool StartsWith(string[] words, string[] prefix)
    {
        for (var i = 0; i < prefix.Length; i++)
            if (!string.Equals(words[i], prefix[i], StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }

    /// <summary>The words, with the punctuation a recogniser adds taken off.</summary>
    /// <remarks>
    /// WHISPER PUNCTUATES. It returned "Hey, B." - a comma and a full stop that
    /// nobody spoke - so comparing raw strings would miss the very transcript this
    /// exists for.
    /// </remarks>
    private static string[] Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var cleaned = new System.Text.StringBuilder(text!.Length);
        foreach (var c in text!)
            cleaned.Append(char.IsLetterOrDigit(c) ? c : ' ');

        return cleaned.ToString()
                      .Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
