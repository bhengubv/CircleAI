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

// ------------------------------------------------------- PiperVoiceConfig
{
    // A HAND-BUILT VOCABULARY WITH TWO DIFFERENT PAD IDS, because that is THE
    // rule this module exists to get right: `_` resolves to whatever THAT model
    // calls blank — id 0 in sherpa/MMS exports, 3 in Piper-family ones — and
    // pointing it at an ordinary vocab entry is what made 42 MMS voices speak
    // fluent nonsense. A port that hard-codes 0 passes the first config and
    // fails the second.
    var piperLike = new Dictionary<string, long[]>(StringComparer.Ordinal)
    {
        ["_"] = [0], ["^"] = [1], ["$"] = [2], [" "] = [3],
        ["a"] = [4], ["b"] = [5], ["k"] = [6], ["s"] = [7], ["t"] = [8],
        ["n"] = [9], ["ŋ"] = [10], ["ʃ"] = [11], ["d"] = [12], ["ɡ"] = [13],
    };
    var mmsLike = new Dictionary<string, long[]>(StringComparer.Ordinal)
    {
        ["<PAD>"] = [0], ["<EOS>"] = [1], ["<BOS>"] = [2],
        // Both names point at 3 — the sherpa/MMS convention this catalogue ships.
        ["<BLNK>"] = [3], ["_"] = [3],
        ["a"] = [4], ["b"] = [5], ["k"] = [6], ["s"] = [7], ["t"] = [8], ["n"] = [9],
    };

    var configs = new[]
    {
        (name: "piper-like (pad=0, has BOS/EOS)", map: piperLike, rate: 22050),
        (name: "mms-like (pad=3, no BOS/EOS)", map: mmsLike, rate: 16000),
    };

    // Each case exercises one trap named in the source.
    string[][] phonemeCases =
    [
        ["b", "a", "t"],                 // ordinary
        ["B", "A", "T"],                 // lower-case fallback (grapheme vocabs have no capitals)
        ["a", "ZZZ", "t"],               // unknown symbol: skipped AND reported
        ["ṅ", "a"],                      // exact phonetic stand-in: ṅ IS /ŋ/
        ["š", "a"],                      // exact phonetic stand-in: š IS /ʃ/
        ["ṱ", "a"],                      // diacritic fold to a Latin base: ṱ -> t
        ["ก", "a"],                      // Thai: NOT Latin-based, must NOT be folded
    ];

    var payload = new
    {
        _comment = "Piper phoneme->id layout. THE PAD RULE: `_` resolves to THAT model's blank "
                 + "— 0 in sherpa/MMS exports, 3 in Piper-family ones — never a constant. Layout is "
                 + "[BOS, PAD, id, PAD, id, PAD, ..., EOS], with BOS/EOS emitted only when the map "
                 + "has them. Unknown symbols are SKIPPED and REPORTED, never fatal. Approximations "
                 + "are reported separately because they are a compromise, not a success.",
        _source = "src/CircleAI.Voice/PiperVoiceConfig.cs",
        configs = configs.Select(c =>
        {
            var json = JsonSerializer.Serialize(new
            {
                audio = new { sample_rate = c.rate },
                inference = new { noise_scale = 0.667, length_scale = 1.0, noise_w = 0.8 },
                phoneme_type = "espeak",
                phoneme_id_map = c.map,
            }, System.Text.Json.JsonSerializerOptions.Default);
            var cfg = PiperVoiceConfig.Parse(JsonDocument.Parse(json).RootElement);

            return new
            {
                name = c.name,
                configJson = c.map,
                sampleRate = cfg.SampleRate,
                padId = cfg.PadId,
                hasPhonemeMap = cfg.HasPhonemeMap,
                cases = phonemeCases.Select(p =>
                {
                    var ids = cfg.PhonemesToIds(p, out var skipped, out var skippedSymbols,
                                                out var approximated);
                    return new
                    {
                        phonemes = p,
                        ids,
                        skipped,
                        skippedSymbols = skippedSymbols.ToArray(),
                        approximatedSymbols = approximated.ToArray(),
                    };
                }).ToArray(),
            };
        }).ToArray(),
        splitPhonemeString = new[] { "bat", "bát", "กัb" }
            .Select(s => new { input = s, elements = PiperVoiceConfig.SplitPhonemeString(s).ToArray() })
            .ToArray(),
    };
    Write(Path.Combine(outDir, "voice_piper_config.json"), payload);
}

// -------------------------------------------------------- LexiconTokeniser
{
    // Word-keyed, overlapping entries, so LONGEST MATCH FIRST is observable:
    // あい, あいさつ and あいかわらず all start the same way, and taking the
    // shortest pronounces a different word.
    var tokens = new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["<blank>"] = 0, ["a"] = 1, ["i"] = 2, ["s"] = 3, ["ts"] = 4,
        ["k"] = 5, ["w"] = 6, ["r"] = 7, ["u"] = 8, ["n"] = 9, ["o"] = 10,
    };
    var lexicon = new (string Word, string[] Phonemes)[]
    {
        ("あ", ["a"]),
        ("あい", ["a", "i"]),
        ("あいさつ", ["a", "i", "s", "a", "ts", "u"]),
        ("あいかわらず", ["a", "i", "k", "a", "w", "a", "r", "a", "z", "u"]),
        ("ん", ["n"]),
    };

    var dir = Path.Combine(Path.GetTempPath(), "voicefix-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    File.WriteAllLines(Path.Combine(dir, "tokens.txt"),
        tokens.Select(kv => $"{kv.Key} {kv.Value}"), Encoding.UTF8);
    File.WriteAllLines(Path.Combine(dir, "lexicon.txt"),
        lexicon.Select(e => $"{e.Word} {string.Join(' ', e.Phonemes)}"), Encoding.UTF8);
    var model = Path.Combine(dir, "model.onnx");
    File.WriteAllBytes(model, [0]);

    var lex = LexiconTokeniser.TryLoadForModel(model)
              ?? throw new InvalidOperationException("fixture lexicon failed to load");

    string[] texts = ["あいさつ", "あい", "あ", "あいかわらず", "あい ん", "あXい"];
    var payload = new
    {
        _comment = "Lexicon-driven tokenising: a word->phoneme table plus a phoneme->id table, "
                 + "no phonemizer process and no licence wall. LONGEST MATCH FIRST over the word "
                 + "keys — the entries overlap, and the shortest match pronounces a different word. "
                 + "Falls back to the single character when nothing matches, and REPORTS what it "
                 + "could not map. tokens.txt splits on the LAST space, because the symbol itself "
                 + "may be a space. With add_blank, a blank opens the utterance and follows every "
                 + "token.",
        _source = "src/CircleAI.Voice/LexiconTokeniser.cs",
        tokens,
        lexicon = lexicon.Select(e => new { word = e.Word, phonemes = e.Phonemes }).ToArray(),
        blank = 0,
        cases = texts.Select(t =>
        {
            var bare = lex.Encode(t, interleaveBlank: false);
            var bareUnmapped = lex.LastUnmapped.ToArray();
            var padded = lex.Encode(t, interleaveBlank: true);
            return new { text = t, ids = bare, idsWithBlank = padded, unmapped = bareUnmapped };
        }).ToArray(),
    };
    Write(Path.Combine(outDir, "voice_lexicon_tokeniser.json"), payload);
    Directory.Delete(dir, recursive: true);
}

// ------------------------------------------------------------- AudioFormat
{
    var payload = new
    {
        _comment = "The canonical PCM format the voice components expect. Most open-source ASR "
                 + "engines (sherpa-onnx, Vosk) take this directly.",
        _source = "src/CircleAI.Voice/AudioFormat.cs",
        pcm16Mono16k = new
        {
            sampleRate = AudioFormat.Pcm16Mono16k.SampleRate,
            channels = AudioFormat.Pcm16Mono16k.Channels,
            bitsPerSample = AudioFormat.Pcm16Mono16k.BitsPerSample,
        },
    };
    Write(Path.Combine(outDir, "voice_audio_format.json"), payload);
}

// ------------------------------------------------------------ SentenceSplitter
{
    // The cases are chosen for what BREAKS a port, not for what is typical:
    // a decimal point and a domain name that must NOT split, a danda and an
    // Arabic full stop that must, a CJK stop with no space after it, an
    // over-long run with and without a space to cut at, and a segment of pure
    // punctuation that has no sound to make.
    (string Name, string Text)[] cases =
    [
        ("empty",             ""),
        ("whitespace-only",   "   \t  "),
        ("two-sentences",     "Sawubona. Unjani?"),
        ("clause-breaks",     "Listen: this matters; then go. Done!"),
        ("decimal-point",     "It costs 3.5 rand. Really."),
        ("domain-name",       "Visit thegeek.co.za for more. Thanks."),
        ("devanagari-danda",  "नमस्ते। आप कैसे हैं। ठीक"),
        ("urdu-full-stop",    "السلام علیکم۔ آپ کیسے ہیں؟"),
        ("cjk-no-space",      "你好。你好吗？很好"),
        ("ethiopic-stop",     "ሰላም። እንዴት ነህ"),
        ("khmer-khan",        "សួស្តី។ អ្នកសុខសប្បាយទេ"),
        ("paragraph-break",   "Line one\nLine two."),
        ("ellipsis-absorbed", "Wait... Then go."),
        ("quote-absorbed",    "He said \"go.\" Then left."),
        ("punctuation-only",  "... Hello."),
        ("forced-cut",        new string('a', 60) + " " + new string('b', 60) + " "
                              + new string('c', 60) + " " + new string('d', 60) + " tail."),
        ("no-space-to-cut",   new string('x', 260) + " end."),
    ];

    var payload = new
    {
        _comment = "Sentence-sized units for synthesis, plus the silence that follows each. "
                 + "The voices carry no punctuation tokens, so the PAUSE is the only sentence "
                 + "break they get. Splits at sentence boundaries only, never at commas: a "
                 + "VITS model ends every utterance with falling prosody, so cutting at a comma "
                 + "makes each clause land like a finished sentence. The last segment always "
                 + "has a pause of 0 — trailing silence at the end of a passage serves nothing.",
        _source = "src/CircleAI.Voice/SentenceSplitter.cs",
        maxCharsPerSegment = SentenceSplitter.MaxCharsPerSegment,
        pauses = new { sentence = 280, clause = 200, paragraph = 400, forced = 60 },
        cases = cases.Select(c => new
        {
            name = c.Name,
            text = c.Text,
            segments = SentenceSplitter.Split(c.Text)
                .Select(s => new { text = s.Text, trailingPauseMs = s.TrailingPauseMs })
                .ToArray(),
        }).ToArray(),
    };

    Write(Path.Combine(outDir, "voice_sentence_splitter.json"), payload);
}

// -------------------------------------------------------- LanguageSpanSplitter
{
    string[] splitCases =
    [
        "",
        "Sawubona",
        "Igama lami ngu-CircleAI",
        "Ngicela i-GPS yakho, ngiyabonga",
        "WhatsApp iyasebenza kahle",
        "CircleAI ne-YouTube",
    ];

    string[] spokenCases =
    [
        "CircleAI", "YouTube", "OpenAPIKey", "GPS", "Sawubona", "A", "", "iPhone",
    ];

    string[] foreignCases =
    [
        "CircleAI", "WhatsApp", "GPS", "SMS", "ATM", "Sawubona", "hello", "a",
        "AB", "ABCDEF", "Ngiyabonga", "iPhone",
    ];

    var payload = new
    {
        _comment = "Cuts mixed-language text where the language changes, so each run can be "
                 + "synthesised under its own language id. Detection is deliberately "
                 + "CONSERVATIVE — internal capitals and short all-caps runs only. Guessing at "
                 + "ordinary lowercase words would mispronounce native words to fix foreign "
                 + "ones, which insults the speaker in their own language. Separators ride "
                 + "along with the run they FOLLOW, so a language change never strands a comma.",
        _source = "src/CircleAI.Voice/LanguageSpanSplitter.cs",
        split = splitCases.Select(t => new
        {
            text = t,
            spans = LanguageSpanSplitter.Split(t)
                .Select(s => new { text = s.Text, isForeign = s.IsForeign })
                .ToArray(),
        }).ToArray(),
        toSpokenForm = spokenCases.Select(t => new
        {
            input = t,
            output = LanguageSpanSplitter.ToSpokenForm(t),
        }).ToArray(),
        isForeignWord = foreignCases.Select(w => new
        {
            word = w,
            foreign = LanguageSpanSplitter.IsForeignWord(w),
        }).ToArray(),
    };

    Write(Path.Combine(outDir, "voice_language_spans.json"), payload);
}

// --------------------------------------------------------------- GeezRomanizer
{
    string[] ethiopicCases = ["", "hello", "ኣማርኛ", "abc ሰላም", "123"];
    string[] romanizeCases =
    [
        "",
        "ሰላም",           // selam — the sixth order is SILENT, so no trailing vowel
        "አማርኛ",          // amarnya — a silent-consonant row, heard as "a"
        "እንኳን",          // enkwan — labialised row; plain "k" would give "enkan"
        "ሰላም። እንዴት ነህ",  // Ethiopic full stop must become '.' so splitting still works
        "ሰላም፣ ጤና ይስጥልኝ",
        "abc 123",        // non-Ethiopic passes through untouched
        "ሰላም abc",
        "፩፪፫",            // Ethiopic numerals have no sound and are dropped
        "ፘፙፚ",            // the three LONE syllables, not a row of eight
        "ሰ፟ላ",           // a combining mark has no sound of its own
    ];

    var payload = new
    {
        _comment = "Ethiopic (Ge'ez) -> Latin, because the Amharic and Tigrinya voices are "
                 + "is_uroman:true and hold 27-28 plain LATIN letters — they have never seen "
                 + "an Ethiopic codepoint. Computed, not tabulated: Unicode lays the syllabary "
                 + "out as consecutive blocks of EIGHT codepoints, one consonant across its "
                 + "vowel orders, so consonant = (cp-0x1200)/8 and vowel = (cp-0x1200)%8. Six "
                 + "rows are LABIALISED (the consonant carries a built-in /w/); writing them "
                 + "plain turns 'enkwan' into 'enkan' and silently changes the word.",
        _source = "src/CircleAI.Voice/GeezRomanizer.cs",
        isEthiopic = ethiopicCases.Select(t => new
        {
            text = t,
            ethiopic = GeezRomanizer.IsEthiopic(t),
        }).ToArray(),
        romanize = romanizeCases.Select(t => new
        {
            input = t,
            output = GeezRomanizer.Romanize(t),
        }).ToArray(),
    };

    Write(Path.Combine(outDir, "voice_geez_romanizer.json"), payload);
}

// ------------------------------------------------------------------ ToneShaper
{
    // THE FIXTURE CARRIES THE COEFFICIENTS, and the ports assert two things
    // separately, because the two halves have very different reproducibility.
    //
    // The biquad itself is add and multiply on doubles — bit-reproducible
    // everywhere, so ports filter the fixture's OWN coefficients and must match
    // to 1e-6. Deriving those coefficients needs pow, sin and cos, and no
    // language guarantees those to the last bit; ports compare their own derived
    // values to 1e-9 relative instead of pretending otherwise.
    var shaper = ToneShaper.Warm;

    // A deterministic two-tone signal: a 440 Hz body the low shelf lifts, and a
    // 3 kHz component sitting in the presence dip. Both are audible in the
    // output, so a port that silently applied only one filter fails.
    const int rate = 22050;
    const int n = 64;
    var input = new float[n];
    for (var i = 0; i < n; i++)
    {
        input[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / rate)
                         + 0.2 * Math.Sin(2 * Math.PI * 3000 * i / rate));
    }

    var filtered = (float[])input.Clone();
    shaper.Apply(filtered, rate);

    // A silent buffer must come back untouched: Apply bails when the peak is 0,
    // and a port that divided by that peak instead would produce NaN.
    var silence = new float[8];
    shaper.Apply(silence, rate);

    var payload = new
    {
        _comment = "Two RBJ biquads in series — a low shelf and a peaking dip — over the float "
                 + "waveform before it becomes PCM. The speaker was NOT the lever: across all "
                 + "130 speakers warmth and intelligibility are inversely related, so the "
                 + "waveform is corrected instead. PEAK IS RESTORED afterwards, because lifting "
                 + "the low shelf adds energy and a waveform already near full scale would clip "
                 + "— heard as crackle and blamed on the quantised model rather than on this. "
                 + "Ports assert the filtered waveform to 1e-6 using THESE coefficients, and "
                 + "their own derived coefficients to 1e-9 relative: pow/sin/cos are not "
                 + "bit-identical across languages, but add and multiply are.",
        _source = "src/CircleAI.Voice/ToneShaper.cs",
        waveformTolerance = 1e-6,
        coefficientTolerance = 1e-9,
        settings = new
        {
            lowShelfHz = shaper.LowShelfHz,
            lowShelfDb = shaper.LowShelfDb,
            presenceHz = shaper.PresenceHz,
            presenceDb = shaper.PresenceDb,
            presenceQ = shaper.PresenceQ,
            lowShelfSlope = 0.9,
        },
        coefficients = new[] { 22050, 16000, 24000 }.Select(r => new
        {
            sampleRate = r,
            lowShelf = Coeffs(shaper, r, lowShelf: true),
            peaking = Coeffs(shaper, r, lowShelf: false),
        }).ToArray(),
        waveform = new
        {
            sampleRate = rate,
            input = input.Select(v => (double)v).ToArray(),
            output = filtered.Select(v => (double)v).ToArray(),
        },
        silenceStaysSilent = silence.Select(v => (double)v).ToArray(),
    };

    Write(Path.Combine(outDir, "voice_tone_shaper.json"), payload);

    // Recomputed here rather than exposed on ToneShaper: the coefficients are an
    // implementation detail of the filter, and widening the public surface just
    // to emit a fixture would be the fixture dictating the design.
    static object Coeffs(ToneShaper s, int rate, bool lowShelf)
    {
        double[] b, a;
        if (lowShelf)
        {
            const double slope = 0.9;
            var A = Math.Pow(10, s.LowShelfDb / 40);
            var w0 = 2 * Math.PI * s.LowShelfHz / rate;
            var alpha = Math.Sin(w0) / 2 * Math.Sqrt((A + 1 / A) * (1 / slope - 1) + 2);
            var c = Math.Cos(w0);
            var s2 = 2 * Math.Sqrt(A) * alpha;
            b = [A * ((A + 1) - (A - 1) * c + s2),
                 2 * A * ((A - 1) - (A + 1) * c),
                 A * ((A + 1) - (A - 1) * c - s2)];
            a = [(A + 1) + (A - 1) * c + s2,
                 -2 * ((A - 1) + (A + 1) * c),
                 (A + 1) + (A - 1) * c - s2];
        }
        else
        {
            var A = Math.Pow(10, s.PresenceDb / 40);
            var w0 = 2 * Math.PI * s.PresenceHz / rate;
            var alpha = Math.Sin(w0) / (2 * s.PresenceQ);
            var c = Math.Cos(w0);
            b = [1 + alpha * A, -2 * c, 1 - alpha * A];
            a = [1 + alpha / A, -2 * c, 1 - alpha / A];
        }
        var a0 = a[0];
        for (var i = 0; i < 3; i++) { b[i] /= a0; a[i] /= a0; }
        return new { b, a };
    }
}

// ------------------------------------------------------------- NchltPhonemizer
{
    // A SYNTHETIC MINI-LANGUAGE, not a slice of the real NCHLT data. The real
    // dictionaries are ~15 000 words under CC BY and belong in Data/nchlt, not
    // inlined into nine ports' test fixtures. This one is four graphemes wide
    // and exercises every input the loader takes: dictionary hit, rule
    // prediction, context-dependent rules, a NULL code that must be dropped, a
    // grapheme remap, a grapheme-null substitution, and an unknown grapheme
    // that must be REPORTED rather than guessed at.
    //
    // Rule format is the NCHLT one: grapheme;left;right;code;order[;count].
    // Rules are applied MOST SPECIFIC FIRST, and the sort must be STABLE — two
    // rules of equal order have to stay in file order or ports disagree on ties.
    var dictText = string.Join("\n",
        "sawubona\ts a w u b O n a",
        "sawubona\ts a w u b o n a",          // second variant — the FIRST wins
        "banga\tb a N a",                     // also predictable by rule; dict takes priority
        "\tnot a word",                       // no key — skipped
        "novalue\t");                         // no pronunciation — skipped

    var rulesText = string.Join("\n",
        "a;;;1;0;100",
        "b;;;2;0;100",
        "n;;;3;0;100",
        "n;;g;4;2;40",       // n before g is the velar nasal
        "g;;;5;0;100",
        "g;n;;0;2;40",       // g after n is absorbed into it — code 0 is a NULL
        "bad line without semicolons",
        "x;;;;9;0");         // no code at all — falls back to the null

    var phoneMapText = string.Join("\n",
        "1\ta", "2\tb", "3\tn", "4\tN", "5\tg", "toolong\tz");

    var graphMapText = "b\tq";                // file is funny<TAB>std, so q is read as b
    var gnullsText = "bb;b";                  // a doubled b collapses before the rules run

    (string Name, string Text)[] cases =
    [
        ("dictionary-hit",     "sawubona"),
        ("rule-predicted",     "gaba"),
        ("context-rule",       "nganga"),
        ("grapheme-remap",     "qanga"),      // q is remapped to b
        ("gnull-collapse",     "abba"),       // bb collapses to b
        ("unknown-grapheme",   "azb"),        // z has no rule at all
        ("mixed-sentence",     "Sawubona, gaba!"),
        ("empty",              ""),
        ("punctuation-only",   "!!! ..."),
    ];

    static Stream S(string t) => new MemoryStream(Encoding.UTF8.GetBytes(t));

    var payload = new
    {
        _comment = "Grapheme-to-phoneme over the CC-BY NCHLT resources. NOT espeak (GPLv3 "
                 + "would taint the app), NOT phonemeza (unlicensed, weights unpublished), "
                 + "and not neural. Because the rule set covers ANY word there is no OOV gap: "
                 + "a word is either catalogued exactly or synthesised by the rules, which is "
                 + "what makes agglutinative isiZulu tractable. The data here is a SYNTHETIC "
                 + "mini-language — the real dictionaries are 15 000 words and live in "
                 + "Data/nchlt. Rules sort most-specific-first and the sort MUST BE STABLE.",
        _source = "src/CircleAI.Voice/NchltPhonemizer.cs",
        dict = dictText,
        rules = rulesText,
        phoneMap = phoneMapText,
        graphMap = graphMapText,
        gnulls = gnullsText,
        cases = cases.Select(c =>
        {
            var p = NchltPhonemizer.Load(S(dictText), S(rulesText), S(phoneMapText),
                                         S(graphMapText), S(gnullsText));
            var phones = p.Phonemize(c.Text);
            return new
            {
                name = c.Name,
                text = c.Text,
                phones = phones.ToArray(),
                rulePredictedWords = p.LastRulePredictedWords,
                unknownGraphemes = p.LastUnknownGraphemes.Select(ch => ch.ToString()).ToArray(),
            };
        }).ToArray(),
        predictWord = new[] { "banga", "gaba", "nganga", "azb", "" }.Select(w =>
        {
            var p = NchltPhonemizer.Load(S(dictText), S(rulesText), S(phoneMapText),
                                         S(graphMapText), S(gnullsText));
            return new { word = w, phones = p.PredictWord(w).ToArray() };
        }).ToArray(),
    };

    Write(Path.Combine(outDir, "voice_nchlt_phonemizer.json"), payload);
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
