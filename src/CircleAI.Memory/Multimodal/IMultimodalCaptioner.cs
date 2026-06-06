// IMultimodalCaptioner.cs
//
// Strategy seam — the ingester delegates the actual semantic conversion
// (bytes → caption + embedding) to a captioner. The host wires:
//   • CircleAI.Memory: HeuristicMultimodalCaptioner (no-AI fallback)
//   • CircleAI.Inference.Multimodal: KimiVlCaptioner (real Kimi-VL via MNN)
//   • Cloud: any third-party captioner adapter
//
// The captioner is told the source bytes + mime; it returns a caption and
// (optionally) an embedding. The bytes never leak past the captioner — the
// ingester does not pass them to the store.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Multimodal;

/// <summary>
/// Output of a single captioning call.
/// </summary>
/// <param name="Caption">
/// Human-readable semantic description of the artefact. Must not be empty.
/// </param>
/// <param name="Embedding">
/// Embedding of the artefact (typically of the caption + a vision embedding).
/// Null when the captioner has no embedding backend.
/// </param>
/// <param name="WidthPx">Image / video width when known.</param>
/// <param name="HeightPx">Image / video height when known.</param>
/// <param name="DurationMs">Audio / video duration when known.</param>
public sealed record CaptionResult(
    string Caption,
    float[]? Embedding,
    int? WidthPx = null,
    int? HeightPx = null,
    long? DurationMs = null);

/// <summary>
/// Converts raw media bytes into a semantic representation.
/// </summary>
public interface IMultimodalCaptioner
{
    /// <summary>
    /// Returns true when this captioner can handle the given modality + mime
    /// combination. The ingester picks among multiple captioners using this
    /// predicate.
    /// </summary>
    bool CanCaption(MediaModality modality, string? mimeType);

    /// <summary>
    /// Produces a <see cref="CaptionResult"/> for the given source bytes.
    /// Implementations must not retain the bytes after the call returns.
    /// </summary>
    Task<CaptionResult> CaptionAsync(
        MediaModality modality,
        ReadOnlyMemory<byte> sourceBytes,
        string? mimeType,
        CancellationToken ct = default);
}
