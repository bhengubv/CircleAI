// tts-speak
//
// Proves OnnxTtsEngine actually speaks: downloads the REAL Piper voice, runs the
// REAL engine, writes a playable WAV, and reports whether the output is speech-
// shaped (right sample rate, plausible duration, non-silent) rather than the
// silence/garbage the old char-codepoint tokeniser produced.
//
//   dotnet run --project tools/tts-speak -- [outDir] [ipaText]
//
// Default IPA is "hello world" (həlˈoʊ wˈɚld). Every symbol is confirmed present
// in the lessac voice's phoneme_id_map. espeak-ng is NOT required here — the IPA
// is supplied directly and PassthroughPhonemizer feeds it to the (now correct)
// engine, isolating the proof to the SYNTHESIS half.

using System.Security.Cryptography;
using System.Text;
using CircleAI.Voice;

// ── Sherpa mode ─────────────────────────────────────────────────────────────
// Proves SherpaOnnxTtsEngine speaks a real multi-engine voice BUNDLE
// (Piper / mimic3 / coqui / Kokoro) — e.g. the Afrikaans mimic3 af_ZA voice, the
// first South African voice above English. The bundle (model.onnx + tokens.txt +
// espeak-ng-data/) is expected already extracted on disk.
//   dotnet run --project tools/tts-speak -- --sherpa <bundleDir> [text...]
if (args.Length > 0 && args[0] == "--sherpa")
    return await RunSherpa(args);

// ── G2P mode ────────────────────────────────────────────────────────────────
// Proves NchltPhonemizer (sovereign, CC-BY, no espeak) turns SA text into
// X-SAMPA. With no words it self-verifies the rule-engine port against the
// dictionary and reports accuracy; a faithless port scores low.
//   dotnet run --project tools/tts-speak -- --g2p <lang> [words...]
if (args.Length > 0 && args[0] == "--g2p")
    return await RunG2p(args);

// ── MMS mode ────────────────────────────────────────────────────────────────
// Proves OnnxTtsEngine speaks an MMS/sherpa-style VITS voice — the on-device
// path for African languages. These voices are CHARACTER-driven
// (phoneme_type "text"), so PassthroughPhonemizer feeds graphemes straight in:
// no espeak, no neural G2P, nothing that cannot run on the phone.
//   dotnet run --project tools/tts-speak -- --mms <model.onnx> <outWav> [text...]
if (args.Length > 0 && args[0] == "--mms")
    return await RunMms(args);

// ── ToucanTTS mode ──────────────────────────────────────────────────────────
// The four SA languages no ready-made voice covers — isiZulu, Sepedi, siSwati,
// Tshivenda — via the two-stage Apache model driven by OUR NchltPhonemizer
// instead of its neural G2P. No espeak, no Python, phone-runnable.
//   dotnet run --project tools/tts-speak -- --toucan <assetDir> <nchltDataDir> <lang> <outWav> [text...]
if (args.Length > 0 && args[0] == "--toucan")
    return await RunToucan(args);

// ── SA-11 mode ──────────────────────────────────────────────────────────────
// One multi-lingual, multi-speaker VITS covering ALL ELEVEN official South
// African languages — including isiNdebele, which no other model carries.
// Character-driven, so no phonemizer is involved.
//   dotnet run --project tools/tts-speak -- --sa11 <model.onnx> <lang> <outWav> [text...]
if (args.Length > 0 && args[0] == "--sa11")
    return await RunSa11(args);

var outDir = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
    ? args[0]
    : Path.Combine(Path.GetTempPath(), "circleai-tts");

// Two modes:
//   phoneme mode (default): arg[1] is IPA, fed via PassthroughPhonemizer —
//       isolates the SYNTHESIS half, needs no espeak.
//   text mode ("--text ..."): arg[1..] is plain English, run through
//       EspeakPhonemizer — proves the FULL text→speech chain.
var textMode = args.Length > 1 && args[1] == "--text";
var input = textMode
    ? string.Join(' ', args.Skip(2))
    : (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]) ? args[1] : "həlˈoʊ wˈɚld");
if (textMode && string.IsNullOrWhiteSpace(input))
    input = "The battery is at one hundred percent.";

Directory.CreateDirectory(outDir);

const string repoBase = "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium/";
var onnxPath = Path.Combine(outDir, "en_US-lessac-medium.onnx");
var jsonPath = onnxPath + ".json";  // the sidecar OnnxTtsEngine auto-loads

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("CircleAI-tts-speak/1.0");

// Pinned SHA-256s — the same ones catalogued in the registry. A model this tool
// downloaded but did not verify is not a model I will synthesise from.
await EnsureFile(jsonPath, repoBase + "en_US-lessac-medium.onnx.json",
    "efe19c417bed055f2d69908248c6ba650fa135bc868b0e6abb3da181dab690a0");
await EnsureFile(onnxPath, repoBase + "en_US-lessac-medium.onnx",
    "5efe09e69902187827af646e1a6e9d269dee769f9877d17b16b1b46eeaaf019f");

Console.WriteLine(textMode ? $"TEXT in: {input}" : $"IPA in : {input}");
var config = PiperVoiceConfig.TryLoadForModel(onnxPath)
    ?? throw new InvalidOperationException("sidecar config did not load");
Console.WriteLine($"config : sampleRate={config.SampleRate} scales=({config.NoiseScale},{config.LengthScale},{config.NoiseW}) phonemeMap={config.HasPhonemeMap}");

IPhonemizer phonemizer = textMode
    ? new EspeakPhonemizer("en-us", ResolveEspeak())
    : new PassthroughPhonemizer();
if (textMode)
{
    // Show the espeak output so the chain is inspectable, and fail loudly if
    // espeak-ng is absent rather than emitting silence.
    var phs = phonemizer.Phonemize(input);
    Console.WriteLine($"phonemes: {string.Concat(phs)}  ({phs.Count} symbols)");
}

using var engine = new OnnxTtsEngine(onnxPath, phonemizer);
var result = await engine.SynthesiseAsync(input);

var bytes = result.AudioData;
var samples = bytes.Length / 2;
var seconds = samples / (double)result.SampleRate;

// RMS over the PCM16 — silence is ~0, speech is well above it.
double sumSq = 0;
var span = bytes.Span;
for (var i = 0; i + 1 < span.Length; i += 2)
{
    short s = (short)(span[i] | (span[i + 1] << 8));
    var f = s / 32768.0;
    sumSq += f * f;
}
var rms = samples > 0 ? Math.Sqrt(sumSq / samples) : 0;
var peak = 0;
for (var i = 0; i + 1 < span.Length; i += 2)
{
    short s = (short)(span[i] | (span[i + 1] << 8));
    peak = Math.Max(peak, Math.Abs((int)s));
}

var wavPath = Path.Combine(outDir, "hello.wav");
WriteWav(wavPath, bytes.Span, result.SampleRate, result.Channels, result.BitsPerSample);

Console.WriteLine();
Console.WriteLine($"pcm bytes : {bytes.Length:N0}  ({samples:N0} samples)");
Console.WriteLine($"duration  : {seconds:F2} s @ {result.SampleRate} Hz");
Console.WriteLine($"rms       : {rms:F4}   peak: {peak}/32767");
Console.WriteLine($"wav       : {wavPath}");
Console.WriteLine();

var failures = new List<string>();
if (samples == 0)            failures.Add("no audio produced");
if (result.SampleRate != 22050) failures.Add($"sample rate {result.SampleRate}, expected 22050 from config");
if (seconds < 0.3)          failures.Add($"duration {seconds:F2}s implausibly short for this phrase");
if (rms < 0.005)            failures.Add($"rms {rms:F4} — effectively silent (the old stub's failure mode)");

if (failures.Count > 0)
{
    Console.Error.WriteLine("FAIL:");
    foreach (var f in failures) Console.Error.WriteLine("  - " + f);
    return 1;
}

Console.WriteLine("PASS: real, non-silent speech audio at the config's sample rate. Play the WAV to hear it.");
return 0;

// espeak-ng lands in Program Files via winget and is often NOT on PATH.
static string? ResolveEspeak()
{
    foreach (var p in new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "eSpeak NG", "espeak-ng.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "eSpeak NG", "espeak-ng.exe"),
    })
        if (File.Exists(p)) return p;
    return null; // fall back to PATH lookup inside EspeakPhonemizer
}

async Task EnsureFile(string path, string url, string expectedSha)
{
    if (File.Exists(path) && await Sha256(path) == expectedSha)
    {
        Console.WriteLine($"cached : {Path.GetFileName(path)}");
        return;
    }
    Console.WriteLine($"GET    : {url}");
    var data = await http.GetByteArrayAsync(url);
    await File.WriteAllBytesAsync(path, data);
    var got = await Sha256(path);
    if (got != expectedSha)
        throw new InvalidOperationException($"SHA mismatch for {Path.GetFileName(path)}: got {got}");
}

static async Task<string> Sha256(string path)
{
    await using var s = File.OpenRead(path);
    using var sha = SHA256.Create();
    return Convert.ToHexString(await sha.ComputeHashAsync(s)).ToLowerInvariant();
}

// A symbol the voice could not map is simply not spoken, so it leaves no trace in
// the waveform: duration, pitch and rhythm all still look healthy. That is exactly
// how a lower-case-only vocabulary silently ate every capital letter — and every
// sentence's first syllable with it — while the acoustic checks reported green.
//
// Letters and punctuation are reported differently on purpose. These voices were
// trained on punctuation-stripped text, so a dropped comma is expected and is
// already compensated by the phrasing pass; a dropped LETTER is lost speech. If
// both shouted equally, the shout would stop meaning anything.
static void ReportSkipped(ITtsFrontEndDiagnostics engine)
{
    if (engine.LastSkippedCount == 0) return;

    var lost = new List<string>();
    var expected = new List<string>();
    foreach (var s in engine.LastSkippedSymbols)
        (s.Length > 0 && char.IsLetterOrDigit(s[0]) ? lost : expected).Add(s);

    if (lost.Count > 0)
        Console.WriteLine(
            $"WARNING: speech was LOST — the voice has no token for: {string.Join(" ", lost)}");

    if (expected.Count > 0)
        Console.WriteLine(
            $"note  : punctuation not in this voice's vocabulary (pauses come from phrasing): {string.Join(" ", expected)}");
}

// An approximation is audible but wrong-ish, so it gets its own line: a native
// speaker should be told which sounds are stand-ins rather than left to wonder
// why a familiar word sounds slightly off.
static void ReportApproximations(ITtsFrontEndDiagnostics engine)
{
    if (engine.LastApproximatedSymbols.Count == 0) return;
    Console.WriteLine(
        $"approx: spoken via nearest available sound (voice lacks the exact letter): " +
        string.Join(" ", engine.LastApproximatedSymbols));
}

static void WriteWav(string path, ReadOnlySpan<byte> pcm, int sampleRate, int channels, int bits)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    int byteRate = sampleRate * channels * bits / 8;
    short blockAlign = (short)(channels * bits / 8);
    w.Write(Encoding.ASCII.GetBytes("RIFF"));
    w.Write(36 + pcm.Length);
    w.Write(Encoding.ASCII.GetBytes("WAVE"));
    w.Write(Encoding.ASCII.GetBytes("fmt "));
    w.Write(16); w.Write((short)1); w.Write((short)channels);
    w.Write(sampleRate); w.Write(byteRate); w.Write(blockAlign); w.Write((short)bits);
    w.Write(Encoding.ASCII.GetBytes("data"));
    w.Write(pcm.Length);
    w.Write(pcm);
}

// SA-11 path: the multilingual VITS. Language is selected by the model's own
// langid input, not by loading a different file, so all eleven live in one 116 MB
// graph on the fast single-stage engine.
async Task<int> RunSa11(string[] a)
{
    if (a.Length < 4)
    {
        Console.Error.WriteLine("usage: dotnet run --project tools/tts-speak -- --sa11 <model.onnx> <lang> <outWav> [text...]");
        return 1;
    }

    var modelPath = a[1];
    var lang = a[2];
    var outWav = a[3];
    var text = a.Length > 4 ? string.Join(' ', a.Skip(4)) : "Sawubona umhlaba.";

    // Language ids as published in the model's own language_ids.json.
    var langIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
    {
        ["afr"] = 0, ["eng"] = 1, ["nbl"] = 2, ["nso"] = 3, ["sot"] = 4, ["ssw"] = 5,
        ["tsn"] = 6, ["tso"] = 7, ["ven"] = 8, ["xho"] = 9, ["zul"] = 10
    };
    if (!langIds.TryGetValue(lang, out var langId))
    {
        Console.Error.WriteLine($"FAIL: '{lang}' is not one of {string.Join(", ", langIds.Keys)}");
        return 1;
    }

    var speaker = Environment.GetEnvironmentVariable("SA11_SPEAKER") is { } s && long.TryParse(s, out var sp) ? sp : 0L;

    Console.WriteLine($"model : {Path.GetFileName(modelPath)}");
    Console.WriteLine($"lang  : {lang} (id {langId})  speaker: {speaker}");
    Console.WriteLine($"text  : {text}");

    using var engine = new OnnxTtsEngine(modelPath, new PassthroughPhonemizer())
    {
        LanguageId = langId,
        SpeakerId = speaker
    };
    // The voice has no token for '.', so the pause between sentences has to be
    // supplied around the model rather than by it.
    using var phrased = new PhrasedTtsEngine(engine);
    var result = await phrased.SynthesiseAsync(text);
    Console.WriteLine($"phrase: {phrased.LastSegmentCount} sentence segment(s)");

    var bytes = result.AudioData;
    var samples = bytes.Length / 2;
    var seconds = samples / (double)result.SampleRate;

    double sumSq = 0;
    var peak = 0;
    var span = bytes.Span;
    for (var i = 0; i + 1 < span.Length; i += 2)
    {
        short v = (short)(span[i] | (span[i + 1] << 8));
        var f = v / 32768.0;
        sumSq += f * f;
        peak = Math.Max(peak, Math.Abs((int)v));
    }
    var rms = samples > 0 ? Math.Sqrt(sumSq / samples) : 0;

    Directory.CreateDirectory(Path.GetDirectoryName(outWav)!);
    WriteWav(outWav, span, result.SampleRate, result.Channels, result.BitsPerSample);
    Console.WriteLine($"wav   : {outWav}");
    Console.WriteLine($"audio : {seconds:F2}s @ {result.SampleRate}Hz  rms={rms:F4}  peak={peak}");
    ReportSkipped(phrased);
    ReportApproximations(phrased);

    if (samples < result.SampleRate / 4 || rms < 0.005)
    {
        Console.Error.WriteLine("FAIL: output is not speech-shaped (too short or silent)");
        return 1;
    }

    Console.WriteLine($"OK: {lang} spoke through the SA-11 voice");
    return 0;
}

// ToucanTTS path: NchltPhonemizer (ours) -> articulatory features -> acoustic ONNX
// -> vocoder ONNX -> PCM. Proves the only permissive route to isiZulu, Sepedi,
// siSwati and Tshivenda, with every stage available on Android.
async Task<int> RunToucan(string[] a)
{
    if (a.Length < 5)
    {
        Console.Error.WriteLine("usage: dotnet run --project tools/tts-speak -- --toucan <assetDir> <nchltDataDir> <lang> <outWav> [text...]");
        return 1;
    }

    var assetDir = a[1];
    var nchltDir = a[2];
    var lang = a[3];
    var outWav = a[4];
    var text = a.Length > 5 ? string.Join(' ', a.Skip(5)) : "Sawubona umhlaba.";

    // ToucanTTS language-embedding ids, read from the model itself during export.
    var langIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
    {
        ["zul"] = 7215, ["nso"] = 4491, ["ssw"] = 5611, ["ven"] = 6348
    };
    if (!langIds.TryGetValue(lang, out var langId))
    {
        Console.Error.WriteLine($"FAIL: no ToucanTTS language id for '{lang}' (have: {string.Join(", ", langIds.Keys)})");
        return 1;
    }

    Console.WriteLine($"lang  : {lang} (ToucanTTS id {langId})");
    Console.WriteLine($"text  : {text}");

    var phonemizer = NchltPhonemizer.ForLanguage(nchltDir, lang);
    var phones = phonemizer.Phonemize(text);
    Console.WriteLine($"phones: {string.Join(' ', phones)}  ({phones.Count})");

    using var engine = ToucanOnnxTtsEngine.FromDirectory(assetDir, lang, langId, phonemizer);
    var result = await engine.SynthesiseAsync(text);

    var bytes = result.AudioData;
    var samples = bytes.Length / 2;
    var seconds = samples / (double)result.SampleRate;

    double sumSq = 0;
    var peak = 0;
    var span = bytes.Span;
    for (var i = 0; i + 1 < span.Length; i += 2)
    {
        short s = (short)(span[i] | (span[i + 1] << 8));
        var f = s / 32768.0;
        sumSq += f * f;
        peak = Math.Max(peak, Math.Abs((int)s));
    }
    var rms = samples > 0 ? Math.Sqrt(sumSq / samples) : 0;

    Directory.CreateDirectory(Path.GetDirectoryName(outWav)!);
    WriteWav(outWav, span, result.SampleRate, result.Channels, result.BitsPerSample);
    Console.WriteLine($"wav   : {outWav}");
    Console.WriteLine($"audio : {seconds:F2}s @ {result.SampleRate}Hz  rms={rms:F4}  peak={peak}  skippedPhones={engine.LastSkippedPhoneCount}");

    if (samples < result.SampleRate / 4 || rms < 0.005)
    {
        Console.Error.WriteLine("FAIL: output is not speech-shaped (too short or silent)");
        return 1;
    }

    Console.WriteLine($"OK: {lang} spoke through ToucanOnnxTtsEngine + NchltPhonemizer");
    return 0;
}

// MMS path: run a character-driven MMS/sherpa VITS voice through the REAL
// OnnxTtsEngine and report whether the output is speech-shaped. Everything here
// — char→id via the sidecar map, ONNX Runtime, PCM16 — is available on Android,
// which is the whole point of preferring these voices for the phone.
async Task<int> RunMms(string[] a)
{
    var modelPath = a.Length > 1 ? a[1] : "";
    if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
    {
        Console.Error.WriteLine($"FAIL: MMS model not found: '{modelPath}'");
        Console.Error.WriteLine("usage: dotnet run --project tools/tts-speak -- --mms <model.onnx> <outWav> [text...]");
        return 1;
    }

    var outWav = a.Length > 2 ? a[2] : Path.Combine(Path.GetTempPath(), "circleai-tts", "mms.wav");
    var mmsText = a.Length > 3 ? string.Join(' ', a.Skip(3)) : "Avuxeni, mi amukeriwile.";
    Directory.CreateDirectory(Path.GetDirectoryName(outWav)!);

    var cfg = PiperVoiceConfig.TryLoadForModel(modelPath);
    if (cfg is not { HasPhonemeMap: true })
    {
        Console.Error.WriteLine($"FAIL: no usable sidecar at {PiperVoiceConfig.SidecarPathFor(modelPath)}");
        return 1;
    }

    Console.WriteLine($"model : {Path.GetFileName(modelPath)}");
    Console.WriteLine($"text  : {mmsText}");
    Console.WriteLine($"config: sampleRate={cfg.SampleRate} type={cfg.PhonemeType} scales=({cfg.NoiseScale},{cfg.LengthScale},{cfg.NoiseW})");

    // Characters ARE the tokens for these voices.
    using var engine = new OnnxTtsEngine(modelPath, new PassthroughPhonemizer());
    using var phrased = new PhrasedTtsEngine(engine);
    var result = await phrased.SynthesiseAsync(mmsText);
    Console.WriteLine($"phrase: {phrased.LastSegmentCount} sentence segment(s)");

    var bytes = result.AudioData;
    var samples = bytes.Length / 2;
    var seconds = samples / (double)result.SampleRate;

    double sumSq = 0;
    var peak = 0;
    var span = bytes.Span;
    for (var i = 0; i + 1 < span.Length; i += 2)
    {
        short s = (short)(span[i] | (span[i + 1] << 8));
        var f = s / 32768.0;
        sumSq += f * f;
        peak = Math.Max(peak, Math.Abs((int)s));
    }
    var rms = samples > 0 ? Math.Sqrt(sumSq / samples) : 0;

    WriteWav(outWav, span, result.SampleRate, result.Channels, result.BitsPerSample);
    Console.WriteLine($"wav   : {outWav}");
    Console.WriteLine($"audio : {seconds:F2}s @ {result.SampleRate}Hz  rms={rms:F4}  peak={peak}");
    ReportSkipped(phrased);
    ReportApproximations(phrased);

    // Silence or a handful of samples means the tokenisation was wrong, not that
    // the voice is quiet — fail rather than report a green that is not one.
    if (samples < result.SampleRate / 4 || rms < 0.005)
    {
        Console.Error.WriteLine("FAIL: output is not speech-shaped (too short or silent)");
        return 1;
    }

    Console.WriteLine("OK: MMS voice spoke through OnnxTtsEngine");
    return 0;
}

// Sherpa path: construct SherpaOnnxTtsEngine from an extracted voice bundle and
// prove it emits real, non-silent speech at the model's own sample rate — the
// same bar the Piper proof above applies, applied to the multi-engine runtime.
async Task<int> RunSherpa(string[] a)
{
    var bundleDir = a.Length > 1 ? a[1] : "";
    if (string.IsNullOrWhiteSpace(bundleDir) || !Directory.Exists(bundleDir))
    {
        Console.Error.WriteLine($"FAIL: sherpa bundle directory not found: '{bundleDir}'");
        Console.Error.WriteLine("usage: dotnet run --project tools/tts-speak -- --sherpa <bundleDir> [text...]");
        return 1;
    }

    // Default line is Afrikaans — the first South African voice above English.
    var sherpaText = a.Length > 2 ? string.Join(' ', a.Skip(2)) : "Hallo wêreld. Welkom by Circle.";
    var sherpaOut = Path.Combine(Path.GetTempPath(), "circleai-tts");
    Directory.CreateDirectory(sherpaOut);

    Console.WriteLine($"sherpa bundle: {bundleDir}");
    Console.WriteLine($"text in      : {sherpaText}");

    using var engine = SherpaOnnxTtsEngine.FromBundleDirectory(bundleDir);
    var result = await engine.SynthesiseAsync(sherpaText);

    var bytes = result.AudioData;
    var samples = bytes.Length / 2;
    var seconds = samples / (double)result.SampleRate;

    double sumSq = 0;
    var span = bytes.Span;
    var peak = 0;
    for (var i = 0; i + 1 < span.Length; i += 2)
    {
        short s = (short)(span[i] | (span[i + 1] << 8));
        var f = s / 32768.0;
        sumSq += f * f;
        peak = Math.Max(peak, Math.Abs((int)s));
    }
    var rms = samples > 0 ? Math.Sqrt(sumSq / samples) : 0;

    var wavPath = Path.Combine(sherpaOut, "sherpa.wav");
    WriteWav(wavPath, bytes.Span, result.SampleRate, result.Channels, result.BitsPerSample);

    Console.WriteLine();
    Console.WriteLine($"pcm bytes : {bytes.Length:N0}  ({samples:N0} samples)");
    Console.WriteLine($"duration  : {seconds:F2} s @ {result.SampleRate} Hz");
    Console.WriteLine($"rms       : {rms:F4}   peak: {peak}/32767");
    Console.WriteLine($"wav       : {wavPath}");
    Console.WriteLine();

    var failures = new List<string>();
    if (samples == 0) failures.Add("no audio produced");
    if (seconds < 0.3) failures.Add($"duration {seconds:F2}s implausibly short for this phrase");
    if (rms < 0.005) failures.Add($"rms {rms:F4} — effectively silent");

    if (failures.Count > 0)
    {
        Console.Error.WriteLine("FAIL:");
        foreach (var f in failures) Console.Error.WriteLine("  - " + f);
        return 1;
    }

    Console.WriteLine("PASS: real, non-silent speech from the sherpa-onnx engine. Play the WAV to hear it.");
    return 0;
}

// G2P path: exercise NchltPhonemizer and, with no words given, verify the
// rule-engine port reproduces the CC-BY dictionary (high accuracy == faithful).
async Task<int> RunG2p(string[] a)
{
    var lang = a.Length > 1 ? a[1] : "zul";
    var dataDir = Path.Combine("src", "CircleAI.Voice", "Data", "nchlt");
    if (!Directory.Exists(dataDir))
    {
        Console.Error.WriteLine($"FAIL: NCHLT data dir not found: '{Path.GetFullPath(dataDir)}'");
        return 1;
    }

    var phon = NchltPhonemizer.ForLanguage(dataDir, lang);
    Console.WriteLine($"NCHLT phonemizer loaded for '{lang}' from {dataDir}");

    var words = a.Skip(2).ToArray();
    if (words.Length > 0)
    {
        foreach (var w in words)
        {
            var ph = phon.Phonemize(w);
            var via = phon.LastRulePredictedWords > 0 ? "rule" : "dict";
            Console.WriteLine($"  {w}  [{via}]  ->  {string.Join(' ', ph)}");
        }
        return 0;
    }

    // Self-verify: run the rule engine against the dictionary's own pronunciations.
    var gold = new List<(string word, string pron)>();
    foreach (var line in await File.ReadAllLinesAsync(Path.Combine(dataDir, $"nchlt_{lang}.dict")))
    {
        var tab = line.IndexOf('\t');
        if (tab > 0) gold.Add((line[..tab], line[(tab + 1)..].Trim()));
    }

    int n = 0, wordExact = 0, phoneTotal = 0, phoneHit = 0;
    int stepBy = Math.Max(1, gold.Count / 3000);
    for (int i = 0; i < gold.Count; i += stepBy)
    {
        var (word, goldPron) = gold[i];
        var pred = string.Join(' ', phon.PredictWord(word));
        n++;
        if (pred == goldPron) wordExact++;
        var gp = goldPron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pp = pred.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        phoneTotal += gp.Length;
        int m = Math.Min(gp.Length, pp.Length);
        for (int k = 0; k < m; k++) if (gp[k] == pp[k]) phoneHit++;
    }

    Console.WriteLine();
    Console.WriteLine($"rule-engine vs dictionary ({n:N0} sampled words):");
    Console.WriteLine($"  word  accuracy : {100.0 * wordExact / n:F1}%  ({wordExact:N0}/{n:N0})");
    Console.WriteLine($"  phone accuracy : {100.0 * phoneHit / phoneTotal:F1}%  ({phoneHit:N0}/{phoneTotal:N0})");

    var demo = lang switch
    {
        "zul" => "sawubona umhlaba ngiyabonga",
        "xho" => "molo umhlaba enkosi",
        _ => "hallo wêreld dankie",
    };
    Console.WriteLine();
    Console.WriteLine($"demo — \"{demo}\":");
    var demoPhones = phon.Phonemize(demo);
    Console.WriteLine("  " + string.Join(' ', demoPhones));
    Console.WriteLine($"  ({phon.LastRulePredictedWords} word(s) via rules, rest from dictionary)");

    if (n == 0 || phoneTotal == 0) { Console.Error.WriteLine("FAIL: no data sampled"); return 1; }
    if (100.0 * phoneHit / phoneTotal < 80.0)
    {
        Console.Error.WriteLine("FAIL: phone accuracy < 80% — the rule-engine port is not faithful.");
        return 1;
    }
    Console.WriteLine();
    Console.WriteLine("PASS: sovereign CC-BY phonemizer works — SA text → X-SAMPA, no espeak, no GPL.");
    return 0;
}
