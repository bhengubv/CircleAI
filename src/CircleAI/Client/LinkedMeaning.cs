// LinkedMeaning.cs
//
// Text into a vector by asking the service, whose embedding model it is (OPEN-GAPS A7).
//
// THE MODEL STAYS WHERE THE MODELS ARE. The app is a thin client: it keeps its own
// transcripts and its own small index of them, and asks the service only for vectors - one
// short call a line, once, and one a search. A phone whose service has no embedding model
// answers every call with a refusal, and the search that asked goes on by its words.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Embeddings.Local;
using CircleAI.Linking;

namespace CircleAI.Client;

/// <summary>An <see cref="IEmbeddingEncoder"/> over the link's <see cref="LinkVerb.Embed"/>.</summary>
/// <param name="brain">The linked brain, whose connection this rides - as <see cref="LinkedMemory"/> does.</param>
public sealed class LinkedMeaning(LinkedBrain brain) : IEmbeddingEncoder
{
    private int _dimension;

    /// <inheritdoc />
    /// <remarks>
    /// LEARNED FROM THE MODEL, NOT WRITTEN HERE: the first vector's width, asked for once
    /// with a short text when nothing has been embedded yet.
    /// </remarks>
    public int Dimension =>
        _dimension > 0 ? _dimension : EncodeAsync("width").AsTask().GetAwaiter().GetResult().Length;

    /// <inheritdoc />
    public async ValueTask<float[]> EncodeAsync(string text, CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Circle AI is not linked, so nothing can be searched by meaning.");
        var reply = await client.EmbedAsync(text, ct).ConfigureAwait(false);
        if (!reply.Ok || reply.Rows.Count == 0)
            throw new InvalidOperationException(reply.Error ?? "Circle AI sent no vector.");

        var vector = LinkEmbedRows.Vector(reply.Rows[0]);
        _dimension = vector.Length;
        return vector;
    }
}
