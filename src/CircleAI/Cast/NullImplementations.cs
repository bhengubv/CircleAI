// NullImplementations.cs — (3.5.0) Safe defaults. Absence of a real backend degrades
// to deterministic empty answers (discovery) or a clear fail-closed error (document
// rasterisation), never to a crash on the happy path.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Cast;

/// <summary>Discovery that finds nothing — the DI default when casting is disabled.</summary>
public sealed class NullCastDiscovery : ICastDiscovery
{
    public static readonly NullCastDiscovery Instance = new();

    public string BackendId => "null";

    public async IAsyncEnumerable<ICastTarget> DiscoverAsync(
        TimeSpan searchWindow,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}

/// <summary>
/// Document adapter that fails closed. Casting a deck/CV/invoice to a TV requires a
/// page rasteriser (see <see cref="IDocumentCastAdapter"/> remarks); until one is
/// wired in, this makes the missing capability explicit rather than silently empty.
/// </summary>
public sealed class NullDocumentCastAdapter : IDocumentCastAdapter
{
    public static readonly NullDocumentCastAdapter Instance = new();

    public string BackendId => "null";

    public ValueTask<IReadOnlyList<CastMedia>> ToCastableAsync(CastDocument document, CancellationToken ct = default)
        => throw new NotSupportedException(
            "Document to image rasterisation is not implemented pure-managed. Supply an " +
            "IDocumentCastAdapter backed by a page rasteriser (e.g. PDFium — BSD-3-Clause, or " +
            "SkiaSharp — MIT) to cast decks / CVs / invoices to a TV. Media (video/audio/image) " +
            "casts without an adapter.");
}
