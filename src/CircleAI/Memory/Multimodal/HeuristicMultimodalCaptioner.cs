// HeuristicMultimodalCaptioner.cs
//
// Honest fallback. When no real captioner (Kimi-VL, SenseVoice, etc.) is
// wired, this produces a descriptive shell caption from what the bytes
// announce about themselves (mime, size, the first few bytes' magic
// signature). It does NOT fabricate semantic content it can't see — the
// caption is shaped so consumers can detect the "no captioner present"
// state and react accordingly.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Multimodal;

/// <summary>
/// Default <see cref="IMultimodalCaptioner"/>. Returns a descriptive shell
/// caption — never fabricates semantic content. Always available, zero
/// model dependency, zero token cost.
/// </summary>
public sealed class HeuristicMultimodalCaptioner : IMultimodalCaptioner
{
    /// <inheritdoc/>
    public bool CanCaption(MediaModality modality, string? mimeType) => true;

    /// <inheritdoc/>
    public Task<CaptionResult> CaptionAsync(
        MediaModality modality,
        ReadOnlyMemory<byte> sourceBytes,
        string? mimeType,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var detected = DetectMime(sourceBytes.Span, mimeType);
        var caption = modality switch
        {
            MediaModality.Image        => $"[Image — no captioner wired. {detected}, {sourceBytes.Length} bytes.]",
            MediaModality.Audio        => $"[Audio — no captioner wired. {detected}, {sourceBytes.Length} bytes.]",
            MediaModality.Video        => $"[Video — no captioner wired. {detected}, {sourceBytes.Length} bytes.]",
            MediaModality.TextDocument => $"[Document — no captioner wired. {detected}, {sourceBytes.Length} bytes.]",
            _                          => $"[Media — no captioner wired. {detected}, {sourceBytes.Length} bytes.]",
        };

        return Task.FromResult(new CaptionResult(caption, Embedding: null));
    }

    private static string DetectMime(ReadOnlySpan<byte> bytes, string? declared)
    {
        if (!string.IsNullOrWhiteSpace(declared)) return declared;
        if (bytes.Length >= 4)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xD8) return "image/jpeg";
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return "image/gif";
            if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46) return "audio/wav";
            if (bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46) return "application/pdf";
        }
        return "application/octet-stream";
    }
}
