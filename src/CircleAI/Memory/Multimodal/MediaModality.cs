// MediaModality.cs

namespace CircleAI.Memory.Multimodal;

/// <summary>
/// Modality of a multimodal memory entry. Drives how the ingester routes the
/// raw bytes to the captioner and which side-channel metadata is captured
/// (dimensions for visual, duration for time-based).
/// </summary>
public enum MediaModality
{
    /// <summary>Still image — JPEG, PNG, HEIC, WebP, AVIF.</summary>
    Image,

    /// <summary>Audio clip — Opus, WAV, MP3, M4A.</summary>
    Audio,

    /// <summary>Video — MP4, MOV, WebM. Captioned via key-frame extraction by the host.</summary>
    Video,

    /// <summary>Text document — PDF, DOCX, plain text snippet larger than a single message.</summary>
    TextDocument,
}
