#nullable enable

// DocumentContracts.cs
//
// The renderer-agnostic vocabulary of the document engine. Nothing here knows
// whether the bytes are produced by PDFsharp, QuestPDF, or a future HTML path —
// that is the whole point of the seam.

namespace CircleAI.Documents;

/// <summary>What kind of document is being produced.</summary>
/// <remarks>
/// Every kind rides the SAME <see cref="IDocumentEngine"/> — a CV and an invoice
/// differ only in their model + template, not in the pipeline. New kinds are
/// added here as the document line grows (ROADMAP.md Phase 1).
/// </remarks>
public enum DocumentKind
{
    /// <summary>A curriculum vitae / resume. The confirmed floor.</summary>
    Cv = 0,

    /// <summary>A cover letter (same engine, different template).</summary>
    CoverLetter,

    /// <summary>An invoice.</summary>
    Invoice,

    /// <summary>A report.</summary>
    Report,
}

/// <summary>Output container format.</summary>
/// <remarks>
/// PDF only for v1 — it is the format a person can open and send from a phone.
/// The enum exists so DOCX/HTML can be added later without changing the seam.
/// </remarks>
public enum DocumentFormat
{
    /// <summary>Portable Document Format.</summary>
    Pdf = 0,
}

/// <summary>
/// A request to render one document.
/// </summary>
/// <param name="Kind">Which document — determines which template renders it.</param>
/// <param name="Model">
/// The typed content for this kind. The contract is that <paramref name="Model"/>
/// matches <paramref name="Kind"/>: <see cref="DocumentKind.Cv"/> → a
/// <see cref="CvDocument"/>, and so on. It is <c>object</c> so the engine can
/// stay non-generic across every kind; the engine validates the type and throws
/// a clear error on a mismatch rather than rendering garbage.
/// </param>
/// <param name="TemplateId">
/// Which template to use, from <see cref="IDocumentEngine.AvailableTemplates"/>.
/// A null/empty value selects the engine's default template for the kind.
/// </param>
/// <param name="Format">Output format. PDF for now.</param>
public sealed record DocumentRequest(
    DocumentKind   Kind,
    object         Model,
    string?        TemplateId = null,
    DocumentFormat Format     = DocumentFormat.Pdf);

/// <summary>
/// A rendered document, as bytes.
/// </summary>
/// <remarks>
/// Bytes, not a file path, on purpose: WHERE the document lands (app-private
/// storage, a share sheet, a <c>FileProvider</c> content:// URI) is a
/// platform-specific decision the host owns. The engine stays platform-neutral
/// and never touches the filesystem.
/// </remarks>
/// <param name="Bytes">The rendered document.</param>
/// <param name="MimeType">e.g. <c>application/pdf</c> — for the share/open intent.</param>
/// <param name="SuggestedFileName">e.g. <c>Thabo-Mokoena-CV.pdf</c>. A suggestion; the host may override.</param>
public sealed record DocumentResult(
    byte[] Bytes,
    string MimeType,
    string SuggestedFileName)
{
    /// <summary>Builds a PDF result with the standard MIME type.</summary>
    public static DocumentResult Pdf(byte[] bytes, string suggestedFileName)
        => new(bytes, "application/pdf", suggestedFileName);
}
