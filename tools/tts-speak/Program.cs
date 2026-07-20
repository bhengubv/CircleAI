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
