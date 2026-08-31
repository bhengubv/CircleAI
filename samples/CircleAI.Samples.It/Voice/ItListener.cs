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
    /// The underlying transcriber, so a caller can build a full
    /// <see cref="VoicePipeline"/>/<see cref="VoiceLoop"/> (wake word + VAD +
    /// mic) around the SAME loaded Whisper model instead of loading it twice —
    /// a second copy would be tens of MB of RAM on a phone that has none spare.
    /// </summary>
    public IVoiceTranscriber Transcriber => _transcriber;

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

        // Ask for the PLAN, not a nullable pick: the selector knows whether ASR
        // can be served at all, and its Reason is the sentence to show the user.
        // ASR has no non-model fallback, so an unavailable plan is genuinely the
        // end of the road here — but the caller learns that from one decision
        // rather than inferring it from a null.
        var plan = selector.PlanFor(probe, ModelModality.Asr);
        if (!plan.IsAvailable || plan.Model is null)
            return (null, plan.Reason);

        var pick = plan.Model;
        var entry = registry.GetLatestModel(pick.ModelId);
        if (entry is null || entry.BundleFiles is null || string.IsNullOrWhiteSpace(entry.Repo))
            return (null, $"'{pick.ModelId}' is not a downloadable bundle");

        log?.Invoke($"ears    : {entry.Name} ({pick.Quality}) from {entry.Source}:{entry.Repo}");

        var specs = new List<BundleFileSpec>(entry.BundleFiles.Count);
        foreach (var f in entry.BundleFiles)
            specs.Add(new BundleFileSpec(f.Name, f.Sha256, f.SizeBytes));

        using var downloads = new ModelDownloadService(storageDir);

        // Rich progress: MB, rate, ETA, file N of M, and phase. The old version
        // printed "download 10%" every 10% and nothing else — no way for a user
        // to tell a slow link from a stalled one.
        var lastPct = -1;
        var progress = new Progress<DownloadProgress>(p =>
        {
            // Always announce a phase change; otherwise throttle to every 5%.
            var pct = (int)(p.Ratio * 100);
            var notable = p.Phase is not DownloadPhase.Downloading;
            if (!notable && pct < lastPct + 5) return;
            if (!notable) lastPct = pct;
            log?.Invoke($"  {p.Describe()}");
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

    /// <summary>
    /// Builds the wake-word detector the SELECTOR chose for this device, rather
    /// than hard-coding one.
    /// </summary>
    /// <remarks>
    /// Today every device lands on <see cref="EnergyWakeWordDetector"/>, because
    /// no keyword-spotting model is catalogued yet — but that is the selector's
    /// verdict, not a decision baked into this call site. The moment an
    /// openWakeWord entry is catalogued with a real hash, devices that can hold
    /// it get it and this method does not change.
    /// <para>
    /// Returns the reason alongside, so the host can show WHY it is listening the
    /// way it is: a transcribe-and-match wake word costs meaningfully more
    /// battery than a keyword spotter, and that is worth surfacing rather than
    /// hiding.
    /// </para>
    /// </remarks>
    /// <param name="wakeWords">
    /// The access list. Defaults to the product phrase alone; pass more to let
    /// specific people wake it, or a different set to lock others out.
    /// </param>
    public async Task<(IWakeWordDetector detector, string reason)> CreateWakeDetectorAsync(
        IAudioCapture capture,
        IEnumerable<string>? wakeWords = null,
        string? storageDir = null,
        CancellationToken ct = default)
    {
        var phrases = wakeWords?.ToArray() is { Length: > 0 } supplied
            ? supplied
            : new[] { EnergyWakeWordDetector.DefaultWakeWord };

        using var registry = new ModelRegistryService();
        var plan = new SpeechModelSelector(registry)
            .PlanFor(DeviceProbe.Snapshot(), ModelModality.WakeWord);

        // The selector chose a keyword-spotting model — fetch and run it.
        if (plan.Model is not null)
        {
            var entry = registry.GetLatestModel(plan.Model.ModelId);
            if (entry?.BundleFiles is not null && !string.IsNullOrWhiteSpace(entry.Repo))
            {
                // Same directory as everything else - see ItSession.
                var dir = storageDir ?? ModelPaths.Default;

                var specs = entry.BundleFiles
                    .Select(f => new BundleFileSpec(f.Name, f.Sha256, f.SizeBytes))
                    .ToList();

                using var downloads = new ModelDownloadService(dir);
                // Cast required: a bare `null` is ambiguous between the
                // IProgress<DownloadProgress> and IProgress<double> overloads.
                var modelDir = await downloads
                    .EnsureBundleAsync(entry.Name, entry.Repo!, entry.Source, specs,
                        (IProgress<DownloadProgress>?)null, ct)
                    .ConfigureAwait(false);

                var onnx = Directory
                    .EnumerateFiles(modelDir, "*.onnx", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (onnx is not null)
                {
                    // WakeWordFactory decides which runtime this bundle needs by
                    // looking at what is IN it: a three-graph transducer gets the
                    // zipformer spotter, a single graph gets the classifier. That
                    // used to be decided here, by taking the first .onnx found and
                    // handing it to the classifier — which for a transducer is an
                    // arbitrary third of a model that cannot work.
                    //
                    // It also stops the multi-phrase downgrade below from being
                    // needed at all: the classifier scores one phrase, so an access
                    // list had to fall back to transcribe-and-match, at the cost of
                    // running an ASR model continuously. The transducer matches any
                    // number of phrases from text, so several wake words no longer
                    // cost anything.
                    var bundleDir = Path.GetDirectoryName(onnx)!;
                    var engine = WakeWordFactory.EngineFor(bundleDir);

                    if (engine == WakeEngine.ZipformerTransducer)
                    {
                        var probe = DeviceProbe.Snapshot();
                        var calibrationPath = Path.Combine(dir, "..", "wake-calibration.json");
                        var detector = WakeWordFactory.Create(
                            capture,
                            bundleDir,
                            new WakeHostCapabilities(probe.RamTotalBytes, TranscriberAvailable: true),
                            WakeCalibration.Load(Path.GetFullPath(calibrationPath)),
                            _transcriber);

                        var listening = string.Join(", ", detector.WakeWords);
                        return (detector, $"{plan.Reason} — zipformer transducer, listening for {listening}");
                    }

                    // Single-graph classifier: one phrase only. Rather than accept
                    // an access list and quietly match just the first — access
                    // control in appearance only — fall back to the transcribing
                    // detector, which really does distinguish phrases, and say why.
                    if (phrases.Length > 1)
                        return (new EnergyWakeWordDetector(capture, _transcriber, phrases),
                                $"{plan.Reason} — using transcribe-and-match instead of the KWS model: " +
                                $"{phrases.Length} wake phrases are configured and a KWS model scores only one");

                    return (new KwsWakeWordDetector(capture, new KwsConfig(onnx, phrases[0])),
                            plan.Reason);
                }
            }

            // Catalogued but unusable. Fall back rather than go deaf — but SAY so,
            // because a silent downgrade is how "we ship a keyword spotter"
            // survives as a claim long after it stopped being true.
            return (new EnergyWakeWordDetector(capture, _transcriber, phrases),
                    $"{plan.Reason} — WARNING: wake model '{plan.Model.ModelId}' could not be " +
                    "prepared; fell back to transcribe-and-match");
        }

        return (new EnergyWakeWordDetector(capture, _transcriber, phrases), plan.Reason);
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
