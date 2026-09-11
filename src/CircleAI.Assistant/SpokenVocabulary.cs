// SpokenVocabulary.cs
//
// The words a small model gets wrong are names and money, not grammar.
//
// Measured on a P30 on 2026-09-07. A four-turn meeting played through a laptop
// speaker into the phone came back with seventy-five of seventy-eight words
// exact - whole sentences of ordinary English, perfect. The three it missed:
//
//     "Thandi"  ->  "Tandy"
//     "Sipho"   ->  "Saifo"
//     "rand"    ->  "rent"
//
// Two names and the currency of the country this app is built for. Priming the
// decoder with those words fixed all three, on the same recording, with nothing
// else changed.
//
// KEYED BY LANGUAGE, NOT BAKED IN. Shipping a South African word list to every
// handset on earth is the same mistake the download plan made when it fetched
// eleven South African languages to a phone in Osaka. This follows the language
// the phone is set to, so it helps where it applies and is silent everywhere
// else.
//
// AND ONLY WHERE IT HAS BEEN MEASURED. Every entry below is a language somebody
// has actually put a recording through and compared. There is no entry for a
// language just because the app speaks it, because a prompt is a BIAS: whisper
// will occasionally emit a primed word that was never said, and guessing at a
// vocabulary for a language nobody has tested trades a known small error rate
// for an unknown one.

using System;
using System.Collections.Generic;

namespace CircleAI.Samples.It;

/// <summary>Words worth priming the recogniser with, per language.</summary>
public static class SpokenVocabulary
{
    /// <summary>
    /// The primed text for each language, or nothing where none was measured.
    /// </summary>
    /// <remarks>
    /// Written as a sentence rather than a bare word list because whisper's
    /// prompt is TEXT, not a dictionary - it primes on how the words sit
    /// together, so "The capital request is in rand" pulls harder than "rand".
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Phrases { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // MEASURED, on the recording described in this file's header. English
            // is the language the meeting was in and the language this app falls
            // back to, and these are the words that were actually wrong - the
            // currency, and South African names - rather than a general-purpose
            // glossary somebody imagined would be useful.
            ["en"] =
                "A South African meeting. Amounts are in rand, sometimes written ZAR. "
                + "Places include Durban, Johannesburg, Pretoria, Cape Town, Soweto and Gqeberha. "
                + "Names include Thandi, Sipho, Nomsa, Bongani, Lerato, Ayanda and Naledi.",
        };

    /// <summary>What to prime with for a language tag, or null for nothing.</summary>
    /// <remarks>
    /// COMPARED ON THE LANGUAGE, NOT THE TAG, so "en-ZA" and "en-GB" both find
    /// the English entry - the same rule the wake phrases and the voice rows
    /// needed, for the same reason.
    /// </remarks>
    public static string? For(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var root = tag.Split('-', '_')[0].Trim();
        return Phrases.TryGetValue(root, out var primed) ? primed : null;
    }
}
