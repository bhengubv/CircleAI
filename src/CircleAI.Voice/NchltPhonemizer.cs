#nullable enable

// NchltPhonemizer.cs
//
// A fully sovereign, permissive-licence grapheme-to-phoneme front-end for the
// South African languages — the piece that turns written text into the X-SAMPA
// phonemes a voice model consumes. It is the click-and-tone-aware component that
// generic engines get wrong on Nguni languages.
//
// WHY THIS EXISTS (and what it deliberately is NOT):
//   * NOT espeak-ng. espeak is GPLv3; linking it taints CircleAI. This is pure
//     managed C# over CC-BY data, so it ships inside the app with no GPL wall.
//   * NOT phonemeza. That project is unlicensed (all-rights-reserved) and its
//     trained weights are not even published — a hostage we will not depend on.
//   * NOT a neural model. It needs no GPU to build and no runtime for inference.
//
// It is a faithful C# port of the NCHLT pronunciation predictor (Marelie Davel,
// pron_predict.pl) driven by the NCHLT-inlang resources:
//   dictionary  (word -> X-SAMPA)         — exact for the ~15 000 catalogued words
//   .rules      (grapheme;left;right;code) — context rules for every unseen word
//   .map.phones (code -> X-SAMPA)          — remaps rule codes to the dict alphabet
//   .gnulls / .map.graphs                  — grapheme-null + grapheme remaps
// All of the above are © DAC / CSIR / NWU, licensed CC BY 3.0 — free to use,
// adapt, and use commercially, with attribution (see Data/nchlt/LICENSE.txt).
//
// Because the rule set covers any word, there is no "OOV gap": a word is either
// in the dictionary (exact) or synthesised by the rules (agglutinative isiZulu,
// which is otherwise endless, is handled). isiZulu is only ~74 rules because its
// orthography is near-phonemic — the same reason this approach is sound.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CircleAI.Voice;

/// <summary>
/// Grapheme-to-phoneme for isiZulu, isiXhosa, Afrikaans (and the other NCHLT
/// languages) using the CC-BY NCHLT-inlang dictionary + rule sets. Pure managed
/// code — no espeak, no native library, runs on every host and on the phone.
/// </summary>
public sealed class NchltPhonemizer : IPhonemizer
{
    /// <summary>One context rule: grapheme <c>g</c> in left/right context → code.</summary>
    private readonly record struct Rule(int Order, string Left, string Right, string Code);

    // word -> its X-SAMPA phone tokens (the exact catalogued pronunciation).
    private readonly Dictionary<string, string[]> _dict;
    // grapheme -> its rules, sorted by Order DESCENDING (most specific first).
    private readonly Dictionary<char, Rule[]> _rules;
    // rule phoneme code (single char) -> X-SAMPA symbol (e.g. '3' -> "b_<").
    private readonly Dictionary<char, string> _phoneMap;
    // grapheme remap applied before rule application (usually identity).
    private readonly Dictionary<char, char> _graphMap;
    // grapheme-null insertions: substring -> replacement (empty for Nguni).
    private readonly List<(string From, string To)> _gnulls;

    /// <summary>Number of words in the last <see cref="Phonemize"/> call that were
    /// synthesised by the rule engine rather than found in the dictionary. Useful
    /// for coverage diagnostics; never a failure — the rules always produce output.</summary>
    public int LastRulePredictedWords { get; private set; }

    /// <summary>Graphemes in the last call that no rule covered (e.g. stray
    /// punctuation that survived normalisation). Skipped, never guessed.</summary>
    public IReadOnlyList<char> LastUnknownGraphemes => _lastUnknown;
    private readonly List<char> _lastUnknown = new();

    private NchltPhonemizer(
        Dictionary<string, string[]> dict,
        Dictionary<char, Rule[]> rules,
        Dictionary<char, string> phoneMap,
        Dictionary<char, char> graphMap,
        List<(string, string)> gnulls)
    {
        _dict = dict;
        _rules = rules;
        _phoneMap = phoneMap;
        _graphMap = graphMap;
        _gnulls = gnulls;
    }

    /// <summary>
    /// Load a language from an NCHLT data directory laid out as this repo vendors
    /// it: <c>nchlt_{lang}.dict</c> plus <c>rules/nchlt_{lang}.{rules,map.phones,
    /// map.graphs,gnulls}</c>. <paramref name="lang"/> is an ISO code (zul, xho, afr…).
    /// </summary>
    public static NchltPhonemizer ForLanguage(string dataDir, string lang)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(lang);

        string dict = Path.Combine(dataDir, $"nchlt_{lang}.dict");
        string rulesDir = Path.Combine(dataDir, "rules");
        string rules = Path.Combine(rulesDir, $"nchlt_{lang}.rules");
        string pmap = Path.Combine(rulesDir, $"nchlt_{lang}.map.phones");
        string gmap = Path.Combine(rulesDir, $"nchlt_{lang}.map.graphs");
        string gnulls = Path.Combine(rulesDir, $"nchlt_{lang}.gnulls");

        return Load(
            File.OpenRead(dict),
            File.OpenRead(rules),
            File.OpenRead(pmap),
            File.Exists(gmap) ? File.OpenRead(gmap) : null,
            File.Exists(gnulls) ? File.OpenRead(gnulls) : null);
    }

    /// <summary>
    /// Build from open streams (the streams are read fully and disposed). Enables
    /// loading from embedded resources or a downloaded bundle without a file path.
    /// </summary>
    public static NchltPhonemizer Load(
        Stream dictStream, Stream rulesStream, Stream phoneMapStream,
        Stream? graphMapStream = null, Stream? gnullsStream = null)
    {
        ArgumentNullException.ThrowIfNull(dictStream);
        ArgumentNullException.ThrowIfNull(rulesStream);
        ArgumentNullException.ThrowIfNull(phoneMapStream);

        var dict = ParseDict(dictStream);
        var rules = ParseRules(rulesStream);
        var phoneMap = ParsePhoneMap(phoneMapStream);
        var graphMap = graphMapStream is null ? new() : ParseGraphMap(graphMapStream);
        var gnulls = gnullsStream is null ? new() : ParseGnulls(gnullsStream);

        return new NchltPhonemizer(dict, rules, phoneMap, graphMap, gnulls);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Phonemize(string text)
    {
        LastRulePredictedWords = 0;
        _lastUnknown.Clear();
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var phones = new List<string>();
        foreach (var word in Tokenize(text))
        {
            if (_dict.TryGetValue(word, out var known))
            {
                phones.AddRange(known);
            }
            else
            {
                phones.AddRange(PredictWord(word));
                LastRulePredictedWords++;
            }
        }
        return phones;
    }

    /// <summary>
    /// Predict a single word's X-SAMPA phones from the context rules — the
    /// exact algorithm of <c>g2p_word_olist</c>: for each grapheme, take the
    /// highest-order rule whose left/right context matches, emit its code,
    /// drop nulls (<c>0</c>), then remap codes to X-SAMPA.
    /// </summary>
    public IReadOnlyList<string> PredictWord(string word)
    {
        if (string.IsNullOrEmpty(word)) return Array.Empty<string>();

        // Grapheme remap (usually identity) then grapheme-null insertion.
        string w = ApplyGnulls(MapGraphemes(word));

        var codes = new List<char>(w.Length);
        for (int i = 0; i < w.Length; i++)
        {
            char g = w[i];
            if (!_rules.TryGetValue(g, out var gRules))
            {
                if (!_lastUnknown.Contains(g)) _lastUnknown.Add(g);
                continue; // skip unknown graphemes rather than fabricate a phone
            }

            // pat = " " + left-context + "-" + g + "-" + right-context + " "
            string left = " " + w[..i];
            string right = w[(i + 1)..] + " ";
            string pat = left + "-" + g + "-" + right;

            // Rules are pre-sorted most-specific-first; first context match wins.
            char code = '0';
            foreach (var r in gRules)
            {
                string needle = r.Left + "-" + g + "-" + r.Right;
                if (pat.Contains(needle, StringComparison.Ordinal))
                {
                    code = r.Code.Length > 0 ? r.Code[0] : '0';
                    break;
                }
            }
            if (code != '0') codes.Add(code);
        }

        // Remap each single-char code to its X-SAMPA symbol.
        var phones = new List<string>(codes.Count);
        foreach (char c in codes)
            phones.Add(_phoneMap.TryGetValue(c, out var xs) ? xs : c.ToString());
        return phones;
    }

    // ── Text handling ───────────────────────────────────────────────────

    /// <summary>
    /// Lower-case and split into word tokens on anything that is not a letter.
    /// Diacritics are preserved (Afrikaans ê/ë/ô are real graphemes); digits and
    /// punctuation become separators. Number/abbreviation expansion is out of
    /// scope here and belongs to a text-normalisation pass upstream.
    /// </summary>
    private static IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder();
        foreach (char ch in text.Trim())
        {
            if (char.IsLetter(ch))
            {
                sb.Append(char.ToLower(ch, CultureInfo.InvariantCulture));
            }
            else if (sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private string MapGraphemes(string word)
    {
        if (_graphMap.Count == 0) return word;
        var sb = new StringBuilder(word.Length);
        foreach (char c in word)
            sb.Append(_graphMap.TryGetValue(c, out var m) ? m : c);
        return sb.ToString();
    }

    private string ApplyGnulls(string word)
    {
        if (_gnulls.Count == 0) return word;
        foreach (var (from, to) in _gnulls)
            word = word.Replace(from, to, StringComparison.Ordinal);
        return word;
    }

    // ── Parsers ─────────────────────────────────────────────────────────

    private static Dictionary<string, string[]> ParseDict(Stream s)
    {
        var dict = new Dictionary<string, string[]>(StringComparer.Ordinal);
        using var r = new StreamReader(s, Encoding.UTF8);
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            string word = line[..tab];
            string pron = line[(tab + 1)..].Trim();
            if (pron.Length == 0 || dict.ContainsKey(word)) continue; // keep first variant
            dict[word] = pron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        return dict;
    }

    private static Dictionary<char, Rule[]> ParseRules(Stream s)
    {
        var byGrapheme = new Dictionary<char, List<Rule>>();
        using var r = new StreamReader(s, Encoding.UTF8);
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            // grapheme ; left ; right ; code ; order [ ; count ]
            var f = line.Split(';');
            if (f.Length < 5 || f[0].Length == 0) continue;
            char g = f[0][0];
            if (!int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int order))
                continue;
            var rule = new Rule(order, f[1], f[2], f[3]);
            if (!byGrapheme.TryGetValue(g, out var list))
                byGrapheme[g] = list = new List<Rule>();
            list.Add(rule);
        }

        var rules = new Dictionary<char, Rule[]>(byGrapheme.Count);
        foreach (var (g, list) in byGrapheme)
            rules[g] = list.OrderByDescending(x => x.Order).ToArray();
        return rules;
    }

    private static Dictionary<char, string> ParsePhoneMap(Stream s)
    {
        // Line: "<code>\t<xsampa>"  (code is a single char).
        var map = new Dictionary<char, string>();
        using var r = new StreamReader(s, Encoding.UTF8);
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            string code = line[..tab];
            string xsampa = line[(tab + 1)..];
            if (code.Length == 1) map[code[0]] = xsampa;
        }
        return map;
    }

    private static Dictionary<char, char> ParseGraphMap(Stream s)
    {
        // File line: "<funny>\t<std>" — we map std->funny (per remap_dict's gmap).
        var map = new Dictionary<char, char>();
        using var r = new StreamReader(s, Encoding.UTF8);
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var f = line.Split('\t');
            if (f.Length == 2 && f[0].Length == 1 && f[1].Length == 1 && f[0][0] != f[1][0])
                map[f[1][0]] = f[0][0];
        }
        return map;
    }

    private static List<(string, string)> ParseGnulls(Stream s)
    {
        // File line: "<from>;<to>" — insert grapheme-nulls (empty for Nguni).
        var list = new List<(string, string)>();
        using var r = new StreamReader(s, Encoding.UTF8);
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var f = line.Split(';');
            if (f.Length == 2) list.Add((f[0], f[1]));
        }
        return list;
    }
}
