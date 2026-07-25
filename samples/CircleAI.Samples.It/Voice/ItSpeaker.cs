#nullable enable

// ItSpeaker.cs
//
// Gives IT! a voice, through the REAL SDK path — the sample's proof that the
// speech ladder works end to end, not just in a tool:
//
//   SpeechModelSelector.BestFor(device, Tts)   ← pick a de-Googled voice by modality
//     → registry entry (Repo / Source=HuggingFace / pinned BundleFiles)
//     → ModelDownloadService.EnsureBundleAsync  ← fetch + SHA-verify from HF
//     → OnnxTtsEngine + EspeakPhonemizer         ← text → IPA → speech (now real)
//     → a playable WAV
//
// Lives under Voice/ ON PURPOSE: the console sample's default recursive glob
// compiles it, but the Android head's non-recursive `*.cs` glob does not — so
// the phone APK stays free of ONNX Runtime until Android voice is a deliberate,
// separate build.
//
// Uses ModelDownloadService directly rather than BundleModelLoader: the loader
// assumes MNN's config.json anchor and would reject a Piper bundle (which has no
// config.json). That loader-is-MNN-shaped limitation is recorded in
// capabilities.json; the download service itself is modality-agnostic.

using System.Diagnostics;
using System.Text;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Voice;

namespace CircleAI.Samples.It.Voice;

/// <summary>Acquires a de-Googled TTS voice and speaks text to a WAV file.</summary>
public sealed class ItSpeaker : IDisposable
{
    private readonly OnnxTtsEngine _engine;

    private ItSpeaker(OnnxTtsEngine engine) => _engine = engine;

    /// <summary>
    /// The loaded TTS engine, so a caller can hand it to a
    /// <see cref="VoiceLoop"/> and speak straight to the device speaker instead
    /// of via a WAV file. Same instance — the voice model is loaded once.
    /// </summary>
    public ITtsEngine Engine => _engine;

    /// <summary>
    /// Mobile-only hook that builds the phonemizer for on-device TTS. The Android
    /// head sets this to the OUT-OF-PROCESS espeak client — CircleAI must not link
    /// GPL espeak-ng in-process. The argument is the voice, e.g. "en-us". Left null
    /// on mobile, TTS is reported unavailable rather than throwing.
    /// </summary>
    public static Func<string, IPhonemizer>? MobilePhonemizerFactory { get; set; }

    /// <summary>
    /// Selects the best TTS voice the device can hold, downloads it (first run),
    /// and wires the synthesis engine. Returns null with a reason when the chain
    /// cannot be completed (no voice catalogued, or espeak-ng absent) — the
    /// caller degrades to text-only rather than crashing.
    /// </summary>
    public static async Task<(ItSpeaker? speaker, string status)> TryCreateAsync(
        string storageDir, Action<string>? log = null, CancellationToken ct = default)
    {
        using var registry = new ModelRegistryService();
        var selector = new SpeechModelSelector(registry);

        var probe = DeviceProbe.Snapshot();

        // Plan, not a nullable pick — see ItListener. TTS likewise has no
        // non-model fallback (the de-Googled rule rules out the platform engine),
        // so unavailable means silent, and the plan says why in one sentence.
        var plan = selector.PlanFor(probe, ModelModality.Tts);
        if (!plan.IsAvailable || plan.Model is null)
            return (null, plan.Reason);

        var pick = plan.Model;

        // PHONEMIZER CHOICE IS PLATFORM-DEPENDENT.
        // Desktop shells out to the espeak-ng *executable* (already out-of-process).
        // On mobile there is no executable to launch, and espeak-ng is GPL-3.0 so it
        // must not be linked in-process either — CircleAI is permissive-licensed. So
        // mobile G2P crosses to a SEPARATE espeak app; the Android head wires that
        // out-of-process client into MobilePhonemizerFactory.
        var onMobile = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();
        string? espeak = null;
        if (onMobile)
        {
            if (MobilePhonemizerFactory is null)
                return (null, "on-device phonemizer not wired — set ItSpeaker.MobilePhonemizerFactory (the out-of-process espeak G2P client)");
        }
        else
        {
            espeak = ResolveEspeak();
            // Fail fast on the one dependency this needs, with an actionable
            // message — a silent text-only fallback would hide why IT! never spoke.
            if (espeak is null && !EspeakOnPath())
                return (null, "espeak-ng not found (install it or add to PATH) — TTS needs it for arbitrary text");
        }

        var entry = registry.GetLatestModel(pick.ModelId);
        if (entry is null)
            return (null, $"registry has no entry for '{pick.ModelId}'");

        if (entry.BundleFiles is null || string.IsNullOrWhiteSpace(entry.Repo))
            return (null, $"'{entry.Name}' is not a downloadable bundle");

        log?.Invoke($"voice   : {entry.Name} ({pick.Quality}) from {entry.Source}:{entry.Repo}");

        var specs = new List<BundleFileSpec>(entry.BundleFiles.Count);
        foreach (var f in entry.BundleFiles)
            specs.Add(new BundleFileSpec(f.Name, f.Sha256, f.SizeBytes));

        using var downloads = new ModelDownloadService(storageDir);
        // See ItListener — MB / rate / ETA / file N of M / phase, not a bare %.
        var lastPct = -1;
        var progress = new Progress<DownloadProgress>(p =>
        {
            var pct = (int)(p.Ratio * 100);
            var notable = p.Phase is not DownloadPhase.Downloading;
            if (!notable && pct < lastPct + 5) return;
            if (!notable) lastPct = pct;
            log?.Invoke($"  {p.Describe()}");
        });

        var dir = await downloads.EnsureBundleAsync(entry.Name, entry.Repo!, entry.Source, specs, progress, ct)
            .ConfigureAwait(false);

        // The Piper bundle nests the model (en/en_US/.../*.onnx); find it.
        var onnx = Directory.EnumerateFiles(dir, "*.onnx", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (onnx is null)
            return (null, $"no .onnx found under '{dir}'");

        IPhonemizer phonemizer = onMobile
            ? MobilePhonemizerFactory!("en-us")             // out-of-process espeak service
            : new EspeakPhonemizer("en-us", espeak);         // shell to the binary

        var engine = new OnnxTtsEngine(onnx, phonemizer);
        log?.Invoke($"engine  : OnnxTtsEngine on {Path.GetFileName(onnx)} " +
                    $"({(onMobile ? "out-of-process espeak" : espeak ?? "espeak on PATH")})");
        return (new ItSpeaker(engine), "ready");
    }

    /// <summary>Synthesises <paramref name="text"/> and writes a playable WAV.</summary>
    public async Task<string> SpeakToWavAsync(string text, string wavPath, CancellationToken ct = default)
    {
        var result = await _engine.SynthesiseAsync(text, ct).ConfigureAwait(false);
        WriteWav(wavPath, result.AudioData.Span, result.SampleRate, result.Channels, result.BitsPerSample);
        return wavPath;
    }

    public void Dispose() => _engine.Dispose();

    // ── helpers ──────────────────────────────────────────────────────────

    private static string? ResolveEspeak()
    {
        foreach (var p in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "eSpeak NG", "espeak-ng.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "eSpeak NG", "espeak-ng.exe"),
        })
            if (File.Exists(p)) return p;
        return null;
    }

    private static bool EspeakOnPath()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "espeak-ng", Arguments = "--version",
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            });
            p?.WaitForExit(3000);
            return p is { ExitCode: 0 };
        }
        catch { return false; }
    }

    private static void WriteWav(string path, ReadOnlySpan<byte> pcm, int sampleRate, int channels, int bits)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
}
