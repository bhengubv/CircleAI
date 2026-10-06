#nullable enable

// EmbeddedFontResolver.cs
//
// PDFsharp 6.x is pure-managed and has NO access to the operating system's fonts
// — which is exactly what we want on a de-Googled phone (no dependency on
// whatever EMUI happens to ship). The price is that WE must supply the font
// bytes, via an IFontResolver backed by a font embedded in this assembly.
//
// The embedded font must be free/OFL (see fully-free-opensource-always) — e.g.
// DejaVu Sans, which has strong Latin + diacritic coverage for English,
// Afrikaans, isiZulu, Sesotho and the other SA languages a CV is written in.

using System;
using System.IO;
using System.Reflection;
using PdfSharp.Fonts;

namespace CircleAI.Documents;

/// <summary>Serves an embedded free/OFL font to PDFsharp, so rendering needs no system fonts.</summary>
internal sealed class EmbeddedFontResolver : IFontResolver
{
    /// <summary>The single family name templates ask for. Mapped to the embedded faces.</summary>
    public const string FamilyName = "CircleSans";

    // Face keys we hand back from ResolveTypeface and receive again in GetFont.
    private const string RegularFace = "CircleSans#regular";
    private const string BoldFace    = "CircleSans#bold";

    // Embedded resource names = "{RootNamespace}.{folder}.{file}" with '/' → '.'.
    private const string RegularResource = "CircleAI.Documents.Assets.Fonts.DejaVuSans.ttf";
    private const string BoldResource    = "CircleAI.Documents.Assets.Fonts.DejaVuSans-Bold.ttf";

    private static readonly Assembly Asm = typeof(EmbeddedFontResolver).Assembly;

    /// <inheritdoc />
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        // Italic is intentionally folded onto the upright face: a CV rarely needs
        // italics, and DejaVu's oblique is not worth a third embedded file. Bold
        // is real, because headings and names use it.
        => new FontResolverInfo(isBold ? BoldFace : RegularFace);

    /// <inheritdoc />
    public byte[]? GetFont(string faceName)
    {
        var resource = faceName == BoldFace ? BoldResource : RegularResource;

        using var stream = Asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Embedded font '{resource}' is not in the assembly. Drop a free/OFL font " +
                "(e.g. DejaVu Sans) into src/CircleAI.Documents/Assets/Fonts/ and uncomment the " +
                "EmbeddedResource entries in CircleAI.Documents.csproj before rendering.");

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
