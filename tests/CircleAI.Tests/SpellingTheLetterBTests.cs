// SpellingTheLetterBTests.cs
//
// THE WAKE PHRASE ASKS FOR A TOKEN THE MODEL ALMOST NEVER SAYS.
//
// "Hey B" compiles to three BPE pieces - "_HE Y _B" - proven against the
// registry's published sha256 for keywords.txt (832f9367...). On a P30 on
// 2026-10-11, with a person saying it repeatedly for an hour:
//
//   - "_HE" and "Y" matched; the beam reached 2 of 3 tokens 1,796 times
//   - "_B" was NEVER emitted once in ~14,000 traced frames, peak p=0,0320
//   - the phrase fired zero times
//
// It is not the threshold (Y reached p=0,8981 against a gate of 0,20, and a
// wake fires only at the graph's end node). It is not the quantized encoder
// (EncoderPrecisionOnRealAudioTests measured int8 detecting every keyword float
// did on upstream's own audio). It is not phrase length either - upstream's
// "_FOR E VER" is three tokens and detects fine.
//
// It is the token. The vocabulary has 500 entries and the ids are ordered by
// frequency: "B" is 34, "_BE" is 43, "_B" is 68. The keyword demands the
// RAREST of the three, and in GigaSpeech transcripts a standalone capital B
// appears almost only inside initialisms.
//
// But the letter B is pronounced "bee". Spelled the way the model was trained
// to hear it, the phrase stops asking for a rarity - and gains a fourth token,
// which WakePhraseBook's own rule ("fewer than four tokens and it will not
// survive a room") says is the difference between a phrase and a wish.
//
// Needs the upstream bundle for bpe.model; set KWS_BUNDLE_DIR. Skips otherwise.

using System;
using System.IO;
using System.Linq;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class SpellingTheLetterBTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public SpellingTheLetterBTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static string? BpeModel
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("KWS_BUNDLE_DIR");
            if (string.IsNullOrEmpty(dir)) return null;
            var p = Path.Combine(dir, "bpe.model");
            return File.Exists(p) ? p : null;
        }
    }

    private static string Show(System.Collections.Generic.IReadOnlyList<string> pieces) =>
        string.Join(" ", pieces);

    [Fact]
    public void Spelling_the_letter_out_buys_a_fourth_token_and_drops_the_rare_one()
    {
        var model = BpeModel;
        if (model is null)
        {
            _out.WriteLine("SKIPPED: set KWS_BUNDLE_DIR to an extracted upstream KWS bundle.");
            return;
        }

        var bpe = new SentencePieceTokenizer(model);

        var asLetter = bpe.Encode("HEY B");
        var asSound = bpe.Encode("HEY BEE");

        _out.WriteLine($"HEY B   -> {Show(asLetter)}   ({asLetter.Count} tokens)");
        _out.WriteLine($"HEY BEE -> {Show(asSound)}   ({asSound.Count} tokens)");

        // THE MEASURED SPELLING, pinned so a tokeniser change cannot quietly
        // alter what the wake phrase asks for. This is the exact string whose
        // sha256 the registry publishes.
        Assert.Equal("▁HE Y ▁B", Show(asLetter));

        // AND THE POINT: writing the letter as it is SAID stops the phrase
        // depending on the rarest of the three ways to spell that sound.
        Assert.True(asSound.Count > asLetter.Count,
            $"spelling it out did not lengthen the phrase: {Show(asSound)}");

        Assert.DoesNotContain("▁B", asSound);
        Assert.True(asSound.Count >= 4,
            "WakePhraseBook's own rule: fewer than four tokens will not survive a room");
    }

    [Fact]
    public void The_book_tokenizes_exactly_what_was_typed()
    {
        // THIS TEST ONCE ASSERTED THE OPPOSITE, AND THE OPPOSITE WAS WRONG.
        //
        // For a few hours WakePhraseBook rewrote lone letters into how they are
        // said, so "Hey B!" became "_HE Y _BE E". Every number behind that was
        // real - _B is the rarest of the three spellings in a frequency-ordered
        // vocabulary, it never won a frame on the device, and the fourth token
        // lifted the verdict from Caution to Good.
        //
        // Then the owner's own voice went through the decoder and settled it:
        // "_HE Y _B" fired EIGHT times on that recording and "_HE Y _BE E"
        // fired ZERO. The rewrite was removed. See HeyBOnRealVoiceTests.
        var model = BpeModel;
        if (model is null)
        {
            _out.WriteLine("SKIPPED: set KWS_BUNDLE_DIR to an extracted upstream KWS bundle.");
            return;
        }

        var book = new WakePhraseBook(new SentencePieceTokenizer(model));
        Assert.True(book.TryAdd("Hey B!", out var phrase));

        _out.WriteLine($"typed \"{phrase.Text}\" -> {Show(phrase.Tokens)} ({phrase.Tokens.Count}) [{phrase.Verdict}]");

        Assert.Equal("Hey B!", phrase.Text);
        Assert.Equal("▁HE Y ▁B", Show(phrase.Tokens));
    }

    [Fact]
    public void A_bare_letter_is_the_rarest_way_to_spell_that_sound()
    {
        var dir = Environment.GetEnvironmentVariable("KWS_BUNDLE_DIR");
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "tokens.txt")))
        {
            _out.WriteLine("SKIPPED: set KWS_BUNDLE_DIR to an extracted upstream KWS bundle.");
            return;
        }

        var ids = File.ReadAllLines(Path.Combine(dir, "tokens.txt"))
            .Select(l => l.TrimEnd('\r').Split(' '))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => int.Parse(p[1]));

        Assert.Equal(500, ids.Count);

        // Lower id = more frequent in the BPE merge order. The keyword file asks
        // for the one at the bottom of these three.
        var bare = ids["▁B"];
        var be = ids["▁BE"];
        var cont = ids["B"];

        _out.WriteLine($"B={cont}  ▁BE={be}  ▁B={bare}");

        Assert.True(be < bare, "▁BE should be more frequent than ▁B");
        Assert.True(cont < bare, "bare B should be more frequent than ▁B");

        // And the pieces the spelled-out form needs are all common.
        Assert.True(ids["▁HE"] < 100);
        Assert.True(ids["Y"] < 100);
        Assert.True(ids["E"] < 100);
    }
}
