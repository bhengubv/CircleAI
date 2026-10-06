#nullable enable

// PdfSharpDocumentEngine.cs
//
// The concrete IDocumentEngine, on PDFsharp-MigraDoc (MIT, pure-managed). This
// is the ONLY file that knows which PDF library is in use — swapping it is a
// one-class change no caller sees, which is why the licence choice never blocked
// anything built against IDocumentEngine.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace CircleAI.Documents;

/// <summary>Renders documents to PDF on-device via PDFsharp-MigraDoc.</summary>
public sealed class PdfSharpDocumentEngine : IDocumentEngine
{
    // PDFsharp keeps ONE process-wide GlobalFontSettings.FontResolver and throws
    // if it is set twice. Guard so multiple engine instances (or repeated
    // construction) set it exactly once.
    private static int _fontResolverInstalled;

    public PdfSharpDocumentEngine()
    {
        if (Interlocked.Exchange(ref _fontResolverInstalled, 1) == 0)
            GlobalFontSettings.FontResolver = new EmbeddedFontResolver();
    }

    /// <inheritdoc />
    // One template per kind for v1. The list advertises every id a host can offer;
    // the render path picks the template by document kind (see RenderAsync), so a
    // request's TemplateId is not needed to disambiguate while each kind has one.
    public IReadOnlyList<string> AvailableTemplates { get; } = new[]
    {
        SingleColumnCvTemplate.Id,
        ClassicCoverLetterTemplate.Id,
        ClassicInvoiceTemplate.Id,
        ClassicReportTemplate.Id,
    };

    /// <inheritdoc />
    public ValueTask<DocumentResult> RenderAsync(DocumentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        // Rendering is CPU-bound and synchronous in MigraDoc. It is fast for a
        // 1–2 page CV, but on a phone the caller should still invoke this off the
        // UI thread. We return a completed ValueTask rather than fake async.
        var result = request.Kind switch
        {
            DocumentKind.Cv          => RenderCv(Expect<CvDocument>(request)),
            DocumentKind.CoverLetter => RenderCoverLetter(Expect<CoverLetter>(request)),
            DocumentKind.Invoice     => RenderInvoice(Expect<Invoice>(request)),
            DocumentKind.Report      => RenderReport(Expect<ReportDocument>(request)),
            _ => throw new NotSupportedException(
                $"Document kind '{request.Kind}' is not implemented yet — CVs, cover letters, " +
                "invoices and reports render today."),
        };

        return ValueTask.FromResult(result);
    }

    private static DocumentResult RenderCv(CvDocument cv)
    {
        var doc = SingleColumnCvTemplate.Build(cv);
        var bytes = Render(doc);
        var fileName = $"{SafeFileStem(cv.FullName)}-CV.pdf";
        return DocumentResult.Pdf(bytes, fileName);
    }

    private static DocumentResult RenderCoverLetter(CoverLetter letter)
    {
        var doc = ClassicCoverLetterTemplate.Build(letter);
        var bytes = Render(doc);
        var fileName = $"{SafeFileStem(letter.SenderName)}-Cover-Letter.pdf";
        return DocumentResult.Pdf(bytes, fileName);
    }

    private static DocumentResult RenderInvoice(Invoice invoice)
    {
        var doc = ClassicInvoiceTemplate.Build(invoice);
        var bytes = Render(doc);
        // "Invoice-INV-2026-014.pdf" — the number is the human's filing handle.
        var fileName = $"Invoice-{SafeFileStem(invoice.InvoiceNumber)}.pdf";
        return DocumentResult.Pdf(bytes, fileName);
    }

    private static DocumentResult RenderReport(ReportDocument report)
    {
        var doc = ClassicReportTemplate.Build(report);
        var bytes = Render(doc);
        // "Report-Township-Wi-Fi-Pilot-...pdf" — the title is the human's filing handle.
        var fileName = $"Report-{SafeFileStem(report.Title)}.pdf";
        return DocumentResult.Pdf(bytes, fileName);
    }

    /// <summary>Renders a built MigraDoc document to PDF bytes. Shared by every kind.</summary>
    private static byte[] Render(MigraDoc.DocumentObjectModel.Document doc)
    {
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();

        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    /// <summary>Model must match Kind — a clear throw beats a garbled document.</summary>
    private static T Expect<T>(DocumentRequest request) where T : class
        => request.Model as T
           ?? throw new ArgumentException(
               $"Document kind '{request.Kind}' requires a {typeof(T).Name} model, " +
               $"but got '{request.Model?.GetType().Name ?? "null"}'.",
               nameof(request));

    /// <summary>A filename-safe stem from a person's name, e.g. "Thabo Mokoena" → "Thabo-Mokoena".</summary>
    private static string SafeFileStem(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "CV";

        var sb = new StringBuilder(name.Length);
        var lastWasDash = false;
        foreach (var ch in name.Trim())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); lastWasDash = false; }
            else if (!lastWasDash) { sb.Append('-'); lastWasDash = true; }
        }

        var stem = sb.ToString().Trim('-');
        return stem.Length == 0 ? "CV" : stem;
    }
}
