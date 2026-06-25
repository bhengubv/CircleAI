// OnnxFaceEmbedder.cs
//
// (Phase C3) Real IFaceEmbedder backed by an ArcFace-family ONNX model.
// Input: 112x112 BGR float32 (typical ArcFace preprocessing).
// Output: 512-D L2-normalised vector (model dependent; we re-normalise to
// guarantee cosine == dot).

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CircleAI.Vision;

/// <param name="ModelPath">Path to an ArcFace-family ONNX model.</param>
/// <param name="InputSize">Square input dimension (112 = ArcFace default).</param>
/// <param name="Dimension">Output embedding dimension (typically 512).</param>
public sealed record OnnxFaceEmbedderOptions(
    string ModelPath,
    int    InputSize = 112,
    int    Dimension = 512);

public sealed class OnnxFaceEmbedder : IFaceEmbedder, IDisposable
{
    private readonly OnnxFaceEmbedderOptions _opts;
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    public OnnxFaceEmbedder(OnnxFaceEmbedderOptions opts)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        if (!File.Exists(opts.ModelPath))
            throw new FileNotFoundException("ONNX model not found", opts.ModelPath);
        var sessOpts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session    = new InferenceSession(opts.ModelPath, sessOpts);
        _inputName  = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    public int Dimension => _opts.Dimension;

    public async ValueTask<FaceEmbedding> EmbedAsync(
        ReadOnlyMemory<byte> imageBytes, DetectedFace face, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(face);
        ct.ThrowIfCancellationRequested();

        using var image = Image.Load<Rgb24>(imageBytes.ToArray());
        var region = ClampRegion(face.Region, image.Width, image.Height);
        using var crop = image.Clone(ctx => ctx.Crop(new Rectangle(region.X, region.Y, region.Width, region.Height))
                                              .Resize(_opts.InputSize, _opts.InputSize));

        var tensor = new DenseTensor<float>(new[] { 1, 3, _opts.InputSize, _opts.InputSize });
        crop.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    // ArcFace expects BGR mean-subtracted + scaled. Common: (pixel - 127.5) / 128.0
                    tensor[0, 0, y, x] = (row[x].B - 127.5f) / 128.0f;
                    tensor[0, 1, y, x] = (row[x].G - 127.5f) / 128.0f;
                    tensor[0, 2, y, x] = (row[x].R - 127.5f) / 128.0f;
                }
            }
        });

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results;
        try
        {
            results = await Task.Run(
                () => _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) }),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OnnxFaceEmbedder] inference failed: {ex.Message}");
            return new FaceEmbedding(new float[_opts.Dimension], _opts.Dimension);
        }
        using (results)
        {
            var raw = results.First().AsTensor<float>().ToArray();
            L2Normalise(raw);
            return new FaceEmbedding(raw, raw.Length);
        }
    }

    private static BoundingBox ClampRegion(BoundingBox region, int imageWidth, int imageHeight)
    {
        var x = Math.Clamp(region.X, 0, imageWidth - 1);
        var y = Math.Clamp(region.Y, 0, imageHeight - 1);
        var w = Math.Clamp(region.Width,  1, imageWidth  - x);
        var h = Math.Clamp(region.Height, 1, imageHeight - y);
        return new BoundingBox(x, y, w, h);
    }

    private static void L2Normalise(float[] v)
    {
        double sumSq = 0;
        for (var i = 0; i < v.Length; i++) sumSq += v[i] * v[i];
        var norm = (float)Math.Sqrt(sumSq);
        if (norm < 1e-9f) return;
        for (var i = 0; i < v.Length; i++) v[i] /= norm;
    }

    public void Dispose() => _session.Dispose();
}
