// stt-hear
//
// Proves Whisper ASR actually runs — the input half of IT!'s voice loop, and the
// close of the "no whisper native lib ships" gap. Downloads the catalogued
// ggml-tiny model, transcribes a real speech WAV, and prints what it heard.
//
//   dotnet run --project tools/stt-hear -- [wavPath]
//
// With no wav, it fetches JFK's line (the canonical whisper.cpp sample, 16 kHz)
// and asserts the transcript contains "country". Whisper.net ships the native
// library via NuGet, so there is no DllNotFoundException to hit.

using System.Security.Cryptography;
using Whisper.net;

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
string wavPath;
string? expectWord = null;
if (args.Length > 0 && File.Exists(args[0]))
{
    wavPath = args[0];
}
else
{
    // The canonical whisper.cpp JFK sample (16 kHz mono WAV, clear speech).
    wavPath = Path.Combine(modelDir, "jfk.wav");
    await Ensure(wavPath,
        "https://github.com/ggerganov/whisper.cpp/raw/master/samples/jfk.wav", null);
    expectWord = "country";
}

Console.WriteLine($"model : {modelPath}");
Console.WriteLine($"wav   : {wavPath}");

var samples = LoadWav16kMono(wavPath, out var srcRate, out var srcChannels);
Console.WriteLine($"audio : {srcRate} Hz {srcChannels}ch → 16000 Hz mono, {samples.Length} samples ({samples.Length / 16000.0:F1} s)");

var sw = System.Diagnostics.Stopwatch.StartNew();
// LANGUAGE IS AN ARGUMENT, NOT A CONSTANT. Hard-coded to "en" this tool will
// happily "transcribe" Japanese into English-looking nonsense and report success,
// which makes it useless for testing any other language — the failure looks like
// a bad recording rather than a misconfigured recogniser. "auto" lets whisper
// detect, which is also what the product does.
var language = args.Length > 1 ? args[1] : "auto";
Console.WriteLine($"lang  : {language}");

using var factory = WhisperFactory.FromPath(modelPath);
using var processor = factory.CreateBuilder().WithLanguage(language).Build();

var heard = new System.Text.StringBuilder();
await foreach (var seg in processor.ProcessAsync(samples))
    heard.Append(seg.Text);
sw.Stop();

var text = heard.ToString().Trim();
Console.WriteLine();
Console.WriteLine($"HEARD : \"{text}\"");
Console.WriteLine($"time  : {sw.Elapsed.TotalSeconds:F1} s");
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

Console.WriteLine("PASS: Whisper ASR ran and produced real text. IT! can hear.");
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

// Minimal WAV reader → 16 kHz mono float[-1,1], resampling by linear
// interpolation when the source rate differs (Piper is 22050; JFK is 16000).
static float[] LoadWav16kMono(string path, out int srcRate, out int channels)
{
    var bytes = File.ReadAllBytes(path);
    // Walk chunks to find "fmt " and "data" (some WAVs carry LIST/fact chunks).
    int fmtRate = 16000, ch = 1, bits = 16, dataOff = -1, dataLen = 0;
    int pos = 12; // skip RIFF....WAVE
    while (pos + 8 <= bytes.Length)
    {
        var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
        int sz = BitConverter.ToInt32(bytes, pos + 4);
        var body = pos + 8;
        if (id == "fmt ")
        {
            ch = BitConverter.ToInt16(bytes, body + 2);
            fmtRate = BitConverter.ToInt32(bytes, body + 4);
            bits = BitConverter.ToInt16(bytes, body + 14);
        }
        else if (id == "data") { dataOff = body; dataLen = sz; }
        pos = body + sz + (sz & 1);
    }
    srcRate = fmtRate; channels = ch;
    if (dataOff < 0 || bits != 16)
        throw new InvalidOperationException($"unsupported WAV (bits={bits}, data={dataOff})");

    int frames = dataLen / (2 * ch);
    var mono = new float[frames];
    for (int i = 0; i < frames; i++)
    {
        int acc = 0;
        for (int c = 0; c < ch; c++)
            acc += BitConverter.ToInt16(bytes, dataOff + (i * ch + c) * 2);
        mono[i] = acc / (float)ch / 32768f;
    }
    if (fmtRate == 16000) return mono;

    // Linear resample to 16 kHz.
    int outLen = (int)((long)frames * 16000 / fmtRate);
    var outBuf = new float[outLen];
    double step = (double)frames / outLen;
    for (int i = 0; i < outLen; i++)
    {
        double src = i * step;
        int i0 = (int)src;
        double frac = src - i0;
        float a = mono[i0];
        float b = i0 + 1 < frames ? mono[i0 + 1] : a;
        outBuf[i] = (float)(a + (b - a) * frac);
    }
    return outBuf;
}
