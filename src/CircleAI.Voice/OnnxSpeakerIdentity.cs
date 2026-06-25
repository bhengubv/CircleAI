// OnnxSpeakerIdentity.cs
//
// (Phase E5) Neural speaker diarisation / identification via ONNX.
// Replaces EnergyBandVoiceIdentity (handcrafted MFCC + nearest-centroid)
// with a real published embedding architecture — by default an
// ECAPA-TDNN-style model trained on VoxCeleb 1+2, which emits a fixed
// 192-D speaker vector per utterance.
//
// Model contract:
//   - Input:  log-mel spectrogram tensor [1, NMelBins, NFrames]
//             OR raw waveform [1, SampleCount] (configurable).
//   - Output: embedding tensor [1, EmbeddingDim] (commonly 192-D or 256-D).
//
// Enrollment: averages all observed embeddings per user, persisting
// centroids to a JSON file beside the model. Identification: cosine-
// similarity match against every enrolled centroid; user with similarity
// above `MatchThreshold` wins. If no enrolled user passes the threshold
// IdentifyAsync returns null.
//
// Compatible with most production speaker-embedding models — point this
// at a SpeechBrain ECAPA-TDNN, NeMo TitaNet, or 3D-Speaker CAM++ ONNX
// export and you're running real neural diarisation.

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CircleAI.Voice;

public enum SpeakerEmbedderInputKind { LogMel, RawWaveform }

/// <summary>(Phase E5) Per-user enrollment record used for cosine-similarity ID.</summary>
public sealed record EnrolledSpeaker(string UserId, float[] Centroid, int SampleCount);

/// <summary>(Phase E5) Identify-or-enroll surface — wrapped by an IVoiceIdentity adapter in CircleAI.Companion.</summary>
public interface ISpeakerIdentity : IAsyncDisposable
{
    ValueTask<string?> IdentifyAsync(ReadOnlyMemory<byte> audioPcm16, int sampleRateHz, CancellationToken ct = default);
    ValueTask          EnrollAsync(string userId, ReadOnlyMemory<byte> audioPcm16, int sampleRateHz, CancellationToken ct = default);
}

/// <summary>(Phase E5) Configuration for <see cref="OnnxSpeakerIdentity"/>.</summary>
public sealed record SpeakerIdentityConfig(
    string                    ModelPath,
    string                    EnrollmentStorePath,
    SpeakerEmbedderInputKind  InputKind        = SpeakerEmbedderInputKind.LogMel,
    int                       SampleRateHz     = 16_000,
    int                       NMelBins         = 80,
    int                       MelFrameMs       = 25,
    int                       MelHopMs         = 10,
    int                       MinUtteranceMs   = 1_000,
    int                       MaxUtteranceMs   = 8_000,
    double                    MatchThreshold   = 0.55);

public sealed class OnnxSpeakerIdentity : ISpeakerIdentity
{
    private readonly SpeakerIdentityConfig _config;
    private readonly InferenceSession      _session;
    private readonly string                _inputName;
    private readonly string                _outputName;
    private readonly ConcurrentDictionary<string, EnrolledSpeaker> _enrolled = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _storeGate = new(1, 1);
    private bool _disposed;

    public OnnxSpeakerIdentity(SpeakerIdentityConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!File.Exists(config.ModelPath))
            throw new FileNotFoundException("Speaker-embedding ONNX model not found", config.ModelPath);
        _config = config;

        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode          = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads      = 1,
            IntraOpNumThreads      = 1,
        };
        _session    = new InferenceSession(config.ModelPath, opts);
        _inputName  = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();

        LoadEnrollmentStore();
    }

    public async ValueTask<string?> IdentifyAsync(
        ReadOnlyMemory<byte> audioPcm16,
        int sampleRateHz,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (audioPcm16.IsEmpty) return null;
        if (_enrolled.IsEmpty)  return null;
        ct.ThrowIfCancellationRequested();

        var embedding = ComputeEmbedding(audioPcm16.Span, sampleRateHz);
        if (embedding is null) return null;

        string?  best          = null;
        double   bestSim       = double.MinValue;
        foreach (var (userId, speaker) in _enrolled)
        {
            ct.ThrowIfCancellationRequested();
            var sim = CosineSimilarity(embedding, speaker.Centroid);
            if (sim > bestSim) { bestSim = sim; best = userId; }
        }
        await Task.CompletedTask.ConfigureAwait(false);
        return bestSim >= _config.MatchThreshold ? best : null;
    }

    public async ValueTask EnrollAsync(
        string userId,
        ReadOnlyMemory<byte> audioPcm16,
        int sampleRateHz,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("userId required");
        if (audioPcm16.IsEmpty) throw new ArgumentException("audio required");
        ct.ThrowIfCancellationRequested();

        var embedding = ComputeEmbedding(audioPcm16.Span, sampleRateHz)
            ?? throw new InvalidOperationException("Embedding extraction failed");

        _enrolled.AddOrUpdate(
            userId,
            _ => new EnrolledSpeaker(userId, embedding, 1),
            (_, prev) =>
            {
                var n = prev.SampleCount;
                var newCentroid = new float[prev.Centroid.Length];
                for (var i = 0; i < newCentroid.Length; i++)
                    newCentroid[i] = (prev.Centroid[i] * n + embedding[i]) / (n + 1);
                L2Normalise(newCentroid);
                return prev with { Centroid = newCentroid, SampleCount = n + 1 };
            });
        await SaveEnrollmentStoreAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
        _storeGate.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    // ── Embedding extraction ─────────────────────────────────────────────

    private float[]? ComputeEmbedding(ReadOnlySpan<byte> pcm16, int sampleRateHz)
    {
        try
        {
            if (sampleRateHz != _config.SampleRateHz)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[OnnxSpeakerIdentity] mismatched sample rate {sampleRateHz} vs model {_config.SampleRateHz}");
                return null;
            }
            var minSamples = sampleRateHz * _config.MinUtteranceMs / 1000;
            var maxSamples = sampleRateHz * _config.MaxUtteranceMs / 1000;
            var nSamples   = pcm16.Length / 2;
            if (nSamples < minSamples) return null;
            if (nSamples > maxSamples) nSamples = maxSamples;

            var window = new float[nSamples];
            for (var i = 0; i < nSamples; i++)
            {
                var s = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 2, 2));
                window[i] = s / 32768f;
            }

            DenseTensor<float> tensor = _config.InputKind == SpeakerEmbedderInputKind.RawWaveform
                ? new DenseTensor<float>(window, new[] { 1, nSamples })
                : LogMelTensor(window);

            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
            var output = results.First().AsTensor<float>().ToArray();
            L2Normalise(output);
            return output;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OnnxSpeakerIdentity] embedding failed: {ex.Message}");
            return null;
        }
    }

    private DenseTensor<float> LogMelTensor(float[] window)
    {
        var frameSize = _config.SampleRateHz * _config.MelFrameMs / 1000;
        var hopSize   = _config.SampleRateHz * _config.MelHopMs   / 1000;
        var numFrames = Math.Max(1, (window.Length - frameSize) / hopSize + 1);
        var hamming   = HammingWindow(frameSize);
        var filters   = MelFilterbank(_config.NMelBins, frameSize, _config.SampleRateHz);

        var tensor = new DenseTensor<float>(new[] { 1, _config.NMelBins, numFrames });
        var frame  = new float[frameSize];
        for (var fi = 0; fi < numFrames; fi++)
        {
            var start = fi * hopSize;
            for (var i = 0; i < frameSize; i++)
                frame[i] = (start + i < window.Length ? window[start + i] : 0f) * hamming[i];

            var power = PowerSpectrum(frame);
            for (var m = 0; m < _config.NMelBins; m++)
            {
                var filter = filters[m];
                double sum = 0;
                var len = Math.Min(power.Length, filter.Length);
                for (var k = 0; k < len; k++) sum += power[k] * filter[k];
                tensor[0, m, fi] = (float)Math.Log(Math.Max(1e-10, sum));
            }
        }
        return tensor;
    }

    private void LoadEnrollmentStore()
    {
        try
        {
            if (!File.Exists(_config.EnrollmentStorePath)) return;
            var json = File.ReadAllText(_config.EnrollmentStorePath);
            var records = JsonSerializer.Deserialize<List<EnrolledSpeaker>>(json);
            if (records is null) return;
            foreach (var r in records) _enrolled[r.UserId] = r;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OnnxSpeakerIdentity] enrollment load failed: {ex.Message}");
        }
    }

    private async Task SaveEnrollmentStoreAsync(CancellationToken ct)
    {
        await _storeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_config.EnrollmentStorePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var records = _enrolled.Values.ToList();
            var json    = JsonSerializer.Serialize(records);
            var tmp     = _config.EnrollmentStorePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _config.EnrollmentStorePath, overwrite: true);
        }
        finally { _storeGate.Release(); }
    }

    // ── Linear-algebra helpers ───────────────────────────────────────────

    private static void L2Normalise(float[] v)
    {
        double sumSq = 0;
        for (var i = 0; i < v.Length; i++) sumSq += v[i] * v[i];
        var norm = Math.Sqrt(sumSq);
        if (norm < 1e-9) return;
        for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return -1;
        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }

    private static float[] HammingWindow(int n)
    {
        var w = new float[n];
        for (var i = 0; i < n; i++) w[i] = 0.54f - 0.46f * (float)Math.Cos(2 * Math.PI * i / (n - 1));
        return w;
    }

    private static double[] PowerSpectrum(float[] frame)
    {
        var n = frame.Length;
        var half = n / 2 + 1;
        var spec = new double[half];
        for (var k = 0; k < half; k++)
        {
            double re = 0, im = 0;
            var omega = -2.0 * Math.PI * k / n;
            for (var t = 0; t < n; t++)
            {
                re += frame[t] * Math.Cos(omega * t);
                im += frame[t] * Math.Sin(omega * t);
            }
            spec[k] = re * re + im * im;
        }
        return spec;
    }

    private static double[][] MelFilterbank(int numFilters, int frameSize, int sampleRateHz)
    {
        static double HzToMel(double hz)  => 2595 * Math.Log10(1 + hz / 700.0);
        static double MelToHz(double mel) => 700  * (Math.Pow(10, mel / 2595) - 1);
        var lowMel  = HzToMel(0);
        var highMel = HzToMel(sampleRateHz / 2.0);
        var melPoints = new double[numFilters + 2];
        for (var i = 0; i < melPoints.Length; i++)
            melPoints[i] = lowMel + (highMel - lowMel) * i / (melPoints.Length - 1);
        var binPoints = new int[melPoints.Length];
        for (var i = 0; i < melPoints.Length; i++)
            binPoints[i] = (int)Math.Floor((frameSize + 1) * MelToHz(melPoints[i]) / sampleRateHz);

        var half = frameSize / 2 + 1;
        var filters = new double[numFilters][];
        for (var m = 0; m < numFilters; m++)
        {
            filters[m] = new double[half];
            var left   = binPoints[m];
            var centre = binPoints[m + 1];
            var right  = binPoints[m + 2];
            for (var k = left; k < centre && k < half; k++)
                if (centre != left) filters[m][k] = (k - left) / (double)(centre - left);
            for (var k = centre; k < right && k < half; k++)
                if (right != centre) filters[m][k] = (right - k) / (double)(right - centre);
        }
        return filters;
    }
}
