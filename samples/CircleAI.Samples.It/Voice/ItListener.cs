#nullable enable

// ItListener.cs
//
// Gives IT! ears, through the REAL SDK path — the mirror of ItSpeaker:
//
//   SpeechModelSelector.BestFor(device, Asr)   ← pick a de-Googled ASR model by modality
//     → registry entry (ggerganov/whisper.cpp, Source=HuggingFace, pinned)
//     → ModelDownloadService.EnsureBundleAsync  ← fetch + SHA-verify ggml-tiny from HF
//     → WhisperNetTranscriber                    ← whisper.cpp via Whisper.net (native lib from NuGet)
//     → text
//
// Under Voice/ for the same reason as ItSpeaker: the console's recursive glob
// compiles it, the Android head's non-recursive `*.cs` glob does not — so the
// phone APK stays free of Whisper.net until Android voice is a deliberate build.

using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Voice;

namespace CircleAI.Samples.It.Voice;

/// <summary>Acquires a de-Googled ASR model and transcribes a WAV to text.</summary>
public sealed class ItListener : IAsyncDisposable
{
    private readonly WhisperNetTranscriber _transcriber;

    private ItListener(WhisperNetTranscriber t) => _transcriber = t;

    /// <summary>
    /// Selects the best ASR model the device can hold, downloads it (first run),
    /// and wires Whisper. Returns null with a reason when the chain cannot be
    /// completed — the caller degrades rather than crashing.
    /// </summary>
    public static async Task<(ItListener? listener, string status)> TryCreateAsync(
        string storageDir, Action<string>? log = null, CancellationToken ct = default)
    {
        using var registry = new ModelRegistryService();
        var selector = new SpeechModelSelector(registry);

        var probe = DeviceProbe.Snapshot();
        var pick = selector.BestFor(probe, ModelModality.Asr);
        if (pick is null)
            return (null, "no ASR model is catalogued");

        var entry = registry.GetLatestModel(pick.ModelId);
        if (entry is null || entry.BundleFiles is null || string.IsNullOrWhiteSpace(entry.Repo))
            return (null, $"'{pick.ModelId}' is not a downloadable bundle");

        log?.Invoke($"ears    : {entry.Name} ({pick.Quality}) from {entry.Source}:{entry.Repo}");

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

        var ggml = Directory.EnumerateFiles(dir, "*.bin", SearchOption.AllDirectories).FirstOrDefault();
        if (ggml is null)
            return (null, $"no ggml .bin found under '{dir}'");

        var transcriber = new WhisperNetTranscriber(ggml, "en");
        log?.Invoke($"engine  : WhisperNetTranscriber on {Path.GetFileName(ggml)}");
        return (new ItListener(transcriber), "ready");
    }

    /// <summary>Transcribes a WAV file to text (any rate/channels → 16 kHz mono).</summary>
    public async Task<string> HearAsync(string wavPath, CancellationToken ct = default)
    {
        var pcm = LoadWavAsPcm16Mono16k(wavPath);
        var result = await _transcriber.TranscribeAsync(pcm, ct).ConfigureAwait(false);
        return result.Text;
    }

    public ValueTask DisposeAsync() => _transcriber.DisposeAsync();

    // ── WAV → PCM16 16 kHz mono (what IVoiceTranscriber wants) ───────────────

    private static ReadOnlyMemory<byte> LoadWavAsPcm16Mono16k(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int rate = 16000, ch = 1, bits = 16, dataOff = -1, dataLen = 0;
        int pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int sz = BitConverter.ToInt32(bytes, pos + 4);
            var body = pos + 8;
            if (id == "fmt ")
            {
                ch = BitConverter.ToInt16(bytes, body + 2);
                rate = BitConverter.ToInt32(bytes, body + 4);
                bits = BitConverter.ToInt16(bytes, body + 14);
            }
            else if (id == "data") { dataOff = body; dataLen = sz; }
            pos = body + sz + (sz & 1);
        }
        if (dataOff < 0 || bits != 16)
            throw new InvalidOperationException($"unsupported WAV (bits={bits})");

        int frames = dataLen / (2 * ch);
        // Downmix to mono float first.
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            int acc = 0;
            for (int c = 0; c < ch; c++)
                acc += BitConverter.ToInt16(bytes, dataOff + (i * ch + c) * 2);
            mono[i] = acc / (float)ch;
        }

        // Resample to 16 kHz if needed.
        float[] outF;
        if (rate == 16000) outF = mono;
        else
        {
            int outLen = (int)((long)frames * 16000 / rate);
            outF = new float[outLen];
            double stepR = (double)frames / outLen;
            for (int i = 0; i < outLen; i++)
            {
                double src = i * stepR;
                int i0 = (int)src;
                double frac = src - i0;
                float a = mono[i0];
                float b = i0 + 1 < frames ? mono[i0 + 1] : a;
                outF[i] = (float)(a + (b - a) * frac);
            }
        }

        // Back to little-endian PCM16 bytes.
        var pcm = new byte[outF.Length * 2];
        for (int i = 0; i < outF.Length; i++)
        {
            short s = (short)Math.Clamp(outF[i], short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return pcm;
    }
}
