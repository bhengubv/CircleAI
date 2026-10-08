// OnnxFaceDetector.cs
//
// (Phase C3) Real IFaceDetector backed by an ONNX face detection model.
// Designed against YOLOv8-face / YOLOv5-face / RetinaFace family models
// — all share the same boxes+score+landmarks output shape. The model
// path + input dimensions are configurable so callers can plug in any
// trained model.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// Aliased because this file shares one assembly with the MAUI-targeting android
// leg from 3.8.1, and MAUI's implicit usings bring Microsoft.Maui.Controls, Microsoft.Maui.Graphics into scope.

namespace CircleAI.Vision;

/// <param name="ModelPath">Path to a YOLO-family ONNX face-detection model.</param>
/// <param name="InputSize">Square input dimension (640 = YOLOv8 default).</param>
/// <param name="ConfidenceThreshold">Skip detections under this score (0..1).</param>
/// <param name="IouThreshold">NMS IoU cutoff (0..1).</param>
public sealed record OnnxFaceDetectorOptions(
    string ModelPath,
    int    InputSize           = 640,
    float  ConfidenceThreshold = 0.5f,
    float  IouThreshold        = 0.45f);

public sealed class OnnxFaceDetector : IFaceDetector, IDisposable
{
    private readonly OnnxFaceDetectorOptions _opts;
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    public OnnxFaceDetector(OnnxFaceDetectorOptions opts)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        if (!File.Exists(opts.ModelPath))
            throw new FileNotFoundException("ONNX model not found", opts.ModelPath);
        var sessOpts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session    = new InferenceSession(opts.ModelPath, sessOpts);
        _inputName  = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    public async ValueTask<IReadOnlyList<DetectedFace>> DetectAsync(
        ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (imageBytes.IsEmpty) return Array.Empty<DetectedFace>();

        var image = ImageDecoder.Decode(imageBytes.Span);
        var origW = image.Width;
        var origH = image.Height;

        var (resized, padX, padY, scale) = LetterboxResize(image, _opts.InputSize);
        var tensor = ToTensor(resized);

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results;
        try
        {
            results = await Task.Run(
                () => _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) }),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OnnxFaceDetector] inference failed: {ex.Message}");
            return Array.Empty<DetectedFace>();
        }
        using (results)
        {
            var outTensor = results.First().AsTensor<float>();
            return PostprocessYolo(outTensor, origW, origH, padX, padY, scale);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Scales to fit and centres on a grey canvas, preserving aspect ratio.</summary>
    /// <remarks>
    /// 114/114/114 is kept exactly: it is what YOLO letterboxes with, so it is
    /// what the weights were trained to ignore. Black padding would be read as
    /// image content and move the scores.
    ///
    /// The arithmetic is unchanged from the ImageSharp version - same Math.Min
    /// scale, same Math.Round, same integer-divided padding - because PostprocessYolo
    /// below inverts padX/padY/scale to map boxes back to the original frame. A
    /// rounding difference here would shift every box it returns.
    /// </remarks>
    internal static (RasterImage Resized, int PadX, int PadY, float Scale) LetterboxResize(RasterImage image, int inputSize)
    {
        var scale = Math.Min((float)inputSize / image.Width, (float)inputSize / image.Height);
        var newW  = (int)Math.Round(image.Width * scale);
        var newH  = (int)Math.Round(image.Height * scale);
        var padX  = (inputSize - newW) / 2;
        var padY  = (inputSize - newH) / 2;

        var canvas = RasterImage.Filled(inputSize, inputSize, 114, 114, 114);
        canvas.Draw(image.Resize(newW, newH), padX, padY);
        return (canvas, padX, padY, scale);
    }

    /// <summary>NCHW float tensor, channels planar, 0..1.</summary>
    internal static DenseTensor<float> ToTensor(RasterImage image)
    {
        var w = image.Width;
        var h = image.Height;
        var tensor = new DenseTensor<float>(new[] { 1, 3, h, w });

        // One linear walk over the packed buffer instead of a row accessor
        // callback: the pixels are already contiguous RGB, so the index maths is
        // the whole of what ProcessPixelRows was arranging.
        var px = image.Pixels;
        for (var y = 0; y < h; y++)
        {
            var o = y * w * RasterImage.Channels;
            for (var x = 0; x < w; x++, o += RasterImage.Channels)
            {
                tensor[0, 0, y, x] = px[o]     / 255f;
                tensor[0, 1, y, x] = px[o + 1] / 255f;
                tensor[0, 2, y, x] = px[o + 2] / 255f;
            }
        }
        return tensor;
    }

    /// <summary>(Phase C3) YOLOv8 output layout: [1, 4+1+K, N] where K is class count.
    /// For face models K = 1 (face). For YOLOv8-face with landmarks: [1, 4+1+10+1, N].
    /// We read first 5 channels per box (cx, cy, w, h, score) — enough to derive boxes.</summary>
    private List<DetectedFace> PostprocessYolo(Tensor<float> output, int origW, int origH, int padX, int padY, float scale)
    {
        var dims = output.Dimensions;
        if (dims.Length != 3) return new List<DetectedFace>();
        int channels = dims[1], boxes = dims[2];

        var candidates = new List<(float Score, BoundingBox Box)>();
        var arr = output.ToArray();
        // arr is laid out [batch, channel, box] flattened. Index = c*boxes + n.
        for (var n = 0; n < boxes; n++)
        {
            var cx    = arr[0 * boxes + n];
            var cy    = arr[1 * boxes + n];
            var bw    = arr[2 * boxes + n];
            var bh    = arr[3 * boxes + n];
            var score = arr[4 * boxes + n];
            if (score < _opts.ConfidenceThreshold) continue;

            // Convert back from letterbox space to original pixel space.
            var x1 = (cx - bw / 2 - padX) / scale;
            var y1 = (cy - bh / 2 - padY) / scale;
            var x2 = (cx + bw / 2 - padX) / scale;
            var y2 = (cy + bh / 2 - padY) / scale;
            var bx = Math.Max(0, (int)Math.Floor(x1));
            var by = Math.Max(0, (int)Math.Floor(y1));
            var bxw = Math.Min(origW - bx, (int)Math.Ceiling(x2 - x1));
            var bxh = Math.Min(origH - by, (int)Math.Ceiling(y2 - y1));
            if (bxw <= 0 || bxh <= 0) continue;
            candidates.Add((score, new BoundingBox(bx, by, bxw, bxh)));
        }

        var kept = NonMaxSuppression(candidates, _opts.IouThreshold);
        return kept.Select(c => new DetectedFace(c.Box, c.Score, null)).ToList();
    }

    private static List<(float Score, BoundingBox Box)> NonMaxSuppression(
        List<(float Score, BoundingBox Box)> boxes, float iouThreshold)
    {
        boxes.Sort((a, b) => b.Score.CompareTo(a.Score));
        var kept = new List<(float Score, BoundingBox Box)>();
        foreach (var cand in boxes)
        {
            var keep = true;
            foreach (var k in kept)
                if (Iou(cand.Box, k.Box) > iouThreshold) { keep = false; break; }
            if (keep) kept.Add(cand);
        }
        return kept;
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
