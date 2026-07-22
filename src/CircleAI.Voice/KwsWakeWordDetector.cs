// KwsWakeWordDetector.cs
//
// (Phase A3) Low-latency ONNX keyword-spotting wake-word detector.
//
// Unlike EnergyWakeWordDetector — which runs full ASR on every detected
// speech segment — this detector runs a tiny dedicated KWS CNN on a
// sliding 1-second window of microphone audio every 100 ms. Inference is
// 10–30 ms on phone-grade silicon; full path from word-end to fire is
// typically < 250 ms versus 500–2000 ms for the ASR-based variant.
//
// Model contract:
//   - Input: log-mel spectrogram tensor of shape [1, 1, NMelBins, NFrames]
//     OR raw waveform [1, SampleCount] (configurable at construction).
//   - Output: softmax probabilities of shape [1, NClasses]; the
//     `TargetClassIndex` slot is the wake-word class.
//
// Compatible with most published KWS models (Google Speech Commands V2
// trainings, MatchboxNet, BC-ResNet, KWS-MLP). Bring your own trained
// model + config; this file is the runtime.

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

/// <summary>Whether the model consumes mel-spectrograms or raw waveform.</summary>
public enum KwsInputKind { LogMel, RawWaveform }

/// <summary>(Phase A3) Configuration for <see cref="KwsWakeWordDetector"/>.</summary>
/// <param name="ModelPath">Path to the ONNX model file.</param>
/// <param name="WakeWord">Display phrase that gets surfaced on detection.</param>
/// <param name="InputKind">How the model expects audio.</param>
/// <param name="SampleRateHz">Sample rate of incoming audio (must match model training).</param>
/// <param name="WindowMs">Length of the audio window the model expects.</param>
/// <param name="HopMs">How often to slide the window forward — controls latency/CPU trade-off.</param>
/// <param name="NMelBins">Number of mel bins (for LogMel input). Common: 40, 64, 80.</param>
/// <param name="MelFrameMs">Frame length for mel-spec STFT. Common: 25–32 ms.</param>
/// <param name="MelHopMs">Hop length for mel-spec STFT. Common: 10–16 ms.</param>
/// <param name="TargetClassIndex">Output-slot index for the wake-word class.</param>
/// <param name="Threshold">Min probability for the target class to fire detection (0..1).</param>
/// <param name="MinIntervalBetweenFires">Cooldown so a single utterance doesn't fire repeatedly.</param>
public sealed record KwsConfig(
    string         ModelPath,
    string         WakeWord                  = EnergyWakeWordDetector.DefaultWakeWord,
    KwsInputKind   InputKind                 = KwsInputKind.LogMel,
    int            SampleRateHz              = 16_000,
    int            WindowMs                  = 1000,
    int            HopMs                     = 100,
    int            NMelBins                  = 40,
    int            MelFrameMs                = 25,
    int            MelHopMs                  = 10,
    int            TargetClassIndex          = 1,
    float          Threshold                 = 0.7f,
    TimeSpan?      MinIntervalBetweenFires   = null);

public sealed class KwsWakeWordDetector : IWakeWordDetector
{
    private readonly IAudioCapture _capture;
    private readonly KwsConfig     _config;
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _disposed;
    private DateTimeOffset _lastFireUtc;

    public KwsWakeWordDetector(IAudioCapture capture, KwsConfig config)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(config);
        if (!File.Exists(config.ModelPath))
            throw new FileNotFoundException("KWS ONNX model not found", config.ModelPath);
        if (config.SampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(config.SampleRateHz));
        if (config.WindowMs <= 0)     throw new ArgumentOutOfRangeException(nameof(config.WindowMs));
        if (config.HopMs <= 0)        throw new ArgumentOutOfRangeException(nameof(config.HopMs));
        if (config.Threshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(config.Threshold));

        _capture = capture;
        _config  = config;

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

        WakeWord  = config.WakeWord;
        WakeWords = new[] { config.WakeWord };
    }

    public string WakeWord    { get; }

    /// <inheritdoc />
    /// <remarks>
    /// ALWAYS a single phrase, and that is a real limitation rather than an
    /// oversight: a KWS model scores the one phrase it was trained on, so this
    /// detector cannot implement a per-person access list. It reports a
    /// one-entry list honestly instead of accepting several and silently
    /// matching only the first — which would look like access control while
    /// granting everyone the same key.
    /// <para>
    /// A host that needs multiple phrases must use
    /// <see cref="EnergyWakeWordDetector"/> (transcribe-and-match, any number of
    /// phrases, more battery) or run one KWS model per phrase.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> WakeWords { get; }

    /// <summary>
    /// <c>false</c> — see <see cref="WakeWords"/>. Lets a caller check before
    /// assuming a supplied access list will be honoured.
    /// </summary>
    public bool SupportsPerPhraseMatching => false;

    public bool   IsListening { get; private set; }
    public event EventHandler<WakeWordDetectedEventArgs>? WakeWordDetected;

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (IsListening) return Task.CompletedTask;
            _cts = new CancellationTokenSource();
            IsListening = true;
            _loopTask = Task.Run(() => ListenLoopAsync(_cts.Token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Task? toAwait;
        lock (_gate)
        {
            if (!IsListening) return;
            _cts?.Cancel();
            IsListening = false;
            toAwait = _loopTask;
        }
        if (toAwait is not null)
        {
            try { await toAwait.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
        lock (_gate) { _cts?.Dispose(); _cts = null; _loopTask = null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        // STOP FIRST, THEN MARK DISPOSED. StopAsync guards on _disposed, so
        // setting the flag first made this throw ObjectDisposedException every
        // time — and because the throw escaped (the catch only covers
        // cancellation), _session.Dispose() below never ran. Every teardown
        // leaked the native ONNX session.
        try { await StopAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { /* tear-down */ }

        _disposed = true;

        // Outside the try: the session must be released even if stopping the
        // loop failed for some other reason.
        _session.Dispose();
    }

    // ── Listening loop ───────────────────────────────────────────────────

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var windowSamples = _config.SampleRateHz * _config.WindowMs / 1000;
        var hopSamples    = _config.SampleRateHz * _config.HopMs    / 1000;
        var ringBuffer    = new float[windowSamples];
        var ringFill      = 0;
        var ringWrite     = 0;
        var samplesSinceLastInference = 0;
        var minInterval = _config.MinIntervalBetweenFires ?? TimeSpan.FromSeconds(1.0);

        try
        {
            await foreach (var chunkBytes in _capture.CaptureAsync(ct).ConfigureAwait(false))
            {
                if (chunkBytes.Length == 0) continue;
                ct.ThrowIfCancellationRequested();

                // Convert PCM16 bytes to normalised float32 samples.
                for (var i = 0; i + 1 < chunkBytes.Length; i += 2)
                {
                    var s = BinaryPrimitives.ReadInt16LittleEndian(chunkBytes.Span.Slice(i, 2));
                    ringBuffer[ringWrite] = s / 32768f;
                    ringWrite = (ringWrite + 1) % windowSamples;
                    if (ringFill < windowSamples) ringFill++;
                    samplesSinceLastInference++;

                    // Run inference when:
                    //   1. window is full (initial warm-up)
                    //   2. and we've accumulated at least one hop since last fire.
                    if (ringFill < windowSamples) continue;
                    if (samplesSinceLastInference < hopSamples) continue;
                    samplesSinceLastInference = 0;

                    // Linearise the ring into an in-order window for the model.
                    var window = new float[windowSamples];
                    var splitAt = windowSamples - ringWrite;
                    Array.Copy(ringBuffer, ringWrite, window, 0, splitAt);
                    Array.Copy(ringBuffer, 0, window, splitAt, ringWrite);

                    if (Predict(window) is float prob && prob >= _config.Threshold)
                    {
                        var now = DateTimeOffset.UtcNow;
                        if (now - _lastFireUtc < minInterval) continue;
                        _lastFireUtc = now;
                        WakeWordDetected?.Invoke(this, new WakeWordDetectedEventArgs
                        {
                            WakeWord   = WakeWord,
                            DetectedAt = now,
                            Confidence = prob,
                        });
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            /* normal shutdown */
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[KwsWakeWordDetector] loop error: {ex.Message}");
        }
        finally
        {
            lock (_gate) IsListening = false;
        }
    }

    private float? Predict(float[] window)
    {
        try
        {
            DenseTensor<float> tensor = _config.InputKind == KwsInputKind.RawWaveform
                ? new DenseTensor<float>(window, new[] { 1, window.Length })
                : LogMelTensor(window);
            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
            var output = results.First().AsTensor<float>();
            // Apply softmax across the last dim for stability + interpretability.
            var logits = output.ToArray();
            return Softmax(logits, _config.TargetClassIndex);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[KwsWakeWordDetector] inference failed: {ex.Message}");
            return null;
        }
    }

    private DenseTensor<float> LogMelTensor(float[] window)
    {
        var frameSize = _config.SampleRateHz * _config.MelFrameMs / 1000;
        var hopSize   = _config.SampleRateHz * _config.MelHopMs   / 1000;
        var numFrames = Math.Max(1, (window.Length - frameSize) / hopSize + 1);

        var hamming  = HammingWindow(frameSize);
        var filters  = MelFilterbank(_config.NMelBins, frameSize, _config.SampleRateHz);
        var melBins  = _config.NMelBins;

        // Tensor shape [1, 1, NMelBins, NumFrames] is the most common KWS layout
        // (Channels-first 2-D CNN over the spectrogram).
        var tensor = new DenseTensor<float>(new[] { 1, 1, melBins, numFrames });

        var frame = new float[frameSize];
        for (var fi = 0; fi < numFrames; fi++)
        {
            var start = fi * hopSize;
            for (var i = 0; i < frameSize; i++)
                frame[i] = (start + i < window.Length ? window[start + i] : 0f) * hamming[i];

            var power = PowerSpectrum(frame);
            for (var m = 0; m < melBins; m++)
            {
                var filter = filters[m];
                double sum = 0;
                var len = Math.Min(power.Length, filter.Length);
                for (var k = 0; k < len; k++) sum += power[k] * filter[k];
                tensor[0, 0, m, fi] = (float)Math.Log(Math.Max(1e-10, sum));
            }
        }
        return tensor;
    }

    private static float Softmax(float[] logits, int target)
    {
        if (target < 0 || target >= logits.Length) return 0f;
        var max = float.MinValue;
        for (var i = 0; i < logits.Length; i++) if (logits[i] > max) max = logits[i];
        double denom = 0;
        for (var i = 0; i < logits.Length; i++) denom += Math.Exp(logits[i] - max);
        var num = Math.Exp(logits[target] - max);
        return (float)(num / denom);
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
        static double MelToHz(double mel) => 700 * (Math.Pow(10, mel / 2595) - 1);
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
