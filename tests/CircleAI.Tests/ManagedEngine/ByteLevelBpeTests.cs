// ByteLevelBpeTests.cs
//
// The tokenizer. The ruler is the last test: the pinned fork's own token ids and pieces for
// 205 texts (fixtures/bonsai2/2B/tokens.json - 16 everyday ones, 39 aimed at each rule of
// the qwen35 splitter, 150 fixed-seed fuzz), run on the real 2B when it is on this machine.
// The rest run everywhere, on tiny vocabularies: the merge order, special tokens, pieces,
// what is refused, and the splitter's rules as the fork's code states them.

using System.Text;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Text;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class ByteLevelBpeTests(ITestOutputHelper output)
{
    // The 256 byte symbols, then the given tokens, as a GGUF vocabulary.
    private static byte[] Vocabulary(string[] tokens, string[] merges, int[]? types = null,
        string model = "gpt2", string pre = "qwen35", Func<int, bool>? keepByte = null)
    {
        var texts = new List<string>();
        var kinds = new List<int>();
        for (var b = 0; b < 256; b++)
        {
            if (keepByte is not null && !keepByte(b)) continue;
            texts.Add(ByteLevel.Symbol((byte)b).ToString());
            kinds.Add((int)TokenKind.Normal);
        }
        for (var i = 0; i < tokens.Length; i++)
        {
            texts.Add(tokens[i]);
            kinds.Add(types?[i] ?? (int)TokenKind.Normal);
        }
        return new GgufBuilder()
            .String("tokenizer.ggml.model", model)
            .String("tokenizer.ggml.pre", pre)
            .Strings("tokenizer.ggml.tokens", texts)
            .Int32s("tokenizer.ggml.token_type", kinds)
            .Strings("tokenizer.ggml.merges", merges)
            .Build();
    }

    private static void With(byte[] vocabulary, Action<ByteLevelBpe> check) =>
        GgufBuilder.Read(vocabulary, file => check(ByteLevelBpe.FromGguf(file)));

    // A token's text, as the tiny vocabularies spell it (bytes 0..255 are ids 0..255).
    private static string[] Texts(ByteLevelBpe bpe, IEnumerable<int> ids) =>
        ids.Select(id => Encoding.UTF8.GetString(bpe.Piece(id))).ToArray();

    [Fact]
    public void The_lowest_rank_merges_first()
    {
        With(Vocabulary(["ab", "bc"], ["a b", "b c"]), bpe => Assert.Equal(new[] { "ab", "c" }, Texts(bpe, bpe.Encode("abc"))));
        With(Vocabulary(["ab", "bc"], ["b c", "a b"]), bpe => Assert.Equal(new[] { "a", "bc" }, Texts(bpe, bpe.Encode("abc"))));
    }

    [Fact]
    public void Among_equal_ranks_the_leftmost_merges_first_and_merged_symbols_merge_again()
    {
        With(Vocabulary(["aa", "aaaa"], ["a a", "aa aa"]), bpe =>
        {
            Assert.Equal(new[] { "aa", "a" }, Texts(bpe, bpe.Encode("aaa")));
            Assert.Equal(new[] { "aaaa" }, Texts(bpe, bpe.Encode("aaaa")));
            Assert.Equal(new[] { "aaaa", "a" }, Texts(bpe, bpe.Encode("aaaaa")));
        });
    }

    [Fact]
    public void Words_never_merge_across_each_other()
    {
        // " b" starts a new word, so "a" and " " never meet even though "a Ġ" is a merge.
        With(Vocabulary(["aĠ", "Ġb"], ["a Ġ", "Ġ b"]), bpe => Assert.Equal(new[] { "a", " b" }, Texts(bpe, bpe.Encode("a b"))));
    }

    [Fact]
    public void Special_tokens_split_first_and_longest_first()
    {
        string[] tokens = ["<s>", "<s>x", "<c>", "<u>"];
        int[] types = [(int)TokenKind.Control, (int)TokenKind.Control, (int)TokenKind.Control, (int)TokenKind.UserDefined];
        With(Vocabulary(tokens, [], types), bpe =>
        {
            Assert.True(bpe.TryGetSpecial("<s>x", out var longer));
            Assert.Equal(new[] { longer, 'y' }, bpe.Encode("<s>xy").Select(i => i).ToArray());

            Assert.True(bpe.TryGetSpecial("<c>", out var control));
            Assert.True(bpe.TryGetSpecial("<u>", out var user));
            Assert.Equal(new[] { 'a', control, 'b', user, 'c' }, bpe.Encode("a<c>b<u>c").ToArray());

            // Without special parsing, control tokens are text again; user-defined ones are not.
            var plain = bpe.Encode("a<c>b<u>c", parseSpecial: false);
            Assert.DoesNotContain(control, plain);
            Assert.Contains(user, plain);
            Assert.Equal("a<c>b<u>c", string.Concat(Texts(bpe, plain)));
        });
    }

    [Fact]
    public void Pieces_follow_the_forks_token_to_piece()
    {
        string[] tokens = ["Ġhi", "<c>", "<u>", "reserved"];
        int[] types = [(int)TokenKind.Normal, (int)TokenKind.Control, (int)TokenKind.UserDefined, (int)TokenKind.Unused];
        With(Vocabulary(tokens, [], types), bpe =>
        {
            Assert.Equal(" hi"u8.ToArray(), bpe.Piece(256).ToArray());
            Assert.Equal("<c>"u8.ToArray(), bpe.Piece(257).ToArray());
            Assert.Empty(bpe.Piece(257, special: false).ToArray());
            Assert.Equal("<u>"u8.ToArray(), bpe.Piece(258, special: false).ToArray());
            Assert.Empty(bpe.Piece(259).ToArray());
            Assert.Equal(new byte[] { 0x0A }, bpe.Piece(0x0A).ToArray()); // token 10 is written Ċ, and stands for "\n"
            Assert.Throws<ArgumentOutOfRangeException>(() => bpe.Piece(bpe.Count).Length);
        });
    }

    [Fact]
    public void What_is_not_ported_is_refused_not_guessed()
    {
        GgufBuilder.Read(Vocabulary([], [], model: "llama"), file => Assert.Throws<NotSupportedException>(() => ByteLevelBpe.FromGguf(file)));
        GgufBuilder.Read(Vocabulary([], [], pre: "qwen2"), file => Assert.Throws<NotSupportedException>(() => ByteLevelBpe.FromGguf(file)));
        GgufBuilder.Read(Vocabulary([], ["a b"]), file => Assert.Throws<NotSupportedException>(() => ByteLevelBpe.FromGguf(file)));
        GgufBuilder.Read(Vocabulary([], [], keepByte: b => b != 'q'), file => Assert.Throws<NotSupportedException>(() => ByteLevelBpe.FromGguf(file)));
    }

    // The splitter's rules as the fork's unicode_regex_split_custom_qwen35 states them.
    [Theory]
    [InlineData("Hello, world!", new[] { "Hello", ",", " world", "!" })]
    [InlineData("I'm DON'T we'LL", new[] { "I", "'m", " DON", "'T", " we", "'LL" })]
    [InlineData("  leading", new[] { " ", " leading" })]
    [InlineData("x  ", new[] { "x", "  " })]
    [InlineData("a\n\n b", new[] { "a", "\n\n", " b" })]
    [InlineData("123", new[] { "1", "2", "3" })]
    [InlineData("éx ́abc", new[] { "éx", " ́abc" })]
    [InlineData("!!\n\nabc", new[] { "!!\n\n", "abc" })]
    [InlineData("a+b", new[] { "a", "+b" })]
    [InlineData(" x 　 ", new[] { " x", " 　 " })]
    [InlineData(" ", new[] { " " })]
    [InlineData("", new string[0])]
    public void The_qwen35_splitter_cuts_where_the_fork_does(string text, string[] words)
    {
        Assert.Equal(words, ByteLevelBpe.SplitWords(text));
    }

    [Fact]
    public void The_2B_tokenizes_205_texts_exactly_as_the_pinned_fork()
    {
        var path = Bonsai2Model.TwoB();
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        using var file = GgufFile.Open(path);
        var load = System.Diagnostics.Stopwatch.StartNew();
        var bpe = ByteLevelBpe.FromGguf(file);
        load.Stop();

        using var fixture = Bonsai2Model.Fixture("tokens.json");
        var cases = fixture.RootElement.GetProperty("cases").EnumerateArray().ToList();
        var encode = System.Diagnostics.Stopwatch.StartNew();
        var total = 0;
        foreach (var c in cases)
        {
            var text = c.GetProperty("text").GetString()!;
            var expected = c.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            var actual = bpe.Encode(text).ToArray();
            Assert.True(expected.SequenceEqual(actual),
                $"{Show(text)}\n  fork: [{string.Join(", ", expected)}]\n  C#:   [{string.Join(", ", actual)}]");

            var pieces = c.GetProperty("pieces_hex").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Equal(pieces, actual.Select(id => Convert.ToHexStringLower(bpe.Piece(id))).ToArray());

            // Byte-level BPE loses nothing: the pieces put back together are the text.
            Assert.Equal(Encoding.UTF8.GetBytes(text), actual.SelectMany(id => bpe.Piece(id).ToArray()).ToArray());
            total += actual.Length;
        }
        encode.Stop();

        Assert.Equal(205, cases.Count);
        Assert.Equal(248_320, bpe.Count);
        output.WriteLine($"{cases.Count} texts, {total} tokens: all equal the fork's. Vocabulary loaded in {load.ElapsedMilliseconds} ms; encoding took {encode.ElapsedMilliseconds} ms.");

        static string Show(string s) => string.Concat(s.Select(ch => ch < ' ' || ch > '~' ? $"\\u{(int)ch:x4}" : ch.ToString()));
    }
}
