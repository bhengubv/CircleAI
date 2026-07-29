using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CircleAI.Voice;

/// <summary>
/// Two-stage ONNX TTS for the South African languages no ready-made voice covers:
/// isiZulu, Sepedi, siSwati and Tshivenda.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="OnnxTtsEngine"/> drives single-stage VITS
/// voices (MMS, Piper, Simba) where the model owns its own vocoder. ToucanTTS is
/// split — acoustic model produces a mel, a separate vocoder turns it into audio —
/// and it is the ONLY permissive (Apache-2.0) model that speaks these four.
/// </para>
/// <para>
/// <b>What makes it run on a phone.</b> ToucanTTS's own frontend reaches these
/// languages through <i>transphone</i>, a neural grapheme-to-phoneme model that
/// pulls in torch and cannot run on a Kirin 710. It is replaced here by
/// <see cref="NchltPhonemizer"/> — our C#, CC-BY-3.0 phonemizer for all 11 SA
/// languages — plus a per-language table mapping each NCHLT phone straight to the
/// 64 articulatory features the model expects. The X-SAMPA→IPA→features resolution
/// happens once, offline; on the device it is a dictionary lookup. Nothing here
/// needs Python, espeak, or any GPL component.
/// </para>
/// </remarks>
public sealed class ToucanOnnxTtsEngine : ITtsEngine, IDisposable
{
    /// <summary>Articulatory feature vector width the acoustic model expects.</summary>
    public const int FeatureDim = 64;

    /// <summary>Vocoder output rate. ToucanTTS is 24 kHz — not the 16 kHz of MMS.</summary>
    private const int VocoderSampleRate = 24_000;

    /// <summary>Mel channels ToucanTTS produces. Used to tell time-major from channel-major.</summary>
    private const int MelChannels = 128;

    private const string EosKey = "<eos>";

    /// <summary>
    /// Index of the "word-boundary" articulatory feature. Frames carrying it get a
    /// duration of zero — ToucanTTS does this in Python, inside a loop that a traced
    /// graph would bake to the trace sentence's word positions.
    /// </summary>
    private const int WordBoundaryFeature = 16;

    private readonly string _stageAPath;
    private readonly string _stageBPath;
    private readonly string _vocoderPath;
    private readonly IReadOnlyDictionary<string, float[]> _features;
    private readonly long _languageId;
    private readonly float[] _speakerEmbedding;
    private readonly IPhonemizer _phonemizer;
    private readonly Lock _gate = new();

    private InferenceSession? _stageA;
    private InferenceSession? _stageB;
    private InferenceSession? _vocoder;
    private bool _disposed;

    /// <param name="stageAPath">Encoder + prosody + duration prediction (phones → enriched + durations).</param>
    /// <param name="stageBPath">Decoder + flow matching (upsampled → mel).</param>
    /// <param name="vocoderPath">Vocoder (mel → waveform).</param>
    /// <param name="features">NCHLT phone → 64 articulatory features, for one language.</param>
    /// <param name="languageId">ToucanTTS language-embedding id (e.g. isiZulu = 7215).</param>
    /// <param name="speakerEmbedding">The model's 192-dim default utterance embedding.</param>
    /// <param name="phonemizer">Text → NCHLT phones; normally a <see cref="NchltPhonemizer"/>.</param>
    public ToucanOnnxTtsEngine(
        string stageAPath,
        string stageBPath,
        string vocoderPath,
        IReadOnlyDictionary<string, float[]> features,
        long languageId,
        float[] speakerEmbedding,
        IPhonemizer phonemizer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageAPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageBPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(vocoderPath);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(speakerEmbedding);
        ArgumentNullException.ThrowIfNull(phonemizer);

        _stageAPath = stageAPath;
        _stageBPath = stageBPath;
        _vocoderPath = vocoderPath;
        _features = features;
        _languageId = languageId;
        _speakerEmbedding = speakerEmbedding;
        _phonemizer = phonemizer;
    }

    /// <summary>
    /// Build an engine from the files produced by the offline export: the two ONNX
    /// models, a <c>nchlt_features_&lt;lang&gt;.json</c> table, and
    /// <c>speaker_embedding.json</c>.
    /// </summary>
    public static ToucanOnnxTtsEngine FromDirectory(
        string directory, string language, long languageId, IPhonemizer phonemizer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        // These graphs cost far more to LOAD than to run on a phone, so pick the
        // cheapest-to-load form available:
        //   .ort       — ORT's flatbuffer format: pre-optimised, no protobuf parse
        //   _int8.onnx — fewer bytes to read (falls back if a kernel is missing)
        //   .onnx      — plain
        string Pick(string stem)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(directory, stem + ".ort"),
                         Path.Combine(directory, stem + "_int8.onnx"),
                     })
            {
                if (File.Exists(candidate)) return candidate;
            }
            return Path.Combine(directory, stem + ".onnx");
        }

        var stageA = Pick("toucan_stage_a");
        var stageB = Pick("toucan_stage_b");
        var vocoder = Pick("toucan_vocoder");
        var featurePath = Path.Combine(directory, $"nchlt_features_{language}.json");
        var speakerPath = Path.Combine(directory, "speaker_embedding.json");

        foreach (var required in new[] { stageA, stageB, vocoder, featurePath, speakerPath })
        {
            if (!File.Exists(required))
                throw new FileNotFoundException($"ToucanTTS asset missing: {required}", required);
        }

        return new ToucanOnnxTtsEngine(
            stageA, stageB, vocoder,
            LoadFeatureTable(featurePath),
            languageId,
            LoadSpeakerEmbedding(speakerPath),
            phonemizer);
    }

    /// <summary>Parses a phone → 64-float table.</summary>
    public static IReadOnlyDictionary<string, float[]> LoadFeatureTable(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var map = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var vec = new List<float>(FeatureDim);
            foreach (var v in prop.Value.EnumerateArray())
                vec.Add((float)v.GetDouble());

            // A short vector would be silently zero-padded into a different phone.
            if (vec.Count != FeatureDim)
            {
                throw new InvalidDataException(
                    $"Phone '{prop.Name}' in {Path.GetFileName(path)} has {vec.Count} features, expected {FeatureDim}.");
            }
            map[prop.Name] = vec.ToArray();
        }
        return map;
    }

    /// <summary>Parses the flat 192-float speaker embedding.</summary>
    public static float[] LoadSpeakerEmbedding(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<float>(192);
        foreach (var v in doc.RootElement.EnumerateArray())
            list.Add((float)v.GetDouble());
        return list.ToArray();
    }

    /// <inheritdoc />
    public Task<TtsSynthesisResult> SynthesiseAsync(
        string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(new TtsSynthesisResult(
                ReadOnlyMemory<byte>.Empty, VocoderSampleRate, 1, 16));
        }

        return Task.Run(() => SynthesiseCore(text, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamSynthesiseAsync(
        string text,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var result = await SynthesiseAsync(text, cancellationToken).ConfigureAwait(false);
        if (result.AudioData.Length > 0)
            yield return result.AudioData;
    }

    /// <summary>
    /// Phones → [L,64] features → mel → waveform. Phones absent from the table are
    /// skipped and counted rather than substituted: a wrong vector is a wrong sound,
    /// which is harder to notice than a missing one.
    /// </summary>
    private TtsSynthesisResult SynthesiseCore(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var phones = _phonemizer.Phonemize(text);
        var rows = new List<float[]>(phones.Count + 1);
        var skipped = 0;

        foreach (var p in phones)
        {
            if (_features.TryGetValue(p, out var vec)) rows.Add(vec);
            else skipped++;
        }

        // ToucanTTS's own frontend terminates the sequence; without it the model
        // trails off mid-utterance.
        if (_features.TryGetValue(EosKey, out var eos)) rows.Add(eos);

        LastSkippedPhoneCount = skipped;
        if (rows.Count <= 1)
            return new TtsSynthesisResult(ReadOnlyMemory<byte>.Empty, VocoderSampleRate, 1, 16);

        var loadWatch = System.Diagnostics.Stopwatch.StartNew();
        EnsureSessions();
        loadWatch.Stop();
        LastLoadMs = loadWatch.ElapsedMilliseconds;

        float[] waveform;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var features = new DenseTensor<float>(new[] { rows.Count, FeatureDim });
            for (var i = 0; i < rows.Count; i++)
                for (var j = 0; j < FeatureDim; j++)
                    features[i, j] = rows[i][j];

            var langId = new DenseTensor<long>(new[] { 1 });
            langId[0] = _languageId;

            var speaker = new DenseTensor<float>(new[] { _speakerEmbedding.Length });
            for (var i = 0; i < _speakerEmbedding.Length; i++)
                speaker[i] = _speakerEmbedding[i];

            // ── Stage A: encoder + prosody + durations (L → L) ──────────────
            using var a = _stageA!.Run(
            [
                NamedOnnxValue.CreateFromTensor("phone_features", features),
                NamedOnnxValue.CreateFromTensor("lang_id", langId),
                NamedOnnxValue.CreateFromTensor("utterance_embedding", speaker)
            ]);

            LastStageAMs = sw.ElapsedMilliseconds; sw.Restart();

            var aOut = a.ToList();
            var enriched = aOut.First(v => v.Name == "enriched").AsTensor<float>();
            var durations = aOut.First(v => v.Name == "durations").AsTensor<float>();
            var speakerProc = aOut.First(v => v.Name == "speaker_proc").AsTensor<float>();

            var eDims = enriched.Dimensions;                 // [1, L, D]
            var length = eDims[^2];
            var depth = eDims[^1];
            var eFlat = enriched.ToArray();
            var dFlat = durations.ToArray();

            // ── Length regulation, in C# ─────────────────────────────────────
            // This is the step no traced graph can hold: the output length is
            // decided by the predicted durations. Doing it here is why the two
            // ONNX graphs stay shape-regular and work at any input length.
            var counts = new int[length];
            var total = 0;
            for (var i = 0; i < length; i++)
            {
                var n = (int)Math.Round(i < dFlat.Length ? dFlat[i] : 0f, MidpointRounding.AwayFromZero);
                if (n < 0) n = 0;

                // A long leading pause produces artefacts; ToucanTTS pins it to 1.
                if (i == 0) n = 1;

                // Word-boundary frames are markers, not sounds.
                if (rows[i][WordBoundaryFeature] == 1f) n = 0;

                counts[i] = n;
                total += n;
            }

            if (total <= 0)
                return new TtsSynthesisResult(ReadOnlyMemory<byte>.Empty, VocoderSampleRate, 1, 16);

            var upsampled = new DenseTensor<float>(new[] { 1, total, depth });
            var row = 0;
            for (var i = 0; i < length; i++)
            {
                for (var r = 0; r < counts[i]; r++, row++)
                    for (var j = 0; j < depth; j++)
                        upsampled[0, row, j] = eFlat[(i * depth) + j];
            }

            LastFrameCount = total;

            // ── Stage B: decoder + flow matching (T → T) ─────────────────────
            using var b = _stageB!.Run(
            [
                NamedOnnxValue.CreateFromTensor("upsampled", upsampled),
                NamedOnnxValue.CreateFromTensor("speaker_proc", speakerProc)
            ]);

            LastStageBMs = sw.ElapsedMilliseconds; sw.Restart();

            var mel = b.First().AsTensor<float>();

            // Stage B emits [1, T, 128] — time-major — while the vocoder wants
            // [1, 128, T]. Decide from which axis is the mel-channel count rather
            // than assuming an order: getting this backwards yields a graph that
            // still runs and still produces noise.
            var dims = mel.Dimensions;
            var flat = mel.ToArray();
            int frames, channels;
            bool timeMajor;
            if (dims.Length == 3)
            {
                timeMajor = dims[2] == MelChannels;
                frames = timeMajor ? dims[1] : dims[2];
                channels = timeMajor ? dims[2] : dims[1];
            }
            else
            {
                timeMajor = dims[1] == MelChannels;
                frames = timeMajor ? dims[0] : dims[1];
                channels = timeMajor ? dims[1] : dims[0];
            }

            var melIn = new DenseTensor<float>(new[] { 1, channels, frames });
            for (var c = 0; c < channels; c++)
            {
                for (var t = 0; t < frames; t++)
                {
                    melIn[0, c, t] = timeMajor
                        ? flat[(t * channels) + c]
                        : flat[(c * frames) + t];
                }
            }

            using var wavResult = _vocoder!.Run(
                [NamedOnnxValue.CreateFromTensor("mel", melIn)]);

            waveform = wavResult.First().AsTensor<float>().ToArray();
            LastVocoderMs = sw.ElapsedMilliseconds;
        }

        return new TtsSynthesisResult(FloatToPcm16(waveform), VocoderSampleRate, 1, 16);
    }

    /// <summary>Phones dropped in the last call because the table had no entry.</summary>
    public int LastSkippedPhoneCount { get; private set; }

    /// <summary>Mel frames after length regulation in the last call.</summary>
    public int LastFrameCount { get; private set; }

    /// <summary>
    /// Milliseconds spent creating the three ONNX sessions (first call only).
    /// Separated from inference because on a phone this dominates, and optimising
    /// compute when the cost is actually session load wastes the effort.
    /// </summary>
    public long LastLoadMs { get; private set; }

    /// <summary>Milliseconds in stage A (encoder + prosody + durations).</summary>
    public long LastStageAMs { get; private set; }

    /// <summary>Milliseconds in stage B (decoder + flow matching).</summary>
    public long LastStageBMs { get; private set; }

    /// <summary>Milliseconds in the vocoder.</summary>
    public long LastVocoderMs { get; private set; }

    /// <summary>A one-line breakdown of where the last synthesis spent its time.</summary>
    public string LastTimingSummary =>
        $"load={LastLoadMs}ms stageA={LastStageAMs}ms stageB={LastStageBMs}ms vocoder={LastVocoderMs}ms";

    private void EnsureSessions()
    {
        if (_stageA is not null && _stageB is not null && _vocoder is not null) return;

        lock (_gate)
        {
            if (_stageA is not null && _stageB is not null && _vocoder is not null) return;

            foreach (var p in new[] { _stageAPath, _stageBPath, _vocoderPath })
            {
                if (!File.Exists(p))
                    throw new InvalidOperationException($"ToucanTTS ONNX model not found at '{p}'.");
            }

            _stageA ??= OpenSession(_stageAPath);
            _stageB ??= OpenSession(_stageBPath);
            _vocoder ??= OpenSession(_vocoderPath);
        }
    }

    /// <summary>
    /// Open a session, reusing an ORT-optimised copy of the graph when one exists.
    /// </summary>
    /// <remarks>
    /// <c>ORT_ENABLE_ALL</c> re-runs full graph optimisation on every session
    /// creation. On a phone, against a quarter-gigabyte graph, that is minutes —
    /// and it was being paid again for every single utterance. Writing the
    /// optimised graph out once and loading it thereafter with optimisation
    /// disabled turns that into a plain file read.
    /// </remarks>
    private static InferenceSession OpenSession(string modelPath)
    {
        try
        {
            return OpenSessionCore(modelPath);
        }
        catch (OnnxRuntimeException) when (modelPath.Contains("_int8", StringComparison.Ordinal))
        {
            // A quantised model can load fine on a desktop and be unrunnable on the
            // phone — Android's ONNX Runtime ships without some int8 kernels (e.g.
            // ConvInteger). Losing the size win beats losing the voice.
            var full = modelPath.Replace("_int8", "", StringComparison.Ordinal);
            if (!File.Exists(full)) throw;
            return OpenSessionCore(full);
        }
    }

    private static InferenceSession OpenSessionCore(string modelPath)
    {
        // A .ort file is already optimised and needs no protobuf parse — running the
        // optimiser over it again would throw away the reason for using it.
        if (modelPath.EndsWith(".ort", StringComparison.OrdinalIgnoreCase))
        {
            var ortOpts = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount)
            };
            return new InferenceSession(modelPath, ortOpts);
        }

        var optimised = Path.ChangeExtension(modelPath, null) + ".ort.onnx";

        if (File.Exists(optimised) && new FileInfo(optimised).Length > 1024)
        {
            var fast = new SessionOptions
            {
                // Already optimised on disk — re-optimising it would undo the point.
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount)
            };
            return new InferenceSession(optimised, fast);
        }

        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount)
        };

        // Emit the optimised graph so the NEXT run skips this entirely. Best-effort:
        // a read-only location must not stop us synthesising.
        try { opts.OptimizedModelFilePath = optimised; }
        catch { }

        return new InferenceSession(modelPath, opts);
    }

    private static byte[] FloatToPcm16(ReadOnlySpan<float> waveform)
    {
        var pcm = new byte[waveform.Length * 2];
        for (var i = 0; i < waveform.Length; i++)
        {
            var sample = Math.Clamp(waveform[i], -1f, 1f);
            var value = (short)(sample * 32767f);
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[(i * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }
        return pcm;
    }

    /// <summary>Release both ONNX sessions.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            _stageA?.Dispose(); _stageA = null;
            _stageB?.Dispose(); _stageB = null;
            _vocoder?.Dispose(); _vocoder = null;
        }
    }
}
