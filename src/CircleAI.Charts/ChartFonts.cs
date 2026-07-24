#nullable enable

// ChartFonts.cs
//
// PDFsharp is pure-managed and has NO access to the operating system's fonts —
// which is what we want on a de-Googled phone. The price is that WE must supply
// the font bytes through an IFontResolver, and PDFsharp keeps exactly ONE
// process-wide resolver (GlobalFontSettings.FontResolver) which it refuses to
// replace once set.
//
// That single global slot is shared with CircleAI.Documents, whose
// PdfSharpDocumentEngine installs its OWN resolver. So the rule this file follows
// is: install a resolver ONLY if none exists yet. When a chart is drawn into a
// report that CircleAI.Documents produced, Documents' resolver is already in
// place and we leave it alone; when charts are used standalone, we install ours.
// Either resolver maps every family name to the same embedded DejaVu face, so
// text renders identically whichever one wins.
//
// Ordering note: if a process uses BOTH libraries, ensure a resolver is installed
// before the OTHER library first sets one — in practice CircleAI.Documents'
// engine is constructed first (you build the report, then embed the chart), so
// this is a non-issue. If charts render first and Documents' engine is
// constructed afterwards, Documents' own set-once guard still installs cleanly
// because we never overwrite an existing resolver.

using System.IO;
using System.Reflection;
using System.Threading;
using PdfSharp.Fonts;

namespace CircleAI.Charts;

/// <summary>
/// Font bootstrap for chart text. The renderer calls
/// <see cref="EnsureDefaultFontResolver"/> before drawing, so callers normally
/// need nothing here; it is public for hosts that want to install the resolver
/// explicitly at startup.
/// </summary>
public static class ChartFonts
{
    /// <summary>
    /// The family name the renderer asks PDFsharp for. Any name resolves to the
    /// embedded DejaVu face, but a stable named family keeps XFont caching happy.
    /// </summary>
    public const string FamilyName = EmbeddedChartFontResolver.FamilyName;

    // We attempt the global install at most once per process, whatever the outcome.
    private static int _attempted;

    /// <summary>
    /// Installs the embedded-font resolver as PDFsharp's global resolver IF one is
    /// not already installed. Idempotent and safe to call from any thread.
    /// </summary>
    /// <returns>
    /// <c>true</c> if this call installed the chart resolver; <c>false</c> if a
    /// resolver was already present (e.g. installed by CircleAI.Documents) and is
    /// being reused.
    /// </returns>
    public static bool EnsureDefaultFontResolver()
    {
        // Fast path: someone already installed a resolver — reuse it, no throw.
        if (GlobalFontSettings.FontResolver is not null)
            return false;

        // Only one thread performs the (single) install attempt.
        if (Interlocked.Exchange(ref _attempted, 1) != 0)
            return false;

        // Re-check under the once-guard: a resolver may have appeared between the
        // fast-path check and here. Setting the global slot to a different
        // resolver after it is in use throws InvalidOperationException by design;
        // that only means another component won the race, so we accept its
        // resolver rather than fail the render.
        try
        {
            if (GlobalFontSettings.FontResolver is null)
                GlobalFontSettings.FontResolver = new EmbeddedChartFontResolver();
            return true;
        }
        catch (System.InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// Serves the embedded DejaVu faces to PDFsharp. Like CircleAI.Documents'
/// resolver it is deliberately family-name-agnostic: any requested family maps to
/// DejaVu, so a chart never fails to find a font whatever family a caller names.
/// </summary>
internal sealed class EmbeddedChartFontResolver : IFontResolver
{
    /// <summary>The family name the chart renderer asks for.</summary>
    public const string FamilyName = "CircleChartSans";

    // Face keys we return from ResolveTypeface and receive back in GetFont.
    private const string RegularFace = "CircleChartSans#regular";
    private const string BoldFace = "CircleChartSans#bold";

    // Embedded resource names = "{RootNamespace}.{folder-with-dots}.{file}".
    // RootNamespace is the project name, CircleAI.Charts.
    private const string RegularResource = "CircleAI.Charts.Assets.Fonts.DejaVuSans.ttf";
    private const string BoldResource = "CircleAI.Charts.Assets.Fonts.DejaVuSans-Bold.ttf";

    private static readonly Assembly Asm = typeof(EmbeddedChartFontResolver).Assembly;

    /// <inheritdoc />
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        // Italic folds onto the upright face (charts do not need an oblique cut);
        // bold is real, for the title.
        => new FontResolverInfo(isBold ? BoldFace : RegularFace);

    /// <inheritdoc />
    public byte[]? GetFont(string faceName)
    {
        var resource = faceName == BoldFace ? BoldResource : RegularResource;

        using var stream = Asm.GetManifestResourceStream(resource)
            ?? throw new System.InvalidOperationException(
                $"Embedded font '{resource}' is missing from the assembly. It should be " +
                "included as an EmbeddedResource in CircleAI.Charts.csproj.");

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
