// ByteLevelBpe.cs
//
// The Bonsai 2 fleet's tokenizer - GPT-2 byte-level BPE with the qwen35 pre-tokenizer - run
// the way the pinned fork runs it (src/llama-vocab.cpp, src/unicode.cpp), so every prompt
// becomes the token ids the fork would feed the model:
//
//   1. Special tokens are found in the raw text first, longest first (tokenizer_st_partition).
//   2. Every other stretch is split into words by the fork's hand-written qwen35 splitter
//      (unicode_regex_split_custom_qwen35, ported statement for statement in SplitQwen35),
//      its character classes read from the fork's own Unicode table.
//   3. Each word's UTF-8 bytes become byte-level symbols, merged pair by pair: lowest merge
//      rank first, the leftmost first among equals (llm_tokenizer_bpe_session).
//
// The fork merges by string; this merges by token id, which is the same thing whenever every
// merge that can apply joins two tokens into a token. That is checked when the vocabulary
// loads - all 247,587 of the 2B's merges do - and a vocabulary that breaks it is refused
// rather than guessed at. Pieces (token_to_piece) are decoded once, at load.

using System.Text;
using CircleAI.Inference.Managed.Gguf;

namespace CircleAI.Inference.Managed.Text;

/// <summary>A token's kind, numbered as a GGUF numbers it (<c>tokenizer.ggml.token_type</c>).</summary>
public enum TokenKind : byte
{
    /// <summary>A type the file does not name.</summary>
    Undefined = 0,

    /// <summary>Ordinary text.</summary>
    Normal = 1,

    /// <summary>The unknown token.</summary>
    Unknown = 2,

    /// <summary>A control token such as <c>&lt;|im_start|&gt;</c>.</summary>
    Control = 3,

    /// <summary>An added token such as <c>&lt;think&gt;</c>; split out of text even when control tokens are not.</summary>
    UserDefined = 4,

    /// <summary>A reserved slot.</summary>
    Unused = 5,

    /// <summary>A raw byte (SentencePiece vocabularies).</summary>
    Byte = 6,
}

/// <summary>Byte-level BPE with the Qwen 3.5 pre-tokenizer, tokenizing exactly as the pinned fork does.</summary>
public sealed class ByteLevelBpe
{
    private readonly TokenKind[] _kinds;
    private readonly byte[] _pieces;
    private readonly int[] _pieceStarts;
    private readonly int[] _byteTokens;
    private readonly Dictionary<long, long> _merges;
    private readonly (string Text, int Token)[] _special;
    private readonly Dictionary<string, int> _specialByText;

    private ByteLevelBpe(TokenKind[] kinds, byte[] pieces, int[] pieceStarts, int[] byteTokens,
        Dictionary<long, long> merges, (string Text, int Token)[] special)
    {
        _kinds = kinds;
        _pieces = pieces;
        _pieceStarts = pieceStarts;
        _byteTokens = byteTokens;
        _merges = merges;
        _special = special;
        _specialByText = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (text, token) in special.OrderBy(s => s.Token)) _specialByText[text] = token; // the later of equal texts wins, as in the fork
    }

    /// <summary>Tokens in the vocabulary.</summary>
    public int Count => _kinds.Length;

    /// <summary>The kind of <paramref name="token"/>.</summary>
    public TokenKind Kind(int token) => _kinds[token];

    /// <summary>The control, user-defined or unknown token whose text is <paramref name="text"/>.</summary>
    public bool TryGetSpecial(string text, out int token) => _specialByText.TryGetValue(text, out token);

    /// <summary>
    /// The tokenizer of <paramref name="file"/>'s vocabulary. Only what the fleet uses is ported -
    /// the gpt2 model with the qwen35 pre-tokenizer - and anything else is refused.
    /// </summary>
    public static ByteLevelBpe FromGguf(GgufFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var model = file["tokenizer.ggml.model"].AsString();
        if (model != "gpt2") throw new NotSupportedException($"Tokenizer model {model}: only gpt2 (byte-level BPE) is ported.");
        var pre = file.TryGetValue("tokenizer.ggml.pre", out var preValue) ? preValue.AsString() : "default";
        if (pre != "qwen35") throw new NotSupportedException($"Pre-tokenizer {pre}: only the fleet's qwen35 is ported.");

        var tokens = file["tokenizer.ggml.tokens"].AsArray();
        var count = tokens.Count;
        var types = file.TryGetValue("tokenizer.ggml.token_type", out var typeValue) ? typeValue.AsArray().ToInt32Array() : null;
        if (types is not null && types.Length != count)
            throw new InvalidDataException($"{count} tokens but {types.Length} token types.");

        // Text to id, as the fork builds it: an empty token is named [EMPTY_i], and of two
        // equal texts the later id wins.
        var kinds = new TokenKind[count];
        var texts = new string[count];
        var ids = new Dictionary<string, int>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var text = tokens.GetString(i);
            if (text.Length == 0) text = $"[EMPTY_{i}]";
            texts[i] = text;
            ids[text] = i;
            kinds[i] = types is null ? TokenKind.Normal : types[i] is >= 0 and <= 6 ? (TokenKind)types[i] : TokenKind.Undefined;
        }

        var byteTokens = new int[256];
        for (var b = 0; b < byteTokens.Length; b++)
        {
            if (!ids.TryGetValue(ByteLevel.Symbol((byte)b).ToString(), out byteTokens[b]))
                throw new NotSupportedException($"The vocabulary has no token for byte 0x{b:X2}; symbols that are not tokens are not ported.");
        }

        var mergeList = file["tokenizer.ggml.merges"].AsArray();
        var merges = new Dictionary<long, long>(mergeList.Count);
        for (var rank = 0; rank < mergeList.Count; rank++)
        {
            var merge = mergeList.GetString(rank);
            var space = merge.Length > 1 ? merge.IndexOf(' ', 1) : -1;
            if (space < 0) continue; // the fork keeps an empty pair, which nothing matches
            if (!ids.TryGetValue(merge[..space], out var left) || !ids.TryGetValue(merge[(space + 1)..], out var right))
                continue; // a side that is not a token is never a symbol, so this merge never applies
            if (!ids.TryGetValue(texts[left] + texts[right], out var merged))
                throw new NotSupportedException($"Merge {rank} ({merge}) makes a string that is not a token; merging by string is not ported.");
            merges.TryAdd(Key(left, right), ((long)rank << 32) | (uint)merged); // the lower rank of a repeated pair wins
        }

        // Every piece decoded once, as the fork caches token_to_piece(special: true).
        var pieces = new MemoryStream();
        var pieceStarts = new int[count + 1];
        for (var i = 0; i < count; i++)
        {
            pieceStarts[i] = (int)pieces.Length;
            switch (kinds[i])
            {
                case TokenKind.Control or TokenKind.Unknown or TokenKind.UserDefined:
                    pieces.Write(Encoding.UTF8.GetBytes(texts[i]));
                    break;
                case TokenKind.Normal:
                    ByteLevel.Decode(texts[i], pieces);
                    break;
            }
        }
        pieceStarts[count] = (int)pieces.Length;

        // Special tokens, longest first as the fork sorts them (by UTF-8 length; the fork leaves
        // ties in no particular order, here they go by id).
        var special = Enumerable.Range(0, count)
            .Where(i => kinds[i] is TokenKind.Control or TokenKind.UserDefined or TokenKind.Unknown)
            .Select(i => (Text: texts[i], Token: i))
            .OrderByDescending(s => Encoding.UTF8.GetByteCount(s.Text))
            .ThenBy(s => s.Token)
            .ToArray();

        return new ByteLevelBpe(kinds, pieces.ToArray(), pieceStarts, byteTokens, merges, special);
    }

    /// <summary>
    /// The token ids of <paramref name="text"/>. With <paramref name="parseSpecial"/> off, control
    /// and unknown tokens are tokenized as plain text, but user-defined ones are still split out,
    /// as in the fork. No start-of-text token is added.
    /// </summary>
    public List<int> Encode(string text, bool parseSpecial = true)
    {
        var output = new List<int>();
        Encode(text, output, parseSpecial);
        return output;
    }

    /// <summary>Appends the token ids of <paramref name="text"/> to <paramref name="output"/>.</summary>
    public void Encode(string text, List<int> output, bool parseSpecial = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(output);

        var fragments = new List<(int Start, int Length, int Token)> { (0, text.Length, -1) };
        foreach (var (special, token) in _special)
        {
            if (!parseSpecial && _kinds[token] is TokenKind.Control or TokenKind.Unknown) continue;
            fragments = Partition(text, fragments, special, token);
        }

        var queue = new PriorityQueue<Bigram, (int Rank, int Left)>();
        var codepoints = new List<int>();
        var words = new List<int>();
        var utf8 = new List<byte>();
        Span<byte> scratch = stackalloc byte[4];
        foreach (var (start, length, token) in fragments)
        {
            if (token >= 0)
            {
                output.Add(token);
                continue;
            }

            codepoints.Clear();
            foreach (var rune in text.AsSpan(start, length).EnumerateRunes()) codepoints.Add(rune.Value);
            words.Clear();
            SplitQwen35(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(codepoints), words);

            var at = 0;
            foreach (var word in words)
            {
                utf8.Clear();
                for (var i = at; i < at + word; i++)
                {
                    var n = new Rune(codepoints[i]).EncodeToUtf8(scratch);
                    for (var k = 0; k < n; k++) utf8.Add(scratch[k]);
                }
                Merge(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(utf8), output, queue);
                at += word;
            }
        }
    }

    /// <summary>
    /// The bytes <paramref name="token"/> stands for, as the fork's token_to_piece gives them:
    /// a control or unknown token's text only when <paramref name="special"/> is on, a
    /// user-defined token's text always, a reserved or byte slot nothing.
    /// </summary>
    public ReadOnlySpan<byte> Piece(int token, bool special = true)
    {
        if ((uint)token >= (uint)_kinds.Length) throw new ArgumentOutOfRangeException(nameof(token), token, $"The vocabulary holds {_kinds.Length}.");
        if (!special && _kinds[token] is TokenKind.Control or TokenKind.Unknown) return [];
        return _pieces.AsSpan(_pieceStarts[token], _pieceStarts[token + 1] - _pieceStarts[token]);
    }

    /// <summary>The words the qwen35 pre-tokenizer splits <paramref name="text"/> into (special tokens not considered).</summary>
    internal static List<string> SplitWords(string text)
    {
        var codepoints = new List<int>();
        foreach (var rune in text.EnumerateRunes()) codepoints.Add(rune.Value);
        var lengths = new List<int>();
        SplitQwen35(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(codepoints), lengths);

        var words = new List<string>(lengths.Count);
        var at = 0;
        foreach (var length in lengths)
        {
            var sb = new StringBuilder();
            for (var i = at; i < at + length; i++) sb.Append(new Rune(codepoints[i]).ToString());
            words.Add(sb.ToString());
            at += length;
        }
        return words;
    }

    // tokenizer_st_partition for one special token: every raw stretch is split around each
    // occurrence, left to right; stretches emptied by the split are dropped.
    private static List<(int Start, int Length, int Token)> Partition(
        string text, List<(int Start, int Length, int Token)> fragments, string special, int token)
    {
        var result = new List<(int Start, int Length, int Token)>(fragments.Count);
        foreach (var fragment in fragments)
        {
            if (fragment.Token >= 0)
            {
                result.Add(fragment);
                continue;
            }

            var start = fragment.Start;
            var end = fragment.Start + fragment.Length;
            while (start < end)
            {
                var match = text.IndexOf(special, start, end - start, StringComparison.Ordinal);
                if (match < 0) break;
                if (match > start) result.Add((start, match - start, -1));
                result.Add((match, special.Length, token));
                start = match + special.Length;
            }
            if (start < end) result.Add((start, end - start, -1));
        }
        return result;
    }

    // One word's bytes, merged as the fork's llm_tokenizer_bpe_session merges them.
    private void Merge(ReadOnlySpan<byte> word, List<int> output, PriorityQueue<Bigram, (int Rank, int Left)> queue)
    {
        var n = word.Length;
        if (n == 0) return;
        var symbols = new int[n];
        var previous = new int[n];
        var next = new int[n];
        for (var i = 0; i < n; i++)
        {
            symbols[i] = _byteTokens[word[i]];
            previous[i] = i - 1;
            next[i] = i + 1 < n ? i + 1 : -1;
        }

        queue.Clear();
        for (var i = 1; i < n; i++) Enqueue(queue, symbols, i - 1, i);
        while (queue.TryDequeue(out var bigram, out _))
        {
            // Outdated: one side has merged with something else since this pair was queued.
            if (symbols[bigram.Left] != bigram.LeftToken || symbols[bigram.Right] != bigram.RightToken) continue;

            symbols[bigram.Left] = bigram.Merged;
            symbols[bigram.Right] = -1;
            var after = next[bigram.Right];
            next[bigram.Left] = after;
            if (after >= 0) previous[after] = bigram.Left;
            Enqueue(queue, symbols, previous[bigram.Left], bigram.Left);
            Enqueue(queue, symbols, bigram.Left, after);
        }

        for (var i = 0; i >= 0; i = next[i]) output.Add(symbols[i]);
    }

    private void Enqueue(PriorityQueue<Bigram, (int Rank, int Left)> queue, int[] symbols, int left, int right)
    {
        if (left < 0 || right < 0) return;
        if (!_merges.TryGetValue(Key(symbols[left], symbols[right]), out var packed)) return;
        queue.Enqueue(new Bigram(left, right, symbols[left], symbols[right], (int)packed), ((int)(packed >> 32), left));
    }

    private static long Key(int left, int right) => ((long)left << 32) | (uint)right;

    private readonly record struct Bigram(int Left, int Right, int LeftToken, int RightToken, int Merged);

    // The fork's unicode_regex_split_custom_qwen35, statement for statement. The regex it
    // stands for:
    //   (?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?[\p{L}\p{M}]+|\p{N}
    //   | ?[^\s\p{L}\p{M}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+
    // Appends each word's length in code points to words.
    private static void SplitQwen35(ReadOnlySpan<int> cpts, List<int> words)
    {
        const int OutOfRange = -1;
        var end = cpts.Length;
        var pos = 0;
        while (pos < end)
        {
            var cpt = cpts[pos];
            var flags = UnicodeClasses.Of(cpt);
            var start = pos;

            // (?i:'s|'t|'re|'ve|'m|'ll|'d)
            if (cpt == '\'' && pos + 1 < end)
            {
                var next = AsciiLower(cpts[pos + 1]);
                if (next is 's' or 't' or 'm' or 'd')
                {
                    pos += 2;
                    words.Add(pos - start);
                    continue;
                }
                if (pos + 2 < end)
                {
                    var nextNext = AsciiLower(cpts[pos + 2]);
                    if ((next == 'r' && nextNext == 'e') || (next == 'v' && nextNext == 'e') || (next == 'l' && nextNext == 'l'))
                    {
                        pos += 3;
                        words.Add(pos - start);
                        continue;
                    }
                }
            }

            // [^\r\n\p{L}\p{N}]?[\p{L}\p{M}]+
            if (!(cpt == '\r' || cpt == '\n' || Has(flags, UnicodeClasses.Number)))
            {
                var after = FlagsAt(cpts, pos + 1);
                if (Has(flags, UnicodeClasses.Letter | UnicodeClasses.Mark) || Has(after, UnicodeClasses.Letter | UnicodeClasses.Mark))
                {
                    pos++;
                    while (Has(FlagsAt(cpts, pos), UnicodeClasses.Letter | UnicodeClasses.Mark)) pos++;
                    words.Add(pos - start);
                    continue;
                }
            }

            // \p{N}
            if (Has(flags, UnicodeClasses.Number))
            {
                pos++;
                words.Add(pos - start);
                continue;
            }

            // <space>?[^\s\p{L}\p{M}\p{N}]+[\r\n]*
            const byte NotPunctuation = UnicodeClasses.Whitespace | UnicodeClasses.Letter | UnicodeClasses.Mark | UnicodeClasses.Number;
            var flags2 = cpt == ' ' ? FlagsAt(cpts, pos + 1) : flags;
            if (!Has(flags2, NotPunctuation) && flags != 0)
            {
                if (cpt == ' ') pos++;
                while (!Has(flags2, NotPunctuation) && flags2 != 0) flags2 = FlagsAt(cpts, ++pos);
                var cpt2 = pos < end ? cpts[pos] : OutOfRange;
                while (cpt2 == '\r' || cpt2 == '\n') cpt2 = ++pos < end ? cpts[pos] : OutOfRange;
                words.Add(pos - start);
                continue;
            }

            var whitespaces = 0;
            var lastNewlineEnd = 0;
            while (Has(FlagsAt(cpts, pos + whitespaces), UnicodeClasses.Whitespace))
            {
                var c = cpts[pos + whitespaces];
                if (c == '\r' || c == '\n') lastNewlineEnd = pos + whitespaces + 1;
                whitespaces++;
            }

            // \s*[\r\n]+
            if (lastNewlineEnd > 0)
            {
                pos = lastNewlineEnd;
                words.Add(pos - start);
                continue;
            }

            // \s+(?!\S)
            if (whitespaces > 1 && pos + whitespaces < end)
            {
                pos += whitespaces - 1;
                words.Add(pos - start);
                continue;
            }

            // \s+
            if (whitespaces > 0)
            {
                pos += whitespaces;
                words.Add(pos - start);
                continue;
            }

            // no match: the character alone
            pos++;
            words.Add(pos - start);
        }
    }

    private static byte FlagsAt(ReadOnlySpan<int> cpts, int pos) => (uint)pos < (uint)cpts.Length ? UnicodeClasses.Of(cpts[pos]) : (byte)0;

    private static bool Has(byte flags, byte any) => (flags & any) != 0;

    private static int AsciiLower(int c) => c is >= 'A' and <= 'Z' ? c + 32 : c;
}

/// <summary>GPT-2's byte-to-character map, which byte-level BPE vocabularies are written in.</summary>
internal static class ByteLevel
{
    private static readonly char[] Symbols = BuildSymbols();
    private static readonly short[] Bytes = BuildBytes();

    /// <summary>The character byte <paramref name="b"/> is written as.</summary>
    internal static char Symbol(byte b) => Symbols[b];

    /// <summary>
    /// Writes the bytes a token's text stands for. A character outside the map is written the
    /// way the fork's llama_decode_text writes it: [UNK_BYTE_0x, its UTF-8 in hex, the whole
    /// token text, ].
    /// </summary>
    internal static void Decode(string text, Stream output)
    {
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var rune in text.EnumerateRunes())
        {
            var b = rune.Value < Bytes.Length ? Bytes[rune.Value] : (short)-1;
            if (b >= 0)
            {
                output.WriteByte((byte)b);
                continue;
            }
            var n = rune.EncodeToUtf8(utf8);
            var unknown = new StringBuilder("[UNK_BYTE_0x");
            for (var i = 0; i < n; i++) unknown.Append(utf8[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            unknown.Append(text).Append(']');
            output.Write(Encoding.UTF8.GetBytes(unknown.ToString()));
        }
    }

    // Printable bytes stand for themselves; the others take 256, 257, ... in byte order.
    private static char[] BuildSymbols()
    {
        var symbols = new char[256];
        var next = 256;
        for (var b = 0; b < 256; b++)
            symbols[b] = b is >= 0x21 and <= 0x7E or >= 0xA1 and <= 0xAC or >= 0xAE and <= 0xFF ? (char)b : (char)next++;
        return symbols;
    }

    private static short[] BuildBytes()
    {
        var bytes = new short[256 + 68];
        Array.Fill(bytes, (short)-1);
        for (var b = 0; b < 256; b++) bytes[Symbols[b]] = (short)b;
        return bytes;
    }
}
