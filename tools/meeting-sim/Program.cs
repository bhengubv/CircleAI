// meeting-sim
//
// Runs a real meeting through the real SpokenSession and the real Whisper, so
// the thing under test is the session rather than a harness.
//
//   dotnet run --project tools/meeting-sim -- clip1.wav clip2.wav [...]
//
// The clips are stitched into one recording with silence between them, which is
// what a meeting is: people speaking in turns with gaps. That is exactly the
// shape SpokenSession claims to handle, and nothing about it can be checked with
// synthetic tones - a unit test proves the endpointing arithmetic, and only real
// speech proves the words survive being cut at the joins.
//
// WHY THIS EXISTS AS A TOOL. The session was written to take down meetings and
// had only ever been driven by a test fixture and one person saying one sentence
// into a phone. "It should work for a meeting" is the claim; this is the closest
// thing to checking it that does not need a meeting.
//
// It prints each piece as it lands, then the closing pass over the whole
// recording, then both side by side - because the WHOLE POINT of keeping the
// audio is that those two are not the same, and the difference is only visible
// when you can see them together.

using System.Diagnostics;
using System.Security.Cryptography;
using CircleAI.Voice;

const int Rate = 16_000;

var modelDir = Path.Combine(Path.GetTempPath(), "circleai-stt");
Directory.CreateDirectory(modelDir);
var modelPath = Path.Combine(modelDir, "ggml-tiny.bin");

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("CircleAI-meeting-sim/1.0");

if (!File.Exists(modelPath))
{
    Console.WriteLine("fetching ggml-tiny…");
    var bytes = await http.GetByteArrayAsync(
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin");
    await File.WriteAllBytesAsync(modelPath, bytes);
}

// The clips, in the order they are spoken.
// Anything that is not a file is taken as vocabulary to prime the decoder with.
var clips = args.Where(File.Exists).ToList();
var vocabulary = string.Join(" ", args.Where(a => !File.Exists(a)));
if (clips.Count == 0)
{
    Console.Error.WriteLine("give me one or more 16 kHz mono WAV files");
    return 1;
}

// GAP LONGER THAN THE SESSION'S OWN TIMEOUT, so the pauses genuinely end pieces.
// Shorter and this would test one long utterance wearing a meeting's clothes.
var gapSeconds = 6.0;
var silence = new byte[(int)(Rate * gapSeconds) * 2];

var meeting = new List<byte>();
foreach (var clip in clips)
{
    var pcm = ReadWav(clip);
    Console.WriteLine($"  · {Path.GetFileName(clip)} — {pcm.Length / (double)(Rate * 2):F1}s");
    if (meeting.Count > 0) meeting.AddRange(silence);
    meeting.AddRange(pcm);
}

var total = meeting.Count / (double)(Rate * 2);
Console.WriteLine();
Console.WriteLine($"meeting : {clips.Count} speakers' turns, {total:F1}s including {gapSeconds:F0}s gaps");
Console.WriteLine();

await using var transcriber = new WhisperNetTranscriber(modelPath, "en")
{
    Vocabulary = string.IsNullOrWhiteSpace(vocabulary) ? null : vocabulary,
};
if (!string.IsNullOrWhiteSpace(vocabulary)) Console.WriteLine($"primed  : {vocabulary}");
await using var session = new SpokenSession(new NullAudioCapture(), transcriber, "en")
{
    SilenceToEndMs = 5000,          // the meeting setting the Transcribe screen uses
};

var pieces = 0;
var watch = Stopwatch.StartNew();
var pieceTimes = new List<long>();

session.Heard += (_, piece) =>
{
    if (piece.Final) return;
    pieces++;
    pieceTimes.Add(watch.ElapsedMilliseconds);
    Console.WriteLine($"piece {pieces} ({piece.Seconds:F1}s) : \"{piece.Text}\"");
};

// FED IN BLOCKS, the size a microphone delivers, so the endpointing sees the
// recording the way it would see a room.
const int block = Rate / 10 * 2;                      // 100 ms
for (var at = 0; at < meeting.Count; at += block)
{
    var len = Math.Min(block, meeting.Count - at);
    await session.AcceptAsync(meeting.GetRange(at, len).ToArray());
}

// Whatever was still being said when the recording ran out.
await session.ListenAsync(new CancellationTokenSource(0).Token);

var live = session.Text;
var liveMs = watch.ElapsedMilliseconds;

Console.WriteLine();
Console.WriteLine($"live    : {pieces} pieces in {liveMs / 1000.0:F1}s "
                  + $"(x{liveMs / 1000.0 / total:F2} realtime)");

var again = Stopwatch.StartNew();
var whole = await session.ReadAgainAsync();
again.Stop();

Console.WriteLine($"re-read : {again.ElapsedMilliseconds / 1000.0:F1}s "
                  + $"(x{again.ElapsedMilliseconds / 1000.0 / total:F2} realtime)");
Console.WriteLine();
Console.WriteLine("AS IT HAPPENED");
Console.WriteLine($"  {live}");
Console.WriteLine();
Console.WriteLine("READ AGAIN");
Console.WriteLine($"  {whole}");
Console.WriteLine();
Console.WriteLine(live == whole
    ? "the two agree"
    : "the two differ — which is the point of keeping the audio");

return 0;

/// <summary>16-bit mono PCM out of a WAV, ignoring anything that is not audio.</summary>
static byte[] ReadWav(string path)
{
    var all = File.ReadAllBytes(path);

    // Walk the chunks rather than assuming a 44-byte header: real files carry
    // LIST and fact chunks, and a fixed offset silently reads them as audio.
    var at = 12;
    while (at + 8 <= all.Length)
    {
        var id = System.Text.Encoding.ASCII.GetString(all, at, 4);
        var size = BitConverter.ToInt32(all, at + 4);
        if (id == "data") return all[(at + 8)..Math.Min(at + 8 + size, all.Length)];
        at += 8 + size + (size % 2);
    }

    throw new InvalidDataException($"no data chunk in {path}");
}
