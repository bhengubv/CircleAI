// Contracts.cs
//
// (3.2.0) Image-generation contract surface. CircleAI.Vision is
// detection-only; this package is its generation counterpart. Lift from
// Concierge.Shared.Media.IImageRuntime — same record shapes, just
// renamed for the CircleAI substrate.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Vision.Cloud;

/// <summary>(3.2.0) One image-generation request.</summary>
/// <param name="Prompt">Text prompt.</param>
/// <param name="NegativePrompt">Optional negative prompt (Stability supports it; OpenAI ignores).</param>
/// <param name="Size">Square size in pixels — typical 512 / 768 / 1024 / 1536.</param>
/// <param name="Count">Number of images to produce (1..n).</param>
/// <param name="Style">Optional style preset id (provider-specific).</param>
public sealed record ImageGenerationRequest(
    string  Prompt,
    string? NegativePrompt = null,
    int     Size           = 1024,
    int     Count          = 1,
    string? Style          = null);

/// <summary>(3.2.0) One generated image. Either Url OR Bytes, never both.</summary>
public sealed record ImageArtifact(
    string         GeneratorId,
    string         Prompt,
    string         MimeType,
    string?        Url,
    byte[]?        Bytes,
    DateTimeOffset GeneratedAtUtc);

/// <summary>(3.2.0) Generate images from a text prompt.</summary>
public interface IImageGenerator
{
    /// <summary>Backend self-identification — "openai-images" / "stability" / "null".</summary>
    string GeneratorId { get; }

    /// <summary>Display label for the UI selector.</summary>
    string DisplayLabel { get; }

    /// <summary>True when the generator has the credentials it needs.</summary>
    bool IsConfigured { get; }

    /// <summary>Status message for the UI.</summary>
    string StatusMessage { get; }

    /// <summary>Generate images. Fail-soft: empty list when not configured.</summary>
    Task<IReadOnlyList<ImageArtifact>> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken      ct = default);
}

/// <summary>(3.2.0) Empty generator — always returns no images.</summary>
public sealed class NullImageGenerator : IImageGenerator
{
    public static readonly NullImageGenerator Instance = new();

    public string GeneratorId   => "null";
    public string DisplayLabel  => "No image generator";
    public bool   IsConfigured  => false;
    public string StatusMessage => "No image generator wired. Configure OpenAI:ApiKey or Stability:ApiKey to enable.";

    public Task<IReadOnlyList<ImageArtifact>> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken      ct = default)
        => Task.FromResult<IReadOnlyList<ImageArtifact>>(Array.Empty<ImageArtifact>());
}
