// Options.cs
//
// (3.2.0) Provider-specific options. Concierge's defaults preserved
// verbatim — dall-e-3 / sd3.5-large / response_format=url.

using System;

namespace CircleAI.Vision.Cloud;

/// <summary>(3.2.0) OpenAI image-generation options.</summary>
public sealed class OpenAiImageOptions
{
    public Uri     BaseAddress { get; init; } = new("https://api.openai.com");
    public string? ApiKey      { get; init; }

    /// <summary>Model id. Default <c>dall-e-3</c>.</summary>
    public string  Model       { get; init; } = "dall-e-3";
}

/// <summary>(3.2.0) Stability AI image-generation options.</summary>
public sealed class StabilityImageOptions
{
    public Uri     BaseAddress  { get; init; } = new("https://api.stability.ai");
    public string? ApiKey       { get; init; }

    /// <summary>Model id. Default <c>sd3.5-large</c>.</summary>
    public string  Model        { get; init; } = "sd3.5-large";

    /// <summary>Output format. Default <c>png</c>.</summary>
    public string  OutputFormat { get; init; } = "png";
}
