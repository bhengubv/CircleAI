#nullable enable

// IDocumentEngine.cs
//
// The seam. Content in, bytes out — and NOTHING about which PDF library does the
// rendering. Swapping PDFsharp for QuestPDF (or the reverse) is a one-class
// change that no caller ever sees, which is exactly why the licensing decision
// on the concrete engine does not block anything built against this interface.

using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace CircleAI.Documents;

/// <summary>
/// Renders a <see cref="DocumentRequest"/> to bytes, fully offline and on-device.
/// </summary>
/// <remarks>
/// The engine needs NO model: given a populated <see cref="CvDocument"/> it always
/// produces a PDF on any device. "Scaling up" on better hardware happens on the
/// CONTENT side — the model that fills the words is chosen by the device-aware
/// selector — not here. The same render code runs identically on a P30 Lite and
/// a Pixel.
/// </remarks>
public interface IDocumentEngine
{
    /// <summary>
    /// Renders one document. Throws <see cref="System.ArgumentException"/> if
    /// <see cref="DocumentRequest.Model"/> does not match
    /// <see cref="DocumentRequest.Kind"/> — a clear failure beats a garbled PDF.
    /// </summary>
    ValueTask<DocumentResult> RenderAsync(DocumentRequest request, CancellationToken ct = default);

    /// <summary>
    /// Template ids this engine can render, for a host to offer as a choice.
    /// The first entry is the default when a request names no template.
    /// </summary>
    IReadOnlyList<string> AvailableTemplates { get; }
}
