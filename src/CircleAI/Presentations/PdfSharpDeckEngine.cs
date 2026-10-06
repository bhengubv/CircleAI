#nullable enable

// PdfSharpDeckEngine.cs
//
// The concrete IDeckEngine, on PDFsharp-MigraDoc (MIT, pure-managed) — the same
// renderer CircleAI.Documents uses. This is the ONLY file that knows which PDF
// library is in use, so swapping it is a one-class change no caller sees.
//
// FONT REUSE: PDFsharp keeps ONE process-wide GlobalFontSettings.FontResolver.
// CircleAI.Documents already ships an embedded free/OFL font (DejaVu Sans) behind
// an IFontResolver and installs it, exactly once, in PdfSharpDocumentEngine's
// constructor. Rather than embed a second copy of the font here, we simply
// construct that engine once for its install side effect — after which our
// landscape template renders with the very same font.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Documents;
using MigraDoc.Rendering;

namespace CircleAI.Presentations;

/// <summary>Renders a <see cref="Deck"/> to PDF on-device via PDFsharp-MigraDoc.</summary>
public sealed class PdfSharpDeckEngine : IDeckEngine
{
    // Constructing CircleAI.Documents' engine installs its embedded-font resolver
    // into PDFsharp's process-wide GlobalFontSettings (idempotent — it is guarded
    // there so repeated construction sets it exactly once). Doing it in a static
    // initializer means it happens once, the first time this engine is used, and
    // always before any RenderAsync call resolves a font.
    private static readonly PdfSharpDocumentEngine FontPipeline = new();

    /// <summary>Creates the engine and ensures the shared font resolver is installed.</summary>
    public PdfSharpDeckEngine()
    {
        // Touch the shared pipeline so its static initializer runs (installing the
        // font resolver) even if this is the very first CircleAI type constructed.
        _ = FontPipeline;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> AvailableTemplates { get; } = new[] { LandscapeSlideTemplate.Id };

    /// <inheritdoc />
    public ValueTask<DocumentResult> RenderAsync(Deck deck, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ct.ThrowIfCancellationRequested();

        // Rendering is CPU-bound and synchronous in MigraDoc. It is fast for a
        // handful of slides, but on a phone the caller should still invoke this off
        // the UI thread. We return a completed ValueTask rather than fake async.
        var doc = LandscapeSlideTemplate.Build(deck);

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();

        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, closeStream: false);

        var fileName = $"{SafeFileStem(deck.Title)}.pdf";
        return ValueTask.FromResult(DocumentResult.Pdf(ms.ToArray(), fileName));
    }

    /// <summary>A filename-safe stem from a deck title, e.g. "Q3 Results" → "Q3-Results".</summary>
    private static string SafeFileStem(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Deck";

        var sb = new StringBuilder(title.Length);
        var lastWasDash = false;
        foreach (var ch in title.Trim())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); lastWasDash = false; }
            else if (!lastWasDash)        { sb.Append('-'); lastWasDash = true; }
        }

        var stem = sb.ToString().Trim('-');
        return stem.Length == 0 ? "Deck" : stem;
    }
}
