// MediaTemplates.swift
//
// Three ready-made specs, so a caller who wants a social ad does not have to
// know where a scrim goes.
//
// The numbers here are the composition: which band the headline sits in, how
// far the Ken Burns move travels, when the subline arrives relative to the
// headline. They are the part that is easy to get subtly wrong and impossible
// to notice from code, so they live in one place rather than being retyped at
// each call site.
//
// Ported from src/CircleAI.Media/Rendering/MediaTemplates.cs.

import Foundation

public enum MediaTemplates {

    /// House navy — the same value the rest of the product uses.
    static let defaultBackground = Rgba32(0x0B, 0x1F, 0x3A, 255)
    /// House blue, for accents.
    static let defaultAccent = Rgba32(0x21, 0x96, 0xF3, 255)

    /// A 1x1 solid-colour source, handy as a stretched scrim or colour block.
    ///
    /// One pixel rather than a filled canvas: it is scaled to whatever rectangle
    /// it is placed in, so a full-screen scrim costs four bytes.
    public static func solidColor(_ color: Rgba32) -> ImageSource {
        .raw(rgba: [color.r, color.g, color.b, color.a], width: 1, height: 1)
    }

    /// A short social ad: a full-bleed background (cover-cropped with a slow
    /// Ken Burns move), a legibility scrim, a fading headline and an optional
    /// subline.
    ///
    /// THE SCRIM IS THE POINT. White text over an arbitrary photo is legible or
    /// not depending on the photo, and the one thing a person cannot check
    /// before posting is every frame. A half-height dark band under the text
    /// makes it legible over anything.
    public static func socialAd(
        size: RenderSize,
        background: ImageSource? = nil,
        headline: String,
        subline: String? = nil,
        backgroundColor: Rgba32? = nil,
        textColor: Rgba32? = nil,
        scrimColor: Rgba32? = nil,
        duration: TimeInterval = 6,
        frameRate: Int = 12
    ) -> MediaSpec {
        let bg = backgroundColor ?? defaultBackground
        let col = textColor ?? .white
        let scrim = scrimColor ?? Rgba32(0, 0, 0, 110)

        var images: [ImageLayer] = []
        if let background {
            images.append(ImageLayer(source: background, rect: .full, fit: .cover,
                                     motion: .kenBurns, zOrder: 0, id: "bg"))
        }
        // A fully transparent scrim is not drawn at all rather than composited
        // as a no-op over every frame.
        if scrim.a > 0 {
            images.append(ImageLayer(source: solidColor(scrim),
                                     rect: NormRect(x: 0, y: 0.45, w: 1, h: 0.55),
                                     fit: .fill, zOrder: 5, id: "scrim"))
        }

        var texts = [
            TextOverlay(text: headline,
                        rect: NormRect(x: 0.08, y: 0.55, w: 0.84, h: 0.2),
                        fontHeightFraction: 0.075, color: col, align: .center,
                        motion: .fadeIn, zOrder: 100, id: "headline")
        ]

        if let subline, !subline.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            // Starts AFTER the headline and finishes before it would compete
            // with it: two things fading in together read as one flicker.
            texts.append(TextOverlay(
                text: subline,
                rect: NormRect(x: 0.1, y: 0.77, w: 0.8, h: 0.12),
                fontHeightFraction: 0.04, color: col, align: .center,
                motion: Motion(startFraction: 0.15, endFraction: 0.4,
                               fromOpacity: 0, toOpacity: 1, easing: .easeOut),
                zOrder: 101, id: "subline"))
        }

        return MediaSpec(size: size, background: bg, images: images, texts: texts,
                         duration: duration, frameRate: frameRate)
    }

    /// A video CV card: portrait, name, title, and optional contact line.
    public static func videoCvCard(
        size: RenderSize,
        portrait: ImageSource? = nil,
        name: String,
        title: String,
        contact: String? = nil,
        backgroundColor: Rgba32? = nil,
        textColor: Rgba32? = nil,
        accentColor: Rgba32? = nil,
        duration: TimeInterval = 8,
        frameRate: Int = 12
    ) -> MediaSpec {
        let bg = backgroundColor ?? defaultBackground
        let col = textColor ?? .white
        let accent = accentColor ?? defaultAccent

        var images: [ImageLayer] = []
        if let portrait {
            images.append(ImageLayer(
                source: portrait,
                rect: NormRect(x: 0.3, y: 0.08, w: 0.4, h: 0.34),
                fit: .cover,
                motion: Motion(startFraction: 0, endFraction: 0.2,
                               fromOpacity: 0, toOpacity: 1, easing: .easeOut),
                zOrder: 0, id: "portrait"))
        }

        // Staggered: portrait, then name, then title, then contact. Each starts
        // as the one before is settling, which is what makes it read as one card
        // assembling rather than four things appearing.
        var texts = [
            TextOverlay(text: name,
                        rect: NormRect(x: 0.05, y: 0.46, w: 0.9, h: 0.12),
                        fontHeightFraction: 0.07, color: col, align: .center,
                        motion: .fadeIn, zOrder: 100, id: "name"),
            TextOverlay(text: title,
                        rect: NormRect(x: 0.05, y: 0.59, w: 0.9, h: 0.08),
                        fontHeightFraction: 0.04, color: accent, align: .center,
                        motion: Motion(startFraction: 0.1, endFraction: 0.35,
                                       fromOpacity: 0, toOpacity: 1, easing: .easeOut),
                        zOrder: 101, id: "title"),
        ]

        if let contact, !contact.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            texts.append(TextOverlay(
                text: contact,
                rect: NormRect(x: 0.05, y: 0.83, w: 0.9, h: 0.08),
                fontHeightFraction: 0.032, color: col, align: .center,
                motion: Motion(startFraction: 0.2, endFraction: 0.5,
                               fromOpacity: 0, toOpacity: 1, easing: .easeOut),
                zOrder: 102, id: "contact"))
        }

        return MediaSpec(size: size, background: bg, images: images, texts: texts,
                         duration: duration, frameRate: frameRate)
    }

    /// A scene described as HTML, for the typography the bitmap font cannot do.
    ///
    /// White background, not the house navy: an HTML scene brings its own
    /// styling, and a dark canvas under it shows through every unstyled margin.
    public static func fromHtml(
        size: RenderSize,
        html: String,
        tokens: [String: String]? = nil,
        duration: TimeInterval = 6,
        frameRate: Int = 12,
        background: Rgba32? = nil
    ) -> MediaSpec {
        MediaSpec(size: size, background: background ?? .white,
                  images: [], texts: [], duration: duration, frameRate: frameRate,
                  html: HtmlTemplateSource(html: html, tokens: tokens))
    }
}
