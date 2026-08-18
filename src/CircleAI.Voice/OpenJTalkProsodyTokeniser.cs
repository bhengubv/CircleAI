using System.Globalization;
using System.Text.RegularExpressions;

namespace CircleAI.Voice;

/// <summary>
/// Turns Japanese text into JSUT-VITS token ids, by way of Open JTalk's
/// full-context labels.
/// </summary>
/// <remarks>
/// <para>
/// This is ESPnet's <c>pyopenjtalk_prosody</c> G2P, in C#. It has to be exactly
/// that and not an approximation: the JSUT VITS model was TRAINED on this
/// tokenisation, so any disagreement is heard as a different sentence rather
/// than as a slightly worse one.
/// </para>
/// <para>
/// WHY LABELS AND NOT PHONEMES. Open JTalk can hand back a plain phoneme string,
/// and it is not enough — this vocabulary carries seven prosody symbols
/// (<c>^ $ ? _ # [ ]</c>) that only exist in the full-context labels' accent
/// fields. Japanese is pitch-accent: 箸 and 橋 are the same phonemes and
/// different words. Dropping the accent symbols would produce intelligible but
/// visibly foreign speech, which is the failure mode we already have.
/// </para>
/// <para>
/// THE PREVIOUS JAPANESE VOICE FAILED IN A WAY THIS CANNOT. It ran a 51-token
/// table from a Chinese-dominant model, covering 50/86 hiragana and 25/90
/// katakana as standalone characters, and silently DROPPED whatever it could not
/// map — measured at CER 0.42 against human speech. Here an unmappable symbol
/// becomes <see cref="UnkId"/> and is counted in <see cref="LastUnknown"/>, so
/// the caller can refuse instead of speaking something else.
/// </para>
/// </remarks>
public sealed class OpenJTalkProsodyTokeniser
{
    /// <summary>
    /// The model's vocabulary, in the order its config.yaml lists it — INDEX IS
    /// THE TOKEN ID. Do not sort, dedupe, or "tidy" this array.
    /// </summary>
    private static readonly string[] Vocabulary =
    [
        "<blank>", "<unk>", "a", "o", "i", "[", "#", "u", "]", "e", "k", "n",
        "t", "r", "s", "N", "m", "_", "sh", "d", "g", "^", "$", "w", "cl", "h",
        "y", "b", "j", "ts", "ch", "z", "p", "f", "ky", "ry", "gy", "hy", "ny",
        "by", "my", "py", "v", "dy", "?", "ty", "<sos/eos>",
    ];

    public const int BlankId = 0;
    public const int UnkId = 1;

    private static readonly Dictionary<string, int> Ids = Build();

    private static Dictionary<string, int> Build()
    {
        var map = new Dictionary<string, int>(Vocabulary.Length, StringComparer.Ordinal);
        for (var i = 0; i < Vocabulary.Length; i++) map[Vocabulary[i]] = i;
        return map;
    }

    // Field accessors over a label shaped
    //   xx^xx-k+o=r/A:-2+1+3/B:xx-xx_xx/.../F:5_2/...!0_xx/...
    private static readonly Regex CurrentPhoneme = new(@"\-(.*?)\+", RegexOptions.Compiled);
    private static readonly Regex A1 = new(@"/A:([0-9\-]+)\+", RegexOptions.Compiled);
    private static readonly Regex A2 = new(@"\+(\d+)\+", RegexOptions.Compiled);
    private static readonly Regex A3 = new(@"\+(\d+)/", RegexOptions.Compiled);
    private static readonly Regex F1 = new(@"/F:(\d+)_", RegexOptions.Compiled);
    private static readonly Regex E3 = new(@"!(\d+)_", RegexOptions.Compiled);

    /// <summary>Absent numeric field. ESPnet uses -50; matching it keeps the
    /// comparisons below behaving identically at utterance edges.</summary>
    private const int Absent = -50;

    /// <summary>Symbols the last call could not map. Empty is the good case.</summary>
    public IReadOnlyList<string> LastUnknown { get; private set; } = [];

    /// <summary>The last token sequence, as symbols — for logs and tests.</summary>
    public IReadOnlyList<string> LastSymbols { get; private set; } = [];

    /// <summary>
    /// Tokenise Open JTalk full-context <paramref name="labels"/>, one label per
    /// line, as produced by <c>openjtalk_labels()</c>.
    /// </summary>
    public int[] Encode(string labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var lines = labels.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Encode(lines);
    }

    /// <summary>Tokenise already-split full-context labels.</summary>
    public int[] Encode(IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        var symbols = new List<string>(labels.Count + 8);
        var unknown = new List<string>();

        for (var n = 0; n < labels.Count; n++)
        {
            var current = labels[n];

            var m = CurrentPhoneme.Match(current);
            if (!m.Success) continue;
            var p3 = m.Groups[1].Value;

            // Devoiced vowels are written as capitals by Open JTalk and are NOT
            // in this vocabulary — the model was trained with them folded into
            // the plain vowels. Without this fold, every devoiced vowel (which
            // is most sentence-final -masu, -desu) becomes <unk>.
            if (p3.Length == 1 && "AEIOU".Contains(p3[0], StringComparison.Ordinal))
                p3 = p3.ToLowerInvariant();

            if (string.Equals(p3, "sil", StringComparison.Ordinal))
            {
                // Utterance-boundary silence carries the sentence type rather
                // than a sound: '$' for a statement, '?' for a question. That
                // distinction is the difference between a flat and a rising
                // final contour, so it is worth reading the label for.
                if (n == 0) symbols.Add("^");
                else if (n == labels.Count - 1)
                    symbols.Add(Numeric(E3, current) == 1 ? "?" : "$");
                continue;
            }

            if (string.Equals(p3, "pau", StringComparison.Ordinal))
            {
                symbols.Add("_");
                continue;
            }

            symbols.Add(p3);

            // Accent structure, read from THIS label and the position of the
            // next mora. a1 = pitch offset from the accent nucleus, a2 = mora
            // index in the accent phrase, a3 = mora index counted back,
            // f1 = mora count of the phrase.
            var a1 = Numeric(A1, current);
            var a2 = Numeric(A2, current);
            var a3 = Numeric(A3, current);
            var f1 = Numeric(F1, current);
            var a2Next = n + 1 < labels.Count ? Numeric(A2, labels[n + 1]) : Absent;

            // Only a vowel, moraic n, or the geminate can carry a boundary or a
            // pitch movement — a consonant is mid-mora and gets nothing.
            var carries = p3.Length == 1 && "aeiouAEIOUN".Contains(p3[0], StringComparison.Ordinal)
                          || string.Equals(p3, "cl", StringComparison.Ordinal);

            if (a3 == 1 && a2Next == 1 && carries) symbols.Add("#");        // phrase border
            else if (a1 == 0 && a2Next == a2 + 1 && a2 != f1) symbols.Add("]"); // pitch fall
            else if (a2 == 1 && a2Next == 2) symbols.Add("[");               // pitch rise
        }

        var ids = new int[symbols.Count];
        for (var i = 0; i < symbols.Count; i++)
        {
            if (Ids.TryGetValue(symbols[i], out var id)) ids[i] = id;
            else { ids[i] = UnkId; unknown.Add(symbols[i]); }
        }

        LastSymbols = symbols;
        LastUnknown = unknown;
        return ids;
    }

    private static int Numeric(Regex re, string label)
    {
        var m = re.Match(label);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer,
                                         CultureInfo.InvariantCulture, out var v)
            ? v
            : Absent;
    }

    /// <summary>The symbol for an id, for diagnostics.</summary>
    public static string SymbolFor(int id) =>
        id >= 0 && id < Vocabulary.Length ? Vocabulary[id] : "<oob>";
}
