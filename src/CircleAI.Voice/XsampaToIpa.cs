namespace CircleAI.Voice;

/// <summary>
/// Turns the X-SAMPA that <see cref="NchltPhonemizer"/> emits into the IPA that
/// Mimic3-family voices are trained on.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS, AND WHY IT IS NOT ESPEAK. The SA voices need phonemes, and
/// the obvious route — espeak-ng — cannot be on the install-to-first-use path:
/// it is GPL, so it lives in its own package, and an app may never require a
/// second package to work (see the one-APK rule, and the txtMe identity-service
/// incident that produced it — that "worked" only because someone hand-installed
/// the second package over adb, which is cheating, not a pass).
/// </para>
/// <para>
/// CircleAI already owns the hard half: <see cref="NchltPhonemizer"/> is pure C#
/// over the NCHLT dictionaries and context rules (CC BY 3.0, DAC/CSIR/NWU),
/// vendored in this repo, covering all 11 SA languages and self-verified at 100%
/// word and phone accuracy on isiZulu. It compiles IN. The only gap was alphabet:
/// NCHLT speaks X-SAMPA, Mimic3 listens in IPA. This table is that gap and
/// nothing more.
/// </para>
/// <para>
/// DERIVED FROM THE DATA, NOT FROM MEMORY. The 38 entries are exactly the
/// distinct phones appearing in <c>nchlt_afr.dict</c> (15 094 words), and every
/// IPA character produced was checked to exist in the voice's own
/// <c>tokens.txt</c> before this file was written. A hand-recalled phoneme table
/// is how the Ethiopic romaniser silently dropped characters; a table derived
/// from the corpus and checked against the target vocabulary cannot.
/// </para>
/// <para>
/// LONGEST MATCH FIRST. Several entries are multi-character (<c>A:r</c>,
/// <c>@i</c>, <c>9y</c>), and NCHLT emits them as single tokens. Matching
/// greedily on the token — never character by character — is what keeps
/// <c>A:r</c> from becoming <c>A</c> + <c>:</c> + <c>r</c>.
/// </para>
/// </remarks>
public static class XsampaToIpa
{
    /// <summary>
    /// Every phone in the NCHLT Afrikaans dictionary, mapped to IPA. Vowel
    /// length (<c>ː</c>) and diphthong second elements are emitted as their own
    /// IPA characters because the voice's vocabulary tokenises them separately.
    /// </summary>
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        // Vowels
        ["a"]   = "a",     ["A:"]  = "ɑː",   ["A:r"] = "ɑːr",
        ["E"]   = "ɛ",     ["O"]   = "ɔ",    ["@"]   = "ə",
        ["i"]   = "i",     ["u"]   = "u",    ["y"]   = "y",
        ["9"]   = "œ",     ["2:"]  = "øː",   ["{"]   = "æ",

        // Diphthongs — NCHLT gives one token, the voice wants both elements.
        ["9y"]  = "œy",    ["@i"]  = "əi",   ["@u"]  = "əu",
        ["i@"]  = "iə",    ["u@"]  = "uə",

        // Consonants
        ["b"]   = "b",     ["d"]   = "d",    ["f"]   = "f",
        ["g"]   = "ɡ",     // U+0261 LATIN SMALL LETTER SCRIPT G — the IPA letter,
                           // NOT ASCII 'g'. The voice's vocabulary carries ɡ; a
                           // plain 'g' would miss and be dropped.
        ["j"]   = "j",     ["k"]   = "k",    ["l"]   = "l",
        ["m"]   = "m",     ["n"]   = "n",    ["N"]   = "ŋ",
        ["p"]   = "p",     ["r"]   = "r",    ["s"]   = "s",
        ["S"]   = "ʃ",     ["t"]   = "t",    ["v"]   = "v",
        ["w"]   = "w",     ["x"]   = "x",    ["z"]   = "z",
        ["Z"]   = "ʒ",

        // APPROXIMATION, DELIBERATE AND THE ONLY ONE. X-SAMPA h\ is ɦ, the voiced
        // glottal fricative Afrikaans uses in "hond". This voice's vocabulary has
        // no ɦ, only h. Voicing is lost; the place and manner are right, so the
        // word stays recognisable. Recorded here because a silent approximation
        // is the kind of thing that later reads as a mystery in the audio.
        ["h\\"] = "h",
    };

    /// <summary>Phones the last <see cref="Convert"/> call could not map.</summary>
    /// <remarks>
    /// Empty is the good case. A phone with no mapping produces NO SOUND, and the
    /// audio is merely shorter — every acoustic measure still passes. Counting
    /// them is the only way a caller can refuse rather than speak a shorter
    /// sentence than it was given.
    /// </remarks>
    public static IReadOnlyList<string> LastUnmapped { get; private set; } = [];

    /// <summary>
    /// Convert X-SAMPA phone tokens to a flat IPA symbol list, ready to look up
    /// in a voice's token table.
    /// </summary>
    public static IReadOnlyList<string> Convert(IReadOnlyList<string> xsampa)
    {
        ArgumentNullException.ThrowIfNull(xsampa);

        var ipa = new List<string>(xsampa.Count + 8);
        var unmapped = new List<string>();

        foreach (var phone in xsampa)
        {
            if (string.IsNullOrWhiteSpace(phone)) continue;

            if (Map.TryGetValue(phone, out var mapped))
            {
                // Emit per-character: the voice tokenises ɑ, ː and r separately,
                // so "ɑːr" must arrive as three symbols, not one.
                foreach (var ch in mapped) ipa.Add(ch.ToString());
                continue;
            }

            if (!unmapped.Contains(phone)) unmapped.Add(phone);
        }

        LastUnmapped = unmapped;
        return ipa;
    }

    /// <summary>True when every phone in <paramref name="xsampa"/> has a mapping.</summary>
    public static bool CanSayAll(IReadOnlyList<string> xsampa)
    {
        ArgumentNullException.ThrowIfNull(xsampa);
        foreach (var p in xsampa)
            if (!string.IsNullOrWhiteSpace(p) && !Map.ContainsKey(p)) return false;
        return true;
    }

    /// <summary>The X-SAMPA phones this table knows — for tests and diagnostics.</summary>
    public static IReadOnlyCollection<string> KnownPhones => Map.Keys;
}
