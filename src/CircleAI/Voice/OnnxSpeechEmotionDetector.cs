// OnnxSpeechEmotionDetector.cs
//
// (Phase E6) Real speech-emotion recognition via a vendored wav2vec2-style
// ONNX model. Replaces KeywordEmotionSensor (which only looks at text) for
// any caller that has voice frames.
//
// Model contract:
//   - Input:  raw float waveform at 16 kHz, shape [1, NSamples].
//   - Output: logits over the configured emotion classes, shape
//             [1, NClasses]. The class with the highest softmax probability
//             wins; arousal/valence are looked up from a built-in dimensional
//             mapping (Russell circumplex) per label.
//
// Compatible with most published wav2vec2-emotion exports
// (audeering/wav2vec2-large-robust-12-ft-emotion-msp-dim, superb/ER).

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CircleAI.Voice;

/// <summary>(Phase E6) Output emotion frame from a speech-emotion model.</summary>
/// <param name="Label">Top-1 emotion label (lowercase, e.g. "happy", "angry").</param>
/// <param name="Arousal">Russell-circumplex arousal coordinate in [-1, 1].</param>
/// <param name="Valence">Russell-circumplex valence coordinate in [-1, 1].</param>
/// <param name="Probability">Softmax probability of the winning class.</param>
public sealed record SpeechEmotionFrame(string Label, double Arousal, double Valence, double Probability);

/// <summary>(Phase E6) Configuration for <see cref="OnnxSpeechEmotionDetector"/>.</summary>
public sealed record SpeechEmotionConfig(
    string         ModelPath,
    IReadOnlyList<string>? Labels        = null,
    int            SampleRateHz          = 16_000,
    int            MaxClipMs             = 8_000);

public interface ISpeechEmotionDetector : IAsyncDisposable
{
    ValueTask<SpeechEmotionFrame?> SenseAsync(
        ReadOnlyMemory<byte> audioPcm16,
        int sampleRateHz,
        CancellationToken ct = default);
}

public sealed class OnnxSpeechEmotionDetector : ISpeechEmotionDetector
{
    // SUPERB-ER + IEMOCAP standard 4-class layout (kept as default).
    private static readonly IReadOnlyList<string> DefaultLabels = new[]
    {
        "neutral", "happy", "angry", "sad"
    };

    // Russell circumplex coordinates for the standard discrete emotion labels.
    // Coordinates picked from the emotion-recognition literature (Posner 2005,
    // Mehrabian/Russell). Anything outside the dictionary maps to (0,0) which
    // is "neutral" in dimensional space.
    private static readonly IReadOnlyDictionary<string, (double Arousal, double Valence)> Circumplex
        = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase)
    {
        ["neutral"]    = ( 0.00,  0.00),
        ["happy"]      = ( 0.55,  0.81),
        ["happiness"]  = ( 0.55,  0.81),
        ["joy"]        = ( 0.60,  0.82),
        ["angry"]      = ( 0.74, -0.62),
        ["anger"]      = ( 0.74, -0.62),
        ["sad"]        = (-0.43, -0.65),
        ["sadness"]    = (-0.43, -0.65),
        ["fear"]       = ( 0.78, -0.64),
        ["fearful"]    = ( 0.78, -0.64),
        ["surprise"]   = ( 0.85,  0.40),
        ["surprised"]  = ( 0.85,  0.40),
        ["disgust"]    = ( 0.45, -0.60),
        ["disgusted"]  = ( 0.45, -0.60),
        ["calm"]       = (-0.40,  0.45),
        ["excited"]    = ( 0.82,  0.70),
        ["bored"]      = (-0.65, -0.20),
        ["frustrated"] = ( 0.55, -0.55),
        ["contempt"]   = ( 0.20, -0.55),
    };

    private readonly SpeechEmotionConfig _config;
    private readonly InferenceSession    _session;
    private readonly string              _inputName;
    private readonly string              _outputName;
    private readonly IReadOnlyList<string> _labels;
    private bool _disposed;

    public OnnxSpeechEmotionDetector(SpeechEmotionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!File.Exists(config.ModelPath))
            throw new FileNotFoundException("Speech-emotion ONNX model not found", config.ModelPath);
        _config = config;
        _labels = config.Labels ?? DefaultLabels;

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
    }

    public async ValueTask<SpeechEmotionFrame?> SenseAsync(
        ReadOnlyMemory<byte> audioPcm16,
        int sampleRateHz,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (audioPcm16.IsEmpty) return null;
        if (sampleRateHz != _config.SampleRateHz)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[OnnxSpeechEmotionDetector] mismatched sample rate {sampleRateHz} vs model {_config.SampleRateHz}");
            return null;
        }
        ct.ThrowIfCancellationRequested();

        var maxSamples = sampleRateHz * _config.MaxClipMs / 1000;
        var nSamples   = Math.Min(audioPcm16.Length / 2, maxSamples);
        if (nSamples == 0) return null;

        var window = new float[nSamples];
        var span   = audioPcm16.Span;
        for (var i = 0; i < nSamples; i++)
        {
            var s = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(i * 2, 2));
            window[i] = s / 32768f;
        }

        try
        {
            var tensor = new DenseTensor<float>(window, new[] { 1, nSamples });
            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
            var logits = results.First().AsTensor<float>().ToArray();

            var (bestIdx, bestProb) = Softmax(logits);
            var label = (bestIdx < _labels.Count ? _labels[bestIdx] : "unknown").ToLowerInvariant();
            var (arousal, valence) = Circumplex.TryGetValue(label, out var coords) ? coords : (0d, 0d);
            await Task.CompletedTask.ConfigureAwait(false);
            return new SpeechEmotionFrame(label, arousal, valence, bestProb);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OnnxSpeechEmotionDetector] inference failed: {ex.Message}");
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private static (int Index, double Probability) Softmax(float[] logits)
    {
        if (logits.Length == 0) return (-1, 0);
        var max = logits[0];
        for (var i = 1; i < logits.Length; i++) if (logits[i] > max) max = logits[i];
        double denom = 0;
        for (var i = 0; i < logits.Length; i++) denom += Math.Exp(logits[i] - max);

        var bestIdx  = 0;
        var bestProb = 0d;
        for (var i = 0; i < logits.Length; i++)
        {
            var p = Math.Exp(logits[i] - max) / denom;
            if (p > bestProb) { bestProb = p; bestIdx = i; }
        }
        return (bestIdx, bestProb);
    }
}
