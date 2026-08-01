#nullable enable

// Respeller.cs
//
// One place that decides how a borrowed word is written for a host voice, and one
// wrapper that applies it to anything about to be spoken.
//
// The decision has three rungs and the order is the whole point:
//
//   1. What THIS PERSON says          — learned by listening ([[PersonalRespellings]])
//   2. What the language has settled  — esemese, khompiyutha (LoanwordRespeller)
//   3. What the rule would produce    — English sounds through the Nguni CV rule
//
// A person's own usage outranks anything shipped, because they are the authority
// on their own speech. Below that sits usage the language settled long before we
// arrived. Only when neither has an answer do we derive one, and a derivation is
// a guess we are honest about.
//
// This existed inline in the test probe, where it improved nothing that anyone
// actually hears: the live conversation speaks through the engine directly. Both
// now share this, so the ear teaching the table changes what the mouth says.

using System;
using System.Text;

namespace CircleAI.Voice;

/// <summary>
/// Decides how borrowed words are written for a host-language voice.
/// </summary>
public sealed class Respeller
{
    /// <summary>The language being spoken, which decides whether respelling applies.</summary>
    public string HostLanguage { get; init; } = "";

    /// <summary>What this person has taught us by talking. Outranks everything else.</summary>
    public PersonalRespellings? Personal { get; init; }

    /// <summary>
    /// English pronunciation for words nobody has written down yet.
    /// </summary>
    /// <remarks>
    /// Out-of-process espeak: it is GPL-3.0 and CircleAI never links it. Absent —
    /// the separate app is not installed — the third rung simply does not fire and
    /// the word is left as written, which is honest rather than invented.
    /// </remarks>
    public IPhonemizer? EnglishPhonemizer { get; init; }

    /// <summary>Reports each substitution, for a probe or a log that wants to show its work.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// How to write <paramref name="word"/> in the host orthography, or null when
    /// we have no answer for it.
    /// </summary>
    /// <remarks>
    /// Null rather than a guess. A word we cannot respell is left exactly as
    /// written and read with host letter values — not ideal, but predictable. An
    /// invented spelling sounds like confident nonsense, which is worse.
    /// </remarks>
    public string? For(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        word = word.Trim();

        var learned = Personal?.Respell(word);
        if (learned is not null) return learned;

        var settled = LoanwordRespeller.Respell(word, HostLanguage);
        if (settled is not null) return settled;

        // Nobody has written this one down, so do what a speaker does with an
        // unfamiliar word: hear it, then spell it in their own orthography.
        if (EnglishPhonemizer is null || !LoanwordRespeller.IsNguniOrSotho(HostLanguage))
            return null;

        try
        {
            var ipa = string.Concat(EnglishPhonemizer.Phonemize(word));
            var derived = NguniRespeller.FromIpa(ipa);
            if (string.IsNullOrWhiteSpace(derived)) return null;

            Log?.Invoke($"derived \"{word}\" -> \"{derived}\" (from {ipa})");
            return derived;
        }
        catch (Exception ex)
        {
            // No English G2P on this device. Leave the word alone rather than
            // invent a spelling for it.
            Log?.Invoke($"no English pronunciation for \"{word}\": {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Rewrites every borrowing in <paramref name="text"/> into the host orthography.
    /// </summary>
    /// <remarks>
    /// Words the host language owns are untouched — most of any sentence — so the
    /// common case costs a split and a scan. A foreign word with no respelling is
    /// still made pronounceable: "CircleAI" becomes "Circle A.I." rather than one
    /// unreadable run of letters.
    /// </remarks>
    public string Rewrite(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";

        // A language these spellings were never written for is left completely
        // alone — not even the compound-splitting below. Afrikaans has its own
        // forms for these words, and "S.M.S." is our idea of helpful imposed on a
        // language that did not ask for it. Guarded here rather than only at the
        // factory, so a caller that builds this directly cannot bypass it.
        if (!LoanwordRespeller.IsNguniOrSotho(HostLanguage)) return text;

        var built = new StringBuilder(text.Length + 16);
        foreach (var span in LanguageSpanSplitter.Split(text))
        {
            if (!span.IsForeign) { built.Append(span.Text); continue; }

            var word = span.Text.Trim();
            var respelt = For(word);
            if (respelt is not null)
            {
                Log?.Invoke($"respelt \"{word}\" as \"{respelt}\"");
                // The span's own leading and trailing spacing is preserved, so
                // rewriting a word does not silently glue it to its neighbour.
                built.Append(span.Text.Replace(word, respelt));
            }
            else
            {
                built.Append(LanguageSpanSplitter.ToSpokenForm(span.Text));
            }
        }
        return built.ToString();
    }
}

/// <summary>
/// Wraps a voice so that everything it says is respelt first.
/// </summary>
/// <remarks>
/// A decorator rather than a change to the engine, because respelling is about
/// LANGUAGE and the engine is about SOUND. An engine that knew about isiZulu
/// borrowings would have to know about every language's, and a voice for a
/// language with no such table would carry the machinery for nothing.
/// </remarks>
public sealed class RespellingTtsEngine : ITtsEngine
{
    private readonly ITtsEngine _inner;
    private readonly Respeller _respeller;

    public RespellingTtsEngine(ITtsEngine inner, Respeller respeller)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _respeller = respeller ?? throw new ArgumentNullException(nameof(respeller));
    }

    /// <summary>The voice underneath, for callers that need to tune it.</summary>
    public ITtsEngine Inner => _inner;

    /// <summary>The table in use, so a host can show what has been learned.</summary>
    public Respeller Respeller => _respeller;

    public System.Threading.Tasks.Task<TtsSynthesisResult> SynthesiseAsync(
        string text, System.Threading.CancellationToken cancellationToken = default) =>
        _inner.SynthesiseAsync(_respeller.Rewrite(text), cancellationToken);

    public System.Collections.Generic.IAsyncEnumerable<ReadOnlyMemory<byte>> StreamSynthesiseAsync(
        string text, System.Threading.CancellationToken cancellationToken = default) =>
        _inner.StreamSynthesiseAsync(_respeller.Rewrite(text), cancellationToken);
}
