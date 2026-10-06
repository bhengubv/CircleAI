// MediaTemplates.cs
//
// (Rendering 1.0) The ergonomic front door: turn "a template + the user's own
// images + text" into a ready MediaSpec. These are declarative, fully-managed
// templates (they render offline with zero deps). FromHtml wraps raw HTML for
// the WebView-capture seam instead.

using System;
using System.Collections.Generic;

namespace CircleAI.Media.Rendering;

/// <summary>Built-in declarative templates for common programmatic-media jobs.</summary>
public static class MediaTemplates
{
    /// <summary>A 1x1 solid-colour source, handy as a stretched scrim or colour block.</summary>
    public static ImageSource SolidColor(Rgba32 color)
        => new RawImageSource(new byte[] { color.R, color.G, color.B, color.A }, 1, 1);

    /// <summary>
    /// A short social ad: a full-bleed background image (cover-cropped with a
    /// slow Ken-Burns move), a legibility scrim, a fading-in headline and an
    /// optional subline. Defaults to a 6s clip.
    /// </summary>
    public static MediaSpec SocialAd(
        RenderSize size,
        ImageSource? background,
        string headline,
        string? subline = null,
        Rgba32? backgroundColor = null,
        Rgba32? textColor = null,
        Rgba32? scrimColor = null,
        TimeSpan? duration = null,
        int frameRate = 12)
    {
        ArgumentNullException.ThrowIfNull(headline);

        var bg = backgroundColor ?? Rgba32.FromHex("#0B1F3A");
        var col = textColor ?? Rgba32.White;
        var scrim = scrimColor ?? new Rgba32(0, 0, 0, 110);

        var images = new List<ImageLayer>();
        if (background is not null)
            images.Add(new ImageLayer(background, NormRect.Full, ContentFit.Cover, Motion: Motion.KenBurns, ZOrder: 0, Id: "bg"));
        if (scrim.A > 0)
            images.Add(new ImageLayer(SolidColor(scrim), new NormRect(0, 0.45, 1, 0.55), ContentFit.Fill, ZOrder: 5, Id: "scrim"));

        var texts = new List<TextOverlay>
        {
            new(headline, new NormRect(0.08, 0.55, 0.84, 0.2),
                FontHeightFraction: 0.075, Color: col, Align: TextAlign.Center,
                Motion: Motion.FadeIn, ZOrder: 100, Id: "headline")
        };
        if (!string.IsNullOrWhiteSpace(subline))
            texts.Add(new TextOverlay(subline!, new NormRect(0.1, 0.77, 0.8, 0.12),
                FontHeightFraction: 0.04, Color: col, Align: TextAlign.Center,
                Motion: new Motion(FromOpacity: 0, ToOpacity: 1, StartFraction: 0.15, EndFraction: 0.4, Easing: EasingKind.EaseOut),
                ZOrder: 101, Id: "subline"));

        return new MediaSpec(size, bg, images, texts, duration ?? TimeSpan.FromSeconds(6), frameRate);
    }

    /// <summary>
    /// A video-CV title card: portrait photo, name, role, and optional contact
    /// line, each easing in. Defaults to an 8s clip.
    /// </summary>
    public static MediaSpec VideoCvCard(
        RenderSize size,
        ImageSource? portrait,
        string name,
        string title,
        string? contact = null,
        Rgba32? backgroundColor = null,
        Rgba32? textColor = null,
        Rgba32? accentColor = null,
        TimeSpan? duration = null,
        int frameRate = 12)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(title);

        var bg = backgroundColor ?? Rgba32.FromHex("#0B1F3A");
        var col = textColor ?? Rgba32.White;
        var accent = accentColor ?? Rgba32.FromHex("#2196F3");

        var images = new List<ImageLayer>();
        if (portrait is not null)
            images.Add(new ImageLayer(portrait, new NormRect(0.3, 0.08, 0.4, 0.34), ContentFit.Cover,
                Motion: new Motion(FromOpacity: 0, ToOpacity: 1, EndFraction: 0.2, Easing: EasingKind.EaseOut),
                ZOrder: 0, Id: "portrait"));

        var texts = new List<TextOverlay>
        {
            new(name, new NormRect(0.05, 0.46, 0.9, 0.12),
                FontHeightFraction: 0.07, Color: col, Align: TextAlign.Center,
                Motion: Motion.FadeIn, ZOrder: 100, Id: "name"),
            new(title, new NormRect(0.05, 0.59, 0.9, 0.08),
                FontHeightFraction: 0.04, Color: accent, Align: TextAlign.Center,
                Motion: new Motion(FromOpacity: 0, ToOpacity: 1, StartFraction: 0.1, EndFraction: 0.35, Easing: EasingKind.EaseOut),
                ZOrder: 101, Id: "title")
        };
        if (!string.IsNullOrWhiteSpace(contact))
            texts.Add(new TextOverlay(contact!, new NormRect(0.05, 0.83, 0.9, 0.08),
                FontHeightFraction: 0.032, Color: col, Align: TextAlign.Center,
                Motion: new Motion(FromOpacity: 0, ToOpacity: 1, StartFraction: 0.2, EndFraction: 0.5, Easing: EasingKind.EaseOut),
                ZOrder: 102, Id: "contact"));

        return new MediaSpec(size, bg, images, texts, duration ?? TimeSpan.FromSeconds(8), frameRate);
    }

    /// <summary>Wrap raw HTML for the WebView-capture seam (tokens applied at hand-off).</summary>
    public static MediaSpec FromHtml(
        RenderSize size,
        string html,
        IReadOnlyDictionary<string, string>? tokens = null,
        TimeSpan? duration = null,
        int frameRate = 12,
        Rgba32? background = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        return new MediaSpec(
            size, background ?? Rgba32.White,
            Array.Empty<ImageLayer>(), Array.Empty<TextOverlay>(),
            duration ?? TimeSpan.FromSeconds(6), frameRate,
            new HtmlTemplateSource(html, tokens));
    }
}
