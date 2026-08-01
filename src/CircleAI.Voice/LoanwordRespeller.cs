#nullable enable

// LoanwordRespeller.cs
//
// Writes a borrowed word the way the host language spells it.
//
// The South African voice is GRAPHEME-driven: phoneme_type is "text" and the
// vocabulary is 141 plain letters with no phoneme inventory at all. The letters
// ARE the tokens. That means the language id can change accent, tempo and prosody
// — measurably, isiZulu runs 1.20x slower than English on the same text — but it
// cannot change what a letter sounds like. "WiFi" under any language id is read
// W-i-F-i with isiZulu letter values: wee-fee. Switching language was solving the
// wrong layer.
//
// isiZulu already solved this, long before anyone tried to synthesise it. Borrowed
// words are absorbed into the spelling system: ikhompiyutha, i-inthanethi,
// isiteshi, imoto. A speaker does not read English orthography and produce English
// sounds — they read a Zulu spelling that produces the right sounds. So do we.
//
// A CURATED TABLE, not a rule engine. English spelling is too irregular for
// letter rules, and inventing a respelling for somebody else's language is how you
// end up confidently mispronouncing it. Entries below are marked by how well
// attested they are, and the uncertain ones want a native speaker's eye.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Voice;

/// <summary>How confident we are in a respelling.</summary>
public enum RespellingSource
{
    /// <summary>An established loanword in ordinary written use.</summary>
    Attested,

    /// <summary>Constructed by us from the host language's spelling rules. Wants review.</summary>
    Proposed,
}

/// <summary>Rewrites borrowed words into the host language's spelling.</summary>
public static class LoanwordRespeller
{
    /// <summary>Respellings for isiZulu and the other Nguni orthographies.</summary>
    /// <remarks>
    /// The Attested entries are words that appear in ordinary isiZulu writing. The
    /// Proposed ones are brand and acronym forms we have written out phonetically —
    /// plausible, but not something to ship at a speaker of the language without
    /// them hearing it first.
    /// </remarks>
    private static readonly Dictionary<string, (string Spelling, RespellingSource Source)> Zulu =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Established loanwords — these are simply how the words are written.
            ["internet"]  = ("inthanethi",     RespellingSource.Attested),
            ["computer"]  = ("khompiyutha",    RespellingSource.Attested),
            ["phone"]     = ("foni",           RespellingSource.Attested),
            ["email"]     = ("imeyili",        RespellingSource.Attested),
            ["SMS"]       = ("esemese",        RespellingSource.Attested),
            ["bank"]      = ("bhange",         RespellingSource.Attested),
            ["account"]   = ("akhawunti",      RespellingSource.Attested),
            ["station"]   = ("siteshi",        RespellingSource.Attested),
            ["radio"]     = ("umsakazo",       RespellingSource.Attested),
            ["taxi"]      = ("theksi",         RespellingSource.Attested),
            ["doctor"]    = ("dokotela",       RespellingSource.Attested),
            ["school"]    = ("sikole",         RespellingSource.Attested),

            // Brand and acronym forms written out phonetically. NEEDS REVIEW.
            ["WhatsApp"]  = ("wotsapha",       RespellingSource.Proposed),
            ["WiFi"]      = ("wayifayi",       RespellingSource.Proposed),
            ["GPS"]       = ("jiphiyesi",      RespellingSource.Proposed),
            ["YouTube"]   = ("yuthubhu",       RespellingSource.Proposed),
            ["Google"]    = ("gugule",         RespellingSource.Proposed),
            ["Facebook"]  = ("feyisibhuku",    RespellingSource.Proposed),
            ["airtime"]   = ("eyathayimu",     RespellingSource.Proposed),
            ["data"]      = ("datha",          RespellingSource.Proposed),
            ["ATM"]       = ("eythiyemu",      RespellingSource.Proposed),
            ["PIN"]       = ("phini",          RespellingSource.Proposed),
            ["CircleAI"]  = ("Sekhele Eyi Ayi", RespellingSource.Proposed),
        };

    /// <summary>
    /// The host-language spelling of <paramref name="word"/>, or null when we have
    /// none for it.
    /// </summary>
    /// <remarks>
    /// Returning null rather than guessing is the point. A word we cannot respell
    /// is left exactly as written, which reads it with host letter values — not
    /// ideal, but honest and predictable. A wrong invented spelling sounds like
    /// confident nonsense, which is worse.
    /// </remarks>
    public static string? Respell(string word, string hostLanguage)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        if (!IsNguniOrSotho(hostLanguage)) return null;
        return Zulu.TryGetValue(word, out var e) ? e.Spelling : null;
    }

    /// <summary>How well attested a respelling is, for callers that want to warn.</summary>
    public static RespellingSource? SourceOf(string word) =>
        Zulu.TryGetValue(word, out var e) ? e.Source : null;

    /// <summary>Every word we can respell, so a report can list what was applied.</summary>
    public static IReadOnlyCollection<string> Known => Zulu.Keys;

    /// <summary>
    /// Every word and how we say it today, for a listener learning how one person
    /// actually says them.
    /// </summary>
    /// <remarks>
    /// Learning needs to know what it is comparing against: a transcript token only
    /// means something next to the spelling currently in use. An unsupported host
    /// language yields an empty table, so nothing is learned for a language whose
    /// letters these spellings were never written for.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Table(string hostLanguage) =>
        IsNguniOrSotho(hostLanguage)
            ? Zulu.ToDictionary(kv => kv.Key, kv => kv.Value.Spelling, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>();

    /// <summary>
    /// Do these languages share the orthography this table is written for?
    /// </summary>
    /// <remarks>
    /// The Nguni languages (isiZulu, isiXhosa, siSwati, isiNdebele) and the Sotho
    /// group spell borrowings very similarly, so one table serves them. Listed
    /// explicitly rather than assumed: applying isiZulu spellings to, say, Afrikaans
    /// would mangle words Afrikaans already has its own forms for.
    /// </remarks>
    public static bool IsNguniOrSotho(string tag) => tag.ToLowerInvariant() switch
    {
        "zu" or "zul" or "xh" or "xho" or "ss" or "ssw" or "nr" or "nbl" => true,
        "st" or "sot" or "nso" or "tn" or "tsn" => true,
        _ => false,
    };
}
