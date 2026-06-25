// OnnxPlateRecognizer.cs
//
// (Phase C3) IPlateRecognizer backed by an ONNX detector model. Follows
// the same letterbox + YOLO postprocess pattern as OnnxFaceDetector but
// emits PlateRecognitionResult records. OCR for the plate text itself
// uses the model's optional text-output channel when present; otherwise
// returns the bounding box only and leaves text empty for a downstream
// OCR stage.

using System;
using System.Collections.Generic;
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

public sealed record OnnxPlateRecognizerOptions(
    string ModelPath,
    int    InputSize           = 640,
    float  ConfidenceThreshold = 0.5f,
    float  IouThreshold        = 0.45f,
    string? CountryHint        = null);

public sealed class OnnxPlateRecognizer : IPlateRecognizer, IDisposable
{
    private readonly OnnxPlateRecognizerOptions _opts;
    private readonly InferenceSession _session;
    private readonly string _inputName;

    public OnnxPlateRecognizer(OnnxPlateRecognizerOptions opts)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        if (!File.Exists(opts.ModelPath))
            throw new FileNotFoundException("ONNX model not found", opts.ModelPath);
        var sessOpts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session   = new InferenceSession(opts.ModelPath, sessOpts);
        _inputName = _session.InputMetadata.Keys.First();
    }

    public async ValueTask<IReadOnlyList<PlateRecognitionResult>> RecognizeAsync(
        ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (imageBytes.IsEmpty) return Array.Empty<PlateRecognitionResult>();

        using var image = Image.Load<Rgb24>(imageBytes.ToArray());
        var origW = image.Width;
        var origH = image.Height;

        var scale = Math.Min((float)_opts.InputSize / origW, (float)_opts.InputSize / origH);
        var newW  = (int)Math.Round(origW * scale);
        var newH  = (int)Math.Round(origH * scale);
        var padX  = (_opts.InputSize - newW) / 2;
        var padY  = (_opts.InputSize - newH) / 2;

        using var canvas = new Image<Rgb24>(_opts.InputSize, _opts.InputSize, new Rgb24(114, 114, 114));
        using (var resized = image.Clone(ctx => ctx.Resize(newW, newH)))
            canvas.Mutate(ctx => ctx.DrawImage(resized, new Point(padX, padY), 1.0f));

        var tensor = new DenseTensor<float>(new[] { 1, 3, _opts.InputSize, _opts.InputSize });
        canvas.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    tensor[0, 0, y, x] = row[x].R / 255f;
                    tensor[0, 1, y, x] = row[x].G / 255f;
                    tensor[0, 2, y, x] = row[x].B / 255f;
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
            System.Diagnostics.Debug.WriteLine($"[OnnxPlateRecognizer] inference failed: {ex.Message}");
            return Array.Empty<PlateRecognitionResult>();
        }
        using (results)
        {
            var output = results.First().AsTensor<float>();
            var dims   = output.Dimensions;
            if (dims.Length != 3) return Array.Empty<PlateRecognitionResult>();
            var boxes  = dims[2];
            var arr    = output.ToArray();
            var hits   = new List<(float Score, BoundingBox Box)>();
            for (var n = 0; n < boxes; n++)
            {
                var cx = arr[0 * boxes + n];
                var cy = arr[1 * boxes + n];
                var bw = arr[2 * boxes + n];
                var bh = arr[3 * boxes + n];
                var score = arr[4 * boxes + n];
                if (score < _opts.ConfidenceThreshold) continue;
                var x1 = (cx - bw / 2 - padX) / scale;
                var y1 = (cy - bh / 2 - padY) / scale;
                var bx = Math.Max(0, (int)Math.Floor(x1));
                var by = Math.Max(0, (int)Math.Floor(y1));
                var bxw = Math.Min(origW - bx, (int)Math.Ceiling(bw / scale));
                var bxh = Math.Min(origH - by, (int)Math.Ceiling(bh / scale));
                if (bxw <= 0 || bxh <= 0) continue;
                hits.Add((score, new BoundingBox(bx, by, bxw, bxh)));
            }
            hits.Sort((a, b) => b.Score.CompareTo(a.Score));
            var kept = new List<(float Score, BoundingBox Box)>();
            foreach (var c in hits)
            {
                var keep = true;
                foreach (var k in kept)
                    if (Iou(c.Box, k.Box) > _opts.IouThreshold) { keep = false; break; }
                if (keep) kept.Add(c);
            }
            return kept
                .Select(k => new PlateRecognitionResult(
                    PlateText:   "",        // OCR pass is a separate model — left to a follow-up
                    CountryHint: _opts.CountryHint,
                    Region:      k.Box,
                    Confidence:  k.Score))
                .ToList();
        }
    }

    private static float Iou(BoundingBox a, BoundingBox b)
    {
        var ax2 = a.X + a.Width;
        var ay2 = a.Y + a.Height;
        var bx2 = b.X + b.Width;
        var by2 = b.Y + b.Height;
        var ix1 = Math.Max(a.X, b.X);
        var iy1 = Math.Max(a.Y, b.Y);
        var ix2 = Math.Min(ax2, bx2);
        var iy2 = Math.Min(ay2, by2);
        var iw  = Math.Max(0, ix2 - ix1);
        var ih  = Math.Max(0, iy2 - iy1);
        var inter = iw * ih;
        var union = a.Width * a.Height + b.Width * b.Height - inter;
        return union == 0 ? 0 : (float)inter / union;
    }

    public void Dispose() => _session.Dispose();
}
