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
        var pick = selector.BestFor(probe, ModelModality.Tts);
        if (pick is null)
            return (null, "no TTS voice is catalogued");

        var espeak = ResolveEspeak();
        // Fail fast on the one dependency this needs, with an actionable message
        // — a silent text-only fallback would hide why IT! never spoke.
        if (espeak is null && !EspeakOnPath())
            return (null, "espeak-ng not found (install it or add to PATH) — TTS needs it for arbitrary text");

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
        var lastPct = -1;
        var progress = new Progress<double>(p =>
        {
            var pct = (int)(p * 100);
            if (pct >= lastPct + 10) { lastPct = pct; log?.Invoke($"  download {pct}%"); }
        });

        var dir = await downloads.EnsureBundleAsync(entry.Name, entry.Repo!, entry.Source, specs, progress, ct)
            .ConfigureAwait(false);

        // The Piper bundle nests the model (en/en_US/.../*.onnx); find it.
        var onnx = Directory.EnumerateFiles(dir, "*.onnx", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (onnx is null)
            return (null, $"no .onnx found under '{dir}'");

        var engine = new OnnxTtsEngine(onnx, new EspeakPhonemizer("en-us", espeak));
        log?.Invoke($"engine  : OnnxTtsEngine on {Path.GetFileName(onnx)} (espeak {(espeak ?? "on PATH")})");
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
