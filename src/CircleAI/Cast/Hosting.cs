// Hosting.cs — (3.5.0) Two seams that sit either side of the cast session:
//   ILocalMediaHost      — serve bytes/files over LAN HTTP so a renderer can pull them.
//   IDocumentCastAdapter — turn a document/deck into castable page images.
// The first is genuinely shipped pure-managed (TcpMediaHost). The second is an
// HONEST seam: rasterising PDF/decks needs a page renderer that is not pure-managed,
// so it is defined and marked here rather than faked.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Cast;

/// <summary>
/// Serves byte-/file-backed media over plain HTTP on the LAN so a DLNA renderer can
/// pull it. This is the "media server" leg of casting. The shipped implementation
/// (<c>CircleAI.Cast.Http.TcpMediaHost</c>) serves individual published assets with
/// HTTP Range support — enough to fling one generated asset at a TV.
/// </summary>
/// <remarks>
/// HONEST SCOPE: no transcoding and no library browsing. Content is served
/// byte-for-byte; playback depends on the TV's own codec support (DLNA TVs broadly
/// handle H.264/AAC MP4, JPEG and MP3). A full media server (DIDL browse tree,
/// transcoding, multi-title indexing) is deliberately out of scope — if a target
/// cannot decode an asset, transcode upstream (e.g. an FFmpeg-backed step in the
/// media pipeline) before publishing here.
/// </remarks>
public interface ILocalMediaHost : IAsyncDisposable
{
    /// <summary>Backend self-identification — "tcp-http", "null".</summary>
    string BackendId { get; }

    /// <summary>Whether the host is currently listening.</summary>
    bool IsRunning { get; }

    /// <summary>Base URL clients use once started (e.g. http://192.168.1.10:49xxx/), or null.</summary>
    Uri? BaseUrl { get; }

    /// <summary>Start listening. Idempotent.</summary>
    ValueTask StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Publish a source and return a LAN-reachable URL the renderer can GET. Starts
    /// the host on demand. Throws for <see cref="CastMediaSource.Url"/> sources — those
    /// are already reachable and need no hosting.
    /// </summary>
    ValueTask<Uri> PublishAsync(CastMediaSource source, string mimeType, CancellationToken ct = default);

    /// <summary>Revoke a previously published URL.</summary>
    ValueTask UnpublishAsync(Uri url, CancellationToken ct = default);
}

/// <summary>A source document to be cast — a generated deck, CV, invoice or report.</summary>
public sealed record CastDocument(string Title, CastMediaSource Source, string MimeType);

/// <summary>
/// Turns a document (PDF / deck) into castable assets. A DLNA renderer cannot render
/// a PDF or PPTX directly — it displays images/audio/video — so a deck becomes a
/// slideshow of page images and a report becomes an ordered image set.
/// </summary>
/// <remarks>
/// HONEST SEAM — NOT IMPLEMENTED PURE-MANAGED. Rasterising PDF/deck pages to images
/// needs a page renderer. PdfSharp (used by CircleAI.Presentations / CircleAI.Documents
/// to WRITE PDFs) does not rasterise. A real backend wraps a rasteriser such as PDFium
/// (BSD-3-Clause, free/OSS) or SkiaSharp (MIT) — both native, hence not pure-managed,
/// so this is intentionally left as a seam rather than pulled into a pure-managed lib.
/// The shipped <see cref="NullDocumentCastAdapter"/> fails closed with guidance.
/// </remarks>
public interface IDocumentCastAdapter
{
    /// <summary>Backend self-identification — "pdfium", "skia", "null".</summary>
    string BackendId { get; }

    /// <summary>Render <paramref name="document"/> to an ordered set of castable page images.</summary>
    ValueTask<IReadOnlyList<CastMedia>> ToCastableAsync(CastDocument document, CancellationToken ct = default);
}
