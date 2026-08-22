// voice-fixtures — writes the golden files every language port asserts against.
//
// WHY THIS EXISTS. Nine ports reimplementing a Viterbi tokeniser and a phone
// table will each be "correct" against their own assumptions and disagree with
// one another, which is the thing parity is supposed to prevent. So the C#
// implementation — the reference — EMITS the expected answers, and every port
// asserts against the same JSON. A port that drifts fails; a port that agrees
// is provably identical, not merely plausible.
//
// Regenerate:  dotnet run --project tools/voice-fixtures
// A changed fixture is a CONTRACT CHANGE. If this produces a diff you did not
// intend, the C# side moved and every port now has to move with it.

using System.Text;
using System.Text.Json;
using CircleAI.Voice;

var repo = FindRepoRoot();
var outDir = Path.Combine(repo, "fixtures");
Directory.CreateDirectory(outDir);

var json = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

// ---------------------------------------------------------------- X-SAMPA→IPA
{
    // Every phone in the table, plus cases that exercise the traps: multi-char
    // tokens that must match whole (A:r is not A + : + r), the script-g that is
    // U+0261 and not ASCII 'g', and an unmappable phone which must be REPORTED
    // rather than silently dropped.
    string[][] cases =
    [
        ["h\\", "O", "n", "t"],                       // hond — the one approximation
        ["A:r", "b", "e", "i"],                       // longest-match: A:r stays whole
        ["g", "u", "d"],                              // g must become U+0261
        ["9y", "@i", "@u"],                           // diphthongs, one token each
        ["a", "ZZZ", "b"],                            // unmappable in the middle
    ];

    var payload = new
    {
        _comment = "X-SAMPA (NchltPhonemizer) -> IPA (Mimic3-family voices). "
                 + "Longest-match on WHOLE tokens; 'g' is U+0261 LATIN SMALL LETTER SCRIPT G, "
                 + "not ASCII 'g'. An unmapped phone must be reported, never dropped silently.",
        _source = "src/CircleAI.Voice/XsampaToIpa.cs",
        knownPhones = XsampaToIpa.KnownPhones.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
        cases = cases.Select(c =>
        {
            var ipa = XsampaToIpa.Convert(c);
            return new
            {
                xsampa = c,
                ipa = ipa.ToArray(),
                unmapped = XsampaToIpa.LastUnmapped.ToArray(),
                canSayAll = XsampaToIpa.CanSayAll(c),
            };
        }).ToArray(),
    };
    Write(Path.Combine(outDir, "voice_xsampa_to_ipa.json"), payload);
}

// -------------------------------------------------- SentencePiece unigram
{
    // A SMALL HAND-BUILT VOCABULARY, not the shipped 4000-piece one. The fixture
    // has to be self-contained and reviewable, and the algorithm is what is being
    // pinned — not Kyutai's weights. The scores are chosen so that GREEDY
    // LONGEST-MATCH GIVES A DIFFERENT ANSWER TO VITERBI, which is the whole point:
    // a port that took the greedy shortcut passes a naive test and fails this one.
    var vocab = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["<unk>"] = 0, ["<s>"] = 1, ["</s>"] = 2, ["<pad>"] = 3,
        ["▁"] = 4, ["▁he"] = 5, ["▁hell"] = 6, ["▁hello"] = 7,
        ["h"] = 8, ["e"] = 9, ["l"] = 10, ["o"] = 11, ["w"] = 12, ["r"] = 13, ["d"] = 14,
        ["▁world"] = 15, ["▁wor"] = 16, ["ld"] = 17, ["lo"] = 18, ["ll"] = 19,
        // THE BYTE PIECES ARE NOT OPTIONAL. Without them a character no piece
        // covers produces NOTHING — the audio is simply shorter than the text,
        // which every acoustic check still passes. The first draft of this
        // fixture omitted them and duly recorded "hé" encoding to just h,
        // enshrining the silent drop as the expected answer.
        ["<0xC3>"] = 20, ["<0xA9>"] = 21,
    };
    // Deliberately: "▁hello" scores WORSE than "▁hell" + "o", so greedy longest
    // match picks ▁hello and Viterbi does not.
    var scores = new Dictionary<string, float>(StringComparer.Ordinal)
    {
        ["<unk>"] = 0f, ["<s>"] = 0f, ["</s>"] = 0f, ["<pad>"] = 0f,
        ["▁"] = -6f, ["▁he"] = -4f, ["▁hell"] = -2.0f, ["▁hello"] = -9.0f,
        ["h"] = -7f, ["e"] = -5f, ["l"] = -5f, ["o"] = -1.0f, ["w"] = -7f, ["r"] = -7f, ["d"] = -6f,
        ["▁world"] = -1.5f, ["▁wor"] = -5f, ["ld"] = -3f, ["lo"] = -4f, ["ll"] = -4f,
        ["<0xC3>"] = -20f, ["<0xA9>"] = -20f,
    };

    var tmpVocab = Path.GetTempFileName();
    var tmpScores = Path.GetTempFileName();
    File.WriteAllText(tmpVocab, JsonSerializer.Serialize(vocab, json), Encoding.UTF8);
    File.WriteAllText(tmpScores, JsonSerializer.Serialize(scores, json), Encoding.UTF8);
    var sp = SentencePieceUnigram.Load(tmpVocab, tmpScores);
    File.Delete(tmpVocab); File.Delete(tmpScores);

    string[] texts =
    [
        "hello world",     // the greedy-vs-Viterbi case
        "hello",
        "world",
        "hell",
        "",                // empty in, empty out
        "hé",              // byte fallback: é is in no piece
    ];

    var payload = new
    {
        _comment = "SentencePiece unigram encoding. VITERBI over the piece lattice, NOT greedy "
                 + "longest-match: scores are not monotone in piece length, and this vocabulary is "
                 + "built so the two disagree. Normalise NFKC, replace ' ' with U+2581, prepend one. "
                 + "Unknown characters fall back to <0xNN> BYTE pieces and are never dropped.",
        _source = "src/CircleAI.Voice/SentencePieceUnigram.cs",
        fallbackPenalty = 10.0,
        vocab,
        scores,
        cases = texts.Select(t => new { text = t, ids = sp.Encode(t).ToArray() }).ToArray(),
    };
    Write(Path.Combine(outDir, "voice_sentencepiece_unigram.json"), payload);
}

// ------------------------------------------------------------------- WAV I/O
{
    // Byte-exact little RIFF files, base64'd so the fixture stays one reviewable
    // text file. The LIST chunk case is the one that matters: a reader that
    // assumes data starts at byte 44 reads metadata as audio.
    var cases = new List<object>();
    foreach (var (name, bytes, expect) in BuildWavCases())
        cases.Add(new { name, wavBase64 = Convert.ToBase64String(bytes), expected = expect });

    var payload = new
    {
        _comment = "RIFF/WAVE reading. WALK THE CHUNKS — data does not always start at byte 44; "
                 + "a LIST or fact chunk before it is normal and assuming otherwise reads metadata "
                 + "as audio. Samples are mono float in [-1,1]; multi-channel is averaged.",
        _source = "src/CircleAI.Voice/WavIo.cs",
        cases,
    };
    Write(Path.Combine(outDir, "voice_wav_io.json"), payload);
}

Console.WriteLine("fixtures written to " + outDir);
return 0;

void Write(string path, object payload)
{
    File.WriteAllText(path, JsonSerializer.Serialize(payload, json) + "\n", new UTF8Encoding(false));
    Console.WriteLine($"  {Path.GetFileName(path),-40} {new FileInfo(path).Length,8:N0} bytes");
}

static List<(string Name, byte[] Bytes, object Expected)> BuildWavCases()
{
    var result = new List<(string, byte[], object)>();

    // 16-bit mono, no extra chunks.
    var plain = Wav(1, 16, 24000, [0, 0, 0x00, 0x40, 0x00, 0xC0], withList: false);
    result.Add(("pcm16-mono-plain", plain, Describe(plain)));

    // Same audio, with a LIST chunk in front of the data.
    var listed = Wav(1, 16, 24000, [0, 0, 0x00, 0x40, 0x00, 0xC0], withList: true);
    result.Add(("pcm16-mono-with-LIST-chunk", listed, Describe(listed)));

    // Stereo — must be averaged to mono.
    var stereo = Wav(2, 16, 24000, [0x00, 0x40, 0x00, 0xC0, 0x00, 0x40, 0x00, 0x40], withList: false);
    result.Add(("pcm16-stereo-averaged", stereo, Describe(stereo)));

    return result;

    static object Describe(byte[] wav)
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllBytes(tmp, wav);
        var samples = WavIo.ReadMono24k(tmp);
        File.Delete(tmp);
        return new
        {
            sampleCount = samples.Length,
            samples = samples.Select(s => Math.Round(s, 6)).ToArray(),
        };
    }

    static byte[] Wav(int channels, int bits, int rate, byte[] data, bool withList)
    {
        var m = new MemoryStream();
        var w = new BinaryWriter(m);
        var blockAlign = channels * bits / 8;
        var listLen = withList ? 12 : 0;                 // "LIST" + size + 4 bytes payload
        w.Write("RIFF"u8); w.Write(36 + listLen + data.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16);
        w.Write((short)1); w.Write((short)channels); w.Write(rate);
        w.Write(rate * blockAlign); w.Write((short)blockAlign); w.Write((short)bits);
        if (withList) { w.Write("LIST"u8); w.Write(4); w.Write("INFO"u8); }
        w.Write("data"u8); w.Write(data.Length); w.Write(data);
        w.Flush();
        return m.ToArray();
    }
}

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "CircleAI.sln"))) d = d.Parent;
    return d?.FullName ?? throw new InvalidOperationException("CircleAI.sln not found above the binary.");
}
