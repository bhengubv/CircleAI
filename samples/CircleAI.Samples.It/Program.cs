// Program.cs
//
// IT — desktop console face of the sample. All the real work lives in the shared
// ItSession (composed Neuron + concierge + placeholder brain); this file is just
// a console loop over it. The Android app (MainActivity) drives the exact same
// ItSession — one invention, two faces.
//
// Run:   dotnet run --project samples/CircleAI.Samples.It
//        dotnet run --project samples/CircleAI.Samples.It -- --demo   (scripted, no input)

using CircleAI.Core;
using CircleAI.Samples.It;
using CircleAI.Samples.It.Voice;

PrintBanner();

// Real model by default — that's the honest UX. --stub swaps in the canned
// responder for an instant, download-free look at the plumbing.
var useStub = args.Contains("--stub");
if (!useStub)
{
    Console.WriteLine("  Starting the Neuron. First run picks a model that fits this machine");
    Console.WriteLine("  and downloads it (~433 MB). Pass --stub to skip the model entirely.");
}

await using var session = new ItSession(useStubBrain: useStub);
await session.StartAsync();
Console.WriteLine($"  status: {session.StatusLine}");

// --speak: IT! also SAYS its replies, through the real de-Googled TTS ladder
// (select voice by modality → download from HF → OnnxTtsEngine + espeak). Each
// reply is written to a playable WAV. Degrades to text-only, out loud, if the
// voice or espeak-ng is unavailable — never silently.
ItSpeaker? speaker = null;
var wavDir = Path.Combine(Path.GetTempPath(), "circleai-it-voice");
if (args.Contains("--speak"))
{
    Console.WriteLine("  --speak: setting up voice…");
    var voiceStore = ModelPaths.Default;   // see ItSession
    var (sp, status) = await ItSpeaker.TryCreateAsync(voiceStore, Console.WriteLine);
    speaker = sp;
    Console.WriteLine(speaker is null ? $"  voice OFF: {status}" : $"  voice ON: WAVs → {wavDir}");
}

var turn = 0;
async Task Say(string reply)
{
    if (speaker is null || string.IsNullOrWhiteSpace(reply)) return;
    try
    {
        var wav = await speaker.SpeakToWavAsync(reply, Path.Combine(wavDir, $"it-{++turn:D3}.wav"));
        Console.WriteLine($"   🔊 {wav}");
    }
    catch (Exception ex) { Console.WriteLine($"   (tts failed: {ex.Message})"); }
}

// --hear <wav>: IT! LISTENS. Transcribe the WAV via the real de-Googled ASR
// ladder (select Asr by modality → download whisper-tiny from HF → Whisper.net),
// then treat the text as the user's turn. With --speak too, the whole loop runs:
// audio in → text → IT! → text → audio out.
var hearIdx = Array.IndexOf(args, "--hear");
var hearWav = hearIdx >= 0 && hearIdx + 1 < args.Length ? args[hearIdx + 1] : null;
if (hearWav is not null)
{
    Console.WriteLine("  --hear: setting up ears…");
    var earStore = ModelPaths.Default;   // see ItSession
    var (listener, lstatus) = await ItListener.TryCreateAsync(earStore, Console.WriteLine);
    if (listener is null)
    {
        Console.WriteLine($"  ears OFF: {lstatus}");
    }
    else
    {
        await using (listener)
        {
            Console.WriteLine($"  listening to: {hearWav}");
            var heard = await listener.HearAsync(hearWav);
            Console.WriteLine($"\nyou (spoken) > {heard}");
            var reply = await session.RunTurnStreamingAsync(heard, Console.WriteLine, Console.Write);
            Console.WriteLine();
            await Say(reply);
        }
    }
    if (!args.Contains("--demo")) { speaker?.Dispose(); Console.WriteLine("IT out."); return; }
}

// --demo: run the scripted conversation and exit (no keyboard needed).
if (args.Contains("--demo"))
{
    foreach (var line in ItSession.DemoTurns)
    {
        Console.WriteLine($"\nyou > {line}");
        var reply = await session.RunTurnStreamingAsync(line, Console.WriteLine, Console.Write);
        Console.WriteLine();
        await Say(reply);
    }
    Console.WriteLine("\n(demo complete - run without --demo to chat)");
    return;
}

// Interactive REPL.
Console.WriteLine("Chat with IT. Try: \"my name is ...\" then \"what's my name?\", or");
Console.WriteLine("\"solve ... step by step\" to see it route to a specialist. Type /quit to leave.");
while (true)
{
    Console.Write("\nyou > ");
    var input = Console.ReadLine();
    if (input is null) break;
    input = input.Trim();
    if (input.Length == 0) continue;
    if (input is "/quit" or "/exit") break;
    var reply = await session.RunTurnStreamingAsync(input, Console.WriteLine, Console.Write);
    Console.WriteLine();
    await Say(reply);
}
speaker?.Dispose();
Console.WriteLine("IT out.");

static void PrintBanner()
{
    Console.WriteLine("+------------------------------------------------+");
    Console.WriteLine("|  IT - a CircleAI Neuron reference sample        |");
    Console.WriteLine("|  concierge routing . warm slot . streaming      |");
    Console.WriteLine("+------------------------------------------------+");
}
