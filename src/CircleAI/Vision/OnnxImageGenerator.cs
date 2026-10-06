#nullable enable

// OnnxImageGenerator.cs
//
// Image generation on device, through ONNX Runtime — the engine this repo
// already ships and already drives (the whole speech stack, plus the face and
// plate models next to this file). No new runtime, no native build.
//
// THE SHAPE OF A LATENT DIFFUSION MODEL, and why it is three graphs and not one:
//
//   prompt --[text encoder]--> embedding
//   noise  --[UNet]--> less noisy latent   (repeated `Steps` times)
//          --[VAE decoder]--> pixels
//
// They are separate ONNX files because the UNet runs N times per image while
// the encoder and decoder run once, and because a distilled model swaps the
// UNet alone. Exporters ship them that way (unet/model.onnx, vae_decoder/…),
// so this loads them as they come rather than demanding a merged graph.
//
// ⚠️ SESSION CONSTRUCTION IS THE COST, NOT INFERENCE. Measured on a P30 in the
// TTS work: 98% of a slow run was building ONNX sessions, and the app was
// rebuilding them per utterance — 10:35 became 0:07 by caching the session and
// writing the optimised graph once. So sessions here are built ONCE in the
// constructor and reused for every image. Do not move them into GenerateAsync.
// See ondevice-voice-mms-onnx-path.
//
// STEPS ARE THE OTHER LEVER. The UNet runs once per step, so a 4-step distilled
// model is roughly an order of magnitude cheaper than a 50-step one on the same
// hardware. The default is 4 deliberately.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CircleAI.Vision;

/// <summary>Where the three graphs of a diffusion bundle live.</summary>
/// <param name="UnetPath">The denoiser. Runs once per step.</param>
/// <param name="VaeDecoderPath">Latent -> pixels. Runs once.</param>
/// <param name="TextEncoderPath">
/// Prompt -> embedding. Optional: an unconditional / distilled pipeline can run
/// without one, and saying so beats failing to load a bundle that does not ship it.
/// </param>
/// <param name="LatentChannels">Latent depth (4 for SD-family VAEs).</param>
/// <param name="LatentScale">
/// Pixels per latent cell (8 for SD-family). Width/height are divided by this to
/// size the latent.
/// </param>
public sealed record OnnxImageModel(
    string  UnetPath,
    string  VaeDecoderPath,
    string? TextEncoderPath = null,
    int     LatentChannels  = 4,
    int     LatentScale     = 8);

/// <summary>
/// <see cref="IImageGenerator"/> backed by ONNX Runtime.
/// </summary>
public sealed class OnnxImageGenerator : IImageGenerator
{
    private readonly OnnxImageModel  _model;
    private readonly InferenceSession _unet;
    private readonly InferenceSession _vae;
    private readonly InferenceSession? _textEncoder;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <inheritdoc/>
    public IReadOnlyList<(int Width, int Height)> SupportedSizes { get; }

    /// <summary>Loads a diffusion bundle. Sessions are built once, here.</summary>
    public OnnxImageGenerator(OnnxImageModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        Require(model.UnetPath, "UNet");
        Require(model.VaeDecoderPath, "VAE decoder");

        // ORT_ENABLE_ALL on a large graph costs real time, and it is paid once
        // here rather than per image — the 98%-of-runtime lesson from the TTS work.
        var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };

        _unet = new InferenceSession(model.UnetPath, opts);
        _vae  = new InferenceSession(model.VaeDecoderPath, opts);
        _textEncoder = model.TextEncoderPath is { Length: > 0 } t && File.Exists(t)
            ? new InferenceSession(t, opts)
            : null;

        SupportedSizes = DeriveSupportedSizes();
    }

    private static void Require(string path, string what)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"{what} ONNX model not found.", path);
    }

    /// <summary>
    /// Reads the sizes the UNet was exported for out of its own input shape,
    /// rather than assuming 512x512. A fixed-shape export that is asked for a
    /// size it was not built for fails deep inside ORT with a shape mismatch;
    /// this is what lets a caller ask first.
    /// </summary>
    private IReadOnlyList<(int, int)> DeriveSupportedSizes()
    {
        foreach (var kv in _unet.InputMetadata)
        {
            var dims = kv.Value.Dimensions;
            // Latent input is NCHW: [batch, channels, h, w].
            if (dims.Length == 4 && dims[1] == _model.LatentChannels && dims[2] > 0 && dims[3] > 0)
                return new[] { (dims[3] * _model.LatentScale, dims[2] * _model.LatentScale) };
        }
        // Dynamic axes (-1) mean the graph will take what it is given.
        return Array.Empty<(int, int)>();
    }

    /// <inheritdoc/>
    public async Task<GeneratedImage> GenerateAsync(
        ImageRequest request,
        IProgress<ImageProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // One image at a time: an InferenceSession is not safe to run
        // concurrently on the same handle, the same rule the MNN bridge states.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Run(request, progress, ct), ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private GeneratedImage Run(ImageRequest request, IProgress<ImageProgress>? progress, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var (w, h)  = Snap(request.Width, request.Height);
        var steps   = Math.Max(1, request.Steps);

        var lw = w / _model.LatentScale;
        var lh = h / _model.LatentScale;

        var latent = NewLatent(_model.LatentChannels, lh, lw, request.Seed);
        var unetIn = _unet.InputMetadata.Keys.ToArray();
        var unetOut = _unet.OutputMetadata.Keys.First();

        for (var step = 0; step < steps; step++)
        {
            ct.ThrowIfCancellationRequested();

            var feeds = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(unetIn[0], latent),
            };

            // Timestep, when the graph declares one. Descending, as schedulers run.
            if (unetIn.Length > 1)
            {
                var t = (float)(steps - step) / steps;
                feeds.Add(NamedOnnxValue.CreateFromTensor(unetIn[1],
                    new DenseTensor<float>(new[] { t }, new[] { 1 })));
            }

            using var res = _unet.Run(feeds);
            latent = res.First(v => v.Name == unetOut).AsTensor<float>().ToDenseTensor();

            progress?.Report(new ImageProgress(step + 1, steps));
        }

        // Latent -> pixels.
        var vaeIn  = _vae.InputMetadata.Keys.First();
        var vaeOut = _vae.OutputMetadata.Keys.First();
        using var decoded = _vae.Run(new[] { NamedOnnxValue.CreateFromTensor(vaeIn, latent) });
        var pixels = decoded.First(v => v.Name == vaeOut).AsTensor<float>();

        var png = ToPng(pixels, w, h);
        return new GeneratedImage(png, w, h, steps, started.Elapsed);
    }

    /// <summary>Nearest size the graph actually supports.</summary>
    private (int W, int H) Snap(int w, int h)
    {
        if (SupportedSizes.Count == 0)
        {
            // Dynamic graph: round to the latent grid so the division is exact.
            var s = _model.LatentScale;
            return (Math.Max(s, w / s * s), Math.Max(s, h / s * s));
        }
        return SupportedSizes
            .OrderBy(p => Math.Abs(p.Width - w) + Math.Abs(p.Height - h))
            .First();
    }

    private static DenseTensor<float> NewLatent(int channels, int h, int w, int? seed)
    {
        var rng = seed is int s ? new Random(s) : new Random();
        var t = new DenseTensor<float>(new[] { 1, channels, h, w });
        var buf = t.Buffer.Span;
        for (var i = 0; i < buf.Length; i++)
        {
            // Box-Muller: the latent starts as standard normal noise, not uniform.
            // Uniform noise produces a washed-out image that still looks like an
            // image, which is exactly the kind of wrong that survives review.
            var u1 = 1.0 - rng.NextDouble();
            var u2 = 1.0 - rng.NextDouble();
            buf[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }
        return t;
    }

    /// <summary>
    /// NCHW float in [-1,1] (the VAE's range) to PNG. Values are clamped, not
    /// scaled to fit: a model that emits out-of-range values has a problem the
    /// caller should see as clipping rather than as a silently rescaled image.
    /// </summary>
    private static byte[] ToPng(Tensor<float> pixels, int w, int h)
    {
        using var img = new Image<Rgb24>(w, h);

        // Copied out of the ref-struct span: Tensor<T>.Dimensions is a
        // ReadOnlySpan, and a ref local cannot be captured by the local function
        // below (CS8175).
        var dims = pixels.Dimensions.ToArray();
        var channels = dims.Length == 4 ? dims[1] : 3;
        var maxY = dims.Length == 4 ? dims[2] - 1 : h - 1;
        var maxX = dims.Length == 4 ? dims[3] - 1 : w - 1;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                byte C(int c)
                {
                    var v = channels > c
                        ? pixels[0, c, Math.Min(y, maxY), Math.Min(x, maxX)]
                        : 0f;
                    var b = (v + 1f) * 0.5f * 255f;          // [-1,1] -> [0,255]
                    return (byte)Math.Clamp(b, 0f, 255f);
                }
                img[x, y] = new Rgb24(C(0), C(1), C(2));
            }
        }

        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _unet.Dispose();
        _vae.Dispose();
        _textEncoder?.Dispose();
        _gate.Dispose();
    }
}
