// MultimodalMemoryEntry.cs
//
// Compressed semantic representation of one media artefact. The whole point
// of multimodal memory is that we DO NOT store the pixels / audio samples /
// video frames — we store the caption, the embedding, and a SHA-256 of the
// original so the host can reference it back if it kept the file elsewhere.
//
// Storage footprint comparison (typical):
//   Raw JPEG photo:   ~2 MB
//   This record:      ~2 KB  (caption + 1536-dim FP32 embedding + metadata)
// Compression: ~1000× — and that's before TurboQuant compresses the embedding.

using System;

namespace CircleAI.Memory.Multimodal;

/// <summary>
/// One semantically-compressed media memory. The caption + embedding capture
/// the meaning; raw bytes are never retained by the memory layer.
/// </summary>
public sealed class MultimodalMemoryEntry
{
    /// <summary>Stable identifier.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>UTC timestamp the memory was recorded.</summary>
    public DateTimeOffset RecordedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Which kind of media this came from.</summary>
    public MediaModality Modality { get; init; }

    /// <summary>
    /// Caption — the semantic content. Produced by the registered
    /// <see cref="IMultimodalCaptioner"/>; the heuristic fallback writes a
    /// descriptive shell when no captioner is wired.
    /// </summary>
    public string Caption { get; init; } = string.Empty;

    /// <summary>
    /// Embedding of the caption (and, for richer captioners, the joint
    /// modality embedding). Null when the captioner could not produce one.
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    /// SHA-256 of the original bytes, hex-lower. The memory layer never
    /// stores the bytes themselves, but this hash lets the host:
    ///   • dedupe — refuse to caption the same artefact twice
    ///   • reference — link back to a file the host kept on disk / in the cloud
    ///   • verify — confirm a re-uploaded file matches what was remembered
    /// </summary>
    public string SourceSha256 { get; init; } = string.Empty;

    /// <summary>Original MIME type (e.g. <c>image/jpeg</c>). Captured for diagnostics.</summary>
    public string? SourceMimeType { get; init; }

    /// <summary>Size in bytes of the original artefact.</summary>
    public long SourceByteCount { get; init; }

    /// <summary>
    /// Optional URI of the original artefact if the host retained it
    /// elsewhere (file path, https URL, content-addressed mesh URI).
    /// Null when the host did not preserve the original.
    /// </summary>
    public string? SourceUri { get; init; }

    /// <summary>Image / video width in pixels, when applicable.</summary>
    public int? WidthPx { get; init; }

    /// <summary>Image / video height in pixels, when applicable.</summary>
    public int? HeightPx { get; init; }

    /// <summary>Audio / video duration in milliseconds, when applicable.</summary>
    public long? DurationMs { get; init; }

    /// <summary>
    /// How many times this artefact has been re-presented to the ingester.
    /// Incremented on every dedup hit instead of creating a new entry.
    /// </summary>
    public int ReferenceCount { get; set; } = 1;

    /// <summary>Optional tags (e.g. <c>location</c>, <c>person</c>, <c>topic</c>).</summary>
    public System.Collections.Generic.Dictionary<string, string>? Tags { get; init; }
}
