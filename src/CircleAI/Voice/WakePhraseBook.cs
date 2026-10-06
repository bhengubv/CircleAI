#nullable enable

// WakePhraseBook.cs
//
// The wake phrases a person has chosen, and the rules that stop them choosing one
// that cannot work.
//
// THIS IS WHERE THE MEASUREMENTS BECOME A GUARD RAIL. Everything learned the hard
// way about what makes a wake phrase succeed or fail is encoded here as advice
// given at the moment someone types it, rather than as a surprise discovered
// weeks later when the thing does not answer:
//
//   FEWER THAN FOUR TOKENS AND IT WILL NOT SURVIVE A ROOM. "Hey B" is three, and
//   played through a speaker it was heard once in ten while "Circle" — the same
//   two syllables, four tokens — was heard twelve times out of twelve. Syllables
//   are what a person counts; tokens are what the model gets to see.
//
//   AN ORDINARY WORD WAKES ON ORDINARY SPEECH. "Circle", "listen" and "beacon"
//   each scored full recall and then fired on 21 of 30 clips of normal
//   conversation — every one a sentence containing the word. Stage two removes
//   most of that, but a phrase nobody says by accident is better than a filter.
//
//   A PHRASE THAT ANOTHER PHRASE STARTS WITH CAN NEVER FIRE. Register "Circle"
//   and "Circle AI" together and the shorter one always wins; across eighteen
//   recordings of the longer phrase, every single detection reported the shorter.
//
// None of these are refusals. They are warnings, because it is the owner's phrase
// and there are good reasons to accept a weak one — but nobody should discover
// the trade by accident.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CircleAI.Voice;

/// <summary>How well a chosen phrase is likely to work.</summary>
public enum WakePhraseVerdict
{
    /// <summary>Nothing to say against it.</summary>
    Good,
    /// <summary>Usable, with a caveat the owner should hear.</summary>
    Caution,
    /// <summary>Cannot work at all; the reason says why.</summary>
    Unusable,
}

/// <summary>A phrase, its tokens, and what we think of it.</summary>
/// <param name="Text">As typed, e.g. "hey circle".</param>
/// <param name="Tokens">Sentencepiece pieces the spotter will match.</param>
/// <param name="Verdict">Whether it is worth using.</param>
/// <param name="Advice">Plain language, shown to the person choosing. Empty when Good.</param>
/// <param name="Threshold">Acceptance override, or null for the default.</param>
/// <param name="Boost">Search boost override, or null for the default.</param>
public sealed record WakePhrase(
    string Text,
    IReadOnlyList<string> Tokens,
    WakePhraseVerdict Verdict,
    string Advice,
    double? Threshold = null,
    double? Boost = null);

/// <summary>
/// Chooses, checks and persists wake phrases in the format the spotter reads.
/// </summary>
public sealed class WakePhraseBook
{
    /// <summary>Below this many tokens a phrase does not survive a room.</summary>
    /// <remarks>
    /// Four, measured: three-token "Hey B" was heard 1/10 through air, four-token
    /// "Circle" 12/12, and the two are the same two syllables to say.
    /// <para>
    /// RE-MEASURED ON THE FIXED CODE, AND THE ORIGINAL NUMBER HELD. It was worth
    /// checking: the 1/10 was taken while two faults were in the way - the gate
    /// stood at 0.5 where real speech scores 0.24-0.39, and a completed but
    /// rejected hypothesis was never cleared from the beam, so the spotter went
    /// deaf after two or three near misses. Both are fixed.
    /// </para>
    /// <para>
    /// On 2026-09-05 this note briefly claimed the phrase then "fired four times
    /// in five", and that was five utterances nobody counted properly. Counted
    /// properly on 2026-09-06 - one person, one room, 197 windows of capture -
    /// six spoken attempts produced four partial sightings, ONE completed phrase
    /// and no fires. One in six, on a build with the beam and the gate both
    /// correct.
    /// </para>
    /// <para>
    /// So four tokens is a finding again rather than a convention, and the
    /// English default leads with "Hey Circle AI" for exactly this reason.
    /// "Hey B" stays in the table below it as a fallback for bundles whose
    /// tokenizer cannot represent the longer phrase - not as a recommendation.
    /// </para>
    /// </remarks>
    public const int MinReliableTokens = 4;

    /// <summary>Words common enough that a phrase built only from them will self-trigger.</summary>
    /// <remarks>
    /// Not a spell-checker and not exhaustive — a short list of the words that
    /// actually turned up firing in the corpus, plus the obvious neighbours. The
    /// point is to catch "listen" and "circle", not to police vocabulary.
    /// </remarks>
    private static readonly HashSet<string> Everyday = new(StringComparer.OrdinalIgnoreCase)
    {
        "circle", "listen", "hello", "hey", "okay", "ok", "yes", "no", "stop", "go",
        "play", "open", "close", "help", "please", "wait", "back", "up", "down",
        "phone", "call", "text", "time", "now", "today", "one", "two", "three",
    };

    /// <summary>What the phone should be called, per language.</summary>
    /// <remarks>
    /// "HEY B" IS AN ENGLISH SENTENCE, and the wake word was fixed to it whatever
    /// language the person had chosen — so setting the phone to Japanese left it
    /// listening for a phrase no Japanese speaker would say. The name stays "B";
    /// what changes is the honorific around it, because that is how the name is
    /// actually said in each language.
    /// <para>
    /// LONGER ON PURPOSE. Three-token "Hey B" was heard 1 time in 10 through air
    /// against 12/12 for a four-token phrase — see MinReliableTokens. The
    /// honorific forms are naturally longer, which helps rather than costs.
    /// </para>
    /// <para>
    /// A CANDIDATE, NOT A GUARANTEE. Every phrase here still goes through
    /// <see cref="Evaluate"/> against the bundle's own tokenizer: a wake model
    /// trained on Latin sub-words may not represent kana or Hangul at all, and
    /// the honest outcome then is Unusable rather than a phone that silently
    /// never wakes. Check the verdict before installing one.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, string[]> CandidatesByLanguage { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // "Hey B" IS THREE TOKENS, AND THIS FILE'S OWN RULE CALLS THAT
            // CAUTION - MinReliableTokens is 4, and a longer phrase is still the
            // safer default, which is why the order below is what it is.
            //
            // THE ORIGINAL NOTE HERE WAS RIGHT AND I OVERTURNED IT ON BAD
            // EVIDENCE. It said "neither a synthesised nor a human 'Hey B' ever
            // fired", and on 2026-09-05 that was replaced with a claim that the
            // phrase "fired four times in five on the same P30" once the beam
            // and the gate were fixed. Five utterances, not counted carefully.
            //
            // COUNTED CAREFULLY on 2026-09-06, one person, one room, 197 windows
            // of capture on the fixed build:
            //
            //     6  windows where somebody spoke  (peak > 0,3)
            //     4  partial token sightings
            //     1  completed phrase              (p = 0,246)
            //     0  fired
            //
            // ONE COMPLETION IN SIX ATTEMPTS. That is nearer the 1-in-10 the
            // original note recorded than anything that justified overturning
            // it, and it is what MinReliableTokens has said all along: three
            // tokens does not survive a room. Lowering the gate lets that
            // one-in-six through and does nothing for the five that never
            // complete - the threshold was never the main fault, the phrase is.
            //
            // Left in as a fallback, not as a recommendation.
            //
            // "Hey Circle AI" is first because BestFor takes the LONGEST usable
            // candidate, and it clears both tests here: four or more tokens, and
            // "AI" is not on the Everyday list. Note that "Hey Circle" would NOT
            // clear the second - "hey" and "circle" are both everyday words, so
            // it would wake up mid-conversation.
            //
            // "Hey B" stays: it is the product's name, and BestFor only prefers
            // the longer one, so this remains a working fallback on a bundle
            // whose tokenizer cannot represent the longer phrase.
            ["en"] = ["Hey Circle AI", "Hey B"],

            // LONGEST FIRST, AND ROMANISED FIRST WITHIN THAT, because BestFor
            // takes the first candidate the bundle's tokenizer can represent and
            // that tokenizer is 500 English sub-words - no kana, no han, no
            // hangul. A script the model cannot see scores Unusable and is
            // skipped, so a native-script entry is a courtesy to whoever reads
            // this file rather than something the wake model will ever hear.
            //
            // EACH ONE IS A GREETING SOMEBODY WOULD ACTUALLY SAY, not "hey" with
            // a name bolted on. The point of a wake phrase in your own language
            // is that it is a thing you would say out loud without feeling
            // foolish - and the longer, more natural form is also the one with
            // enough tokens to survive a room. The two goals agree here, which
            // is unusual and worth taking.
            ["ja"] = ["Moshi moshi B san", "Bee san", "ビーさん", "ビーさま"],
            ["ko"] = ["Annyeong B nim", "Bee nim", "비 님"],
            ["zh"] = ["Ni hao Xiao B", "Xiao B", "小B"],
            ["yue"] = ["Nei hou Siu B", "Siu B", "小B"],

            // SOUTH AFRICA FIRST, because that is who this is for. Every one of
            // these is the ordinary greeting in that language followed by the
            // name - "sawubona" and "molo" and "dumela" are what people actually
            // open with, and all of them clear four tokens comfortably where a
            // bare "Hey B" does not.
            ["zu"]  = ["Sawubona B", "Sawubona Circle"],
            ["xh"]  = ["Molo B", "Molo Circle"],
            ["af"]  = ["Haai Circle AI", "Hallo B"],
            ["st"]  = ["Dumela B", "Dumela Circle"],
            ["tn"]  = ["Dumela B", "Dumela Circle"],
            ["nso"] = ["Dumela B", "Dumela Circle"],
            ["ts"]  = ["Avuxeni B", "Avuxeni Circle"],
            ["ve"]  = ["Ndaa B", "Ndaa Circle"],
            ["ss"]  = ["Sawubona B", "Sawubona Circle"],
            ["nr"]  = ["Lotjhani B", "Lotjhani Circle"],

            ["sw"] = ["Habari B", "Habari Circle"],
            ["am"] = ["Selam B", "Selam Circle"],
            ["ha"] = ["Sannu B", "Sannu Circle"],
            ["yo"] = ["Bawo ni B", "Bawo B"],
            ["ig"] = ["Ndewo B", "Ndewo Circle"],

            ["fr"] = ["Salut Circle AI", "Bonjour B"],
            ["es"] = ["Hola Circle AI", "Oye B"],
            ["pt"] = ["Ola Circle AI", "Ola B"],
            ["nl"] = ["Hallo Circle AI", "Hallo B"],
            ["hi"] = ["Namaste B", "Namaste Circle"],
            ["bn"] = ["Nomoshkar B", "Nomoshkar Circle"],
            ["ur"] = ["Salam B", "Salam Circle"],
            ["ar"] = ["Marhaba B", "Salam B"],
            ["ru"] = ["Privet Circle AI", "Privet B"],
            ["id"] = ["Halo Circle AI", "Halo B"],
            ["vi"] = ["Xin chao B", "Xin chao Circle"],
            ["th"] = ["Sawasdee B", "Sawasdee Circle"],
        };

    /// <summary>The wake phrases worth trying for a language, best first.</summary>
    /// <remarks>
    /// Falls back to English rather than returning nothing: a phone that still
    /// answers to "Hey B" is wrong in the way a person can work around, and one
    /// that answers to nothing is not.
    /// </remarks>
    public static IReadOnlyList<string> CandidatesFor(string? languageCode)
    {
        var code = languageCode?.Trim() ?? string.Empty;
        var cut = code.IndexOf('-');
        if (cut > 0) code = code[..cut];
        return CandidatesByLanguage.TryGetValue(code, out var list)
            ? list
            : CandidatesByLanguage["en"];
    }

    /// <summary>
    /// The best phrase this bundle can actually hear for a language, or null.
    /// </summary>
    /// <remarks>
    /// Asks the tokenizer, in order, and takes the first that is not Unusable.
    /// Null means this wake model cannot represent the language's phrases — which
    /// the caller should say out loud rather than quietly listening in English.
    /// </remarks>
    public WakePhrase? BestFor(string? languageCode)
    {
        WakePhrase? best = null;
        foreach (var candidate in CandidatesFor(languageCode))
        {
            var judged = Evaluate(candidate);
            if (judged.Verdict == WakePhraseVerdict.Unusable) continue;
            if (best is null || judged.Tokens.Count > best.Tokens.Count) best = judged;
        }
        return best;
    }

    private readonly SentencePieceTokenizer _tokenizer;
    private readonly List<WakePhrase> _phrases = new();

    public WakePhraseBook(SentencePieceTokenizer tokenizer) => _tokenizer = tokenizer;

    /// <summary>The phrases currently in the book.</summary>
    public IReadOnlyList<WakePhrase> Phrases => _phrases;

    /// <summary>
    /// Judges a phrase without adding it — what a UI calls as someone types.
    /// </summary>
    public WakePhrase Evaluate(string text, double? threshold = null, double? boost = null)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return new WakePhrase(trimmed, Array.Empty<string>(), WakePhraseVerdict.Unusable,
                "Type something to say.", threshold, boost);

        var tokens = _tokenizer.Encode(trimmed);

        if (!_tokenizer.CanRepresent(trimmed, out var unknown))
            return new WakePhrase(trimmed, tokens, WakePhraseVerdict.Unusable,
                $"This wake word uses sounds the listener does not know ({string.Join(", ", unknown)}). " +
                "Try a different word.", threshold, boost);

        // Shadowing: a phrase that an EXISTING one starts with can never fire, and
        // neither can an existing one that starts with this. Both directions.
        foreach (var other in _phrases)
        {
            if (StartsWith(tokens, other.Tokens))
                return new WakePhrase(trimmed, tokens, WakePhraseVerdict.Unusable,
                    $"“{other.Text}” would always trigger first, so this one could never work. " +
                    "Remove that one, or pick something that does not start the same way.",
                    threshold, boost);

            if (StartsWith(other.Tokens, tokens))
                return new WakePhrase(trimmed, tokens, WakePhraseVerdict.Unusable,
                    $"This would always trigger before “{other.Text}”, which would stop working.",
                    threshold, boost);
        }

        if (tokens.Count < MinReliableTokens)
            return new WakePhrase(trimmed, tokens, WakePhraseVerdict.Caution,
                "This is very short, so it may not be heard from across a room. " +
                "A slightly longer phrase is more reliable.", threshold, boost);

        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.All(w => Everyday.Contains(w.Trim(',', '.', '!', '?'))))
            return new WakePhrase(trimmed, tokens, WakePhraseVerdict.Caution,
                "These are everyday words, so it may wake up when you are talking to someone else.",
                threshold, boost);

        return new WakePhrase(trimmed, tokens, WakePhraseVerdict.Good, string.Empty, threshold, boost);
    }

    /// <summary>Adds a phrase unless it is unusable.</summary>
    public bool TryAdd(string text, out WakePhrase phrase, double? threshold = null, double? boost = null)
    {
        phrase = Evaluate(text, threshold, boost);
        if (phrase.Verdict == WakePhraseVerdict.Unusable) return false;
        _phrases.Add(phrase);
        return true;
    }

    /// <summary>Removes a phrase by its text, case-insensitively.</summary>
    public bool Remove(string text) =>
        _phrases.RemoveAll(p => string.Equals(p.Text, text, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>Writes the keywords file the spotter loads.</summary>
    /// <remarks>
    /// sherpa's format: the tokens, then optional <c>:boost</c>, <c>#threshold</c>
    /// and <c>@phrase</c>. The label is written with underscores because the
    /// format splits on spaces — without that, a two-word phrase comes back from a
    /// detection as only its first word.
    /// </remarks>
    public void Save(string path)
    {
        var sb = new StringBuilder();
        foreach (var p in _phrases)
        {
            sb.Append(string.Join(' ', p.Tokens));
            if (p.Boost is { } b) sb.Append(CultureInfo.InvariantCulture, $" :{b}");
            if (p.Threshold is { } t) sb.Append(CultureInfo.InvariantCulture, $" #{t}");
            sb.Append(" @").Append(p.Text.Replace(' ', '_'));
            sb.Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Reads a keywords file back, re-judging each phrase.</summary>
    public static WakePhraseBook Load(string path, SentencePieceTokenizer tokenizer)
    {
        var book = new WakePhraseBook(tokenizer);
        if (!File.Exists(path)) return book;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string? label = null;
            double? threshold = null, boost = null;
            var tokens = new List<string>();

            foreach (var w in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                switch (w[0])
                {
                    case ':': boost = Num(w); break;
                    case '#': threshold = Num(w); break;
                    case '@': label = w[1..].Replace('_', ' '); break;
                    default: tokens.Add(w); break;
                }
            }

            var text = label ?? string.Concat(tokens)
                .Replace(SentencePieceTokenizer.WordStart, ' ').Trim();
            book._phrases.Add(book.Evaluate(text, threshold, boost) with { Tokens = tokens });
        }
        return book;

        static double? Num(string w) =>
            double.TryParse(w[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static bool StartsWith(IReadOnlyList<string> longer, IReadOnlyList<string> prefix)
    {
        if (prefix.Count == 0 || prefix.Count >= longer.Count) return false;
        for (var i = 0; i < prefix.Count; i++)
            if (!string.Equals(longer[i], prefix[i], StringComparison.Ordinal)) return false;
        return true;
    }
}
