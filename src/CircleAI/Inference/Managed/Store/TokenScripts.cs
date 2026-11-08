// TokenScripts.cs
//
// WHICH WRITING SYSTEM A TOKEN BELONGS TO, so the output head can be stored by script and a turn can
// read only the scripts its conversation uses. A token is a run of bytes (byte-level BPE), not always
// whole characters, so:
//
//   special (control, added, unknown)     Special: every surface holds them
//   one byte, or no letters at all        Neutral: digits, punctuation, spaces, symbols, emoji, and the
//                                         256 byte tokens that can spell anything - every surface
//   letters of one script                 that script
//   letters of several                    Other
//   starting inside a character           Fragment: the tail of a character split across tokens
//
// A character cut off at the token's end is placed by what its first bytes can still be. The scripts
// are coarse on purpose - a group per writing system a person writes in - and a turn that writes in
// any other script reads the whole head.

using System.Buffers;
using System.Globalization;
using System.Text;

namespace CircleAI.Inference.Managed.Store;

/// <summary>A writing system, as the head's store groups its rows.</summary>
public enum Script : byte
{
    /// <summary>Control, added and unknown tokens.</summary>
    Special,

    /// <summary>No letters: digits, punctuation, spaces, symbols, emoji, and every single byte.</summary>
    Neutral,

    /// <summary>Latin letters - English, isiZulu, Afrikaans and the rest.</summary>
    Latin,

    /// <summary>Cyrillic letters.</summary>
    Cyrillic,

    /// <summary>Greek letters.</summary>
    Greek,

    /// <summary>Arabic letters.</summary>
    Arabic,

    /// <summary>Hebrew letters.</summary>
    Hebrew,

    /// <summary>The scripts of India and Sri Lanka, Devanagari to Sinhala.</summary>
    Indic,

    /// <summary>Thai and Lao.</summary>
    Thai,

    /// <summary>Chinese characters, kana and fullwidth forms.</summary>
    Cjk,

    /// <summary>Hangul.</summary>
    Hangul,

    /// <summary>Any other script, or letters of more than one.</summary>
    Other,

    /// <summary>A token that starts inside a character.</summary>
    Fragment,
}

/// <summary>Places each token in a <see cref="Script"/>.</summary>
public static class TokenScripts
{
    /// <summary>The script of a token whose bytes are <paramref name="piece"/>; <paramref name="special"/> for a control, added or unknown token.</summary>
    public static Script Of(ReadOnlySpan<byte> piece, bool special)
    {
        if (special) return Script.Special;
        if (piece.Length <= 1) return Script.Neutral;
        if (piece[0] is >= 0x80 and <= 0xBF) return Script.Fragment;

        Script? found = null;
        var at = 0;
        while (at < piece.Length)
        {
            var status = Rune.DecodeFromUtf8(piece[at..], out var rune, out var used);
            Script? script;
            if (status == OperationStatus.Done)
            {
                script = OfRune(rune);
                at += used;
            }
            else
            {
                // A character cut off at the end of the token, or a byte that is no character's: placed
                // by what its first bytes can still become.
                script = OfLead(piece[at..]);
                at += Math.Max(1, used);
            }
            if (script is not { } s) continue;
            if (found is null) found = s;
            else if (found != s) return Script.Other;
        }
        return found ?? Script.Neutral;
    }

    // A letter's or mark's script; null for anything else (digits, punctuation, spaces, symbols, emoji).
    private static Script? OfRune(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        var letter = category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
        return letter ? OfCodePoint(rune.Value) : null;
    }

    // The script whose block holds code point c, for a letter or mark; null for the combining marks every
    // alphabet shares.
    private static Script? OfCodePoint(int c) => c switch
    {
        <= 0x02AF => Script.Latin,                  // Basic Latin, Latin-1, Latin Extended A and B, IPA
        <= 0x02FF => Script.Latin,                  // spacing modifier letters
        <= 0x036F => null,                          // combining diacritical marks: every alphabet's
        <= 0x03FF => Script.Greek,
        <= 0x052F => Script.Cyrillic,
        <= 0x058F => Script.Other,                  // Armenian
        <= 0x05FF => Script.Hebrew,
        <= 0x06FF => Script.Arabic,
        <= 0x074F => Script.Other,                  // Syriac
        <= 0x077F => Script.Arabic,                 // Arabic Supplement
        <= 0x089F => Script.Other,                  // Thaana, NKo, Samaritan, Mandaic
        <= 0x08FF => Script.Arabic,                 // Arabic Extended
        <= 0x0DFF => Script.Indic,                  // Devanagari to Sinhala
        <= 0x0EFF => Script.Thai,                   // Thai, Lao
        <= 0x10FF => Script.Other,                  // Tibetan, Myanmar, Georgian
        <= 0x11FF => Script.Hangul,                 // Hangul Jamo
        >= 0x1C80 and <= 0x1C8F => Script.Cyrillic, // Cyrillic Extended C
        >= 0x1D00 and <= 0x1DBF => Script.Latin,    // phonetic extensions
        >= 0x1DC0 and <= 0x1DFF => null,            // combining marks supplement
        >= 0x1E00 and <= 0x1EFF => Script.Latin,    // Latin Extended Additional
        >= 0x1F00 and <= 0x1FFF => Script.Greek,    // Greek Extended
        >= 0x2C60 and <= 0x2C7F => Script.Latin,    // Latin Extended C
        >= 0x2DE0 and <= 0x2DFF => Script.Cyrillic, // Cyrillic Extended A
        >= 0x2E80 and <= 0x2FDF => Script.Cjk,      // radicals
        >= 0x3000 and <= 0x30FF => Script.Cjk,      // CJK symbols, kana
        >= 0x3130 and <= 0x318F => Script.Hangul,   // compatibility Jamo
        >= 0x31F0 and <= 0x31FF => Script.Cjk,      // kana extensions
        >= 0x3400 and <= 0x4DBF => Script.Cjk,
        >= 0x4E00 and <= 0x9FFF => Script.Cjk,
        >= 0xA640 and <= 0xA69F => Script.Cyrillic, // Cyrillic Extended B
        >= 0xA720 and <= 0xA7FF => Script.Latin,    // Latin Extended D
        >= 0xA960 and <= 0xA97F => Script.Hangul,
        >= 0xAB30 and <= 0xAB6F => Script.Latin,    // Latin Extended E
        >= 0xAC00 and <= 0xD7FF => Script.Hangul,   // syllables and Jamo Extended B
        >= 0xF900 and <= 0xFAFF => Script.Cjk,      // compatibility ideographs
        >= 0xFB00 and <= 0xFB06 => Script.Latin,    // ligatures: ff, fi, fl
        >= 0xFB1D and <= 0xFB4F => Script.Hebrew,
        >= 0xFB50 and <= 0xFDFF => Script.Arabic,   // presentation forms A
        >= 0xFE20 and <= 0xFE2F => null,            // combining half marks
        >= 0xFE70 and <= 0xFEFF => Script.Arabic,   // presentation forms B
        >= 0xFF00 and <= 0xFFEF => Script.Cjk,      // fullwidth and halfwidth forms
        >= 0x20000 and <= 0x3FFFF => Script.Cjk,    // ideographs, extensions B onwards
        _ => Script.Other,
    };

    // What a character cut off after its first bytes can still be. Null where it can still be anything
    // a turn in any script writes - punctuation, symbols, emoji.
    private static Script? OfLead(ReadOnlySpan<byte> bytes)
    {
        var lead = bytes[0];
        if (lead < 0xC2 || lead > 0xF4) return null; // a stray continuation byte or one no character starts with

        // The range of code points the bytes present can still begin: from the smallest to the largest
        // a full character would reach.
        var length = lead < 0xE0 ? 2 : lead < 0xF0 ? 3 : 4;
        var value = lead & (length == 2 ? 0x1F : length == 3 ? 0x0F : 0x07);
        var known = 1;
        for (; known < bytes.Length && known < length && bytes[known] is >= 0x80 and <= 0xBF; known++)
            value = value << 6 | bytes[known] & 0x3F;
        var free = 6 * (length - known);
        var first = Math.Max(value << free, length == 3 ? 0x800 : length == 4 ? 0x10000 : 0x80);
        var last = value << free | ((1 << free) - 1);

        // Punctuation, symbols and emoji: what any turn writes.
        if (Within(first, last, 0x2000, 0x2BFF) || Within(first, last, 0x2E00, 0x2E7F) || Within(first, last, 0x1F000, 0x1FFFF))
            return null;

        // One script's block from end to end: that script.
        var a = OfCodePoint(first);
        var b = OfCodePoint(last);
        if (a == b && a != Script.Other) return a;

        // The range spans blocks: the leads whose whole range is one script's for all a turn writes.
        return lead switch
        {
            <= 0xCB => Script.Latin,              // U+0080-02FF
            <= 0xCD => null,                      // combining marks, and the Greek they run into
            <= 0xCF => Script.Greek,              // U+0380-03FF
            <= 0xD4 => Script.Cyrillic,           // U+0400-053F
            <= 0xD6 => Script.Other,              // Armenian
            0xD7 => Script.Hebrew,                // U+05C0-05FF
            <= 0xDB => Script.Arabic,             // U+0600-06FF
            <= 0xDF => Script.Other,
            0xE2 => null,                         // U+2000-2FFF: punctuation, symbols, arrows
            >= 0xE3 and <= 0xE9 => Script.Cjk,    // U+3000-9FFF
            >= 0xEB and <= 0xED => Script.Hangul, // U+B000-DFFF
            _ => Script.Other,
        };
    }

    private static bool Within(int first, int last, int from, int to) => first >= from && last <= to;
}
