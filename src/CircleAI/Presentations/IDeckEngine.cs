#nullable enable

// IDeckEngine.cs
//
// The seam. A Deck in, PDF bytes out — and NOTHING about which PDF library does
// the rendering. Mirrors IDocumentEngine in CircleAI.Documents on purpose: the
// two engines are siblings, and swapping the concrete renderer is a one-class
// change no caller ever sees.
//
// We reuse CircleAI.Documents' public DocumentResult as the output type rather
// than inventing a parallel one — a rendered deck is just another "bytes + MIME +
// suggested filename" artifact, so the host's share/open plumbing is identical.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Documents;

namespace CircleAI.Presentations;

/// <summary>
/// Renders a <see cref="Deck"/> to a PDF (one slide per page), fully offline and
/// on-device.
/// </summary>
public interface IDeckEngine
{
    /// <summary>
    /// Renders one deck to PDF bytes. Throws <see cref="System.ArgumentNullException"/>
    /// if <paramref name="deck"/> is null.
    /// </summary>
    ValueTask<DocumentResult> RenderAsync(Deck deck, CancellationToken ct = default);

    /// <summary>
    /// Template ids this engine can render, for a host to offer as a choice. The
    /// first entry is the default when none is requested.
    /// </summary>
    IReadOnlyList<string> AvailableTemplates { get; }
}
