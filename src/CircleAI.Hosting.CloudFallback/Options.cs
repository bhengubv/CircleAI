// Options.cs
//
// (3.2.0) Per-provider option shapes. Lifted from Concierge with no
// substantive change — the host owns the factory, the key comes from
// IConfiguration / env, the runtime never embeds it.

using System;

namespace CircleAI.Hosting.CloudFallback;

/// <summary>(3.2.0) OpenAI Chat Completions options. Defaults match Concierge's working config.</summary>
public sealed class OpenAiChatOptions
{
    public Uri    BaseAddress { get; init; } = new("https://api.openai.com");
    public string? ApiKey      { get; init; }
    public string Model       { get; init; } = "gpt-4o-mini";
    public float  Temperature { get; init; } = 0.7f;
    public int    MaxTokens   { get; init; } = 1024;
}

/// <summary>(3.2.0) Anthropic Messages options.</summary>
public sealed class AnthropicChatOptions
{
    public Uri     BaseAddress     { get; init; } = new("https://api.anthropic.com");
    public string? ApiKey          { get; init; }
    public string  Model           { get; init; } = "claude-3-5-sonnet-latest";
    public float   Temperature     { get; init; } = 0.7f;
    public int     MaxTokens       { get; init; } = 1024;
    public string  AnthropicVersion { get; init; } = "2023-06-01";
}

/// <summary>(3.2.0) Google Gemini options.</summary>
public sealed class GeminiChatOptions
{
    public Uri    BaseAddress { get; init; } = new("https://generativelanguage.googleapis.com");
    public string? ApiKey      { get; init; }
    public string Model       { get; init; } = "gemini-2.0-flash";
    public float  Temperature { get; init; } = 0.7f;
    public int    MaxOutputTokens { get; init; } = 1024;
}
