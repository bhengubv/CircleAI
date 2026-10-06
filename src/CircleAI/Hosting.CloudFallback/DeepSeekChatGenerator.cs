// DeepSeekChatGenerator.cs
//
// (3.3.0) IChatGenerator backed by DeepSeek (OpenAI-compatible at /v1/chat/completions).

using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace CircleAI.Hosting.CloudFallback;

public sealed class DeepSeekChatGenerator : OpenAiCompatibleChatGeneratorBase
{
    private readonly DeepSeekChatOptions _options;

    public DeepSeekChatGenerator(HttpClient http, DeepSeekChatOptions options, ILogger<DeepSeekChatGenerator>? logger = null)
        : base(http, options?.BaseAddress!, logger!)
    {
        _options = options ?? throw new System.ArgumentNullException(nameof(options));
    }

    public override string Id          => "deepseek";
    public override string EngineLabel => $"DeepSeek · {_options.Model}";
    protected override string?  ApiKey             => _options.ApiKey;
    protected override string   Model              => _options.Model;
    protected override float    DefaultTemperature => _options.Temperature;
    protected override int      DefaultMaxTokens   => _options.MaxTokens;
}
