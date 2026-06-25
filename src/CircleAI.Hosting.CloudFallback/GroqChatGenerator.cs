// GroqChatGenerator.cs
//
// (3.3.0) IChatGenerator backed by Groq's OpenAI-compatible API.
// Endpoint: /openai/v1/chat/completions. Famously fast.

using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace CircleAI.Hosting.CloudFallback;

public sealed class GroqChatGenerator : OpenAiCompatibleChatGeneratorBase
{
    private readonly GroqChatOptions _options;

    public GroqChatGenerator(HttpClient http, GroqChatOptions options, ILogger<GroqChatGenerator>? logger = null)
        : base(http, options?.BaseAddress!, logger!)
    {
        _options = options ?? throw new System.ArgumentNullException(nameof(options));
    }

    public override string Id          => "groq";
    public override string EngineLabel => $"Groq · {_options.Model}";
    protected override string?  ApiKey             => _options.ApiKey;
    protected override string   Model              => _options.Model;
    protected override float    DefaultTemperature => _options.Temperature;
    protected override int      DefaultMaxTokens   => _options.MaxTokens;
    protected override string   ChatCompletionsPath => "/openai/v1/chat/completions";
}
