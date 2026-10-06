// MediaSpec.cs
//
// (Rendering 1.0) The declarative model for programmatic — NOT generative —
// media. A MediaSpec describes a canvas: a background, a stack of image
// layers (the user's OWN photos), text overlays, and a timeline. It is pure
// data with no rendering dependency, so hosting layers can build, serialise,
// or template it freely.
//
// Design lineage:
//   * html-video  — an animated single-file scene captured to frames.
//   * ASCILINE    — ship composed frames, not a hardware codec.
// On-device (de-Googled, low-end Android) the honest split is: this library
// composes stills + a frame sequence in pure managed code; a real H.264/MP4
// muxer is a documented seam (see IVideoEncoder / NullVideoEncoder).

using System;
using System.Collections.Generic;

namespace CircleAI.Media.Rendering;

/// <summary>Straight-alpha 32-bit colour (R,G,B,A), 0-255 per channel.</summary>
public readonly record struct Rgba32(byte R, byte G, byte B, byte A)
{
    public static Rgba32 Transparent => new(0, 0, 0, 0);
    public static Rgba32 Black => new(0, 0, 0, 255);
    public static Rgba32 White => new(255, 255, 255, 255);

    /// <summary>Opaque colour from R,G,B.</summary>
    public static Rgba32 FromRgb(byte r, byte g, byte b) => new(r, g, b, 255);

    /// <summary>Same colour with a replaced alpha channel.</summary>
    public Rgba32 WithAlpha(byte a) => new(R, G, B, a);

    /// <summary>Parse "#RGB", "#RRGGBB" or "#RRGGBBAA" (leading '#' optional).</summary>
    public static Rgba32 FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var s = hex.StartsWith('#') ? hex[1..] : hex;
        switch (s.Length)
        {
            case 3:
                return new Rgba32(Dup(s[0]), Dup(s[1]), Dup(s[2]), 255);
            case 6:
                return new Rgba32(Hex2(s, 0), Hex2(s, 2), Hex2(s, 4), 255);
            case 8:
                return new Rgba32(Hex2(s, 0), Hex2(s, 2), Hex2(s, 4), Hex2(s, 6));
            default:
                throw new FormatException($"Unrecognised colour '{hex}'. Use #RGB, #RRGGBB or #RRGGBBAA.");
        }

        static byte Dup(char c) { var v = Nib(c); return (byte)((v << 4) | v); }
        static byte Hex2(string src, int i) => (byte)((Nib(src[i]) << 4) | Nib(src[i + 1]));
        static int Nib(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => throw new FormatException($"Invalid hex digit '{c}'.")
        };
    }
}

/// <summary>Output pixel dimensions for the whole canvas.</summary>
public readonly record struct RenderSize(int Width, int Height)
{
    /// <summary>1080x1080 — feed/square ad.</summary>
    public static RenderSize Square1080 => new(1080, 1080);
    /// <summary>1080x1920 — story/reel/vertical CV.</summary>
    public static RenderSize Portrait1080x1920 => new(1080, 1920);
    /// <summary>1920x1080 — landscape.</summary>
    public static RenderSize Landscape1920x1080 => new(1920, 1080);
    /// <summary>540x960 — light preview default for low-end devices.</summary>
    public static RenderSize Preview540x960 => new(540, 960);

    public long PixelCount => (long)Width * Height;
}

/// <summary>A rectangle in normalised 0..1 canvas coordinates (origin top-left).</summary>
public readonly record struct NormRect(double X, double Y, double W, double H)
{
    /// <summary>The whole canvas.</summary>
    public static NormRect Full => new(0, 0, 1, 1);
}

/// <summary>A 2D vector in normalised canvas units (fraction of width/height).</summary>
public readonly record struct NormVec(double X, double Y);

/// <summary>How a source image is fitted into its target rectangle.</summary>
public enum ContentFit
{
    /// <summary>Stretch to fill exactly (may distort).</summary>
    Fill,
    /// <summary>Scale to fit inside, preserving aspect (letterbox).</summary>
    Contain,
    /// <summary>Scale to cover, preserving aspect (centre-crop). Default for full-bleed backgrounds.</summary>
    Cover
}

/// <summary>Horizontal text alignment inside the overlay rectangle.</summary>
public enum TextAlign { Left, Center, Right }

/// <summary>Interpolation curve for a Motion track.</summary>
public enum EasingKind { Linear, EaseIn, EaseOut, EaseInOut }

/// <summary>
/// A single animation track applied to one layer across the clip timeline.
/// All values interpolate from the "From*" to the "To*" endpoint between
/// StartFraction and EndFraction (0..1 of the clip). Opacity, uniform scale
/// (about the layer centre) and a normalised translate combine to give
/// fades, zooms and Ken-Burns pans.
/// </summary>
public sealed record Motion(
    double StartFraction = 0.0,
    double EndFraction = 1.0,
    double FromOpacity = 1.0,
    double ToOpacity = 1.0,
    double FromScale = 1.0,
    double ToScale = 1.0,
    NormVec FromTranslate = default,
    NormVec ToTranslate = default,
    EasingKind Easing = EasingKind.Linear)
{
    /// <summary>No movement.</summary>
    public static Motion None => new();

    /// <summary>Fade opacity 0 -> 1 over the first quarter of the clip.</summary>
    public static Motion FadeIn => new(FromOpacity: 0.0, ToOpacity: 1.0, StartFraction: 0.0, EndFraction: 0.25, Easing: EasingKind.EaseOut);

    /// <summary>Fade opacity 1 -> 0 over the last quarter of the clip.</summary>
    public static Motion FadeOut => new(FromOpacity: 1.0, ToOpacity: 0.0, StartFraction: 0.75, EndFraction: 1.0, Easing: EasingKind.EaseIn);

    /// <summary>Slow zoom + drift — the classic documentary "Ken Burns" move.</summary>
    public static Motion KenBurns => new(FromScale: 1.0, ToScale: 1.12, ToTranslate: new NormVec(0.03, 0.02), Easing: EasingKind.EaseInOut);
}

/// <summary>Where a layer's pixels come from. Either raw RGBA or encoded bytes decoded via IImageDecoder.</summary>
public abstract record ImageSource;

/// <summary>Pre-decoded, tightly-packed RGBA (length = Width*Height*4). Always renderable with zero deps.</summary>
public sealed record RawImageSource(byte[] Rgba, int Width, int Height) : ImageSource;

/// <summary>Encoded image bytes (PNG/BMP handled in-managed; JPEG via a platform IImageDecoder).</summary>
public sealed record EncodedImageSource(ReadOnlyMemory<byte> Bytes, string? MimeHint = null) : ImageSource;

/// <summary>One image layer — the user's own photo/logo placed on the canvas.</summary>
public sealed record ImageLayer(
    ImageSource Source,
    NormRect Rect,
    ContentFit Fit = ContentFit.Cover,
    double Opacity = 1.0,
    Motion? Motion = null,
    int ZOrder = 0,
    string? Id = null);

/// <summary>
/// One text overlay. Rendered with the built-in bitmap headline font (real,
/// offline, no font dependency). Rich typography/emoji is delegated to the
/// HTML/WebView seam (IHtmlFrameProvider).
/// </summary>
public sealed record TextOverlay(
    string Text,
    NormRect Rect,
    double FontHeightFraction = 0.08,
    Rgba32 Color = default,
    TextAlign Align = TextAlign.Center,
    Rgba32 BoxColor = default,
    double LetterSpacingFraction = 0.2,
    double LineSpacingFraction = 0.35,
    Motion? Motion = null,
    int ZOrder = 100,
    string? Id = null);

/// <summary>
/// Raw HTML for the WebView-capture seam. A pure-managed library cannot lay
/// out arbitrary HTML/CSS (that needs a browser engine); on-device the MAUI
/// host renders this in a WebView and captures frames via IHtmlFrameProvider.
/// Tokens are substituted as {{key}} before hand-off.
/// </summary>
public sealed record HtmlTemplateSource(string Html, IReadOnlyDictionary<string, string>? Tokens = null);

/// <summary>
/// The complete declarative description of a still or short clip:
/// size, background, image layers, text overlays, and a timeline.
/// </summary>
public sealed record MediaSpec(
    RenderSize Size,
    Rgba32 Background,
    IReadOnlyList<ImageLayer> Images,
    IReadOnlyList<TextOverlay> Texts,
    TimeSpan Duration,
    int FrameRate = 12,
    HtmlTemplateSource? Html = null)
{
    /// <summary>True when this is a single still (non-positive duration).</summary>
    public bool IsStill => Duration <= TimeSpan.Zero;

    /// <summary>Number of frames the timeline yields (1 for a still).</summary>
    public int FrameCount => IsStill
        ? 1
        : Math.Max(1, (int)Math.Round(Duration.TotalSeconds * Math.Max(1, FrameRate), MidpointRounding.AwayFromZero));

    /// <summary>Build a single-still spec.</summary>
    public static MediaSpec Still(
        RenderSize size,
        Rgba32 background,
        IReadOnlyList<ImageLayer>? images = null,
        IReadOnlyList<TextOverlay>? texts = null)
        => new(size, background, images ?? Array.Empty<ImageLayer>(), texts ?? Array.Empty<TextOverlay>(), TimeSpan.Zero, 1);

    /// <summary>Substitute {{key}} tokens in a template string.</summary>
    public static string ApplyTokens(string template, IReadOnlyDictionary<string, string>? tokens)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (tokens is null || tokens.Count == 0) return template;
        var sb = new System.Text.StringBuilder(template);
        foreach (var kv in tokens)
            sb.Replace("{{" + kv.Key + "}}", kv.Value ?? string.Empty);
        return sb.ToString();
    }
}

/// <summary>
/// A mutable RGBA32 raster surface — the target of all compositing.
/// Pixels are straight-alpha, row-major, 4 bytes/pixel (R,G,B,A).
/// </summary>
public sealed class PixelBuffer
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major RGBA bytes, length = Width*Height*4.</summary>
    public byte[] Pixels { get; }

    /// <summary>Bytes per row.</summary>
    public int Stride => Width * 4;

    public PixelBuffer(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 4)];
    }

    public PixelBuffer(int width, int height, byte[] pixels)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != checked(width * height * 4))
            throw new ArgumentException("pixels length must equal width*height*4.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    // Unused helper kept intentionally small; formatting kept explicit for clarity.
    internal int Index(int x, int y) => (y * Width + x) * 4;
}
