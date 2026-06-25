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

/// <summary>(3.3.0) Groq Chat Completions options. OpenAI-compatible at <c>/openai/v1/chat/completions</c>.</summary>
public sealed class GroqChatOptions
{
    public Uri    BaseAddress { get; init; } = new("https://api.groq.com");
    public string? ApiKey      { get; init; }
    public string Model       { get; init; } = "llama-3.3-70b-versatile";
    public float  Temperature { get; init; } = 0.7f;
    public int    MaxTokens   { get; init; } = 1024;
}

/// <summary>(3.3.0) Cerebras options. OpenAI-compatible at <c>/v1/chat/completions</c>.</summary>
public sealed class CerebrasChatOptions
{
    public Uri    BaseAddress { get; init; } = new("https://api.cerebras.ai");
    public string? ApiKey      { get; init; }
    public string Model       { get; init; } = "llama3.3-70b";
    public float  Temperature { get; init; } = 0.7f;
    public int    MaxTokens   { get; init; } = 1024;
}

/// <summary>(3.3.0) Together AI options. OpenAI-compatible at <c>/v1/chat/completions</c>.</summary>
public sealed class TogetherChatOptions
{
    public Uri    BaseAddress { get; init; } = new("https://api.together.xyz");
    public string? ApiKey      { get; init; }
    public string Model       { get; init; } = "meta-llama/Llama-3.3-70B-Instruct-Turbo";
    public float  Temperature { get; init; } = 0.7f;
    public int    MaxTokens   { get; init; } = 1024;
}

/// <summary>(3.3.0) DeepSeek options. OpenAI-compatible at <c>/v1/chat/completions</c>.</summary>
public sealed class DeepSeekChatOptions
{
    public Uri    BaseAddress { get; init; } = new("https://api.deepseek.com");
    public string? ApiKey      { get; init; }
    public string Model       { get; init; } = "deepseek-chat";
    public float  Temperature { get; init; } = 0.7f;
    public int    MaxTokens   { get; init; } = 1024;
}
