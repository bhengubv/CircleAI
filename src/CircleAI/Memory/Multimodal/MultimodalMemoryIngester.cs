// MultimodalMemoryIngester.cs
//
// The orchestration entry point. Hosts call this when the user shares an
// image / audio / video / document. The ingester:
//
//   1. Hashes the source (SHA-256, hex-lower)
//   2. Dedupes — if the hash is already known, reinforces the existing
//      entry and returns it. No re-captioning, no duplicate storage.
//   3. Picks a captioner via IMultimodalCaptioner.CanCaption()
//   4. Asks the captioner for a CaptionResult
//   5. Persists a MultimodalMemoryEntry to the store
//
// Raw bytes are never persisted. The hash is the only durable handle the
// memory layer keeps for the original artefact.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Multimodal;

/// <summary>
/// Outcome of an <see cref="MultimodalMemoryIngester.IngestAsync"/> call.
/// </summary>
public sealed record IngestionResult(MultimodalMemoryEntry Entry, bool WasDeduplicated);

/// <summary>
/// Ingests raw media bytes into compressed semantic memory.
/// </summary>
public sealed class MultimodalMemoryIngester
{
    private readonly IReadOnlyList<IMultimodalCaptioner> _captioners;
    private readonly IMultimodalMemoryStore _store;

    /// <summary>
    /// Constructs an ingester. Captioners are tried in order — the first one
    /// whose <see cref="IMultimodalCaptioner.CanCaption"/> returns true wins.
    /// The host typically registers richer captioners first and the heuristic
    /// fallback last.
    /// </summary>
    public MultimodalMemoryIngester(
        IEnumerable<IMultimodalCaptioner> captioners,
        IMultimodalMemoryStore store)
    {
        ArgumentNullException.ThrowIfNull(captioners);
        ArgumentNullException.ThrowIfNull(store);
        _captioners = captioners is IReadOnlyList<IMultimodalCaptioner> list
            ? list
            : new List<IMultimodalCaptioner>(captioners);
        if (_captioners.Count == 0)
            throw new ArgumentException("At least one captioner is required.", nameof(captioners));
        _store = store;
    }

    /// <summary>
    /// Ingests an artefact. When the SHA-256 matches an existing entry the
    /// stored record is reinforced rather than re-captioned, and the result's
    /// <see cref="IngestionResult.WasDeduplicated"/> is true.
    /// </summary>
    /// <param name="modality">The kind of media.</param>
    /// <param name="sourceBytes">Raw bytes of the artefact.</param>
    /// <param name="mimeType">Optional MIME type for the source.</param>
    /// <param name="sourceUri">Optional URI of the original (host-retained).</param>
    /// <param name="tags">Optional caller-supplied tags.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IngestionResult> IngestAsync(
        MediaModality modality,
        ReadOnlyMemory<byte> sourceBytes,
        string? mimeType = null,
        string? sourceUri = null,
        Dictionary<string, string>? tags = null,
        CancellationToken ct = default)
    {
        if (sourceBytes.IsEmpty)
            throw new ArgumentException("Source bytes are empty.", nameof(sourceBytes));

        var hash = ComputeSha256(sourceBytes.Span);
        var existing = await _store.GetByHashAsync(hash, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            await _store.ReinforceAsync(hash, ct).ConfigureAwait(false);
            return new IngestionResult(existing, WasDeduplicated: true);
        }

        var captioner = PickCaptioner(modality, mimeType);
        var caption = await captioner.CaptionAsync(modality, sourceBytes, mimeType, ct)
            .ConfigureAwait(false);

        var entry = new MultimodalMemoryEntry
        {
            Modality = modality,
            Caption = caption.Caption,
            Embedding = caption.Embedding,
            SourceSha256 = hash,
            SourceMimeType = mimeType,
            SourceByteCount = sourceBytes.Length,
            SourceUri = sourceUri,
            WidthPx = caption.WidthPx,
            HeightPx = caption.HeightPx,
            DurationMs = caption.DurationMs,
            Tags = tags,
        };

        await _store.AddAsync(entry, ct).ConfigureAwait(false);
        return new IngestionResult(entry, WasDeduplicated: false);
    }

    private IMultimodalCaptioner PickCaptioner(MediaModality modality, string? mime)
    {
        foreach (var c in _captioners)
        {
            if (c.CanCaption(modality, mime)) return c;
        }
        // Guaranteed: the last captioner registered should accept everything;
        // if no host-supplied captioner matches, the heuristic fallback wins.
        return _captioners[^1];
    }

    private static string ComputeSha256(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
