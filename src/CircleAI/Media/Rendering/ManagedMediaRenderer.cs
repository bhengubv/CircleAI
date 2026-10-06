// ManagedMediaRenderer.cs
//
// (Rendering 1.0) The genuine, pure-managed IMediaRenderer. It decodes the
// user's images, composites them (aspect-fitted, alpha-blended) under text
// overlays, and walks the timeline evaluating each layer's Motion track to
// produce a frame sequence — fades, zooms, Ken-Burns pans. Clip muxing is
// delegated to an injected IVideoEncoder.
//
// The declarative path (layers + text) is fully real and offline. The HTML
// path is a seam: when a spec carries Html AND a non-null IHtmlFrameProvider
// is supplied, RenderClipAsync hands frame production to that provider (the
// on-device WebView-capture analogue of html-video). Without a provider the
// HTML is ignored and only the declarative layers render.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Media.Rendering;

/// <summary>Composes a <see cref="MediaSpec"/> into stills and frame sequences, pure-managed.</summary>
public sealed class ManagedMediaRenderer : IMediaRenderer
{
    private readonly IImageDecoder _decoder;
    private readonly IHtmlFrameProvider? _html;
    private readonly BitmapFont _font;

    public string BackendId => "managed";

    public ManagedMediaRenderer(
        IImageDecoder? decoder = null,
        IHtmlFrameProvider? htmlFrameProvider = null,
        BitmapFont? font = null)
    {
        _decoder = decoder ?? ManagedImageDecoder.Instance;
        _html = htmlFrameProvider;
        _font = font ?? BitmapFont.Default;
    }

    /// <summary>Compose a single still from the declarative layers (HTML posters use RenderClipAsync/provider).</summary>
    public PixelBuffer RenderStill(MediaSpec spec, double posterFraction = 0.0)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var decoded = DecodeLayers(spec);
        return Compose(spec, Math.Clamp(posterFraction, 0.0, 1.0), decoded);
    }

    public IEnumerable<PixelBuffer> EnumerateFrames(MediaSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Frames(spec);
    }

    public async ValueTask<EncodedClip> RenderClipAsync(MediaSpec spec, IVideoEncoder encoder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(encoder);

        var options = new ClipEncodeOptions(spec.Size, Math.Max(1, spec.FrameRate), spec.FrameCount);

        if (spec.Html is not null && _html is not null && !string.Equals(_html.BackendId, "null", StringComparison.Ordinal))
        {
            var frames = await _html
                .RenderHtmlFramesAsync(WithTokens(spec.Html), spec.Size, spec.FrameCount, Math.Max(1, spec.FrameRate), ct)
                .ConfigureAwait(false);
            return await encoder.EncodeAsync(frames, options, ct).ConfigureAwait(false);
        }

        return await encoder.EncodeAsync(EnumerateFrames(spec), options, ct).ConfigureAwait(false);
    }

    // ---- internals -------------------------------------------------------

    private IEnumerable<PixelBuffer> Frames(MediaSpec spec)
    {
        int n = spec.FrameCount;
        var decoded = DecodeLayers(spec);
        for (int i = 0; i < n; i++)
        {
            double g = n <= 1 ? 0.0 : (double)i / (n - 1);
            yield return Compose(spec, g, decoded);
        }
    }

    private (ImageLayer layer, PixelBuffer pixels)[] DecodeLayers(MediaSpec spec)
    {
        var result = new (ImageLayer, PixelBuffer)[spec.Images.Count];
        for (int i = 0; i < spec.Images.Count; i++)
        {
            var layer = spec.Images[i];
            PixelBuffer px = layer.Source switch
            {
                RawImageSource raw => new PixelBuffer(raw.Width, raw.Height, raw.Rgba),
                EncodedImageSource enc => _decoder.Decode(enc.Bytes, enc.MimeHint),
                _ => throw new NotSupportedException($"Unknown image source '{layer.Source.GetType().Name}'.")
            };
            result[i] = (layer, px);
        }
        return result;
    }

    private PixelBuffer Compose(MediaSpec spec, double g, (ImageLayer layer, PixelBuffer pixels)[] decoded)
    {
        var canvas = new RasterCanvas(spec.Size.Width, spec.Size.Height);
        canvas.Clear(spec.Background);

        foreach (var (layer, pixels) in OrderImages(decoded))
        {
            var (op, scale, tr) = Eval(layer.Motion, g);
            double opacity = layer.Opacity * op;
            if (opacity <= 0) continue;
            var (dx, dy, dw, dh) = PlaceRect(layer.Rect, spec.Size, scale, tr);
            canvas.DrawImage(pixels, dx, dy, dw, dh, layer.Fit, opacity);
        }

        foreach (var overlay in OrderText(spec.Texts))
        {
            if (string.IsNullOrEmpty(overlay.Text)) continue;
            var (op, _, tr) = Eval(overlay.Motion, g);
            if (op <= 0) continue;
            var (rx, ry, rw, rh) = PlaceRect(overlay.Rect, spec.Size, 1.0, tr);
            var color = overlay.Color.A == 0 ? Rgba32.White : overlay.Color;
            int fontPx = Math.Max(BitmapFont.Rows, (int)Math.Round(overlay.FontHeightFraction * spec.Size.Height));
            canvas.DrawText(
                _font, overlay.Text,
                (int)Math.Round(rx), (int)Math.Round(ry), (int)Math.Round(rw), (int)Math.Round(rh),
                fontPx, color, overlay.Align, overlay.BoxColor,
                overlay.LetterSpacingFraction, overlay.LineSpacingFraction, op);
        }

        return canvas.Buffer;
    }

    private static List<(ImageLayer layer, PixelBuffer pixels)> OrderImages((ImageLayer layer, PixelBuffer pixels)[] items)
    {
        var copy = new List<(ImageLayer layer, PixelBuffer pixels)>(items);
        copy.Sort(static (a, b) => a.layer.ZOrder.CompareTo(b.layer.ZOrder));
        return copy;
    }

    private static List<TextOverlay> OrderText(IReadOnlyList<TextOverlay> texts)
    {
        var copy = new List<TextOverlay>(texts);
        copy.Sort(static (a, b) => a.ZOrder.CompareTo(b.ZOrder));
        return copy;
    }

    private static HtmlTemplateSource WithTokens(HtmlTemplateSource html)
        => html.Tokens is { Count: > 0 }
            ? html with { Html = MediaSpec.ApplyTokens(html.Html, html.Tokens), Tokens = null }
            : html;

    private static (double x, double y, double w, double h) PlaceRect(NormRect rect, RenderSize size, double scale, NormVec translate)
    {
        double x = rect.X * size.Width;
        double y = rect.Y * size.Height;
        double w = rect.W * size.Width;
        double h = rect.H * size.Height;

        double cx = x + w / 2.0, cy = y + h / 2.0;
        w *= scale; h *= scale;
        x = cx - w / 2.0; y = cy - h / 2.0;

        x += translate.X * size.Width;
        y += translate.Y * size.Height;
        return (x, y, w, h);
    }

    private static (double opacity, double scale, NormVec translate) Eval(Motion? m, double g)
    {
        if (m is null) return (1.0, 1.0, default);
        double span = m.EndFraction - m.StartFraction;
        double local = span <= 0.0
            ? (g >= m.EndFraction ? 1.0 : 0.0)
            : Math.Clamp((g - m.StartFraction) / span, 0.0, 1.0);
        double e = Ease(m.Easing, local);
        return (
            Lerp(m.FromOpacity, m.ToOpacity, e),
            Lerp(m.FromScale, m.ToScale, e),
            new NormVec(Lerp(m.FromTranslate.X, m.ToTranslate.X, e), Lerp(m.FromTranslate.Y, m.ToTranslate.Y, e)));
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double Ease(EasingKind kind, double t) => kind switch
    {
        EasingKind.EaseIn => t * t,
        EasingKind.EaseOut => 1.0 - (1.0 - t) * (1.0 - t),
        EasingKind.EaseInOut => t * t * (3.0 - 2.0 * t),
        _ => t
    };
}
