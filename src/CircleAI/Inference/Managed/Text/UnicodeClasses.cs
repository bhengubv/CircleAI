// UnicodeClasses.cs
//
// The character classes the fork's pre-tokenizer asks about - letter (\p{L}), mark (\p{M}),
// number (\p{N}) and whitespace (\s) - read from the fork's own Unicode table
// (UnicodeClasses.g.cs), so the C# tokenizer splits text exactly where the fork does even
// for characters the two Unicode versions disagree on. The Basic Multilingual Plane is
// looked up in a 64 KB table built once; the rest by binary search.

namespace CircleAI.Inference.Managed.Text;

internal static partial class UnicodeClasses
{
    /// <summary>A number (\p{N}).</summary>
    internal const byte Number = 1;

    /// <summary>A letter (\p{L}).</summary>
    internal const byte Letter = 2;

    /// <summary>A combining mark (\p{M}).</summary>
    internal const byte Mark = 4;

    /// <summary>Whitespace as the fork's \s matches it.</summary>
    internal const byte Whitespace = 8;

    /// <summary>Set for every code point: the fork's flags are never all zero for one.</summary>
    internal const byte Defined = 16;

    // The fork's category flags (unicode.h, unicode_cpt_flags).
    private const uint ForkNumber = 0x02, ForkLetter = 0x04, ForkAccentMark = 0x10;

    private static readonly byte[] Bmp = BuildBmp();

    /// <summary>The classes of code point <paramref name="cpt"/>, as bits.</summary>
    internal static byte Of(int cpt) => (uint)cpt < 0x10000 ? Bmp[cpt] : Beyond(cpt);

    private static byte Beyond(int cpt)
    {
        // Past the last code point the fork answers UNDEFINED: still a character, of no class.
        if ((uint)cpt >= 0x110000) return Defined;
        var ranges = Ranges;
        int lo = 0, hi = ranges.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (ranges[mid] >> 8 <= (uint)cpt) lo = mid;
            else hi = mid - 1;
        }
        return Classes(ranges[lo] & 0xFF, cpt);
    }

    private static byte Classes(uint flags, int cpt)
    {
        var classes = Defined;
        if ((flags & ForkNumber) != 0) classes |= Number;
        if ((flags & ForkLetter) != 0) classes |= Letter;
        if ((flags & ForkAccentMark) != 0) classes |= Mark;
        if (WhitespaceSet.BinarySearch(cpt) >= 0) classes |= Whitespace;
        return classes;
    }

    private static byte[] BuildBmp()
    {
        var table = new byte[0x10000];
        var ranges = Ranges;
        for (var i = 0; i + 1 < ranges.Length; i++)
        {
            var start = (int)(ranges[i] >> 8);
            if (start >= table.Length) break;
            var end = Math.Min((int)(ranges[i + 1] >> 8), table.Length);
            var flags = ranges[i] & 0xFF;
            for (var c = start; c < end; c++) table[c] = Classes(flags, c);
        }
        return table;
    }
}
