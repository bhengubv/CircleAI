// stt-hear
//
// Proves Whisper ASR actually runs — the input half of IT!'s voice loop, and the
// close of the "no whisper native lib ships" gap. Downloads the catalogued
// ggml-tiny model, transcribes a real speech file, prints what it heard AND
// WHEN, and writes a subtitle file beside it.
//
//   dotnet run --project tools/stt-hear -- [audioPath] [language]
//
// With no file, it fetches JFK's line (the canonical whisper.cpp sample, 16 kHz)
// and asserts the transcript contains "country".
//
// IT NOW RUNS THE PRODUCT'S OWN PATH. This tool used to carry its own RIFF
// parser, its own downmix and its own resampler — fifty lines sitting beside
// WavIo, which already had all three and handled four more sample formats than
// the copy did. Two owners for one fact, which is this repo's documented
// anti-pattern, and the copy was the worse of the two: it rejected any WAV that
// was not 16-bit, so a 24-bit recording failed here while the app read it fine.
//
// Going through CircleAI.Voice means a pass here is evidence about the SHIPPING
// code rather than about a parallel implementation that happens to live in the
// same repo.

using System.Security.Cryptography;
using CircleAI.Voice;

var modelDir = Path.Combine(Path.GetTempPath(), "circleai-stt");
Directory.CreateDirectory(modelDir);

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("CircleAI-stt-hear/1.0");

// The exact ggml-tiny catalogued in the registry — verified by its pinned SHA.
var modelPath = Path.Combine(modelDir, "ggml-tiny.bin");
await Ensure(modelPath,
    "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin",
    "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21");

// The audio to transcribe.
string audioPath;
string? expectWord = null;
if (args.Length > 0 && File.Exists(args[0]))
{
    audioPath = args[0];
}
else
{
    // The canonical whisper.cpp JFK sample (16 kHz mono WAV, clear speech).
    audioPath = Path.Combine(modelDir, "jfk.wav");
    await Ensure(audioPath,
        "https://github.com/ggerganov/whisper.cpp/raw/master/samples/jfk.wav", null);
    expectWord = "country";
}

Console.WriteLine($"model : {modelPath}");
Console.WriteLine($"audio : {audioPath}");

// LANGUAGE IS AN ARGUMENT, NOT A CONSTANT. Hard-coded to "en" this tool will
// happily "transcribe" Japanese into English-looking nonsense and report success,
// which makes it useless for testing any other language — the failure looks like
// a bad recording rather than a misconfigured recogniser. "auto" lets whisper
// detect, which is also what the product does.
var language = args.Length > 1 ? args[1] : "auto";
Console.WriteLine($"lang  : {language}");

var sw = System.Diagnostics.Stopwatch.StartNew();

await using var transcriber = new WhisperNetTranscriber(modelPath, language);

// Progress matters here for the same reason it matters in the app: tiny runs at
// about real time, so a long recording is a long wait, and a wait with no number
// looks like a hang.
var lastShown = -1;
var progress = new Progress<double>(p =>
{
    var pct = (int)(p * 100);
    if (pct / 10 == lastShown / 10) return;
    lastShown = pct;
    Console.WriteLine($"      : {pct,3}%");
});

var result = await Transcribing.FileAsync(
    transcriber, audioPath, decoder: null, language: language, progress: progress);

sw.Stop();

var text = result.Text;
Console.WriteLine();
Console.WriteLine($"HEARD : \"{text}\"");
Console.WriteLine($"lang  : {result.LanguageCode}   confidence: {result.Confidence:0.00}");
Console.WriteLine($"time  : {sw.Elapsed.TotalSeconds:F1} s");

// WHEN, NOT JUST WHAT. The timings are the half that was being thrown away, so
// a tool that proves ASR runs should show them rather than take them on trust.
if (result.Timed.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"timed : {result.Timed.Count} segment(s)");
    foreach (var seg in result.Timed.Take(10))
        Console.WriteLine($"        [{seg.Start:hh\\:mm\\:ss\\.ff} -> {seg.End:hh\\:mm\\:ss\\.ff}] {seg.Text}");
    if (result.Timed.Count > 10) Console.WriteLine($"        ... and {result.Timed.Count - 10} more");

    var srtPath = Path.ChangeExtension(audioPath, ".srt");
    await File.WriteAllTextAsync(srtPath, Subtitles.ToSrt(result.Timed));
    Console.WriteLine($"srt   : {srtPath}");
}
else
{
    Console.WriteLine();
    Console.WriteLine("timed : NONE — the engine reported no segment timings.");
}

Console.WriteLine();

if (string.IsNullOrWhiteSpace(text))
{
    Console.Error.WriteLine("FAIL: empty transcription.");
    return 1;
}
if (expectWord is not null &&
    !text.Contains(expectWord, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"FAIL: expected the transcript to contain '{expectWord}'.");
    return 1;
}
if (result.Timed.Count == 0)
{
    Console.Error.WriteLine("FAIL: transcript carried no timings.");
    return 1;
}

Console.WriteLine("PASS: Whisper ASR ran, produced real text, and timed it. IT! can hear.");
return 0;

async Task Ensure(string path, string url, string? sha)
{
    if (File.Exists(path) && (sha is null || await Sha(path) == sha))
    {
        Console.WriteLine($"cached: {Path.GetFileName(path)}");
        return;
    }
    Console.WriteLine($"GET   : {url}");
    try
    {
        var data = await http.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(path, data);
    }
    catch (Exception ex) when (sha is null)
    {
        Console.WriteLine($"  (skip {Path.GetFileName(path)}: {ex.Message})");
        return;
    }
    if (sha is not null && await Sha(path) != sha)
        throw new InvalidOperationException($"SHA mismatch for {Path.GetFileName(path)}");
}

static async Task<string> Sha(string p)
{
    await using var s = File.OpenRead(p);
    using var sha = SHA256.Create();
    return Convert.ToHexString(await sha.ComputeHashAsync(s)).ToLowerInvariant();
}
